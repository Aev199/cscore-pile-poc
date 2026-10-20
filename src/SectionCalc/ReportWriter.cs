using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace SectionCalc;

/// <summary>Short, readable Word report of one check (and an optional selection).</summary>
public static class ReportWriter
{
    const string Accent = "216897", Text = "263238", Muted = "616161", Header = "EDF1F2", Border = "C8D1D5";
    const string OkFill = "EAF3EB", OkText = "337C3B", BadFill = "FFEBEE", BadText = "D32F2F", WarnFill = "FFF4E5", WarnText = "8F5000";
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static void Write(string path, CheckResult result, SelectionResult? selection = null, DateTime? date = null)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new Document(new Body());
        AddStyles(main);
        var body = main.Document.Body!;
        var p = result.Project;

        body.Append(Para(Title(p), "Title"));
        body.Append(Para($"{KindText(p)} · бетон {p.ConcreteClass} · арматура {p.RebarClass} · {(date ?? DateTime.Now).ToString("dd.MM.yyyy", Ru)}", "Subtitle"));
        body.Append(Verdict(result));

        body.Append(Para("1. Исходные данные", "Heading1"));
        body.Append(KeyValueTable(Inputs(result)));
        body.Append(Image(main, SectionSketch.Png(p, 900, 640, 22), 12.0, 8.53));
        body.Append(Para("Эскиз сечения: точки — продольная арматура, пунктир — ось поперечной арматуры. Положительный Mx растягивает верхнюю грань (y > 0).", "Note"));

        body.Append(Para("2. Усилия", "Heading1"));
        body.Append(Para("Расчётные усилия; знак N: сжатие — минус. Нормативные = расчётные / γf, длительные = kдл × нормативные (для трещин и η).", "Note"));
        body.Append(LoadsTable(p));

        body.Append(Para("3. Результаты проверок", "Heading1"));
        body.Append(Para("Коэффициент использования — отношение усилия (ширины раскрытия) к предельному; проверка выполнена при K ≤ 1. Показано определяющее сочетание.", "Note"));
        body.Append(SummaryTable(result));

        body.Append(Para("4. Проверки по каждому сочетанию", "Heading1"));
        body.Append(DetailTable(result));

        if (selection is not null && selection.Outcomes.Count > 0)
        {
            body.Append(Para("5. Подбор армирования", "Heading1"));
            body.Append(Para($"Варианты проверены в порядке возрастания площади арматуры. Отброшено по геометрии: {selection.GeometricallyRejected}; не проверялось после найденного решения: {selection.NotChecked}.", "Note"));
            body.Append(SelectionTable(selection));
        }

        body.Append(Para(selection is not null && selection.Outcomes.Count > 0 ? "6. Примечания" : "5. Примечания", "Heading1"));
        foreach (var note in result.Notes.Concat(StandardNotes(p)))
            body.Append(Para("• " + note, "Normal"));
        body.Append(SectionProps());
        main.Document.Save();
    }

    static string Title(SectionProject p) => $"Расчёт сечения: {p.Title}";

    static string KindText(SectionProject p) => p.Kind == SectionKind.Pile
        ? $"БНС Ø{p.Pile.DiameterMm:0}" : $"СВГ {p.Wall.WidthMm:0}×{p.Wall.ThicknessMm:0}";

    static string F(double v, string format = "0.###") => v.ToString(format, Ru);

    public static string Utilization(double? u) => u is double d ? d.ToString("0.000", Ru) : "—";

    public static string VerdictText(Verdict v) => v switch
    {
        SectionCalc.Verdict.Passed => "Все выполненные проверки удовлетворяются",
        SectionCalc.Verdict.Failed => "Сечение не удовлетворяет требованиям",
        SectionCalc.Verdict.Review => "Результат на границе — требуется проверка инженером",
        _ => "Расчёт неполный",
    };

    static OpenXmlElement Verdict(CheckResult r)
    {
        var (fill, color) = Colors(r.Verdict);
        string line = r.Governing is CheckRow g ? $"Определяющая проверка: {g.Title}, сочетание «{g.Combination}», K = {Utilization(g.Utilization)}." : "";
        var failed = r.Summary.Where(x => x.IsFailure).Select(x => x.Title).ToList();
        if (failed.Count > 0) line += " Не выполнено: " + string.Join("; ", failed) + ".";
        var table = new Table(new TableProperties(new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
            new TableBorders(new LeftBorder { Val = BorderValues.Single, Size = 18, Color = color })));
        var cell = new TableCell(new TableCellProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = fill },
            new TableCellMargin(new TopMargin { Width = "120", Type = TableWidthUnitValues.Dxa }, new LeftMargin { Width = "160", Type = TableWidthUnitValues.Dxa },
                new BottomMargin { Width = "120", Type = TableWidthUnitValues.Dxa })));
        cell.Append(new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "40" }),
            Run(VerdictText(r.Verdict), bold: true, color: color, size: 24)));
        cell.Append(new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "0" }), Run(line.Trim(), color: Text)));
        table.Append(new TableGrid(new GridColumn { Width = "9600" }));
        table.Append(new TableRow(cell));
        return table;
    }

    static List<(string, string)> Inputs(CheckResult r)
    {
        var p = r.Project; var c = p.Conditions; var f = p.Concrete;
        var concrete = MaterialTables.Concrete(p.ConcreteClass); var rebar = MaterialTables.Rebar(p.RebarClass);
        var rows = new List<(string, string)>();
        if (p.Kind == SectionKind.Pile)
        {
            var g = p.Pile;
            rows.Add(("Сечение", $"круглое, D = {F(g.DiameterMm)} мм"));
            rows.Add(("Продольная арматура", $"{g.BarCount} Ø{F(g.BarDiameterMm)} {p.RebarClass}, As = {F(r.Layout.SteelAreaMm2, "0")} мм² (μ = {F(r.Layout.SteelAreaMm2 / (Math.PI * g.DiameterMm * g.DiameterMm / 4) * 100, "0.00")} %)"));
            rows.Add(("Защитный слой до навивки / Ø навивки", $"{F(g.CoverMm)} мм / Ø{F(g.SpiralDiameterMm)} {p.TransverseClass}"));
        }
        else
        {
            var g = p.Wall;
            rows.Add(("Сечение", $"прямоугольное, b × h = {F(g.WidthMm)} × {F(g.ThicknessMm)} мм"));
            rows.Add(("Арматура грани «+»", $"{g.PositiveFace.BarCount} Ø{F(g.PositiveFace.BarDiameterMm)} {p.RebarClass}"));
            rows.Add(("Арматура грани «−»", $"{g.NegativeFace.BarCount} Ø{F(g.NegativeFace.BarDiameterMm)} {p.RebarClass}"));
            rows.Add(("Площадь арматуры", $"As = {F(r.Layout.SteelAreaMm2, "0")} мм²"));
            rows.Add(("Защитный слой до хомута / Ø хомута", $"{F(g.CoverMm)} мм / Ø{F(g.StirrupDiameterMm)} {p.TransverseClass}"));
        }
        rows.Add(("Бетон (тяжёлый)", $"{p.ConcreteClass}: Rb = {F(concrete.Rb)} МПа, Rbt = {F(concrete.Rbt)} МПа, Rb,ser = {F(concrete.RbSer)} МПа, Rbt,ser = {F(concrete.RbtSer)} МПа, Eb = {F(concrete.Eb, "0")} МПа"));
        rows.Add(("Коэффициенты условий работы бетона", $"γb1 = {F(f.Gb1)} (кратковременные сочетания — 1), γb2 = {F(f.Gb2)}, γb3 = {F(f.Gb3)}, γb5 = {F(f.Gb5)}; к Eb — коэффициент упрочнения {F(f.HardeningFactor)}"));
        rows.Add(("Продольная арматура", $"{p.RebarClass}: Rs = {F(rebar.Rs)} МПа, Rs,ser = {F(rebar.RsSer)} МПа, Es = {F(rebar.Es, "0")} МПа; γs = {F(p.LongitudinalFactor)}"));
        rows.Add(("Поперечная арматура", $"{p.TransverseClass}: Rsw = {F(MaterialTables.Rebar(p.TransverseClass).Rsw)} МПа; γs = {F(p.TransverseFactor)}"));
        var kinds = p.Loads.Select(CombinationKind.Of).Distinct().ToList();
        foreach (var kind in kinds)
        {
            var m = MaterialTables.Uls(p, kind);
            // Seismic: Rsw works only in inclined sections, which take their own m_tr.
            double rsw = kind.Seismic ? MaterialTables.Uls(p, kind, inclined: true).Rsw : m.Rsw;
            rows.Add(($"Расчётные сопротивления: {m.Label}", $"Rb = {F(m.Rb, "0.##")} МПа, Rbt = {F(m.Rbt, "0.###")} МПа, Rs = {F(m.Rs, "0.#")} МПа, Rsw = {F(rsw, "0.#")} МПа"));
        }
        rows.Add(("Коэффициент надёжности γn", $"по прочности {F(c.ReliabilityUls)}, по трещиностойкости {F(c.ReliabilitySls)}"));
        rows.Add(("Элемент", $"L = {F(c.ElementLengthM)} м; μ в плоскости Mx {F(c.MuX)}, в плоскости My {F(c.MuY)}: l₀x = {F(c.L0xM)} м, l₀y = {F(c.L0yM)} м; предельная гибкость {F(c.SlendernessLimit)}"));
        rows.Add(("Случайный эксцентриситет", c.Eccentricity == EccentricityMode.Sp63
            ? $"{(c.EaXMm > 0 || c.EaYMm > 0 ? "задан" : "по СП 63")}, схема {(c.StaticallyDeterminate ? "статически определимая" : "статически неопределимая")}; ea,x = {F(r.EaXMm ?? 0, "0.#")} мм, ea,y = {F(r.EaYMm ?? 0, "0.#")} мм"
            : "включён в заданные моменты"));
        rows.Add(("Влияние прогиба (η)", c.SecondOrder switch
        {
            SecondOrderMode.Cscore => "рассчитывается CScore по l₀ и доле длительной нагрузки kдл каждого сочетания",
            SecondOrderMode.InLoads => "учтено в заданных усилиях",
            _ => "не учитывается (проверка сечения)",
        }));
        rows.Add(("Трещиностойкость", c.CrackCategory == CrackCategory.NoCracks ? "трещины не допускаются"
            : $"требование: {(c.PermeabilityLimits ? "ограничение проницаемости" : "сохранность арматуры")}; предельная ширина: продолжительное {F(c.CrackLimitLongMm)} мм, непродолжительное {F(c.CrackLimitShortMm)} мм"));
        rows.Add(("Поперечная сила", !c.ShearCheck ? "не проверяется"
            : p.Kind == SectionKind.Pile ? $"навивка шагом {F(c.ShearPitchMm)} мм, приведённое сечение b = 0,885D, h0 = b − a, a = {F(c.PileShearAMm)} мм; виток — две ветви"
            : $"хомуты Ø{F(p.Wall.StirrupDiameterMm)} шагом {F(c.ShearPitchMm)} мм, рабочих ветвей {c.StirrupLegs}"));
        if (c.ShearCheck && c.InclinedMoment)
            rows.Add(("Момент в наклонном сечении", $"k = {F(c.AnchorageFactor)}, до опор {F(c.SupportDistanceDownM)} м вниз и {F(c.SupportDistanceUpM)} м вверх"));
        if (p.Loads.Any(l => l.Seismic))
            rows.Add(("Сейсмика (СП 14.13330)", $"m_tr: нормальные сечения {F(p.Seismic.NormalSectionFactor)}, наклонные {F(p.Seismic.InclinedSectionFactor)}; трещины {(p.Seismic.CheckCracks ? "проверяются" : "не проверяются")}"));
        if (p.Loads.Any(l => l.Special && !l.Seismic))
        {
            var sp = p.Special;
            rows.Add(("Особые сочетания (СП 296.1325800)", $"{(sp.NormativeProperties ? "нормативные" : "расчётные")} характеристики; γb = {F(sp.ConcreteFactor)}, γs = {F(sp.LongitudinalFactor)}, γsw = {F(sp.TransverseFactor)}, дополнительный понижающий {F(sp.AdditionalFactor)}"));
        }
        return rows;
    }

    static string Flags(LoadRow l)
    {
        var tags = new List<string>();
        if (l.ShortTerm) tags.Add("кратковр.");
        if (l.Seismic) tags.Add("сейсмика");
        if (l.Special && !l.Seismic) tags.Add("особое");
        return tags.Count > 0 ? string.Join(", ", tags) : "основное";
    }

    static Table LoadsTable(SectionProject p)
    {
        var t = NewTable(["Сочетание", "N, кН", "Mx, кН·м", "My, кН·м", "Q, кН", "γf", "kдл", "Вид"], [16, 11, 11, 11, 10, 8, 8, 25]);
        foreach (var l in p.Loads)
            t.Append(Row([l.Name, F(l.N, "0.#"), F(l.Mx, "0.#"), F(l.My, "0.#"), F(l.Q, "0.#"), F(l.LoadFactor, "0.###"), F(l.SustainedShare, "0.###"), Flags(l)]));
        return t;
    }

    static Table SummaryTable(CheckResult r)
    {
        var t = NewTable(["Проверка", "Определяющее сочетание", "K", "Результат"], [40, 30, 10, 20]);
        foreach (var row in r.Summary)
            t.Append(Row([row.Title, row.Combination, UtilizationText(row, r), StatusText(row, r)], statusColumn: 3, row: row));
        return t;
    }

    static Table DetailTable(CheckResult r)
    {
        var t = NewTable(["Проверка", "Сочетание", "K", "Результат"], [40, 30, 10, 20]);
        foreach (var row in r.Rows.Where(x => x.Status != "not_evaluated" && x.CheckId != "crack_formation"))
            t.Append(Row([row.Title, row.Combination, UtilizationText(row, r), StatusText(row, r)], statusColumn: 3, row: row));
        return t;
    }

    static Table SelectionTable(SelectionResult s)
    {
        var t = NewTable(["Вариант", "As, мм²", "Определяющая проверка", "K", "Результат"], [26, 11, 37, 9, 17]);
        foreach (var o in s.Outcomes)
        {
            string result = o.Error is not null ? "ошибка расчёта" : o == s.Best ? "принят (самый лёгкий)" : VerdictShort(o.Verdict);
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
        SectionCalc.Verdict.Review => "на границе", _ => "неполный расчёт",
    };

    /// <summary>K for display: a crack width of an uncracked section is shown as "—", not 0.</summary>
    public static string UtilizationText(CheckRow row, CheckResult result) =>
        row.CheckId.StartsWith("crack_width") && !Cracked(result, row.CaseId) ? "—" : Utilization(row.Utilization);

    public static string StatusText(CheckRow row, CheckResult? result = null) =>
        row.Status == "not_evaluated" ? "не выполнялась" + ReasonText(row.Reason)
        : row.CheckId.StartsWith("crack_width") && result is not null && !Cracked(result, row.CaseId) ? "трещины не образуются"
        : Engine.StatusText.GetValueOrDefault(row.Status, row.Status);

    static bool Cracked(CheckResult r, string caseId) => r.Rows.Any(x => x.CheckId == "crack_formation" && x.CaseId == caseId
        && x.Details is { } d && d.TryGetProperty("cracked", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.True);

    static string ReasonText(string reason) => reason switch
    {
        "" => "",
        var s when s.Contains("uls_fail") || s.Contains("member_strength_not_confirmed") => " (не прошла прочность)",
        var s when s.Contains("not_requested") || s.Contains("SLS_loads_missing") => " (не задана)",
        var s when s.Contains("anchorage") || s.Contains("detailing") => " (этап конструирования)",
        _ => "",
    };

    static IEnumerable<string> StandardNotes(SectionProject p)
    {
        yield return $"Прочность, η, трещины и Q вычислены ядром CScore (OpenCS {Engine.EngineCommit[..7]}, с исправлением φn по изменению № 2 к СП 63). Собственных формул сопротивления программа не содержит.";
        yield return "Ширина раскрытия трещин — консервативная оценка по нелинейной деформационной модели (σs,crc при моменте трещинообразования, разделение длительной и полной нагрузки по п. 8.2.6 СП 63). Усилия для трещин берутся без случайного эксцентриситета и η.";
        if (p.Kind == SectionKind.Pile && p.Conditions.ShearCheck)
            yield return "Q круглого сечения со спиралью проверен по принятой инженерной модели (приведённое сечение ВНИИГС 1984, виток — две ветви); это допущение, а не прямое указание СП 63.";
        yield return "Анкеровка, стыки, обрывы стержней и конструктивные требования не проверялись. Результат предварительный: перед выпуском рабочей документации требуется проверка инженером.";
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
            Style("Note", "Пояснение", "16", Muted, false));
    }

    static Style Style(string id, string name, string? size, string? color, bool bold, string? before = null, string? after = null)
    {
        var rpr = new StyleRunProperties();
        if (bold) rpr.Append(new Bold());
        if (color is not null) rpr.Append(new Color { Val = color });
        if (size is not null) rpr.Append(new FontSize { Val = size });
        var style = new Style(new StyleName { Val = name }, new PrimaryStyle()) { Type = StyleValues.Paragraph, StyleId = id };
        if (before is not null || after is not null)
        {
            var spacing = new SpacingBetweenLines();
            if (before is not null) spacing.Before = before;
            if (after is not null) spacing.After = after;
            style.Append(new StyleParagraphProperties(new KeepNext(), spacing));
        }
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
