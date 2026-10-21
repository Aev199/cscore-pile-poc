using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace SectionCalc.Profile;

/// <summary>
/// Reinforcement profile for the Revit reinforcement module, format "section-app/reinforcement-profile" version 1
/// (docs/reinforcement-profile-json.md). Levels are PLAXIS elevations in metres, areas in cm², Asw/sw in cm²/m.
/// </summary>
public static class RevitExport
{
    public const string Format = "section-app/reinforcement-profile";
    public const int Version = 1;
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    public static string Json(ReinforcementProfile r, DateTime? created = null) => Build(r, created).ToJsonString(Options);

    public static void Write(string path, ReinforcementProfile r) => File.WriteAllText(path, Json(r));

    static double R(double v, int digits = 3) => Math.Round(v, digits);

    /// <summary>bar — to the longitudinal bar surface, transverse — to the spiral/stirrup, axis — to the bar axis.</summary>
    static string Reference(CoverReference r) => r switch { CoverReference.Bar => "bar", CoverReference.Axis => "axis", _ => "transverse" };

    static string Side(int excavation, int face) => excavation == 0 ? "" : excavation == face ? " (котлован)" : " (грунт)";

    public static JsonObject Build(ReinforcementProfile r, DateTime? created = null)
    {
        var p = r.Project; var s = p.Section; var set = p.Settings; var f = r.Forces;
        bool wall = s.Kind == SectionKind.Wall;
        var element = new JsonObject
        {
            ["type"] = wall ? "wall" : "pile",
            ["top_level_m"] = R(f.TopLevel), ["bottom_level_m"] = R(f.BottomLevel), ["length_m"] = R(f.Length), ["x_m"] = R(f.X),
        };
        if (wall)
            element["wall"] = new JsonObject
            {
                ["width_mm"] = s.Wall.WidthMm, ["thickness_mm"] = s.Wall.ThicknessMm, ["cover_mm"] = s.Wall.CoverMm,
                ["cover_reference"] = Reference(s.Wall.CoverReference),
                ["stirrup_diameter_mm"] = s.Wall.StirrupDiameterMm,
                ["right_face_rows"] = s.Wall.PositiveFace.Rows, ["left_face_rows"] = s.Wall.NegativeFace.Rows,
                ["right_face_axis_mm"] = R(s.Wall.AxisDistanceMm(s.Wall.PositiveFace), 1), ["left_face_axis_mm"] = R(s.Wall.AxisDistanceMm(s.Wall.NegativeFace), 1),
                ["right_row_pitch_mm"] = R(s.Wall.PositiveFace.RowPitchMm, 1), ["left_row_pitch_mm"] = R(s.Wall.NegativeFace.RowPitchMm, 1),
            };
        else
            element["pile"] = new JsonObject
            {
                ["diameter_mm"] = s.Pile.DiameterMm, ["cover_mm"] = s.Pile.CoverMm, ["cover_reference"] = Reference(s.Pile.CoverReference),
                ["spiral_diameter_mm"] = s.Pile.SpiralDiameterMm,
                ["bar_count"] = s.Pile.BarCount, ["spacing_m"] = set.Scenario == ProfileScenario.RetainingPiles ? set.PileSpacingM : null,
            };
        if (f.Is3D)
            // One reinforcement for every pile of the group; levels are those of the highest head, each pile
            // is placed from its own head (shift_m) when the heads are aligned.
            element["group"] = new JsonObject
            {
                ["uniform"] = true,
                ["level_reference"] = set.AlignPileHeads ? "head" : "elevation",
                ["piles"] = new JsonArray(f.Piles.Select(x => (JsonNode)new JsonObject
                {
                    ["name"] = x.Name, ["x_m"] = R(x.X), ["y_m"] = R(x.Y),
                    ["head_level_m"] = R(x.Head), ["tip_level_m"] = R(x.Tip), ["shift_m"] = R(x.Shift),
                }).ToArray()),
            };
        var src = p.Source!;
        var phases = new JsonArray(p.Phases.Where(ph => ph.Include).Select(ph => (JsonNode)new JsonObject
        {
            ["name"] = ph.Name, ["title"] = src.Phases.FirstOrDefault(x => x.Id == ph.Id)?.Title,
            ["covers"] = new JsonArray((src.Phases.FirstOrDefault(x => x.Id == ph.Id)?.Covers ?? []).Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
            ["kind"] = ph.Seismic ? "seismic" : ph.Special ? "special" : "main", ["short_term"] = ph.ShortTerm,
        }).ToArray());
        var c = s.Conditions;
        var stations = new JsonArray(r.Stations.Select(st =>
        {
            var o = new JsonObject
            {
                ["level_m"] = R(st.Station.Level), ["depth_m"] = R(f.TopLevel - st.Station.Level),
                ["from_level_m"] = R(st.Station.Top), ["to_level_m"] = R(st.Station.Bottom),
            };
            if (wall) { o["as_right_cm2"] = R(st.AsRightMm2 / 100, 2); o["as_left_cm2"] = R(st.AsLeftMm2 / 100, 2); }
            else o["as_cm2"] = R(st.AsRightMm2 / 100, 2);
            o["asw_cm2_per_m"] = st.AswCm2PerM is double a ? R(a, 2) : null;
            o["status"] = st.Status;
            o["governing"] = st.Governing is null ? null : new JsonObject
            {
                ["check"] = st.Governing, ["combination"] = st.Combination, ["k"] = st.K is double k ? R(k) : null,
            };
            o["note"] = st.Note;
            return (JsonNode)o;
        }).ToArray());
        double h0 = s.Wall.ThicknessMm - s.Wall.CentroidMm(s.Wall.PositiveFace);
        double minArea = wall ? set.MinPercent / 100 * s.Wall.WidthMm * h0 : set.MinPercent / 100 * Math.PI * s.Pile.DiameterMm * s.Pile.DiameterMm / 4;
        return new JsonObject
        {
            ["format"] = Format, ["version"] = Version,
            ["created"] = (created ?? DateTime.Now).ToString("s", CultureInfo.InvariantCulture),
            ["title"] = p.Title,
            // required — required areas; check — the given section checked (as_* — given areas, governing.k — its K).
            ["mode"] = r.IsCheck ? "check" : "required",
            ["scenario"] = set.Scenario switch
            {
                ProfileScenario.Wall => "wall_2d", ProfileScenario.RetainingPiles => "retaining_piles_2d", _ => "piles_3d",
            },
            ["element"] = element,
            ["materials"] = new JsonObject { ["concrete"] = s.ConcreteClass, ["longitudinal"] = s.RebarClass, ["transverse"] = s.TransverseClass },
            ["faces"] = wall ? new JsonObject
            {
                ["right"] = "грань со стороны большего X модели PLAXIS" + Side(set.ExcavationSide, 1),
                ["left"] = "грань со стороны меньшего X модели PLAXIS" + Side(set.ExcavationSide, -1),
                ["excavation"] = set.ExcavationSide switch { 1 => "right", -1 => "left", _ => null },
            } : null,
            ["source"] = new JsonObject
            {
                ["program"] = f.Is3D ? "PLAXIS 3D" : "PLAXIS 2D", ["version"] = src.Info.Version, ["project"] = src.Info.Title,
                ["forces"] = src.IsEnvelope ? "envelope" : "phases",
                ["objects"] = new JsonArray(src.Objects.Select(o => (JsonNode)JsonValue.Create(o.Name)!).ToArray()),
                ["phases"] = phases,
            },
            ["design"] = new JsonObject
            {
                ["code"] = "СП 63.13330.2018",
                ["forces"] = "design",
                ["force_width_m"] = f.Is3D ? null : wall ? s.Wall.WidthMm / 1000 : set.PileSpacingM,
                ["moment_sign"] = set.MomentSign,
                ["station_step_m"] = set.StationStepM,
                ["min_percent"] = set.MinPercent,
                ["as_min_cm2"] = R(minArea / 100, 2),
                ["service_divisor"] = set.ServiceDivisor,
                ["sustained_share"] = set.SustainedShare,
                ["crack_limits_mm"] = c.CrackCategory == CrackCategory.NoCracks ? null
                    : new JsonObject { ["long"] = c.CrackLimitLongMm, ["short"] = c.CrackLimitShortMm },
                ["crack_method"] = c.CrackMethod == CrackMethod.Refined ? "refined" : "conservative",
                ["ignore_axial_force"] = set.IgnoreAxialForce,
                ["peak_cuts"] = new JsonArray(set.PeakCuts.Select(k => (JsonNode)new JsonObject
                {
                    ["top_level_m"] = k.TopLevel, ["bottom_level_m"] = k.BottomLevel, ["moment"] = k.Moment, ["shear"] = k.Shear,
                    ["method"] = k.Method == PeakCutMethod.Limit ? "limit" : "chord",
                    ["moment_limit_knm"] = k.Method == PeakCutMethod.Limit && k.Moment ? k.MomentLimit : null,
                    ["shear_limit_kn"] = k.Method == PeakCutMethod.Limit && k.Shear ? k.ShearLimit : null,
                }).ToArray()),
                ["shear_check"] = c.ShearCheck,
                ["shear_pitch_mm"] = c.ShearCheck ? c.ShearPitchMm : null,
            },
            ["units"] = new JsonObject { ["level"] = "m", ["as"] = "cm2", ["asw"] = "cm2/m", ["force"] = "kN", ["moment"] = "kN*m" },
            ["stations"] = stations,
            ["envelope"] = new JsonArray(f.Envelope.Select(e => (JsonNode)new JsonObject
            {
                ["level_m"] = R(e.Level), ["n_min_kn"] = R(e.NMin, 1), ["n_max_kn"] = R(e.NMax, 1),
                ["m_min_knm"] = R(e.MMin, 1), ["m_max_knm"] = R(e.MMax, 1), ["q_abs_max_kn"] = R(e.QAbsMax, 1),
            }).ToArray()),
        };
    }
}
