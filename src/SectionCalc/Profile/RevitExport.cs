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
                ["stirrup_diameter_mm"] = s.Wall.StirrupDiameterMm,
                ["right_face_bars"] = s.Wall.PositiveFace.BarCount, ["left_face_bars"] = s.Wall.NegativeFace.BarCount,
            };
        else
            element["pile"] = new JsonObject
            {
                ["diameter_mm"] = s.Pile.DiameterMm, ["cover_mm"] = s.Pile.CoverMm, ["spiral_diameter_mm"] = s.Pile.SpiralDiameterMm,
                ["bar_count"] = s.Pile.BarCount, ["spacing_m"] = set.PileSpacingM,
            };
        var src = p.Source!;
        var phases = new JsonArray(p.Phases.Where(ph => ph.Include).Select(ph => (JsonNode)new JsonObject
        {
            ["name"] = ph.Name, ["title"] = src.Phases.FirstOrDefault(x => x.Id == ph.Id)?.Title,
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
        double h0 = s.Wall.ThicknessMm - s.Wall.CoverMm - s.Wall.StirrupDiameterMm - 10;
        double minArea = wall ? set.MinPercent / 100 * s.Wall.WidthMm * h0 : set.MinPercent / 100 * Math.PI * s.Pile.DiameterMm * s.Pile.DiameterMm / 4;
        return new JsonObject
        {
            ["format"] = Format, ["version"] = Version,
            ["created"] = (created ?? DateTime.Now).ToString("s", CultureInfo.InvariantCulture),
            ["title"] = p.Title,
            ["element"] = element,
            ["materials"] = new JsonObject { ["concrete"] = s.ConcreteClass, ["longitudinal"] = s.RebarClass, ["transverse"] = s.TransverseClass },
            ["faces"] = wall ? new JsonObject
            {
                ["right"] = "грань со стороны большего X модели PLAXIS",
                ["left"] = "грань со стороны меньшего X модели PLAXIS",
            } : null,
            ["source"] = new JsonObject
            {
                ["program"] = "PLAXIS 2D", ["version"] = src.Info.Version, ["project"] = src.Info.Title,
                ["objects"] = new JsonArray(src.Objects.Select(o => (JsonNode)JsonValue.Create(o.Name)!).ToArray()),
                ["phases"] = phases,
            },
            ["design"] = new JsonObject
            {
                ["code"] = "СП 63.13330.2018",
                ["forces"] = "design",
                ["force_width_m"] = wall ? s.Wall.WidthMm / 1000 : set.PileSpacingM,
                ["moment_sign"] = set.MomentSign,
                ["station_step_m"] = set.StationStepM,
                ["min_percent"] = set.MinPercent,
                ["as_min_cm2"] = R(minArea / 100, 2),
                ["service_divisor"] = set.ServiceDivisor,
                ["sustained_share"] = set.SustainedShare,
                ["crack_limits_mm"] = c.CrackCategory == CrackCategory.NoCracks ? null
                    : new JsonObject { ["long"] = c.CrackLimitLongMm, ["short"] = c.CrackLimitShortMm },
                ["crack_method"] = c.CrackMethod == CrackMethod.Refined ? "refined" : "conservative",
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
