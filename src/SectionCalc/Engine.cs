using System.Text.Json;
using System.Text.Json.Nodes;

namespace SectionCalc;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum Verdict { Passed, Failed, Review, Incomplete }

/// <summary>One check result for one combination.</summary>
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
    internal static readonly HashSet<string> InfoOnly = ["anchorage", "detailing", "crack_formation", "member_stability", "sls_member_stability"];
    static readonly HashSet<string> ShearChecks = ["shear_strip", "shear_inclined", "inclined_moment"];
    static readonly HashSet<string> SlsChecks = ["crack_formation", "crack_width_long", "crack_width_short", "sls_member_stability"];
    static readonly HashSet<string> GlobalChecks = ["anchorage", "detailing"];

    public static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>
    {
        ["normal_strength"] = "Прочность нормального сечения (N, M)",
        ["member_stability"] = "Устойчивость, N / Ncr",
        ["normal_member_strength"] = "Прочность с учётом прогиба (η)",
        ["shear_strip"] = "Q: прочность бетонной полосы (8.55)",
        ["shear_inclined"] = "Q: наклонное сечение (8.56)",
        ["inclined_moment"] = "Момент в наклонном сечении (8.63)",
        ["sls_member_stability"] = "Устойчивость при нормативных усилиях",
        ["crack_formation"] = "Образование трещин",
        ["no_cracks"] = "Трещины не допускаются: M / Mcrc",
        ["crack_width_long"] = "Раскрытие трещин, продолжительное",
        ["crack_width_short"] = "Раскрытие трещин, непродолжительное",
        ["slenderness_x"] = "Предельная гибкость в плоскости Mx",
        ["slenderness_y"] = "Предельная гибкость в плоскости My",
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
        var c = project.Conditions;
        var rows = new List<CheckRow>();
        double? eax = null, eay = null;
        var normalByCase = new Dictionary<string, double>();
        // SLS: normative = design / γf × γn,ser; sustained = k × normative. Evaluated in the first engine
        // request (its materials are the SLS ones) and kept only if every ULS check of every group passed.
        var sls = ServiceActions(project);
        var slsRows = new List<CheckRow>();
        bool first = true;

        // ULS: one engine request per kind of combination (materials) and per sustained share (η).
        foreach (var group in project.Loads.GroupBy(r => (Kind: CombinationKind.Of(r),
                     Share: c.SecondOrder == SecondOrderMode.Cscore ? Math.Clamp(r.SustainedShare, 0, 1) : 1.0)))
        {
            cancel.ThrowIfCancellationRequested();
            var kind = group.Key.Kind;
            // γn does not apply to special combinations (SP 296, as in ARBAT).
            double gn = kind.Special ? 1 : c.ReliabilityUls;
            var source = group.Select(r => new UlsLoad(r.Name.Trim(), r.Name.Trim(), r.N * gn, r.Mx * gn, r.My * gn, r.Q * gn)).ToList();
            var (uls, ex, ey) = PrepareUls(project, source);
            eax ??= ex; eay ??= ey;
            foreach (var l in uls) normalByCase[l.Id] = l.N;
            var materials = MaterialTables.Uls(project, kind);
            var all = Run(project, uls, first ? sls : [], materials, group.Key.Share);
            if (first) slsRows = all.Where(r => SlsChecks.Contains(r.CheckId)).ToList();
            first = false;
            var groupRows = all.Where(r => !SlsChecks.Contains(r.CheckId) && !GlobalChecks.Contains(r.CheckId)).ToList();
            if (kind.Seismic && c.ShearCheck)
            {
                // SP 14 table 5.4: inclined sections take their own m_tr.
                groupRows.RemoveAll(r => ShearChecks.Contains(r.CheckId));
                groupRows.AddRange(Run(project, uls, [], MaterialTables.Uls(project, kind, inclined: true), group.Key.Share)
                    .Where(r => ShearChecks.Contains(r.CheckId)));
            }
            rows.AddRange(groupRows.Select(r => r with { Combination = Combination(r.CaseId, group.First(), kind) }));
        }

        if (sls.Count > 0)
        {
            bool ulsOk = !rows.Any(r => r.IsFailure || r.Status == "indeterminate");
            if (ulsOk)
                rows.AddRange(slsRows.Select(r => r with { Combination = r.CaseId + " · нормативное" }));
            else
                foreach (var a in sls)
                    foreach (var id in new[] { "crack_width_long", "crack_width_short" })
                        rows.Add(new CheckRow(id, Titles[id], a.Id, a.Id + " · нормативное", "not_evaluated", null,
                            "skipped_uls_fail", "", null));
        }
        rows.Add(new CheckRow("anchorage", Titles["anchorage"], "", "", "not_evaluated", null, "anchorage_stage_deferred", "", null));
        rows.Add(new CheckRow("detailing", Titles["detailing"], "", "", "not_evaluated", null, "reinforcement_detailing_stage_deferred", "", null));

        rows = rows.Select(r => r.CheckId == "member_stability" ? WithNcr(r, normalByCase) : r).ToList();
        if (c.CrackCategory == CrackCategory.NoCracks) rows = NoCracks(rows, sls);
        rows.InsertRange(0, Slenderness(project));

        var layout = RequestBuilder.Layout(project);
        var scored = rows.Where(r => !InfoOnly.Contains(r.CheckId)).ToList();
        var governing = scored.Where(r => r.Utilization is not null).OrderByDescending(r => r.Utilization).FirstOrDefault();
        var verdict = scored.Any(r => r.IsFailure) ? Verdict.Failed
            : scored.Any(r => r.IsReview) ? Verdict.Review
            : scored.Any(r => r.Status == "passed" || r.Status.StartsWith("preliminary")) ? Verdict.Passed : Verdict.Incomplete;
        return new CheckResult(project, layout, rows, eax, eay, verdict, governing, Notes(project, rows));
    }

    static List<CheckRow> Run(SectionProject p, List<UlsLoad> uls, List<SlsAction> sls, MaterialSet materials, double share)
    {
        var request = RequestBuilder.Build(p, uls, sls, materials, share);
        var raw = JsonNode.Parse(Program.Execute("", request.ToJsonString()))!;
        var report = raw["cases"]![0]!["connection_report"] ?? throw new InvalidOperationException("Ядро не вернуло отчёт проверок");
        return report["checks"]!.AsArray().Select(node =>
        {
            string id = (string)node!["check_id"]!, caseId = (string?)node["case_id"] ?? "";
            double? u = node["utilization"] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;
            JsonElement? details = node["details"] is JsonNode dn ? JsonSerializer.Deserialize<JsonElement>(dn.ToJsonString()) : null;
            return new CheckRow(id, Titles.GetValueOrDefault(id, id), caseId, caseId, (string)node["status"]!, u,
                (string?)node["reason"] ?? "", (string?)node["engine_method"] ?? "", details);
        }).Where(r => p.Conditions.ShearCheck || !ShearChecks.Contains(r.CheckId)).ToList();
    }

    /// <summary>Normative actions of the combinations that take part in the crack checks.</summary>
    internal static List<SlsAction> ServiceActions(SectionProject p)
    {
        double gn = p.Conditions.ReliabilitySls;
        return p.Loads.Where(r => !(r.Special && !r.Seismic) && (!r.Seismic || p.Seismic.CheckCracks)).Select(r =>
        {
            double f = gn / r.LoadFactor, k = r.SustainedShare;
            return new SlsAction(r.Name.Trim(), r.N * f, k * r.N * f, r.Mx * f, k * r.Mx * f, r.My * f, k * r.My * f);
        }).ToList();
    }

    static string Combination(string caseId, LoadRow row, CombinationKind kind)
    {
        string label = Branch(caseId);
        var tags = new List<string>();
        if (kind.Seismic) tags.Add("сейсмика");
        if (kind.Special) tags.Add("особое");
        if (kind.ShortTerm && !kind.Special) tags.Add("кратковр.");
        return tags.Count > 0 ? $"{label} · {string.Join(", ", tags)}" : label;
    }

    /// <summary>"Name [ea +1,-1]" (engine branch of the accidental eccentricity signs) → "Name (ea: +x, −y)".</summary>
    internal static string Branch(string caseId)
    {
        int i = caseId.IndexOf(" [ea ", StringComparison.Ordinal);
        if (i < 0 || !caseId.EndsWith(']')) return caseId;
        var signs = caseId[(i + 5)..^1].Split(',');
        string Sign(string s) => s.Trim().StartsWith('-') ? "−" : "+";
        return signs.Length == 2 ? $"{caseId[..i]} (ea: {Sign(signs[0])}x, {Sign(signs[1])}y)" : caseId;
    }

    /// <summary>N / Ncr of CScore's eta diagnostics, shown with the stability row.</summary>
    static CheckRow WithNcr(CheckRow r, Dictionary<string, double> n)
    {
        if (r.Details is not { } d || !n.TryGetValue(r.CaseId, out var force) || force >= 0) return r;
        double? Ncr(string key) => d.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
        var ncr = new[] { Ncr("ncr_x_kn"), Ncr("ncr_y_kn") }.Where(v => v is > 0).Select(v => v!.Value).ToList();
        return ncr.Count == 0 ? r : r with { Utilization = Math.Abs(force) / ncr.Min() };
    }

    /// <summary>"Cracks not allowed": K = M / Mcrc of CScore's crack formation check; widths are not needed.</summary>
    static List<CheckRow> NoCracks(List<CheckRow> rows, List<SlsAction> sls)
    {
        var result = rows.Where(r => !r.CheckId.StartsWith("crack_width")).ToList();
        foreach (var f in rows.Where(r => r.CheckId == "crack_formation").ToList())
        {
            var action = sls.FirstOrDefault(a => a.Id == f.CaseId);
            double? mcrc = f.Details is { } d && d.TryGetProperty("mcrc_knm", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetDouble() : null;
            bool cracked = f.Details is { } dd && dd.TryGetProperty("cracked", out var cr) && cr.ValueKind == JsonValueKind.True;
            double? k = action is not null && mcrc is > 0 ? Math.Sqrt(action.Mx * action.Mx + action.My * action.My) / mcrc : null;
            result.Add(f with { CheckId = "no_cracks", Title = Titles["no_cracks"], Utilization = k,
                Status = f.Status != "calculated" ? f.Status : cracked ? "failed" : "passed" });
        }
        foreach (var skipped in rows.Where(r => r.CheckId == "crack_width_long" && r.Status == "not_evaluated"))
            result.Add(skipped with { CheckId = "no_cracks", Title = Titles["no_cracks"] });
        return result;
    }

    /// <summary>SP 63 10.2.2: λ = l0 / i of the gross concrete section against the limit (only with compression).</summary>
    static List<CheckRow> Slenderness(SectionProject p)
    {
        if (!p.Loads.Any(l => l.N < 0)) return [];
        var c = p.Conditions;
        double ix = p.Kind == SectionKind.Pile ? p.Pile.DiameterMm / 4 : p.Wall.ThicknessMm / Math.Sqrt(12);
        double iy = p.Kind == SectionKind.Pile ? p.Pile.DiameterMm / 4 : p.Wall.WidthMm / Math.Sqrt(12);
        CheckRow Row(string id, double l0, double i)
        {
            double k = l0 * 1000 / i / c.SlendernessLimit;
            return new CheckRow(id, Titles[id], "", "", k <= 1 ? "passed" : "failed", k, "", "geometry_l0_over_i", null);
        }
        return [Row("slenderness_x", c.L0xM, ix), Row("slenderness_y", c.L0yM, iy)];
    }

    /// <summary>ULS actions after the optional SP 63 accidental eccentricity prepared by CScore.</summary>
    internal static (List<UlsLoad> Loads, double? EaX, double? EaY) PrepareUls(SectionProject p, List<UlsLoad> source)
    {
        var c = p.Conditions;
        if (c.Eccentricity != EccentricityMode.Sp63) return (source, null, null);
        double hx = p.Kind == SectionKind.Pile ? p.Pile.DiameterMm : p.Wall.ThicknessMm;
        double hy = p.Kind == SectionKind.Pile ? p.Pile.DiameterMm : p.Wall.WidthMm;
        var payload = new JsonObject
        {
            ["length_m"] = c.ElementLengthM, ["height_x_m"] = hx / 1000, ["height_y_m"] = hy / 1000,
            ["structural_scheme"] = c.StaticallyDeterminate ? "statically_determinate" : "statically_indeterminate",
            ["ea_x_mm"] = c.EaXMm > 0 ? c.EaXMm : null, ["ea_y_mm"] = c.EaYMm > 0 ? c.EaYMm : null,
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
        if (c.SecondOrder == SecondOrderMode.Cscore && p.Loads.Any(l => l.SustainedShare is < 0 or > 1))
            notes.Add("Для η доля длительной нагрузки вне 0…1 ограничена этим диапазоном.");
        if (!c.ShearCheck) notes.Add("Поперечная сила не проверялась.");
        if (rows.Any(r => r.Reason == "skipped_uls_fail")) notes.Add("Трещины не проверялись: сечение не прошло проверку прочности.");
        if (p.Loads.Any(l => l.Seismic))
        {
            notes.Add($"Сейсмические сочетания: к расчётным сопротивлениям введён m_tr = {p.Seismic.NormalSectionFactor:0.##} для нормальных сечений и {p.Seismic.InclinedSectionFactor:0.##} для наклонных (СП 14.13330 табл. 5.4).");
            notes.Add("Снижение граничной высоты сжатой зоны ξR (СП 14.13330 п. 6.7.2) в нелинейной деформационной модели не применяется: прочность определяется предельными деформациями.");
            if (!p.Seismic.CheckCracks) notes.Add("Трещиностойкость при сейсмических сочетаниях не проверялась (СП 14.13330 п. 5.18).");
        }
        if (p.Loads.Any(l => l.Special && !l.Seismic))
            notes.Add($"Особые сочетания (СП 296.1325800): {(p.Special.NormativeProperties ? "нормативные" : "расчётные")} характеристики материалов, коэффициенты задаются пользователем, γn = 1, трещины не проверяются.");
        return notes;
    }
}
