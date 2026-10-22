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

/// <summary>One engine load case (a source combination with one sign of the accidental eccentricity).</summary>
public sealed record CaseInfo(string Id, string Source, CombinationKind Kind, double N, double Mx, double My, double Q,
    double SourceMx, double SourceMy, MaterialSet Materials, MaterialSet ShearMaterials, double? UltimateFactor);

/// <summary>Normative actions of one combination and the engine's crack calculation for them.</summary>
public sealed record ServiceInfo(string Id, double N, double NLong, double Mx, double MxLong, double My, double MyLong, JsonElement? Crack);

public sealed record CheckResult(SectionProject Project, BarLayout Layout, IReadOnlyList<CheckRow> Rows,
    double? EaXMm, double? EaYMm, Verdict Verdict, CheckRow? Governing, IReadOnlyList<string> Notes,
    IReadOnlyDictionary<string, CaseInfo> Cases, IReadOnlyList<ServiceInfo> Service)
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
        ["normal_strength"] = "Прочность нормального сечения",
        ["member_stability"] = "Продольный изгиб, N/Ncr",
        ["normal_member_strength"] = "Прочность нормального сечения с учётом прогиба",
        ["shear_strip"] = "Прочность по наклонной полосе",
        ["shear_inclined"] = "Прочность наклонного сечения на Q",
        ["inclined_moment"] = "Прочность наклонного сечения на M",
        ["sls_member_stability"] = "Продольный изгиб при нормативных усилиях",
        ["crack_formation"] = "Образование трещин",
        ["no_cracks"] = "Образование трещин, M/Mcrc",
        ["crack_width_long"] = "Ширина раскрытия трещин, продолжительное",
        ["crack_width_short"] = "Ширина раскрытия трещин, непродолжительное",
        ["slenderness_x"] = "Гибкость в плоскости Mx",
        ["slenderness_y"] = "Гибкость в плоскости My",
        ["anchorage"] = "Анкеровка",
        ["detailing"] = "Конструктивные требования",
    };

    public static readonly IReadOnlyDictionary<string, string> StatusText = new Dictionary<string, string>
    {
        ["passed"] = "выполнено", ["failed"] = "не выполнено", ["stable"] = "устойчив", ["unstable"] = "неустойчив",
        ["calculated"] = "рассчитано", ["not_evaluated"] = "не выполнялась", ["not_applicable"] = "неприменимо",
        ["indeterminate"] = "не определено", ["accounted_in_loads"] = "учтено в усилиях",
        ["preliminary_below_limit"] = "выполнено", ["preliminary_exceeds_limit"] = "не выполнено",
        ["engineering_review"] = "на пределе",
    };

    public static CheckResult Check(SectionProject project, CancellationToken cancel = default)
    {
        var problems = RequestBuilder.Validate(project);
        if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems));
        var c = project.Conditions;
        var rows = new List<CheckRow>();
        double? eax = null, eay = null;
        var normalByCase = new Dictionary<string, double>();
        var cases = new Dictionary<string, CaseInfo>();
        var service = new List<ServiceInfo>();
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
            var (all, raw) = Run(project, uls, first ? sls : [], materials, group.Key.Share);
            if (first)
            {
                slsRows = all.Where(r => SlsChecks.Contains(r.CheckId)).ToList();
                var probes = raw["sls_probe"] is JsonArray pa
                    ? pa.Where(x => x is not null).ToDictionary(x => (string)x!["id"]!, x => JsonSerializer.Deserialize<JsonElement>(x!.ToJsonString()))
                    : [];
                service.AddRange(sls.Select(a => new ServiceInfo(a.Id, a.N, a.NLong, a.Mx, a.MxLong, a.My, a.MyLong,
                    probes.TryGetValue(a.Id, out var e) ? e : null)));
            }
            first = false;
            var groupRows = all.Where(r => !SlsChecks.Contains(r.CheckId) && !GlobalChecks.Contains(r.CheckId)).ToList();
            var shearMaterials = materials;
            if (kind.Seismic && c.ShearCheck)
            {
                // SP 14 table 5.4: inclined sections take their own m_tr.
                shearMaterials = MaterialTables.Uls(project, kind, inclined: true);
                groupRows.RemoveAll(r => ShearChecks.Contains(r.CheckId));
                groupRows.AddRange(Run(project, uls, [], shearMaterials, group.Key.Share).Rows
                    .Where(r => ShearChecks.Contains(r.CheckId)));
            }
            var factors = raw["loads"] is JsonArray la
                ? la.Where(x => x is not null).ToDictionary(x => (string)x!["id"]!, x => x!["factor"] is JsonValue fv && fv.TryGetValue<double>(out var f) ? f : (double?)null)
                : [];
            foreach (var l in uls)
                cases[l.Id] = new CaseInfo(l.Id, l.SourceName, kind, l.N, l.Mx, l.My, l.Q, l.SourceMx ?? l.Mx, l.SourceMy ?? l.My,
                    materials, shearMaterials, factors.GetValueOrDefault(l.Id));
            rows.AddRange(groupRows.Select(r => r with
            {
                Combination = Combination(cases.TryGetValue(r.CaseId, out var ci) ? ci.Source : r.CaseId, kind),
            }));
        }

        if (sls.Count > 0)
        {
            bool ulsOk = !rows.Any(r => r.IsFailure || r.Status == "indeterminate");
            if (ulsOk)
                rows.AddRange(slsRows);
            else
                foreach (var a in sls)
                    foreach (var id in new[] { "crack_width_long", "crack_width_short" })
                        rows.Add(new CheckRow(id, Titles[id], a.Id, a.Id, "not_evaluated", null,
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
        return new CheckResult(project, layout, rows, eax, eay, verdict, governing, Notes(project, rows), cases, service);
    }

    static (List<CheckRow> Rows, JsonNode Raw) Run(SectionProject p, List<UlsLoad> uls, List<SlsAction> sls, MaterialSet materials, double share)
    {
        var request = RequestBuilder.Build(p, uls, sls, materials, share);
        var raw = JsonNode.Parse(Program.Execute("", request.ToJsonString()))!["cases"]![0]!;
        var report = raw["connection_report"] ?? throw new InvalidOperationException("Ядро не вернуло отчёт проверок");
        var rows = report["checks"]!.AsArray().Select(node =>
        {
            string id = (string)node!["check_id"]!, caseId = (string?)node["case_id"] ?? "";
            double? u = node["utilization"] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;
            JsonElement? details = node["details"] is JsonNode dn ? JsonSerializer.Deserialize<JsonElement>(dn.ToJsonString()) : null;
            return new CheckRow(id, Titles.GetValueOrDefault(id, id), caseId, caseId, (string)node["status"]!, u,
                (string?)node["reason"] ?? "", (string?)node["engine_method"] ?? "", details);
        }).Where(r => p.Conditions.ShearCheck || !ShearChecks.Contains(r.CheckId)).ToList();
        return (rows, raw);
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

    /// <summary>Combination name with its kind: "3 · сейсм.". The sign of ea is not shown: the worst one governs.</summary>
    internal static string Combination(string source, CombinationKind kind) =>
        KindTag(kind) is { Length: > 0 } tag ? $"{source} · {tag}" : source;

    public static string KindTag(CombinationKind kind) =>
        kind.Seismic ? "сейсмическое" : kind.Special ? "особое" : kind.ShortTerm ? "кратковременное" : "";

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
        return c.OnePlane ? [Row("slenderness_x", c.L0xM, ix)] : [Row("slenderness_x", c.L0xM, ix), Row("slenderness_y", c.L0yM, iy)];
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
            // One plane: the out-of-plane eccentricity is dropped, its sign branches become duplicates.
            return new UlsLoad((string)r["id"]!, src.SourceName, (double)r["n_kn"]!, (double)r["mx_total_knm"]!,
                c.OnePlane ? src.My : (double)r["my_total_knm"]!, src.Q, src.Mx, src.My);
        }).ToList();
        if (c.OnePlane)
            prepared = prepared.GroupBy(l => (l.SourceName, l.N, l.Mx, l.My)).Select(g => g.First()).ToList();
        return (prepared, (double)result["ea_x_mm"]!, (double)result["ea_y_mm"]!);
    }

    static List<string> Notes(SectionProject p, List<CheckRow> rows)
    {
        var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        string F(double v) => v.ToString("0.##", ru);
        var notes = new List<string>();
        var c = p.Conditions;
        if (c.Eccentricity == EccentricityMode.InLoads) notes.Add("Случайный эксцентриситет учтён в заданных моментах.");
        if (c.SecondOrder == SecondOrderMode.InLoads) notes.Add("Влияние прогиба учтено в заданных усилиях.");
        if (c.SecondOrder == SecondOrderMode.SectionOnly) notes.Add("Влияние прогиба не учитывалось.");
        if (c.SecondOrder == SecondOrderMode.Cscore && p.Loads.Any(l => l.SustainedShare is < 0 or > 1))
            notes.Add("При определении η значения kдл вне диапазона 0…1 приняты равными ближайшей границе.");
        if (!c.ShearCheck) notes.Add("Расчёт на действие поперечной силы не выполнялся.");
        if (rows.Any(r => r.Reason == "skipped_uls_fail")) notes.Add("Трещиностойкость не проверялась: не обеспечена прочность.");
        if (p.Loads.Any(l => l.Seismic))
        {
            notes.Add($"Сейсмические сочетания — по СП 14.13330: m_tr = {F(p.Seismic.NormalSectionFactor)} для нормальных и {F(p.Seismic.InclinedSectionFactor)} для наклонных сечений (табл. 5.4). Ограничение ξR (п. 6.7.2) при расчёте по деформационной модели не применяется.");
            if (!p.Seismic.CheckCracks) notes.Add("Трещиностойкость при сейсмических сочетаниях не проверялась (п. 5.18 СП 14.13330).");
        }
        if (p.Loads.Any(l => l.Special && !l.Seismic))
            notes.Add($"Особые сочетания — по СП 296.1325800: {(p.Special.NormativeProperties ? "нормативные" : "расчётные")} характеристики материалов, γn = 1; трещиностойкость не проверяется.");
        return notes;
    }
}
