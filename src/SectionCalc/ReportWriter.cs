using System.Globalization;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace SectionCalc;

/// <summary>
/// Calculation note in the usual engineering layout: input data, results, then the calculation of the
/// governing combinations with formulas and substituted values, as returned by the engine.
/// </summary>
public sealed record CombinationLine(string Name, IReadOnlyList<string> Cells, IReadOnlyList<bool> Exceeded);

public static class ReportWriter
{
    const string Accent = "216897", Text = "263238", Muted = "616161", Header = "EDF1F2", Border = "C8D1D5";
    const string OkFill = "EAF3EB", OkText = "337C3B", BadFill = "FFEBEE", BadText = "D32F2F", WarnFill = "FFF4E5", WarnText = "8F5000";
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    static readonly HashSet<string> Hidden = ["crack_formation", "sls_member_stability", "anchorage", "detailing"];

    public static void Write(string path, CheckResult result, SelectionResult? selection = null, DateTime? date = null)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        AddStyles(main);
        var body = main.Document.Body!;
        var p = result.Project;
        var c = p.Conditions;

        body.Append(Para("Расчёт железобетонного сечения", "Title"));
        body.Append(Para($"{p.Title} · СП 63.13330.2018 · {(date ?? DateTime.Now).ToString("dd.MM.yyyy", Ru)}", "Subtitle"));

        body.Append(Para("1. Исходные данные", "Heading1"));
        body.Append(Para("1.1. Сечение", "Heading2"));
        body.Append(KeyValueTable(SectionInputs(result)));
        body.Append(Image(main, SectionSketch.Png(p, 900, 640, 22), 11.0, 7.82));
        body.Append(Para("Рис. 1. Сечение. Положительный момент Mx растягивает грань y > 0.", "Caption"));
        body.Append(Para("1.2. Материалы", "Heading2"));
        body.Append(MaterialsTable(p));
        body.Append(Para("Расчётные сопротивления по видам сочетаний, МПа:", "Lead"));
        body.Append(ResistanceTable(p));
        body.Append(Para("1.3. Расчётные параметры", "Heading2"));
        body.Append(KeyValueTable(DesignInputs(result)));
        body.Append(Para("1.4. Расчётные усилия", "Heading2"));
        body.Append(LoadsTable(p));
        body.Append(Para("N < 0 — сжатие. Нормативные усилия: Nn = N·γn/γf; длительная часть: Nl = kдл·Nn (так же для моментов).", "Note"));

        body.Append(Para("2. Результаты расчёта", "Heading1"));
        foreach (var line in Conclusion(result)) body.Append(Para(line, "Normal"));
        body.Append(SummaryTable(result));
        body.Append(Para("Коэффициенты использования по сочетаниям:", "Lead"));
        body.Append(CombinationTable(result));
        body.Append(Para("K — коэффициент использования, условие выполнено при K ≤ 1; «нет» — трещины не образуются; «—» — не проверялось.", "Note"));

        int chapter = 3;
        body.Append(Para($"{chapter++}. Расчёт по прочности", "Heading1"));
        foreach (var e in StrengthCalculation(result)) body.Append(e);
        if (c.ShearCheck)
        {
            body.Append(Para($"{chapter++}. Расчёт на действие поперечной силы", "Heading1"));
            foreach (var e in ShearCalculation(result)) body.Append(e);
        }
        body.Append(Para($"{chapter++}. Расчёт по трещиностойкости", "Heading1"));
        foreach (var e in CrackCalculation(result)) body.Append(e);

        if (selection is not null && selection.Outcomes.Count > 0)
        {
            body.Append(Para($"{chapter++}. Подбор армирования", "Heading1"));
            body.Append(Para($"Варианты проверены в порядке возрастания площади арматуры. Не помещаются в сечении: {selection.GeometricallyRejected}.", "Normal"));
            body.Append(SelectionTable(selection));
        }

        body.Append(Para($"{chapter}. Примечания", "Heading1"));
        int n = 1;
        foreach (var note in result.Notes.Concat(StandardNotes(result)))
            body.Append(Para($"{n++}. {note}", "Normal"));
        body.Append(SectionProps());
        main.Document.Save();
    }

    // ---- formatting ----

    static string F(double v, string format = "0.###") => v.ToString(format, Ru);
    static string F1(double v) => v.ToString("0.0", Ru);
    static string F3(double v) => v.ToString("0.000", Ru);
    /// <summary>Value for a product: negative numbers in parentheses.</summary>
    static string P(double v, string format = "0.0") => v < 0 ? $"({v.ToString(format, Ru)})" : v.ToString(format, Ru);

    public static string Utilization(double? u) => u is double d ? d.ToString("0.000", Ru) : "—";

    public static string VerdictText(Verdict v) => v switch
    {
        SectionCalc.Verdict.Passed => "Требования выполнены",
        SectionCalc.Verdict.Failed => "Требования не выполнены",
        SectionCalc.Verdict.Review => "Коэффициент использования на пределе",
        _ => "Расчёт выполнен не полностью",
    };

    static IEnumerable<string> Conclusion(CheckResult r)
    {
        string line = $"Вывод: {VerdictText(r.Verdict).ToLower(Ru)}.";
        if (r.Governing is CheckRow g) line += $" Наибольший коэффициент использования K = {Utilization(g.Utilization)} — {Lower(g.Title)}, сочетание {g.Combination}.";
        yield return line;
        var failed = r.Summary.Where(x => x.IsFailure).Select(x => Lower(x.Title)).ToList();
        if (failed.Count > 0) yield return "Не выполнено: " + string.Join("; ", failed) + ".";
    }

    static string Lower(string s) => s.Length == 0 ? s : char.ToLower(s[0], Ru) + s[1..];

    // ---- input tables ----

    static List<(string, string)> SectionInputs(CheckResult r)
    {
        var p = r.Project;
        var rows = new List<(string, string)>();
        if (p.Kind == SectionKind.Pile)
        {
            var g = p.Pile;
            double area = Math.PI * g.DiameterMm * g.DiameterMm / 4;
            rows.Add(("Тип", "буронабивная свая, круглое сечение"));
            rows.Add(("Диаметр D", $"{F(g.DiameterMm)} мм"));
            rows.Add(("Продольная арматура", $"{g.BarCount} Ø{F(g.BarDiameterMm)} {p.RebarClass}, As = {F(r.Layout.SteelAreaMm2, "0")} мм², μs = {F(r.Layout.SteelAreaMm2 / area * 100, "0.00")} %"));
            rows.Add(("Спираль", $"Ø{F(g.SpiralDiameterMm)} {p.TransverseClass}"));
            rows.Add(("Защитный слой", $"{F(g.CoverMm)} мм {CoverText(g.CoverReference, "спирали")}; до оси стержней {F(g.AxisDistanceMm, "0.#")} мм"));
        }
        else
        {
            var g = p.Wall;
            rows.Add(("Тип", "стена в грунте, прямоугольное сечение"));
            rows.Add(("Размеры b × h", $"{F(g.WidthMm)} × {F(g.ThicknessMm)} мм"));
            string Face(WallFace f) => (f.Rows > 1 ? $"{f.Rows} ряда × " : "") + $"{f.BarCount} Ø{F(f.BarDiameterMm)} {p.RebarClass}"
                + (f.Rows > 1 ? $", зазор между рядами {F(f.RowGapMm)} мм" : "");
            rows.Add(("Арматура грани «+» (y > 0)", Face(g.PositiveFace)));
            rows.Add(("Арматура грани «−» (y < 0)", Face(g.NegativeFace)));
            rows.Add(("Площадь арматуры", $"As = {F(r.Layout.SteelAreaMm2, "0")} мм², μs = {F(r.Layout.SteelAreaMm2 / (g.WidthMm * g.ThicknessMm) * 100, "0.00")} %"));
            rows.Add(("Хомуты", $"Ø{F(g.StirrupDiameterMm)} {p.TransverseClass}"));
            rows.Add(("Защитный слой", $"{F(g.CoverMm)} мм {CoverText(g.CoverReference, "хомута")}; до оси стержней {F(g.AxisDistanceMm(g.PositiveFace), "0.#")} мм (грань «+»), {F(g.AxisDistanceMm(g.NegativeFace), "0.#")} мм (грань «−»)"));
        }
        return rows;
    }

    static string CoverText(CoverReference r, string transverse) => r switch
    {
        CoverReference.Bar => "до продольных стержней в свету",
        CoverReference.Axis => "до оси продольных стержней",
        _ => "до " + transverse,
    };

    static Table MaterialsTable(SectionProject p)
    {
        var c = MaterialTables.Concrete(p.ConcreteClass); var s = MaterialTables.Rebar(p.RebarClass); var t = MaterialTables.Rebar(p.TransverseClass);
        var f = p.Concrete;
        var table = NewTable(["Материал", "Нормативные и расчётные характеристики, МПа", "Коэффициенты"], [22, 50, 28]);
        table.Append(Row([$"Бетон {p.ConcreteClass}", $"Rb = {F(c.Rb)}; Rbt = {F(c.Rbt)}; Rb,n = {F(c.RbSer)}; Rbt,n = {F(c.RbtSer)}; Eb = {F(c.Eb, "0")}",
            $"γb1 = {F(f.Gb1)}; γb2 = {F(f.Gb2)}; γb3 = {F(f.Gb3)}; γb5 = {F(f.Gb5)}" + (f.HardeningFactor != 1 ? $"; к Eb {F(f.HardeningFactor)}" : "")]));
        table.Append(Row([$"Продольная арматура {p.RebarClass}", (p.RscByTable ? $"Rs = {F(s.Rs)}; Rsc = {F(s.Rsc)} ({F(s.RscShort)} при кратковременной); " : $"Rs = Rsc = {F(s.Rs)}; ") + $"Rs,n = {F(s.RsSer)}; Es = {F(s.Es, "0")}", $"γs = {F(p.LongitudinalFactor)}"]));
        table.Append(Row([$"Поперечная арматура {p.TransverseClass}", $"Rsw = {F(t.Rsw)}", $"γs = {F(p.TransverseFactor)}"]));
        return table;
    }

    static Table ResistanceTable(SectionProject p)
    {
        var table = NewTable(["Вид сочетания", "Rb", "Rbt", "Rs", "Rsc", "Rsw"], [36, 13, 13, 13, 12, 13]);
        foreach (var kind in p.Loads.Select(CombinationKind.Of).Distinct())
        {
            var m = MaterialTables.Uls(p, kind);
            double rsw = kind.Seismic ? MaterialTables.Uls(p, kind, inclined: true).Rsw : m.Rsw;
            table.Append(Row([KindName(kind, p), F(m.Rb, "0.##"), F(m.Rbt, "0.###"), F(m.Rs, "0.#"), F(m.Rsc, "0.#"), F(rsw, "0.#")]));
        }
        var sls = MaterialTables.Sls(p);
        table.Append(Row(["Трещиностойкость (нормативные)", F(sls.Rb, "0.##"), F(sls.Rbt, "0.###"), F(sls.Rs, "0.#"), F(sls.Rsc, "0.#"), "—"]));
        return table;
    }

    static string KindName(CombinationKind k, SectionProject p) =>
        k.Special ? (p.Special.NormativeProperties ? "Особое (нормативные характеристики)" : "Особое")
        : k.Seismic ? $"Сейсмическое, m_tr = {F(p.Seismic.NormalSectionFactor)} / {F(p.Seismic.InclinedSectionFactor)}" + (k.ShortTerm ? ", γb1 = 1" : "")
        : k.ShortTerm ? "Основное, кратковременное (γb1 = 1)" : "Основное";

    static List<(string, string)> DesignInputs(CheckResult r)
    {
        var p = r.Project; var c = p.Conditions;
        var rows = new List<(string, string)>
        {
            ("Длина элемента, расчётные длины", $"L = {F(c.ElementLengthM)} м; l₀,x = μx·L = {F(c.MuX)}·{F(c.ElementLengthM)} = {F(c.L0xM)} м; l₀,y = μy·L = {F(c.MuY)}·{F(c.ElementLengthM)} = {F(c.L0yM)} м"),
            ("Предельная гибкость", $"λult = {F(c.SlendernessLimit)}"),
            ("Расчётная схема", c.StaticallyDeterminate ? "статически определимая" : "статически неопределимая"),
            ("Случайный эксцентриситет", c.Eccentricity == EccentricityMode.InLoads ? "учтён в заданных моментах"
                : c.EaXMm > 0 || c.EaYMm > 0 ? $"задан: ea,x = {F(r.EaXMm ?? c.EaXMm, "0.#")} мм, ea,y = {F(r.EaYMm ?? c.EaYMm, "0.#")} мм" : "по п. 8.1.7 СП 63"),
            ("Влияние прогиба", c.SecondOrder switch
            {
                SecondOrderMode.Cscore => "коэффициент η по п. 8.1.15 СП 63",
                SecondOrderMode.InLoads => "учтено в заданных усилиях",
                _ => "не учитывается",
            }),
            ("Коэффициент надёжности по ответственности", $"γn = {F(c.ReliabilityUls)} (прочность), {F(c.ReliabilitySls)} (трещиностойкость)"),
            ("Трещиностойкость", c.CrackCategory == CrackCategory.NoCracks ? "трещины не допускаются"
                : $"{(c.PermeabilityLimits ? "ограничение проницаемости" : "сохранность арматуры")}: acrc,ult = {F(c.CrackLimitLongMm)} мм (продолжительное), {F(c.CrackLimitShortMm)} мм (непродолжительное); "
                  + (c.CrackMethod == CrackMethod.Refined ? "ψs уточнённый (σs,crc по сечению с трещиной)" : "ψs консервативный (σs,crc по сечению без трещин)")),
        };
        if (c.ShearCheck)
            rows.Add(("Поперечное армирование", p.Kind == SectionKind.Pile
                ? $"спираль Ø{F(p.Pile.SpiralDiameterMm)} {p.TransverseClass} с шагом {F(c.ShearPitchMm)} мм"
                : $"хомуты Ø{F(p.Wall.StirrupDiameterMm)} {p.TransverseClass} с шагом {F(c.ShearPitchMm)} мм, {c.StirrupLegs} ветви в сечении"));
        if (c.ShearCheck && c.InclinedMoment)
            rows.Add(("Наклонное сечение на M", $"коэффициент анкеровки {F(c.AnchorageFactor)}; расстояния до опор {F(c.SupportDistanceDownM)} и {F(c.SupportDistanceUpM)} м"));
        if (p.Loads.Any(l => l.Special && !l.Seismic))
        {
            var sp = p.Special;
            rows.Add(("Особые сочетания (СП 296)", $"γb = {F(sp.ConcreteFactor)}; γs = {F(sp.LongitudinalFactor)}; γsw = {F(sp.TransverseFactor)}; понижающий коэффициент {F(sp.AdditionalFactor)}"));
        }
        return rows;
    }

    static Table LoadsTable(SectionProject p)
    {
        var t = NewTable(["№", "N, кН", "Mx, кН·м", "My, кН·м", "Q, кН", "γf", "kдл", "Вид"], [10, 13, 13, 13, 11, 9, 9, 22]);
        foreach (var l in p.Loads)
        {
            string kind = Engine.KindTag(CombinationKind.Of(l));
            t.Append(Row([l.Name, F(l.N, "0.#"), F(l.Mx, "0.#"), F(l.My, "0.#"), F(l.Q, "0.#"), F(l.LoadFactor, "0.###"), F(l.SustainedShare, "0.###"),
                kind.Length > 0 ? kind : "основное"]));
        }
        return t;
    }

    // ---- results ----

    /// <summary>Rows reduced to one per check and combination (the worst sign of ea), in the order of the loads table.</summary>
    public static List<CheckRow> ByCombination(CheckResult r)
    {
        bool withEta = r.Rows.Any(x => x.CheckId == "normal_member_strength" && x.Utilization is not null);
        var order = r.Project.Loads.Select((l, i) => (l.Name.Trim(), i)).ToDictionary(x => x.Item1, x => x.i);
        int Order(CheckRow x) => order.TryGetValue(Source(r, x), out var i) ? i : -1;
        return r.Rows.Where(x => x.Status != "not_evaluated" && !Hidden.Contains(x.CheckId) && !(withEta && x.CheckId == "normal_strength"))
            .GroupBy(x => (x.CheckId, x.Combination))
            .Select(g => g.OrderByDescending(x => x.IsFailure).ThenByDescending(x => x.Utilization ?? -1).First())
            .OrderBy(Order).ToList();
    }

    /// <summary>Governing row per check; strength without η is omitted when the η check exists.</summary>
    public static IEnumerable<CheckRow> SummaryRows(CheckResult r) =>
        r.Summary.Where(x => !(x.CheckId == "normal_strength" && r.Summary.Any(y => y.CheckId == "normal_member_strength" && y.Utilization is not null)));

    public static string GoverningText(CheckResult r) => r.Governing is CheckRow g
        ? $"Kmax = {Utilization(g.Utilization)}: {Lower(g.Title)}, сочетание {g.Combination}" : "";

    static string Source(CheckResult r, CheckRow x) => r.Cases.TryGetValue(x.CaseId, out var c) ? c.Source : x.CaseId;

    static Table SummaryTable(CheckResult r)
    {
        var t = NewTable(["Проверка", "Сочетание", "K", "Результат"], [46, 24, 10, 20]);
        foreach (var row in SummaryRows(r))
            t.Append(Row([row.Title, row.Combination.Length > 0 ? row.Combination : "—", UtilizationText(row, r), StatusText(row, r)], statusColumn: 3, row: row));
        return t;
    }

    /// <summary>Header of the per-combination table: strength, N/Ncr, Q, then the two crack columns.</summary>
    public static string[] CombinationHeaders(CheckResult r) => r.Project.Conditions.CrackCategory == CrackCategory.NoCracks
        ? ["Прочность", "N/Ncr", "Q", "M/Mcrc", ""]
        : ["Прочность", "N/Ncr", "Q", "acrc, прод.", "acrc, непрод."];

    /// <summary>Worst K of each check type per combination. "нет" — no cracks, "—" — not checked.</summary>
    public static List<CombinationLine> Combinations(CheckResult r)
    {
        var rows = ByCombination(r);
        string strength = rows.Any(x => x.CheckId == "normal_member_strength") ? "normal_member_strength" : "normal_strength";
        bool noCracks = r.Project.Conditions.CrackCategory == CrackCategory.NoCracks;
        var lines = new List<CombinationLine>();
        foreach (var l in r.Project.Loads)
        {
            string name = l.Name.Trim();
            var mine = rows.Where(x => Source(r, x) == name).ToList();
            (string, bool) K(params string[] ids) => mine.Where(x => ids.Contains(x.CheckId)).Max(x => x.Utilization) is double u ? (F3(u), u > 1) : ("—", false);
            (string, bool) Crack(string id) => mine.FirstOrDefault(x => x.CheckId == id) is { } row && row.Status != "not_evaluated"
                ? Cracked(r, name) ? (Utilization(row.Utilization), row.Utilization > 1) : ("нет", false) : ("—", false);
            var cells = new[] { K(strength), K("member_stability"), K("shear_strip", "shear_inclined", "inclined_moment"),
                noCracks ? K("no_cracks") : Crack("crack_width_long"), noCracks ? ("", false) : Crack("crack_width_short") };
            lines.Add(new CombinationLine(Engine.Combination(name, CombinationKind.Of(l)), cells.Select(x => x.Item1).ToList(), cells.Select(x => x.Item2).ToList()));
        }
        return lines;
    }

    static Table CombinationTable(CheckResult r)
    {
        var lines = Combinations(r);
        var headers = CombinationHeaders(r);
        // A column is shown when it has any value (N/Ncr without η, Q without the check are dropped).
        var columns = Enumerable.Range(0, headers.Length).Where(i => headers[i].Length > 0 && (i >= 3 || lines.Any(l => l.Cells[i] != "—"))).ToList();
        int width = 82 / columns.Count;
        var t = NewTable(["№", .. columns.Select(i => headers[i])], [18, .. columns.Select(_ => width)]);
        foreach (var line in lines)
        {
            var row = Row([line.Name, .. columns.Select(i => line.Cells[i])]);
            int cell = 1;
            foreach (int i in columns)
            {
                if (line.Exceeded[i]) Paint(row.Elements<TableCell>().ElementAt(cell), BadFill, BadText);
                cell++;
            }
            t.Append(row);
        }
        return t;
    }

    static Table SelectionTable(SelectionResult s)
    {
        var t = NewTable(["Вариант", "As, мм²", "Определяющая проверка", "K", "Результат"], [26, 11, 37, 9, 17]);
        foreach (var o in s.Outcomes)
        {
            string result = o.Error is not null ? "ошибка расчёта" : o == s.Best ? "принят" : VerdictShort(o.Verdict);
            var cells = Row([o.Candidate.Description, F(o.Candidate.SteelAreaMm2, "0"), o.Governing?.Title ?? (o.Error ?? "—"), Utilization(o.Governing?.Utilization), result]);
            var (fill, color) = Colors(o.Verdict);
            Paint(cells.Elements<TableCell>().Last(), fill, color);
            t.Append(cells);
        }
        return t;
    }

    static string VerdictShort(Verdict v) => v switch
    {
        SectionCalc.Verdict.Passed => "проходит", SectionCalc.Verdict.Failed => "не проходит",
        SectionCalc.Verdict.Review => "на пределе", _ => "не определено",
    };

    /// <summary>K for display: a crack width of an uncracked section is shown as "—", not 0.</summary>
    public static string UtilizationText(CheckRow row, CheckResult result) =>
        row.CheckId.StartsWith("crack_width") && !Cracked(result, row.CaseId) ? "—" : Utilization(row.Utilization);

    public static string StatusText(CheckRow row, CheckResult? result = null) =>
        row.Status == "not_evaluated" ? "не проверялось" + ReasonText(row.Reason)
        : row.CheckId.StartsWith("crack_width") && result is not null && !Cracked(result, row.CaseId) ? "трещин нет"
        : Engine.StatusText.GetValueOrDefault(row.Status, row.Status);

    static bool Cracked(CheckResult r, string caseId) => r.Rows.Any(x => x.CheckId == "crack_formation" && x.CaseId == caseId
        && x.Details is { } d && d.TryGetProperty("cracked", out var c) && c.ValueKind == JsonValueKind.True);

    static string ReasonText(string reason) => reason switch
    {
        var s when s.Contains("uls_fail") || s.Contains("member_strength_not_confirmed") => " (не обеспечена прочность)",
        _ => "",
    };

    // ---- calculation with formulas ----

    static double? Num(JsonElement? e, params string[] path)
    {
        if (e is not { } cur) return null;
        foreach (var key in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(key, out var next)) return null;
            cur = next;
        }
        return cur.ValueKind == JsonValueKind.Number && double.IsFinite(cur.GetDouble()) ? cur.GetDouble() : null;
    }

    static string? Str(JsonElement? e, string key) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Close(double a, double b, double rel = 0.01) => Math.Abs(a - b) <= rel * Math.Max(Math.Abs(b), 1e-9);

    static CheckRow? Worst(CheckResult r, string id) =>
        r.Rows.Where(x => x.CheckId == id && x.Utilization is not null).OrderByDescending(x => x.Utilization).FirstOrDefault();

    static Paragraph Calc(string text) => Para(text, "Calc");

    static string Verdict(double k, string ok, string bad) => k <= 1 ? $"K = {F3(k)} ≤ 1 — {ok}." : $"K = {F3(k)} > 1 — {bad}.";

    static IEnumerable<OpenXmlElement> StrengthCalculation(CheckResult r)
    {
        var p = r.Project; var c = p.Conditions;
        string strengthId = r.Rows.Any(x => x.CheckId == "normal_member_strength" && x.Utilization is not null) ? "normal_member_strength" : "normal_strength";
        var gov = Worst(r, strengthId);
        bool compressed = p.Loads.Any(l => l.N < 0);

        if (compressed)
        {
            yield return Para("Гибкость элемента", "Heading2");
            if (p.Kind == SectionKind.Pile)
            {
                double i = p.Pile.DiameterMm / 4;
                yield return Calc($"i = D/4 = {F(p.Pile.DiameterMm)}/4 = {F1(i)} мм;");
                yield return Calc($"λx = l₀,x/i = {F(c.L0xM * 1000, "0")}/{F1(i)} = {F1(c.L0xM * 1000 / i)};  λy = l₀,y/i = {F(c.L0yM * 1000, "0")}/{F1(i)} = {F1(c.L0yM * 1000 / i)}.");
            }
            else
            {
                double ix = p.Wall.ThicknessMm / Math.Sqrt(12), iy = p.Wall.WidthMm / Math.Sqrt(12);
                yield return Calc($"ix = h/√12 = {F(p.Wall.ThicknessMm)}/3,464 = {F1(ix)} мм;  iy = b/√12 = {F(p.Wall.WidthMm)}/3,464 = {F1(iy)} мм;");
                yield return Calc($"λx = l₀,x/ix = {F(c.L0xM * 1000, "0")}/{F1(ix)} = {F1(c.L0xM * 1000 / ix)};  λy = l₀,y/iy = {F(c.L0yM * 1000, "0")}/{F1(iy)} = {F1(c.L0yM * 1000 / iy)}.");
            }
            double lmax = Math.Max(Worst(r, "slenderness_x")?.Utilization ?? 0, Worst(r, "slenderness_y")?.Utilization ?? 0) * c.SlendernessLimit;
            yield return Calc($"λmax = {F1(lmax)} {(lmax <= c.SlendernessLimit ? "≤" : ">")} λult = {F(c.SlendernessLimit)}.");
        }

        if (gov is null || !r.Cases.TryGetValue(gov.CaseId, out var info))
        {
            yield return Para("Расчёт по прочности не выполнен.", "Normal");
            yield break;
        }
        double gnUls = info.Kind.Special ? 1 : c.ReliabilityUls;
        yield return Para($"Определяющее сочетание — {gov.Combination}" + (gnUls != 1 ? $", с учётом γn = {F(gnUls)}" : "")
            + $": N = {F1(info.N)} кН, Mx = {F1(info.SourceMx)} кН·м, My = {F1(info.SourceMy)} кН·м.", "Normal");

        if (c.Eccentricity == EccentricityMode.Sp63 && info.N < 0)
        {
            yield return Para("Случайный эксцентриситет (п. 8.1.7)", "Heading2");
            double hx = p.Kind == SectionKind.Pile ? p.Pile.DiameterMm : p.Wall.ThicknessMm;
            double hy = p.Kind == SectionKind.Pile ? p.Pile.DiameterMm : p.Wall.WidthMm;
            double L = c.ElementLengthM * 1000;
            foreach (var (axis, h, given, ea) in new[] { ("x", hx, c.EaXMm, r.EaXMm ?? 0), ("y", hy, c.EaYMm, r.EaYMm ?? 0) })
            {
                double sp = Math.Max(Math.Max(L / 600, h / 30), 10);
                yield return Calc(given > 0 && !Close(sp, ea, 1e-3)
                    ? $"ea,{axis} = {F1(ea)} мм (задан, не менее 10 мм);"
                    : $"ea,{axis} = max(L/600; h/30; 10) = max({F1(L / 600)}; {F1(h / 30)}; 10) = {F1(ea)} мм;");
            }
            double n = Math.Abs(info.N);
            foreach (var (axis, m0, mPrepared, ea) in new[] { ("x", info.SourceMx, info.Mx, r.EaXMm ?? 0), ("y", info.SourceMy, info.My, r.EaYMm ?? 0) })
            {
                double e = Math.Abs(m0) / n * 1000;
                string formula = c.StaticallyDeterminate
                    ? $"e0,{axis} = |M{axis}/N| + ea,{axis} = {F1(e)} + {F1(ea)} = {F1(e + ea)} мм"
                    : $"e0,{axis} = max(|M{axis}/N|; ea,{axis}) = max({F1(e)}; {F1(ea)}) = {F1(Math.Max(e, ea))} мм";
                yield return Calc($"{formula};  M{axis},0 = {F1(mPrepared)} кН·м.");
            }
        }

        var stability = r.Rows.FirstOrDefault(x => x.CheckId == "member_stability" && x.CaseId == gov.CaseId);
        if (c.SecondOrder == SecondOrderMode.Cscore && stability?.Details is { } sd && Num(sd, "eta_x") is double ex)
        {
            yield return Para("Влияние прогиба (пп. 8.1.15, 8.1.16)", "Heading2");
            double n = Math.Abs(info.N);
            foreach (var (axis, eta, ncr, m0, meff) in new[]
                     {
                         ("x", ex, Num(sd, "ncr_x_kn"), info.Mx, Num(sd, "mx_eff_knm")),
                         ("y", Num(sd, "eta_y") ?? 1, Num(sd, "ncr_y_kn"), info.My, Num(sd, "my_eff_knm")),
                     })
            {
                if (ncr is double nc && nc > 0 && Close(1 / (1 - n / nc), eta, 1e-4))
                    yield return Calc($"Ncr,{axis} = {F(nc, "0")} кН;  η{axis} = 1/(1 − N/Ncr,{axis}) = 1/(1 − {F(n, "0.#")}/{F(nc, "0")}) = {F3(eta)};");
                else
                    yield return Calc($"η{axis} = {F3(eta)};");
                if (meff is double me && Math.Abs(m0) > 1e-9)
                    yield return Calc($"M{axis} = η{axis}·M{axis},0 = {F3(eta)}·{P(m0)} = {F1(me)} кН·м.");
            }
        }

        yield return Para("Прочность нормального сечения (пп. 8.1.20–8.1.30)", "Heading2");
        yield return Para("Расчёт по нелинейной деформационной модели. Диаграммы деформирования: бетона — двухлинейная " +
            "(εb1,red = 0,0015; εb2 = 0,0035; при растяжении εbt1,red = 0,00008; εbt2 = 0,00015), арматуры — двухлинейная (εs2 = 0,025).", "Normal");
        var m = info.Materials;
        yield return Calc($"Rb = {F(m.Rb, "0.##")} МПа;  Rbt = {F(m.Rbt, "0.###")} МПа;  {(Math.Abs(m.Rsc - m.Rs) < 1e-9 ? $"Rs = Rsc = {F(m.Rs, "0.#")} МПа" : $"Rs = {F(m.Rs, "0.#")} МПа;  Rsc = {F(m.Rsc, "0.#")} МПа (табл. 6.14)")};  Eb = {F(m.Eb, "0")} МПа;  Es = {F(m.Es, "0")} МПа.");
        double N = info.N, Mx = info.Mx, My = info.My;
        if (gov.Details is { } gd && Num(gd, "n_kn") is double dn) { N = dn; Mx = Num(gd, "mx_knm") ?? Mx; My = Num(gd, "my_knm") ?? My; }
        yield return Calc($"Усилия в сечении: N = {F1(N)} кН;  Mx = {F1(Mx)} кН·м;  My = {F1(My)} кН·м.");
        double k = gov.Utilization!.Value;
        yield return Calc($"Предельные усилия при пропорциональном увеличении: kult = {F3(1 / k)};  Nult = {F1(N / k)} кН;  Mx,ult = {F1(Mx / k)} кН·м;  My,ult = {F1(My / k)} кН·м.");
        yield return Calc(Verdict(k, "прочность обеспечена", "прочность не обеспечена"));
    }

    static IEnumerable<OpenXmlElement> ShearCalculation(CheckResult r)
    {
        var p = r.Project; var c = p.Conditions;
        var gov = Worst(r, "shear_inclined");
        if (gov?.Details is not { } d || !r.Cases.TryGetValue(gov.CaseId, out var info))
        {
            yield return Para("Расчёт не выполнен.", "Normal");
            yield break;
        }
        double b = (Num(d, "variables", "b") ?? 0) * 1000, h0 = (Num(d, "variables", "h0") ?? 0) * 1000;
        double phiN = Num(d, "variables", "phiN") ?? 1;
        var m = info.ShearMaterials;
        yield return Para($"Определяющее сочетание — {gov.Combination}: Q = {F1(info.Q)} кН, N = {F1(info.N)} кН.", "Normal");
        if (p.Kind == SectionKind.Pile)
            yield return Calc($"Круглое сечение приводится к прямоугольному: b = 0,885·D = 0,885·{F(p.Pile.DiameterMm)} = {F(b, "0")} мм;  h0 = b − a = {F(b, "0")} − {F(c.PileShearAMm)} = {F(h0, "0")} мм.");
        else
            yield return Calc($"b = {F(b, "0")} мм;  h0 = {F(h0, "0.#")} мм.");
        yield return Calc($"Rb = {F(m.Rb, "0.##")} МПа;  Rbt = {F(m.Rbt, "0.###")} МПа;  Rsw = {F(m.Rsw, "0.#")} МПа;  φn = {F3(phiN)} (п. 8.1.32).");

        var strip = r.Rows.FirstOrDefault(x => x.CheckId == "shear_strip" && x.CaseId == gov.CaseId);
        if (strip?.Details is { } sd && Num(sd, "allowable") is double qmax)
        {
            yield return Para("Прочность по наклонной полосе (8.55)", "Heading2");
            double calc = 0.3 * phiN * m.Rb * b * h0 / 1000;
            yield return Calc(Close(calc, qmax)
                ? $"Q ≤ φb1·φn·Rb·b·h0 = 0,3·{F3(phiN)}·{F(m.Rb, "0.##")}·{F(b, "0")}·{F(h0, "0.#")}·10⁻³ = {F1(qmax)} кН;"
                : $"Q ≤ φb1·φn·Rb·b·h0 = {F1(qmax)} кН;");
            yield return Calc($"Q = {F1(info.Q)} кН;  " + Verdict(strip.Utilization ?? info.Q / qmax, "условие выполнено", "условие не выполнено"));
        }

        yield return Para("Прочность по наклонному сечению (8.56)", "Heading2");
        double? qsw = Num(d, "variables", "qsw"), C = Num(d, "variables", "C"), Qb = Num(d, "variables", "Qb"), Qsw = Num(d, "variables", "Qsw");
        double asw = p.Kind == SectionKind.Pile
            ? Num(d, "equivalence", "effective_asw_mm2") ?? 2 * Math.PI * Math.Pow(p.Pile.SpiralDiameterMm, 2) / 4
            : c.StirrupLegs * Math.PI * Math.Pow(p.Wall.StirrupDiameterMm, 2) / 4;
        if (qsw is double q)
        {
            double calc = m.Rsw * asw / c.ShearPitchMm;
            string legs = p.Kind == SectionKind.Pile ? "две ветви витка" : $"{c.StirrupLegs} ветви";
            yield return Calc(Close(calc, q)
                ? $"qsw = Rsw·Asw/sw = {F(m.Rsw, "0.#")}·{F1(asw)}/{F(c.ShearPitchMm)} = {F1(q)} Н/мм (Asw — {legs});"
                : $"qsw = {F1(q)} Н/мм;");
        }
        if (C is double cc)
        {
            double c0 = Math.Min(cc * 1000, 2 * h0);
            yield return Calc($"C = {F(cc * 1000, "0")} мм — наиболее опасная проекция наклонного сечения;  C0 = min(C; 2h0) = {F(c0, "0")} мм;");
            if (Qsw is double qs && qsw is double q2)
                yield return Calc(Close(0.75 * q2 * c0 / 1000, qs)
                    ? $"Qsw = 0,75·qsw·C0 = 0,75·{F1(q2)}·{F(c0, "0")}·10⁻³ = {F1(qs)} кН;"
                    : $"Qsw = {F1(qs)} кН;");
        }
        if (Qb is double qb)
        {
            double lo = 0.5 * phiN * m.Rbt * b * h0 / 1000, hi = 2.5 * phiN * m.Rbt * b * h0 / 1000;
            string bound = Close(qb, lo, 2e-3) ? " — принято по нижнему пределу" : Close(qb, hi, 2e-3) ? " — принято по верхнему пределу" : "";
            yield return Calc($"Qb = {F1(qb)} кН (п. 8.1.33; 0,5·φn·Rbt·b·h0 = {F1(lo)} ≤ Qb ≤ 2,5·φn·Rbt·b·h0 = {F1(hi)}){bound};");
        }
        if (Num(d, "allowable") is double allow)
            yield return Calc($"Q = {F1(info.Q)} кН ≤ Qb + Qsw = {F1(allow)} кН;  " + Verdict(gov.Utilization ?? 0, "условие выполнено", "условие не выполнено"));

        if (Worst(r, "inclined_moment") is { } im)
        {
            yield return Para("Прочность наклонного сечения на действие момента (п. 8.1.35)", "Heading2");
            yield return Calc($"Сочетание {im.Combination}:  " + Verdict(im.Utilization!.Value, "условие выполнено", "условие не выполнено"));
        }
    }

    static IEnumerable<OpenXmlElement> CrackCalculation(CheckResult r)
    {
        var p = r.Project; var c = p.Conditions;
        if (r.Service.Count == 0)
        {
            yield return Para("Сочетаний для расчёта по трещиностойкости нет.", "Normal");
            yield break;
        }
        if (r.Rows.Any(x => x.Reason == "skipped_uls_fail"))
        {
            yield return Para("Расчёт не выполнялся: не обеспечена прочность.", "Normal");
            yield break;
        }
        // Governing: the largest width utilisation, or the largest M/Mcrc when nothing cracks.
        static double Mres(ServiceInfo s) => Math.Sqrt(s.Mx * s.Mx + s.My * s.My);
        var cracked = r.Service.Where(s => s.Crack is { } e && e.TryGetProperty("cracked", out var cr) && cr.ValueKind == JsonValueKind.True).ToList();
        double Width(ServiceInfo s) => r.Rows.Where(x => x.CaseId == s.Id && x.CheckId.StartsWith("crack_width")).Max(x => x.Utilization) ?? 0;
        var gov = cracked.Count > 0 ? cracked.OrderByDescending(Width).First()
            : r.Service.OrderByDescending(s => Mres(s) / (Num(s.Crack, "mcrc_knm") ?? double.MaxValue)).First();
        var load = p.Loads.First(l => l.Name.Trim() == gov.Id);
        double gn = c.ReliabilitySls;

        yield return Para($"Определяющее сочетание — {gov.Id}. Нормативные усилия:", "Normal");
        yield return Calc($"N = Nd·γn/γf = {P(load.N)}·{F(gn)}/{F(load.LoadFactor)} = {F1(gov.N)} кН;  Mx = {F1(gov.Mx)} кН·м;  My = {F1(gov.My)} кН·м;");
        yield return Calc($"длительная часть, kдл = {F(load.SustainedShare)}: Nl = {F1(gov.NLong)} кН;  Mx,l = {F1(gov.MxLong)} кН·м;  My,l = {F1(gov.MyLong)} кН·м.");
        yield return Para("Расчёт по нелинейной деформационной модели при нормативных сопротивлениях; случайный эксцентриситет и η не учитываются.", "Normal");

        double? mcrc = Num(gov.Crack, "mcrc_knm");
        bool isCracked = cracked.Contains(gov);
        yield return Para("Образование трещин (пп. 8.2.8–8.2.14)", "Heading2");
        if (mcrc is double mc)
            yield return Calc($"Mcrc = {F1(mc)} кН·м;  M = √(Mx² + My²) = {F1(Mres(gov))} кН·м {(isCracked ? ">" : "≤")} Mcrc — трещины {(isCracked ? "образуются" : "не образуются")}.");
        if (c.CrackCategory == CrackCategory.NoCracks)
        {
            if (Worst(r, "no_cracks") is { } nc) yield return Calc($"Наибольшее M/Mcrc — сочетание {nc.Combination}:  " + Verdict(nc.Utilization!.Value, "трещины не образуются", "трещины образуются"));
            yield break;
        }
        if (!isCracked)
        {
            yield return Calc("Трещины не образуются ни в одном сочетании; ширина раскрытия не определяется.");
            yield break;
        }

        yield return Para("Ширина раскрытия трещин (пп. 8.2.15–8.2.17)", "Heading2");
        yield return Calc("acrc = φ1·φ2·φ3·ψs·σs/Es·ls;  φ1 = 1,4 (продолжительное) или 1,0 (непродолжительное);  φ2 = 0,5;  φ3 = 1,0.");
        var crack = gov.Crack!.Value;
        bool split = crack.TryGetProperty("total_state", out var ts) && ts.ValueKind == JsonValueKind.Object;
        JsonElement state = split ? ts : crack;
        double es = MaterialTables.Rebar(p.RebarClass).Es;
        double? sigma = Num(state, "sigma_rebar_kpa") / 1000, ls = Num(state, "crack_spacing_m") * 1000, phi2 = Num(state, "phi2");
        double? aTotal = Num(state, "preliminary_width_long_mm");
        string basis = Str(state, "preliminary_basis") ?? "";
        double? psi = null;
        if (basis.StartsWith("precrack") && Num(state, "psi_s_precrack_hypothesis") is double pp && Num(state, "sigma_rebar_precrack_kpa") is double scrc && sigma is double s0)
        {
            psi = pp;
            yield return Calc($"σs = {F1(s0)} МПа — напряжение в арматуре в сечении с трещиной от {(split ? "полных" : "")} нормативных усилий;");
            yield return Calc($"σs,crc = {F1(scrc / 1000)} МПа — то же при M = Mcrc по сечению без трещин;  ψs = 1 − 0,8·σs,crc/σs = 1 − 0,8·{F1(scrc / 1000)}/{F1(s0)} = {F3(pp)};");
        }
        else if (basis.Contains("psi_s_one") && sigma is double s1)
        {
            psi = 1;
            yield return Calc($"σs = {F1(s1)} МПа;  ψs = 1 (п. 8.2.15).");
        }
        else if (Num(state, "psi_s") is double pn && sigma is double s2)
        {
            psi = pn;
            yield return Calc($"σs = {F1(s2)} МПа — напряжение в арматуре в сечении с трещиной от {(split ? "полных" : "")} нормативных усилий;");
            if (Num(state, "sigma_rebar_at_crack_kpa") is double sc && Close(1 - 0.8 * sc / 1000 / s2, pn, 2e-3))
                yield return Calc($"σs,crc = {F1(sc / 1000)} МПа — то же при M = Mcrc по сечению с трещиной;  ψs = 1 − 0,8·σs,crc/σs = 1 − 0,8·{F1(sc / 1000)}/{F1(s2)} = {F3(pn)};");
            else
                yield return Calc($"ψs = {F3(pn)};");
        }
        if (ls is double l) yield return Calc($"ls = {F(l, "0")} мм — базовое расстояние между трещинами (п. 8.2.17);  Es = {F(es, "0")} МПа.");
        if (psi is double ps && sigma is double sg && ls is double l2 && aTotal is double at)
        {
            double calc = 1.4 * (phi2 ?? 0.5) * ps * sg / es * l2;
            yield return Calc(Close(calc, at)
                ? $"acrc(N, M; φ1 = 1,4) = 1,4·0,5·1,0·{F3(ps)}·{F1(sg)}/{F(es, "0")}·{F(l2, "0")} = {F3(at)} мм;"
                : $"acrc(N, M; φ1 = 1,4) = {F3(at)} мм;");
        }
        var longRow = r.Rows.FirstOrDefault(x => x.CheckId == "crack_width_long" && x.CaseId == gov.Id);
        var shortRow = r.Rows.FirstOrDefault(x => x.CheckId == "crack_width_short" && x.CaseId == gov.Id);
        double? aLong = Num(longRow?.Details, "width_mm"), aShort = Num(shortRow?.Details, "width_mm");
        if (split && Num(crack, "a_crc_1_mm") is double a1 && Num(crack, "a_crc_2_mm") is double a2 && Num(crack, "a_crc_3_mm") is double a3)
        {
            bool sustainedCracked = crack.TryGetProperty("sustained_state", out var ss) && ss.TryGetProperty("cracked", out var sc) && sc.ValueKind == JsonValueKind.True;
            if (!sustainedCracked && aTotal is double at2 && at2 > 0)
                yield return Calc($"acrc,1 = acrc(N, M; φ1 = 1,4)·σs,l/σs = {F3(at2)}·{F3(a1 / at2)} = {F3(a1)} мм — от длительных усилий (Ml < Mcrc, σs,l — по сечению с трещиной);");
            else
                yield return Calc($"acrc,1 = {F3(a1)} мм — от длительных усилий, φ1 = 1,4;");
            yield return Calc(aTotal is double at3 && Close(at3 / 1.4, a2)
                ? $"acrc,2 = acrc(N, M; φ1 = 1,0) = {F3(at3)}/1,4 = {F3(a2)} мм — от полных усилий;"
                : $"acrc,2 = {F3(a2)} мм — от полных усилий, φ1 = 1,0;");
            yield return Calc(Close(a1 / 1.4, a3)
                ? $"acrc,3 = acrc,1/1,4 = {F3(a3)} мм — от длительных усилий, φ1 = 1,0;"
                : $"acrc,3 = {F3(a3)} мм — от длительных усилий, φ1 = 1,0;");
            yield return Calc($"acrc,прод = acrc,1 = {F3(aLong ?? a1)} мм;  acrc,непрод = acrc,1 + acrc,2 − acrc,3 = {F3(a1)} + {F3(a2)} − {F3(a3)} = {F3(aShort ?? a1 + a2 - a3)} мм.");
        }
        else
            yield return Calc($"Нагрузка полностью длительная (kдл = 1): acrc,2 = acrc,3, acrc,прод = acrc,непрод = {F3(aLong ?? aTotal ?? 0)} мм.");
        if (longRow?.Utilization is double kl && aLong is double wl)
            yield return Calc($"acrc,прод = {F3(wl)} мм {(kl <= 1 ? "≤" : ">")} acrc,ult = {F(c.CrackLimitLongMm)} мм;  K = {F3(kl)}.");
        if (shortRow?.Utilization is double ks && aShort is double wsh)
            yield return Calc($"acrc,непрод = {F3(wsh)} мм {(ks <= 1 ? "≤" : ">")} acrc,ult = {F(c.CrackLimitShortMm)} мм;  K = {F3(ks)}.");
    }

    static IEnumerable<string> StandardNotes(CheckResult r)
    {
        var p = r.Project;
        if (p.Kind == SectionKind.Pile && p.Conditions.ShearCheck)
            yield return "Расчёт круглого сечения на действие поперечной силы выполнен по приведённому прямоугольному сечению (b = 0,885D), один виток спирали учитывается как две ветви. Методика не регламентирована СП 63.";
        if (p.Conditions.CrackMethod == CrackMethod.Conservative
            && r.Service.Any(s => s.Crack is { } e && e.TryGetProperty("cracked", out var cr) && cr.ValueKind == JsonValueKind.True))
            yield return "При определении ψs напряжение σs,crc принято по сечению без трещин при M = Mcrc, что идёт в запас.";
        yield return "Не проверялись: анкеровка и стыки арматуры, конструктивные требования раздела 10 СП 63.";
    }

    // ---- OpenXml helpers ----

    static void AddStyles(MainDocumentPart main)
    {
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(
            new DocDefaults(new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = "Arial", HighAnsi = "Arial", ComplexScript = "Arial" },
                new Color { Val = Text }, new FontSize { Val = "19" }, new Languages { Val = "ru-RU" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { After = "80", Line = "264", LineRule = LineSpacingRuleValues.Auto }))),
            Style("Normal", "Обычный", null, null, false),
            Style("Title", "Заголовок", "34", Text, true, after: "40"),
            Style("Subtitle", "Подзаголовок", "19", Muted, false, after: "200"),
            Style("Heading1", "Заголовок 1", "24", Accent, true, before: "280", after: "100"),
            Style("Heading2", "Заголовок 2", "20", Text, true, before: "160", after: "60"),
            Style("Lead", "Подпись таблицы", null, null, false, before: "160", after: "60"),
            Style("Note", "Пояснение", "16", Muted, false),
            Style("Caption", "Название объекта", "17", Muted, false, after: "160", center: true),
            Style("Calc", "Расчёт", null, null, false, after: "40", indentTwips: "426"));
    }

    static Style Style(string id, string name, string? size, string? color, bool bold, string? before = null, string? after = null,
        bool center = false, string? indentTwips = null)
    {
        var rpr = new StyleRunProperties();
        if (bold) rpr.Append(new Bold());
        if (color is not null) rpr.Append(new Color { Val = color });
        if (size is not null) rpr.Append(new FontSize { Val = size });
        var style = new Style(new StyleName { Val = name }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = id };
        var ppr = new StyleParagraphProperties();
        // Schema order: keepNext, spacing, ind, jc.
        if (id.StartsWith("Heading")) ppr.Append(new KeepNext());
        if (before is not null || after is not null)
        {
            var spacing = new SpacingBetweenLines();
            if (before is not null) spacing.Before = before;
            if (after is not null) spacing.After = after;
            ppr.Append(spacing);
        }
        if (indentTwips is not null) ppr.Append(new Indentation { Left = indentTwips });
        if (center) ppr.Append(new Justification { Val = JustificationValues.Center });
        if (ppr.HasChildren) style.Append(ppr);
        style.Append(rpr);
        return style;
    }

    static Paragraph Para(string text, string style) =>
        new(new ParagraphProperties(new ParagraphStyleId { Val = style }), Run(text));

    static Run Run(string text, bool bold = false, string? color = null, int? size = null)
    {
        var rpr = new RunProperties();
        if (bold) rpr.Append(new Bold());
        if (color is not null) rpr.Append(new Color { Val = color });
        if (size is int s) rpr.Append(new FontSize { Val = s.ToString(CultureInfo.InvariantCulture) });
        return new Run(rpr, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
    }

    static Table NewTable(string[] headers, int[] widthsPct)
    {
        var borders = new TableBorders(
            new TopBorder { Val = BorderValues.Single, Size = 4, Color = Border }, new LeftBorder { Val = BorderValues.Single, Size = 4, Color = Border },
            new BottomBorder { Val = BorderValues.Single, Size = 4, Color = Border }, new RightBorder { Val = BorderValues.Single, Size = 4, Color = Border },
            new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = Border }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = Border });
        var t = new Table(new TableProperties(new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }, borders,
            new TableCellMarginDefault(new TopMargin { Width = "50", Type = TableWidthUnitValues.Dxa }, new TableCellLeftMargin { Width = 90, Type = TableWidthValues.Dxa },
                new BottomMargin { Width = "50", Type = TableWidthUnitValues.Dxa }, new TableCellRightMargin { Width = 90, Type = TableWidthValues.Dxa })));
        t.Append(new TableGrid(widthsPct.Select(w => new GridColumn { Width = (w * 96).ToString(CultureInfo.InvariantCulture) })));
        var header = new TableRow(new TableRowProperties(new TableHeader()));
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = Cell(headers[i], widthsPct[i], bold: true, color: "455A64");
            Paint(cell, Header, null);
            header.Append(cell);
        }
        t.Append(header);
        return t;
    }

    static TableRow Row(string[] values, int statusColumn = -1, CheckRow? row = null)
    {
        var r = new TableRow(new TableRowProperties(new CantSplit()));
        for (int i = 0; i < values.Length; i++)
        {
            var cell = Cell(values[i], null);
            if (i == statusColumn && row is not null)
            {
                var (fill, color) = row.IsFailure ? (BadFill, BadText) : row.IsReview ? (WarnFill, WarnText)
                    : row.Status is "passed" or "preliminary_below_limit" or "stable" ? (OkFill, OkText) : ("FFFFFF", Muted);
                Paint(cell, fill, color);
            }
            r.Append(cell);
        }
        return r;
    }

    static TableCell Cell(string text, int? widthPct, bool bold = false, string? color = null)
    {
        var props = new TableCellProperties();
        if (widthPct is int w) props.Append(new TableCellWidth { Width = (w * 50).ToString(CultureInfo.InvariantCulture), Type = TableWidthUnitValues.Pct });
        return new TableCell(props, new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "0" }),
            Run(text, bold, color, 17)));
    }

    static void Paint(TableCell cell, string fill, string? color)
    {
        var props = cell.TableCellProperties ?? cell.PrependChild(new TableCellProperties());
        props.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = fill });
        if (color is null) return;
        foreach (var run in cell.Descendants<Run>())
        {
            var rpr = run.RunProperties ??= new RunProperties();
            rpr.RemoveAllChildren<Color>();
            // Schema order: colour precedes the font size.
            if (rpr.GetFirstChild<FontSize>() is FontSize size) rpr.InsertBefore(new Color { Val = color }, size);
            else rpr.Append(new Color { Val = color });
        }
    }

    static (string Fill, string Text) Colors(Verdict v) => v switch
    {
        SectionCalc.Verdict.Passed => (OkFill, OkText), SectionCalc.Verdict.Failed => (BadFill, BadText), _ => (WarnFill, WarnText),
    };

    static Table KeyValueTable(List<(string Key, string Value)> rows)
    {
        var t = NewTable(["Параметр", "Значение"], [32, 68]);
        foreach (var (k, v) in rows) t.Append(Row([k, v]));
        return t;
    }

    static Paragraph Image(MainDocumentPart main, byte[] png, double widthCm, double heightCm)
    {
        var part = main.AddImagePart(ImagePartType.Png);
        using (var s = new MemoryStream(png)) part.FeedData(s);
        string id = main.GetIdOfPart(part);
        long cx = (long)(widthCm * 360000), cy = (long)(heightCm * 360000);
        var inline = new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.DocProperties { Id = 1U, Name = "Эскиз сечения" },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(new PIC.Picture(
                new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = 0U, Name = "section.png" }, new PIC.NonVisualPictureDrawingProperties()),
                new PIC.BlipFill(new A.Blip { Embed = id }, new A.Stretch(new A.FillRectangle())),
                new PIC.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }),
                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U };
        return new Paragraph(new ParagraphProperties(new SpacingBetweenLines { Before = "160" }, new Justification { Val = JustificationValues.Center }),
            new Run(new Drawing(inline)));
    }

    static SectionProperties SectionProps() => new(
        new PageSize { Width = 11906U, Height = 16838U },
        new PageMargin { Top = 1134, Bottom = 1134, Left = 1418U, Right = 850U, Header = 709U, Footer = 709U });
}
