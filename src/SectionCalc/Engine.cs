using System.Text.Json;
using System.Text.Json.Nodes;

namespace SectionCalc;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum Verdict { Passed, Failed, Review, Incomplete }

/// <summary>One native check result for one combination.</summary>
public sealed record CheckRow(string CheckId, string Title, string CaseId, string Combination, string Status,
    double? Utilization, string Reason, string Method, JsonElement? Details)
{
    public bool IsFailure => Status is "failed" or "unstable" or "preliminary_exceeds_limit";
    public bool IsReview => Status is "engineering_review" or "indeterminate";
}

public sealed record CheckResult(SectionProject Project, BarLayout Layout, IReadOnlyList<CheckRow> Rows,
    double? EaXMm, double? EaYMm, Verdict Verdict, CheckRow? Governing, IReadOnlyList<string> Notes)
{
    /// <summary>Largest utilisation per check type, for the summary list.</summary>
    public IEnumerable<CheckRow> Summary => Rows.Where(r => !Engine.InfoOnly.Contains(r.CheckId) && !r.Reason.EndsWith("not_requested"))
        .GroupBy(r => r.CheckId).Select(g => g.OrderByDescending(r => r.IsFailure).ThenByDescending(r => r.IsReview)
            .ThenByDescending(r => r.Utilization ?? -1).First());
}

/// <summary>Runs the pinned CScore through the in-process runner. All resistances come from CScore.</summary>
public static class Engine
{
    public const string EngineCommit = "4ae6aa077be5b29145930ec093ba1c9eb7776222";
    // Without a requested Q check the engine still lists these rows as not applicable; they are omitted.
    static readonly HashSet<string> ShearChecks = ["shear_strip", "shear_inclined", "inclined_moment"];
    internal static readonly HashSet<string> InfoOnly = ["anchorage", "detailing", "crack_formation", "member_stability", "sls_member_stability"];

    public static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>
    {
        ["normal_strength"] = "Прочность нормального сечения (N, M)",
        ["member_stability"] = "Устойчивость элемента, η",
        ["normal_member_strength"] = "Прочность с учётом прогиба (η)",
        ["shear_strip"] = "Q: прочность бетонной полосы (8.55)",
        ["shear_inclined"] = "Q: наклонное сечение (8.56)",
        ["inclined_moment"] = "Момент в наклонном сечении (8.63)",
        ["sls_member_stability"] = "Устойчивость при нормативных усилиях",
        ["crack_formation"] = "Образование трещин",
        ["crack_width_long"] = "Раскрытие трещин, продолжительное",
        ["crack_width_short"] = "Раскрытие трещин, непродолжительное",
        ["anchorage"] = "Анкеровка",
        ["detailing"] = "Конструктивные требования",
    };

    public static readonly IReadOnlyDictionary<string, string> StatusText = new Dictionary<string, string>
    {
        ["passed"] = "выполнено", ["failed"] = "не выполнено", ["stable"] = "устойчив", ["unstable"] = "неустойчив",
        ["calculated"] = "рассчитано", ["not_evaluated"] = "не выполнялась", ["not_applicable"] = "неприменимо",
        ["indeterminate"] = "не определено", ["accounted_in_loads"] = "учтено в усилиях",
        ["preliminary_below_limit"] = "выполнено", ["preliminary_exceeds_limit"] = "не выполнено",
        ["engineering_review"] = "на границе, проверить",
    };

    public static CheckResult Check(SectionProject project, CancellationToken cancel = default)
    {
        var problems = RequestBuilder.Validate(project);
        if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems));
        cancel.ThrowIfCancellationRequested();
        var (uls, eax, eay) = PrepareUls(project);
        var request = RequestBuilder.Build(project, uls);
        cancel.ThrowIfCancellationRequested();
        var raw = JsonNode.Parse(Program.Execute("", request.ToJsonString()))!;
        var report = raw["cases"]![0]!["connection_report"] ?? throw new InvalidOperationException("Ядро не вернуло отчёт проверок");
        var rows = report["checks"]!.AsArray().Select(c =>
        {
            string id = (string)c!["check_id"]!, caseId = (string?)c["case_id"] ?? "";
            // Eccentricity branches arrive as "Name [ea +1,-1]": the signs of ea in x and y.
            string combo = Combination(caseId);
            double? u = c["utilization"] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;
            JsonElement? details = c["details"] is JsonNode dn ? JsonSerializer.Deserialize<JsonElement>(dn.ToJsonString()) : null;
            return new CheckRow(id, Titles.GetValueOrDefault(id, id), caseId, combo, (string)c["status"]!, u,
                (string?)c["reason"] ?? "", (string?)c["engine_method"] ?? "", details);
        }).Where(r => project.Conditions.ShearCheck || !ShearChecks.Contains(r.CheckId)).ToList();
        var layout = RequestBuilder.Layout(project);
        var scored = rows.Where(r => !InfoOnly.Contains(r.CheckId)).ToList();
        var governing = scored.Where(r => r.Utilization is not null).OrderByDescending(r => r.Utilization).FirstOrDefault();
        var verdict = scored.Any(r => r.IsFailure) ? Verdict.Failed
            : scored.Any(r => r.IsReview) ? Verdict.Review
            : scored.Any(r => r.Status == "passed" || r.Status.StartsWith("preliminary")) ? Verdict.Passed : Verdict.Incomplete;
        return new CheckResult(project, layout, rows, eax, eay, verdict, governing, Notes(project, rows));
    }

    /// <summary>"Name [ea +1,-1]" (engine branch of the accidental eccentricity signs) → "Name (ea: +x, −y)".</summary>
    internal static string Combination(string caseId)
    {
        int i = caseId.IndexOf(" [ea ", StringComparison.Ordinal);
        if (i < 0 || !caseId.EndsWith(']')) return caseId;
        var signs = caseId[(i + 5)..^1].Split(',');
        string Sign(string s) => s.Trim().StartsWith('-') ? "−" : "+";
        return signs.Length == 2 ? $"{caseId[..i]} (ea: {Sign(signs[0])}x, {Sign(signs[1])}y)" : caseId;
    }

    /// <summary>ULS actions after the optional SP 63 accidental eccentricity prepared by CScore (ULS only).</summary>
    internal static (List<UlsLoad> Loads, double? EaX, double? EaY) PrepareUls(SectionProject p)
    {
        var source = p.Loads.Where(l => l.Basis == LoadBasis.Uls).Select(l => new UlsLoad(l.Name.Trim(), l.Name.Trim(), l.N, l.Mx, l.My, l.Q)).ToList();
        if (p.Conditions.Eccentricity != EccentricityMode.Sp63) return (source, null, null);
        double hx = p.Kind == SectionKind.Pile ? p.Pile.DiameterMm : p.Wall.ThicknessMm;
        double hy = p.Kind == SectionKind.Pile ? p.Pile.DiameterMm : p.Wall.WidthMm;
        var payload = new JsonObject
        {
            ["length_m"] = p.Conditions.MemberLengthM, ["height_x_m"] = hx / 1000, ["height_y_m"] = hy / 1000,
            ["structural_scheme"] = p.Conditions.StaticallyDeterminate ? "statically_determinate" : "statically_indeterminate",
            ["loads"] = new JsonArray(source.Select(l => (JsonNode)new JsonObject
            {
                ["id"] = l.Id, ["n_kn"] = l.N, ["mx_total_knm"] = l.Mx, ["my_total_knm"] = l.My,
                ["mx_long_knm"] = l.Mx, ["my_long_knm"] = l.My,
            }).ToArray()),
        };
        var result = JsonNode.Parse(Program.Execute("--prepare-member-loads", payload.ToJsonString()))!;
        var byId = source.ToDictionary(l => l.Id);
        var prepared = result["loads"]!.AsArray().Select(r =>
        {
            var src = byId[(string)r!["source_id"]!];
            return new UlsLoad((string)r["id"]!, src.SourceName, (double)r["n_kn"]!, (double)r["mx_total_knm"]!,
                (double)r["my_total_knm"]!, src.Q, src.Mx, src.My);
        }).ToList();
        return (prepared, (double)result["ea_x_mm"]!, (double)result["ea_y_mm"]!);
    }

    static List<string> Notes(SectionProject p, List<CheckRow> rows)
    {
        var notes = new List<string>();
        var c = p.Conditions;
        if (c.Eccentricity == EccentricityMode.InLoads) notes.Add("Случайный эксцентриситет принят включённым в заданные моменты.");
        if (c.SecondOrder == SecondOrderMode.InLoads) notes.Add("Влияние прогиба принято учтённым в заданных усилиях.");
        if (c.SecondOrder == SecondOrderMode.SectionOnly) notes.Add("Проверено только сечение: влияние прогиба (η) не учитывалось.");
        if (!p.Loads.Any(l => l.Basis == LoadBasis.Sls)) notes.Add("Нормативные сочетания не заданы: трещины не проверялись.");
        if (!c.ShearCheck) notes.Add("Поперечная сила не проверялась.");
        if (rows.Any(r => r.Status == "not_evaluated" && r.Reason.Contains("uls")))
            notes.Add("Трещины не проверялись: сечение не прошло проверку прочности.");
        return notes;
    }
}
