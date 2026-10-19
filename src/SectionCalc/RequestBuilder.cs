using System.Text.Json.Nodes;

namespace SectionCalc;

public sealed record BarPoint(double XMm, double YMm, double DiameterMm);

/// <summary>Physical layout of the longitudinal bars (geometry only, no SP 63 detailing check).</summary>
public sealed record BarLayout(IReadOnlyList<BarPoint> Bars, double SteelAreaMm2, double MinClearanceMm, string? Problem)
{
    public bool Fits => Problem is null;
}

/// <summary>
/// Builds the CScorePileRunner request for one adopted section, mirroring the
/// single-section path of workbench.model.check_adopted. No resistance formulas.
/// </summary>
public static class RequestBuilder
{
    public const int MeshResolution = 41;
    public const int PolygonSides = 96;

    public static BarLayout Layout(SectionProject p) => p.Kind == SectionKind.Pile ? PileLayout(p.Pile) : WallLayout(p.Wall);

    public static BarLayout PileLayout(PileGeometry g)
    {
        double axis = g.DiameterMm / 2 - g.CoverMm - g.SpiralDiameterMm - g.BarDiameterMm / 2;
        // Chord between neighbouring bar axes minus one diameter (pile_rebar.make_candidates).
        double clear = 2 * axis * Math.Sin(Math.PI / g.BarCount) - g.BarDiameterMm;
        var bars = Enumerable.Range(0, g.BarCount).Select(i =>
        {
            double a = 2 * Math.PI * i / g.BarCount;
            return new BarPoint(axis * Math.Cos(a), axis * Math.Sin(a), g.BarDiameterMm);
        }).ToList();
        string? problem = g.BarCount < 6 ? "В кольце должно быть не менее 6 стержней"
            : axis <= 0 ? "Стержни не помещаются в сечении"
            : clear < g.MinClearSpacingMm ? $"Просвет между стержнями {clear:0} мм меньше {g.MinClearSpacingMm:0} мм" : null;
        return new BarLayout(bars, g.BarCount * Math.PI * g.BarDiameterMm * g.BarDiameterMm / 4, clear, problem);
    }

    public static BarLayout WallLayout(WallGeometry g)
    {
        // One cage across the checked width; end cover equals the face cover (wall_rebar.make_candidates).
        double cage = g.WidthMm - 2 * g.CoverMm;
        var bars = new List<BarPoint>(); var clear = new List<double>(); string? problem = null;
        foreach (var (face, sign) in new[] { (g.NegativeFace, -1), (g.PositiveFace, 1) })
        {
            double inset = g.StirrupDiameterMm + face.BarDiameterMm / 2;
            double depth = g.ThicknessMm / 2 - g.CoverMm - inset;
            if (face.BarCount < 2) { problem ??= "На каждой грани нужно не менее 2 стержней"; continue; }
            double pitch = (cage - 2 * inset) / (face.BarCount - 1);
            clear.Add(pitch - face.BarDiameterMm);
            if (depth <= face.BarDiameterMm / 2 || pitch - face.BarDiameterMm < g.MinClearSpacingMm)
                problem ??= $"Стержни грани {(sign > 0 ? "«+»" : "«−»")} не помещаются с просветом {g.MinClearSpacingMm:0} мм";
            for (int i = 0; i < face.BarCount; i++)
                bars.Add(new BarPoint(-cage / 2 + inset + i * pitch, sign * depth, face.BarDiameterMm));
        }
        double through = g.ThicknessMm - 2 * g.CoverMm - 2 * g.StirrupDiameterMm
            - g.PositiveFace.BarDiameterMm - g.NegativeFace.BarDiameterMm;
        clear.Add(through);
        if (through < g.MinClearSpacingMm) problem ??= "Недостаточный просвет между гранями";
        double area = bars.Sum(b => Math.PI * b.DiameterMm * b.DiameterMm / 4);
        return new BarLayout(bars, area, clear.Count > 0 ? clear.Min() : 0, problem);
    }

    /// <summary>Checks the user input; returns readable problems (empty list when the section can be calculated).</summary>
    public static List<string> Validate(SectionProject p)
    {
        var e = new List<string>();
        if (p.Kind == SectionKind.Pile)
        {
            var g = p.Pile;
            if (g.DiameterMm <= 0 || g.CoverMm < 0 || g.SpiralDiameterMm <= 0 || g.BarDiameterMm <= 0)
                e.Add("Размеры сечения и диаметры должны быть положительными");
        }
        else
        {
            var g = p.Wall;
            if (g.WidthMm <= 0 || g.ThicknessMm <= 0 || g.CoverMm < 0 || g.StirrupDiameterMm <= 0)
                e.Add("Размеры сечения и диаметры должны быть положительными");
        }
        if (e.Count == 0 && Layout(p).Problem is string layout) e.Add(layout);
        var uls = p.Loads.Where(l => l.Basis == LoadBasis.Uls).ToList();
        if (uls.Count == 0) e.Add("Добавьте хотя бы одно расчётное сочетание (ULS)");
        var names = p.Loads.Select(l => l.Name.Trim()).ToList();
        if (names.Any(string.IsNullOrEmpty)) e.Add("У каждого сочетания должно быть название");
        else if (names.Distinct().Count() != names.Count) e.Add("Названия сочетаний повторяются");
        if (p.Loads.Any(l => new[] { l.N, l.Mx, l.My, l.Q, l.NLong ?? 0, l.MxLong ?? 0, l.MyLong ?? 0 }.Any(v => !double.IsFinite(v))))
            e.Add("Усилия должны быть числами");
        var c = p.Conditions;
        if (c.Eccentricity == EccentricityMode.Sp63 && c.MemberLengthM <= 0) e.Add("Для случайного эксцентриситета задайте длину элемента");
        if (c.SecondOrder == SecondOrderMode.Cscore && (c.L0xM <= 0 || c.L0yM <= 0)) e.Add("Для расчёта η задайте расчётные длины l₀x и l₀y");
        if (c.SustainedShare is < 0 or > 1) e.Add("Доля длительной нагрузки для η должна быть от 0 до 1");
        if (c.CrackLimitLongMm <= 0 || c.CrackLimitShortMm <= 0) e.Add("Предельная ширина раскрытия трещин должна быть положительной");
        if (c.ShearCheck)
        {
            if (c.ShearPitchMm <= 0) e.Add("Задайте шаг поперечной арматуры");
            if (p.Kind == SectionKind.Wall && c.StirrupLegs < 1) e.Add("Задайте число рабочих ветвей хомутов");
            if (p.Kind == SectionKind.Pile && (c.PileShearAMm <= 0 || c.PileShearAMm >= 0.885 * p.Pile.DiameterMm))
                e.Add("Для Q круглого сечения задайте a: 0 < a < 0,885D");
        }
        if (c.InclinedMoment && (!c.ShearCheck || c.AnchorageFactor is <= 0 or > 1 || c.SupportDistanceDownM <= 0 || c.SupportDistanceUpM <= 0))
            e.Add("Для момента в наклонном сечении включите проверку Q и задайте k (0…1) и расстояния до опор");
        return e;
    }

    /// <summary>Request for one section; ULS loads are those after the optional eccentricity preparation.</summary>
    internal static JsonObject Build(SectionProject p, IReadOnlyList<UlsLoad> uls)
    {
        var layout = Layout(p);
        var c = p.Conditions;
        var req = new JsonObject { ["schema_version"] = 1 };
        if (p.Kind == SectionKind.Pile)
        {
            var g = p.Pile;
            req["pile"] = new JsonObject { ["diameter_mm"] = g.DiameterMm, ["mesh_resolution"] = MeshResolution, ["polygon_sides"] = PolygonSides };
            req["candidates"] = new JsonArray(new JsonObject
            {
                ["id"] = $"{g.BarCount}x{g.BarDiameterMm:0.##}", ["bar_count"] = g.BarCount, ["bar_diameter_mm"] = g.BarDiameterMm,
                ["rotation_deg"] = 0, ["bar_axis_radius_mm"] = g.DiameterMm / 2 - g.CoverMm - g.SpiralDiameterMm - g.BarDiameterMm / 2,
                ["steel_area_mm2"] = layout.SteelAreaMm2, ["clearance_mm"] = layout.MinClearanceMm,
            });
        }
        else
        {
            var g = p.Wall;
            req["pile"] = new JsonObject { ["diameter_mm"] = 0, ["polygon_sides"] = 0, ["mesh_resolution"] = MeshResolution };
            req["research_rectangle"] = new JsonObject
            {
                ["width_mm"] = g.WidthMm, ["height_mm"] = g.ThicknessMm, ["mesh_resolution"] = MeshResolution,
                ["bars"] = new JsonArray(layout.Bars.Select(b => (JsonNode)new JsonObject
                    { ["x_mm"] = b.XMm, ["y_mm"] = b.YMm, ["diameter_mm"] = b.DiameterMm }).ToArray()),
            };
            req["candidates"] = new JsonArray(new JsonObject
            {
                ["id"] = $"neg{g.NegativeFace.BarCount}d{g.NegativeFace.BarDiameterMm:0.##}_pos{g.PositiveFace.BarCount}d{g.PositiveFace.BarDiameterMm:0.##}",
                ["bar_count"] = g.NegativeFace.BarCount + g.PositiveFace.BarCount,
                ["bar_diameter_mm"] = Math.Max(g.NegativeFace.BarDiameterMm, g.PositiveFace.BarDiameterMm),
                ["rotation_deg"] = 0, ["bar_axis_radius_mm"] = 0,
                ["steel_area_mm2"] = layout.SteelAreaMm2, ["clearance_mm"] = layout.MinClearanceMm,
            });
        }
        req["materials"] = ToJson(MaterialTables.UlsMaterials(p));
        req["loads"] = new JsonArray(uls.Select(l =>
        {
            var o = new JsonObject { ["id"] = l.Id, ["n_kn"] = l.N, ["mx_knm"] = l.Mx, ["my_knm"] = l.My };
            if (c.ShearCheck) o["q_kn"] = l.Q;
            if (l.SourceMx is double sx) { o["source_mx_knm"] = sx; o["source_my_knm"] = l.SourceMy ?? 0; }
            return (JsonNode)o;
        }).ToArray());
        var sls = p.Loads.Where(l => l.Basis == LoadBasis.Sls).ToList();
        if (sls.Count > 0)
        {
            var probe = new JsonObject
            {
                ["short_materials"] = ToJson(MaterialTables.SlsMaterials(p)), ["long_materials"] = ToJson(MaterialTables.SlsMaterials(p)),
                ["phi2"] = 0.5, ["acrc_ult_long_mm"] = c.CrackLimitLongMm, ["acrc_ult_short_mm"] = c.CrackLimitShortMm,
                ["preliminary_method"] = "arbat_conservative", ["review_band"] = 0.03,
            };
            probe["loads"] = new JsonArray(sls.Select(l =>
            {
                var o = new JsonObject
                {
                    ["id"] = l.Name.Trim(), ["n_kn"] = l.N, ["mx_total_knm"] = l.Mx, ["my_total_knm"] = l.My,
                    ["mx_long_knm"] = l.MxLong ?? l.Mx, ["my_long_knm"] = l.MyLong ?? l.My,
                };
                if (l.NLong is double nl) o["n_long_kn"] = nl;
                return (JsonNode)o;
            }).ToArray());
            req["sls_probe"] = probe;
        }
        req["sls_execution_policy"] = "uls_pass_only";
        if (c.ShearCheck)
        {
            var t = MaterialTables.Rebar(p.TransverseClass);
            req["shear_screening"] = new JsonObject
            {
                ["enabled"] = true,
                ["diameter_mm"] = p.Kind == SectionKind.Pile ? p.Pile.SpiralDiameterMm : p.Wall.StirrupDiameterMm,
                ["spacing_mm"] = c.ShearPitchMm,
                ["effective_legs_per_cage"] = p.Kind == SectionKind.Pile ? 2 : c.StirrupLegs,
                ["cage_count"] = 1, ["rsw_kpa"] = t.Rsw * 1000, ["constructive_confirmed"] = c.ShearModelConfirmed,
            };
        }
        req["connection_context"] = Context(p, uls.Any(l => l.SourceMx is not null));
        return req;
    }

    static JsonObject Context(SectionProject p, bool eccentricityPrepared)
    {
        var c = p.Conditions;
        var o = new JsonObject
        {
            ["second_order_mode"] = c.SecondOrder switch
            {
                SecondOrderMode.Cscore => "cscore_member", SecondOrderMode.InLoads => "already_in_loads", _ => "section_only",
            },
            ["l0_x_m"] = c.SecondOrder == SecondOrderMode.Cscore ? c.L0xM : null,
            ["l0_y_m"] = c.SecondOrder == SecondOrderMode.Cscore ? c.L0yM : null,
            ["psi"] = c.SecondOrder == SecondOrderMode.Cscore ? c.SustainedShare : 1.0,
            ["inclined_moment_requested"] = c.ShearCheck && c.InclinedMoment,
            ["anchorage_confirmed"] = c.ShearCheck && c.InclinedMoment,
            ["anchorage_factor"] = c.ShearCheck && c.InclinedMoment ? c.AnchorageFactor : null,
            ["support_distance_positive_m"] = c.ShearCheck && c.InclinedMoment ? c.SupportDistanceDownM : null,
            ["support_distance_negative_m"] = c.ShearCheck && c.InclinedMoment ? c.SupportDistanceUpM : null,
            ["eccentricity_in_loads"] = c.Eccentricity == EccentricityMode.InLoads || eccentricityPrepared,
            ["circular_shear"] = null,
        };
        if (p.Kind == SectionKind.Pile && c.ShearCheck)
            o["circular_shear"] = new JsonObject
            {
                ["method"] = "vniigs_1984_two_legs", ["a_mm"] = c.PileShearAMm, ["winding_diameter_mm"] = null,
                ["circular_efficiency"] = null, ["confirmed"] = c.ShearModelConfirmed,
                ["efficiency_basis"] = "Принятая модель: один полный виток — две условные ветви; b = 0,885D, h0 = b − a.",
            };
        return o;
    }

    static JsonObject ToJson(Dictionary<string, object> values) =>
        new(values.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(Convert.ToDouble(kv.Value)))));
}

/// <summary>A ULS action passed to the engine; Source* are the external moments before eccentricity.</summary>
internal sealed record UlsLoad(string Id, string SourceName, double N, double Mx, double My, double Q, double? SourceMx = null, double? SourceMy = null);
