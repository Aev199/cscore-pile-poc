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
        double axis = g.DiameterMm / 2 - g.AxisDistanceMm;
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
        double end = Math.Min(g.StirrupCoverMm(g.NegativeFace.BarDiameterMm), g.StirrupCoverMm(g.PositiveFace.BarDiameterMm));
        double cage = g.WidthMm - 2 * end;
        var bars = new List<BarPoint>(); var clear = new List<double>(); string? problem = null;
        foreach (var (face, sign) in new[] { (g.NegativeFace, -1), (g.PositiveFace, 1) })
        {
            double inset = g.StirrupDiameterMm + face.BarDiameterMm / 2;
            if (face.BarCount < 2) { problem ??= "На каждой грани нужно не менее 2 стержней"; continue; }
            if (face.Rows is < 1 or > 3) { problem ??= "Рядов стержней у грани — от 1 до 3"; continue; }
            double pitch = (cage - 2 * inset) / (face.BarCount - 1);
            clear.Add(pitch - face.BarDiameterMm);
            if (face.Rows > 1) clear.Add(face.RowGapMm);
            double depth = g.ThicknessMm / 2 - g.AxisDistanceMm(face);
            if (depth <= face.BarDiameterMm / 2 || pitch - face.BarDiameterMm < g.MinClearSpacingMm)
                problem ??= $"Стержни грани {(sign > 0 ? "«+»" : "«−»")} не помещаются с просветом {g.MinClearSpacingMm:0} мм";
            for (int row = 0; row < face.Rows; row++)
            {
                double y = g.ThicknessMm / 2 - g.RowAxisMm(face, row);
                for (int i = 0; i < face.BarCount; i++)
                    bars.Add(new BarPoint(-cage / 2 + inset + i * pitch, sign * y, face.BarDiameterMm));
            }
        }
        // Clear distance between the inner rows of the two faces.
        double through = g.ThicknessMm - g.RowAxisMm(g.PositiveFace, g.PositiveFace.Rows - 1) - g.RowAxisMm(g.NegativeFace, g.NegativeFace.Rows - 1)
            - g.PositiveFace.BarDiameterMm / 2 - g.NegativeFace.BarDiameterMm / 2;
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
            else if (g.SpiralCoverMm < 0)
                e.Add("Защитный слой меньше, чем помещается спираль снаружи стержней");
        }
        else
        {
            var g = p.Wall;
            if (g.WidthMm <= 0 || g.ThicknessMm <= 0 || g.CoverMm < 0 || g.StirrupDiameterMm <= 0)
                e.Add("Размеры сечения и диаметры должны быть положительными");
            else if (Math.Min(g.StirrupCoverMm(g.PositiveFace.BarDiameterMm), g.StirrupCoverMm(g.NegativeFace.BarDiameterMm)) < 0)
                e.Add("Защитный слой меньше, чем помещается хомут снаружи стержней");
        }
        if (e.Count == 0 && Layout(p).Problem is string layout) e.Add(layout);
        if (p.Loads.Count == 0) e.Add("Добавьте хотя бы одно сочетание усилий");
        var names = p.Loads.Select(l => l.Name.Trim()).ToList();
        if (names.Any(string.IsNullOrEmpty)) e.Add("У каждого сочетания должно быть название (номер)");
        else if (names.Distinct().Count() != names.Count) e.Add("Названия сочетаний повторяются");
        if (p.Loads.Any(l => new[] { l.N, l.Mx, l.My, l.Q, l.LoadFactor, l.SustainedShare }.Any(v => !double.IsFinite(v))))
            e.Add("Усилия и коэффициенты сочетаний должны быть числами");
        if (p.Loads.Any(l => l.LoadFactor <= 0)) e.Add("Коэффициент надёжности по нагрузке γf должен быть больше 0");
        var c = p.Conditions; var f = p.Concrete;
        if (c.ElementLengthM <= 0) e.Add("Задайте длину элемента");
        if (c.MuX <= 0 || c.MuY <= 0) e.Add("Коэффициенты расчётной длины должны быть больше 0");
        if (c.SlendernessLimit <= 0) e.Add("Предельная гибкость должна быть больше 0");
        if (c.EaXMm < 0 || c.EaYMm < 0) e.Add("Случайный эксцентриситет не может быть отрицательным");
        if (c.ReliabilityUls <= 0 || c.ReliabilitySls <= 0) e.Add("Коэффициенты надёжности по ответственности должны быть больше 0");
        if (new[] { f.Gb1, f.Gb2, f.Gb3, f.Gb5, f.HardeningFactor, p.LongitudinalFactor, p.TransverseFactor }.Any(v => !(v > 0)))
            e.Add("Коэффициенты условий работы должны быть больше 0");
        if (p.Loads.Any(l => l.Seismic) && !(p.Seismic.NormalSectionFactor > 0 && p.Seismic.InclinedSectionFactor > 0))
            e.Add("Коэффициенты m_tr для сейсмики должны быть больше 0");
        if (p.Loads.Any(l => l.Special && !l.Seismic) && new[] { p.Special.ConcreteFactor, p.Special.LongitudinalFactor,
                p.Special.TransverseFactor, p.Special.AdditionalFactor }.Any(v => !(v > 0)))
            e.Add("Коэффициенты для особых сочетаний должны быть больше 0");
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

    /// <summary>
    /// Request for one section and one material set; ULS loads are those after the optional
    /// eccentricity preparation, <paramref name="sustainedShare"/> is the η share of these loads.
    /// </summary>
    internal static JsonObject Build(SectionProject p, IReadOnlyList<UlsLoad> uls, IReadOnlyList<SlsAction> sls,
        MaterialSet materials, double sustainedShare)
    {
        var layout = Layout(p);
        var c = p.Conditions;
        int MeshResolution = c.CoarseMesh ? 21 : RequestBuilder.MeshResolution, PolygonSides = c.CoarseMesh ? 48 : RequestBuilder.PolygonSides;
        var req = new JsonObject { ["schema_version"] = 1 };
        if (p.Kind == SectionKind.Pile)
        {
            var g = p.Pile;
            req["pile"] = new JsonObject { ["diameter_mm"] = g.DiameterMm, ["mesh_resolution"] = MeshResolution, ["polygon_sides"] = PolygonSides };
            req["candidates"] = new JsonArray(new JsonObject
            {
                ["id"] = $"{g.BarCount}x{g.BarDiameterMm:0.##}", ["bar_count"] = g.BarCount, ["bar_diameter_mm"] = g.BarDiameterMm,
                ["rotation_deg"] = 0, ["bar_axis_radius_mm"] = g.DiameterMm / 2 - g.AxisDistanceMm,
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
        req["materials"] = ToJson(materials.Engine());
        req["loads"] = new JsonArray(uls.Select(l =>
        {
            var o = new JsonObject { ["id"] = l.Id, ["n_kn"] = l.N, ["mx_knm"] = l.Mx, ["my_knm"] = l.My };
            if (c.ShearCheck) o["q_kn"] = l.Q;
            if (l.SourceMx is double sx) { o["source_mx_knm"] = sx; o["source_my_knm"] = l.SourceMy ?? 0; }
            return (JsonNode)o;
        }).ToArray());
        if (sls.Count > 0)
        {
            var service = MaterialTables.Sls(p).Engine();
            var probe = new JsonObject
            {
                ["short_materials"] = ToJson(service), ["long_materials"] = ToJson(service),
                ["phi2"] = 0.5, ["acrc_ult_long_mm"] = c.CrackLimitLongMm, ["acrc_ult_short_mm"] = c.CrackLimitShortMm,
                ["preliminary_method"] = c.CrackMethod == CrackMethod.Refined ? "native_refined_split" : "arbat_conservative", ["review_band"] = 0.03,
            };
            probe["loads"] = new JsonArray(sls.Select(l =>
            {
                var o = new JsonObject
                {
                    ["id"] = l.Id, ["n_kn"] = l.N, ["mx_total_knm"] = l.Mx, ["my_total_knm"] = l.My,
                    ["mx_long_knm"] = l.MxLong, ["my_long_knm"] = l.MyLong,
                };
                if (Math.Abs(l.NLong - l.N) > 1e-12) o["n_long_kn"] = l.NLong;
                return (JsonNode)o;
            }).ToArray());
            req["sls_probe"] = probe;
        }
        req["sls_execution_policy"] = "uls_pass_only";
        if (c.ShearCheck)
        {
            req["shear_screening"] = new JsonObject
            {
                ["enabled"] = true,
                ["diameter_mm"] = p.Kind == SectionKind.Pile ? p.Pile.SpiralDiameterMm : p.Wall.StirrupDiameterMm,
                ["spacing_mm"] = c.ShearPitchMm,
                ["effective_legs_per_cage"] = p.Kind == SectionKind.Pile ? 2 : c.StirrupLegs,
                ["cage_count"] = 1, ["rsw_kpa"] = materials.Rsw * 1000, ["constructive_confirmed"] = c.ShearModelConfirmed,
            };
        }
        req["connection_context"] = Context(p, uls.Any(l => l.SourceMx is not null), sustainedShare);
        return req;
    }

    static JsonObject Context(SectionProject p, bool eccentricityPrepared, double sustainedShare)
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
            ["psi"] = c.SecondOrder == SecondOrderMode.Cscore ? Math.Clamp(sustainedShare, 0, 1) : 1.0,
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

/// <summary>Normative total and sustained actions of one combination for the crack checks.</summary>
internal sealed record SlsAction(string Id, double N, double NLong, double Mx, double MxLong, double My, double MyLong);
