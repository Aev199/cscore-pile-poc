using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SectionCalc;

namespace SectionApp.ViewModels;

public partial class MainViewModel : ObservableObject
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    static readonly HashSet<string> NonInputs =
    [
        nameof(Sketch), nameof(IsBusy), nameof(StatusText), nameof(Progress), nameof(ShowProgress), nameof(HasResult),
        nameof(ResultIsStale), nameof(VerdictText), nameof(VerdictTone), nameof(GoverningText), nameof(InputErrors),
        nameof(HasInputErrors), nameof(SelectionSummary), nameof(HasSelection), nameof(SteelText), nameof(SelectedTab),
        nameof(VerdictOk), nameof(VerdictBad), nameof(VerdictWarn), nameof(VerdictInfo), nameof(IsWall), nameof(KindTitle), nameof(EtaByCscore),
    ];

    CancellationTokenSource? cancel;
    CheckResult? lastResult;
    SelectionResult? lastSelection;
    bool loading;

    public MainViewModel() => Load(SectionProject.Example(SectionKind.Pile));

    /// <summary>Set by the window: file dialogs need the top-level storage provider.</summary>
    public Func<IStorageProvider?>? Storage { get; set; }

    public IReadOnlyList<string> ConcreteClasses { get; } = MaterialTables.Concretes.Select(c => c.Name).ToList();
    public IReadOnlyList<string> RebarClasses { get; } = MaterialTables.Rebars.Select(r => r.Name).ToList();
    public IReadOnlyList<string> SecondOrderOptions { get; } =
        ["Рассчитать η в CScore", "Уже учтено в усилиях", "Не учитывать (только сечение)"];

    // ---- section ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWall), nameof(KindTitle))] bool isPile = true;
    public bool IsWall => !IsPile;
    public string KindTitle => IsPile ? "Буронабивная свая" : "Стена в грунте";
    [ObservableProperty] string title = "";
    [ObservableProperty] string pileDiameter = "", pileCover = "", pileSpiral = "", pileBarCount = "", pileBarDiameter = "";
    [ObservableProperty] string wallWidth = "", wallThickness = "", wallCover = "", wallStirrup = "";
    [ObservableProperty] string positiveCount = "", positiveDiameter = "", negativeCount = "", negativeDiameter = "";
    [ObservableProperty] string minClearSpacing = "";

    // ---- materials ----
    [ObservableProperty] string concreteClass = "B30", rebarClass = "A500", transverseClass = "A240";
    [ObservableProperty] bool sustainedFactor = true;

    // ---- conditions ----
    [ObservableProperty] bool eccentricitySp63 = true;
    [ObservableProperty] string memberLength = "";
    [ObservableProperty] bool staticallyDeterminate;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EtaByCscore))] int secondOrderIndex;
    public bool EtaByCscore => SecondOrderIndex == 0;
    [ObservableProperty] string l0x = "", l0y = "", sustainedShare = "";
    [ObservableProperty] string crackLimitLong = "", crackLimitShort = "";
    [ObservableProperty] bool shearCheck;
    [ObservableProperty] string shearPitch = "", stirrupLegs = "", pileShearA = "";
    [ObservableProperty] bool shearConfirmed;
    [ObservableProperty] bool inclinedMoment;
    [ObservableProperty] string anchorageFactor = "", supportDown = "", supportUp = "";

    // ---- selection ----
    [ObservableProperty] string selectionCountMin = "", selectionCountMax = "", selectionCountStep = "";
    [ObservableProperty] bool selectionSymmetric = true;
    [ObservableProperty] string selectionDiameters = "";

    public ObservableCollection<LoadRowViewModel> Loads { get; } = [];
    public ObservableCollection<CheckRowViewModel> SummaryRows { get; } = [];
    public ObservableCollection<CheckRowViewModel> DetailRows { get; } = [];
    public ObservableCollection<SelectionRowViewModel> SelectionRows { get; } = [];

    // ---- state ----
    [ObservableProperty] Bitmap? sketch;
    [ObservableProperty] string steelText = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(SelectCommand), nameof(ExportReportCommand))]
    bool isBusy;
    [ObservableProperty] string statusText = "Готово";
    [ObservableProperty] double progress;
    [ObservableProperty] bool showProgress;
    [ObservableProperty] bool hasResult;
    [ObservableProperty] bool resultIsStale;
    [ObservableProperty] string verdictText = "Расчёт ещё не выполнялся";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VerdictOk), nameof(VerdictBad), nameof(VerdictWarn), nameof(VerdictInfo))]
    string verdictTone = "info";
    public bool VerdictOk => VerdictTone == "ok";
    public bool VerdictBad => VerdictTone == "bad";
    public bool VerdictWarn => VerdictTone == "warn";
    public bool VerdictInfo => VerdictTone == "info";
    [ObservableProperty] string governingText = "Заполните данные и нажмите «Проверить».";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasInputErrors))] string inputErrors = "";
    public bool HasInputErrors => InputErrors.Length > 0;
    [ObservableProperty] string selectionSummary = "";
    [ObservableProperty] bool hasSelection;
    [ObservableProperty] int selectedTab;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!loading && e.PropertyName is { } name && !NonInputs.Contains(name)) InputsChanged();
    }

    internal void InputsChanged()
    {
        if (loading) return;
        if (HasResult) ResultIsStale = true;
        RefreshSketch();
    }

    void RefreshSketch()
    {
        var p = TryBuild(out var errors, quiet: true);
        InputErrors = string.Join("\n", errors);
        if (p is null) return;
        var layout = RequestBuilder.Layout(p);
        SteelText = $"As = {layout.SteelAreaMm2.ToString("0", Ru)} мм²" + (layout.Problem is string problem ? $" · {problem}" : "");
        using var stream = new MemoryStream(SectionSketch.Png(p, 600, 500, 26));
        Sketch = new Bitmap(stream);
    }

    // ---- project <-> fields ----

    internal void Load(SectionProject p)
    {
        loading = true;
        try
        {
            IsPile = p.Kind == SectionKind.Pile; Title = p.Title;
            PileDiameter = S(p.Pile.DiameterMm); PileCover = S(p.Pile.CoverMm); PileSpiral = S(p.Pile.SpiralDiameterMm);
            PileBarCount = S(p.Pile.BarCount); PileBarDiameter = S(p.Pile.BarDiameterMm);
            WallWidth = S(p.Wall.WidthMm); WallThickness = S(p.Wall.ThicknessMm); WallCover = S(p.Wall.CoverMm); WallStirrup = S(p.Wall.StirrupDiameterMm);
            PositiveCount = S(p.Wall.PositiveFace.BarCount); PositiveDiameter = S(p.Wall.PositiveFace.BarDiameterMm);
            NegativeCount = S(p.Wall.NegativeFace.BarCount); NegativeDiameter = S(p.Wall.NegativeFace.BarDiameterMm);
            MinClearSpacing = S(IsPile ? p.Pile.MinClearSpacingMm : p.Wall.MinClearSpacingMm);
            ConcreteClass = p.ConcreteClass; RebarClass = p.RebarClass; TransverseClass = p.TransverseClass; SustainedFactor = p.SustainedLoadFactor;
            var c = p.Conditions;
            EccentricitySp63 = c.Eccentricity == EccentricityMode.Sp63; MemberLength = S(c.MemberLengthM); StaticallyDeterminate = c.StaticallyDeterminate;
            SecondOrderIndex = c.SecondOrder switch { SecondOrderMode.Cscore => 0, SecondOrderMode.InLoads => 1, _ => 2 };
            L0x = S(c.L0xM); L0y = S(c.L0yM); SustainedShare = S(c.SustainedShare);
            CrackLimitLong = S(c.CrackLimitLongMm); CrackLimitShort = S(c.CrackLimitShortMm);
            ShearCheck = c.ShearCheck; ShearPitch = S(c.ShearPitchMm); StirrupLegs = S(c.StirrupLegs); PileShearA = S(c.PileShearAMm);
            ShearConfirmed = c.ShearModelConfirmed; InclinedMoment = c.InclinedMoment;
            AnchorageFactor = S(c.AnchorageFactor); SupportDown = S(c.SupportDistanceDownM); SupportUp = S(c.SupportDistanceUpM);
            var s = p.Selection;
            SelectionCountMin = S(IsPile ? s.PileCountMin : s.WallCountMin); SelectionCountMax = S(IsPile ? s.PileCountMax : s.WallCountMax);
            SelectionCountStep = S(s.PileCountStep); SelectionSymmetric = s.WallSymmetric;
            SelectionDiameters = string.Join(", ", s.Diameters.Select(d => S(d)));
            Loads.Clear();
            foreach (var l in p.Loads) Loads.Add(LoadRowViewModel.From(l, this));
        }
        finally { loading = false; }
        ClearResults();
        RefreshSketch();
    }

    /// <summary>Project from the fields; null with readable messages when a value is not a number.</summary>
    internal SectionProject? TryBuild(out List<string> errors, bool quiet = false)
    {
        var e = new List<string>();
        double D(string text, string label)
        {
            if (double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)) return v;
            e.Add($"«{label}»: введите число"); return 0;
        }
        int I(string text, string label)
        {
            if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
            e.Add($"«{label}»: введите целое число"); return 0;
        }
        var p = new SectionProject
        {
            Kind = IsPile ? SectionKind.Pile : SectionKind.Wall, Title = string.IsNullOrWhiteSpace(Title) ? KindTitle : Title.Trim(),
            ConcreteClass = ConcreteClass, RebarClass = RebarClass, TransverseClass = TransverseClass, SustainedLoadFactor = SustainedFactor,
        };
        double clear = D(MinClearSpacing, "Мин. просвет");
        if (IsPile)
            p.Pile = new PileGeometry
            {
                DiameterMm = D(PileDiameter, "Диаметр D"), CoverMm = D(PileCover, "Защитный слой"), SpiralDiameterMm = D(PileSpiral, "Ø навивки"),
                BarCount = I(PileBarCount, "Число стержней"), BarDiameterMm = D(PileBarDiameter, "Ø стержней"), MinClearSpacingMm = clear,
            };
        else
            p.Wall = new WallGeometry
            {
                WidthMm = D(WallWidth, "Ширина b"), ThicknessMm = D(WallThickness, "Толщина h"), CoverMm = D(WallCover, "Защитный слой"),
                StirrupDiameterMm = D(WallStirrup, "Ø хомута"), MinClearSpacingMm = clear,
                PositiveFace = new WallFace { BarCount = I(PositiveCount, "Стержней на грани «+»"), BarDiameterMm = D(PositiveDiameter, "Ø на грани «+»") },
                NegativeFace = new WallFace { BarCount = I(NegativeCount, "Стержней на грани «−»"), BarDiameterMm = D(NegativeDiameter, "Ø на грани «−»") },
            };
        var c = p.Conditions;
        c.Eccentricity = EccentricitySp63 ? EccentricityMode.Sp63 : EccentricityMode.InLoads;
        c.MemberLengthM = EccentricitySp63 ? D(MemberLength, "Длина элемента") : 1;
        c.StaticallyDeterminate = StaticallyDeterminate;
        c.SecondOrder = SecondOrderIndex switch { 0 => SecondOrderMode.Cscore, 1 => SecondOrderMode.InLoads, _ => SecondOrderMode.SectionOnly };
        if (EtaByCscore) { c.L0xM = D(L0x, "l₀x"); c.L0yM = D(L0y, "l₀y"); c.SustainedShare = D(SustainedShare, "Доля длительной нагрузки"); }
        c.CrackLimitLongMm = D(CrackLimitLong, "Предел продолжительного раскрытия");
        c.CrackLimitShortMm = D(CrackLimitShort, "Предел непродолжительного раскрытия");
        c.ShearCheck = ShearCheck; c.ShearModelConfirmed = ShearConfirmed; c.InclinedMoment = ShearCheck && InclinedMoment;
        if (ShearCheck)
        {
            c.ShearPitchMm = D(ShearPitch, "Шаг поперечной арматуры");
            if (IsPile) c.PileShearAMm = D(PileShearA, "a для приведённого сечения"); else c.StirrupLegs = I(StirrupLegs, "Рабочих ветвей");
            if (c.InclinedMoment)
            {
                c.AnchorageFactor = D(AnchorageFactor, "Коэффициент k"); c.SupportDistanceDownM = D(SupportDown, "До опоры вниз");
                c.SupportDistanceUpM = D(SupportUp, "До опоры вверх");
            }
        }
        var s = p.Selection;
        if (IsPile) { s.PileCountMin = I(SelectionCountMin, "Подбор: от"); s.PileCountMax = I(SelectionCountMax, "Подбор: до"); s.PileCountStep = I(SelectionCountStep, "Подбор: шаг"); }
        else { s.WallCountMin = I(SelectionCountMin, "Подбор: от"); s.WallCountMax = I(SelectionCountMax, "Подбор: до"); s.WallSymmetric = SelectionSymmetric; }
        s.Diameters = SelectionDiameters.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => D(t, "Подбор: диаметры")).ToList();
        foreach (var row in Loads) p.Loads.Add(row.ToModel(e));
        errors = e.Distinct().ToList();
        if (errors.Count > 0) return null;
        errors.AddRange(RequestBuilder.Validate(p));
        return quiet || errors.Count == 0 ? p : null;
    }

    static string S(double v) => v.ToString("0.###", Ru);

    // ---- commands ----

    [RelayCommand] void SetPile() => SwitchKind(true);
    [RelayCommand] void SetWall() => SwitchKind(false);

    void SwitchKind(bool pile)
    {
        if (IsPile == pile) return;
        var current = TryBuild(out _, quiet: true);
        var example = SectionProject.Example(pile ? SectionKind.Pile : SectionKind.Wall);
        if (current is not null) { example.Loads = current.Loads; example.Conditions = current.Conditions; example.ConcreteClass = current.ConcreteClass; example.RebarClass = current.RebarClass; }
        Load(example);
    }

    [RelayCommand] void AddUls() => AddLoad(LoadBasis.Uls);
    [RelayCommand] void AddSls() => AddLoad(LoadBasis.Sls);

    void AddLoad(LoadBasis basis)
    {
        int n = Loads.Count(l => l.IsSls == (basis == LoadBasis.Sls)) + 1;
        Loads.Add(LoadRowViewModel.From(new LoadRow { Basis = basis, Name = (basis == LoadBasis.Uls ? "Расчётное " : "Нормативное ") + n }, this));
        InputsChanged();
    }

    internal void RemoveLoad(LoadRowViewModel row) { Loads.Remove(row); InputsChanged(); }

    bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task Check()
    {
        var p = TryBuild(out var errors);
        if (p is null) { ShowErrors(errors); return; }
        await Run("Расчёт…", token => Engine.Check(p, token), Show);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task Select()
    {
        // The adopted bars are replaced by the candidates; Selection.Run validates everything else.
        var p = TryBuild(out var errors, quiet: true);
        if (p is null) { ShowErrors(errors); return; }
        var progress = new Progress<(int Done, int Total, string Label)>(x =>
        {
            Progress = x.Total == 0 ? 0 : 100.0 * x.Done / x.Total;
            StatusText = x.Label.Length > 0 ? $"Подбор: {x.Label} ({x.Done + 1} из {x.Total} в очереди)" : "Подбор завершён";
        });
        await Run("Подбор армирования…", token => Selection.Run(p, progress, token), ShowSelection);
    }

    [RelayCommand] void Cancel() => cancel?.Cancel();

    async Task Run<T>(string message, Func<CancellationToken, T> work, Action<T> done)
    {
        cancel = new CancellationTokenSource();
        IsBusy = true; ShowProgress = true; Progress = 0; StatusText = message;
        try
        {
            var token = cancel.Token;
            var result = await Task.Run(() => work(token), token);
            done(result);
        }
        catch (OperationCanceledException) { StatusText = "Остановлено"; }
        catch (Exception ex) { StatusText = "Ошибка расчёта"; ShowErrors([ex.Message]); }
        finally { IsBusy = false; ShowProgress = false; cancel.Dispose(); cancel = null; }
    }

    void ShowErrors(IEnumerable<string> errors)
    {
        InputErrors = string.Join("\n", errors);
        StatusText = "Проверьте исходные данные";
    }

    internal void Show(CheckResult r)
    {
        lastResult = r;
        HasResult = true; ResultIsStale = false; InputErrors = "";
        VerdictText = ReportWriter.VerdictText(r.Verdict);
        VerdictTone = r.Verdict switch { Verdict.Passed => "ok", Verdict.Failed => "bad", _ => "warn" };
        GoverningText = r.Governing is CheckRow g
            ? $"Определяющая: {g.Title} · {g.Combination} · K = {ReportWriter.Utilization(g.Utilization)}"
            : "Коэффициенты не получены";
        SummaryRows.Clear();
        foreach (var row in r.Summary) SummaryRows.Add(CheckRowViewModel.From(row, r));
        DetailRows.Clear();
        foreach (var row in r.Rows.Where(x => x.Status != "not_evaluated" && x.CheckId != "crack_formation")) DetailRows.Add(CheckRowViewModel.From(row, r));
        StatusText = $"Расчёт выполнен · {DateTime.Now:HH:mm}";
    }

    void ShowSelection(SelectionResult s)
    {
        lastSelection = s;
        SelectionRows.Clear();
        foreach (var o in s.Outcomes) SelectionRows.Add(new SelectionRowViewModel(o, o == s.Best, this));
        HasSelection = true; SelectedTab = 1;
        SelectionSummary = s.Best is SelectionOutcome best
            ? $"Самый лёгкий проходящий вариант: {best.Candidate.Description}, As = {best.Candidate.SteelAreaMm2.ToString("0", Ru)} мм². Нажмите «Принять», чтобы перенести его в сечение."
            : "Ни один вариант из заданного диапазона не прошёл все проверки. Расширьте диапазон числа или диаметров стержней.";
        if (s.GeometricallyRejected > 0) SelectionSummary += $" Не поместились в сечении: {s.GeometricallyRejected}.";
        StatusText = "Подбор завершён";
    }

    internal async Task Apply(SelectionOutcome outcome)
    {
        var keepSelection = lastSelection;
        Load(outcome.Candidate.Project);
        lastSelection = keepSelection;
        foreach (var row in SelectionRows) row.Refresh();
        await Check();
        SelectedTab = 0;
    }

    void ClearResults()
    {
        lastResult = null; lastSelection = null; HasResult = false; ResultIsStale = false; HasSelection = false;
        SummaryRows.Clear(); DetailRows.Clear(); SelectionRows.Clear();
        VerdictText = "Расчёт ещё не выполнялся"; VerdictTone = "info"; SelectionSummary = "";
        GoverningText = "Заполните данные и нажмите «Проверить».";
    }

    // ---- files ----

    static readonly FilePickerFileType ProjectType = new("Проект сечения") { Patterns = ["*.section.json", "*.json"] };
    static readonly FilePickerFileType WordType = new("Документ Word") { Patterns = ["*.docx"] };

    [RelayCommand] void New() => Load(SectionProject.Example(IsPile ? SectionKind.Pile : SectionKind.Wall));

    [RelayCommand]
    async Task Open()
    {
        if (Storage?.Invoke() is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Открыть проект", FileTypeFilter = [ProjectType] });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            Load(ProjectStore.Deserialize(await reader.ReadToEndAsync()));
            StatusText = $"Открыт {files[0].Name}";
        }
        catch (Exception ex) { ShowErrors([$"Не удалось открыть файл: {ex.Message}"]); }
    }

    [RelayCommand]
    async Task Save()
    {
        var p = TryBuild(out var errors, quiet: true);
        if (p is null) { ShowErrors(errors); return; }
        if (Storage?.Invoke() is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить проект", SuggestedFileName = Safe(p.Title) + ".section.json", DefaultExtension = "json", FileTypeChoices = [ProjectType],
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(ProjectStore.Serialize(p));
        StatusText = $"Сохранён {file.Name}";
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task ExportReport()
    {
        if (lastResult is null || ResultIsStale) await Check();
        if (lastResult is null || ResultIsStale) return;
        if (Storage?.Invoke() is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить отчёт", SuggestedFileName = "Отчёт — " + Safe(lastResult.Project.Title) + ".docx", DefaultExtension = "docx", FileTypeChoices = [WordType],
        });
        if (file is null) return;
        try
        {
            string temp = Path.Combine(Path.GetTempPath(), $"section-{Guid.NewGuid():N}.docx");
            ReportWriter.Write(temp, lastResult, SelectionMatches(lastResult) ? lastSelection : null);
            await using (var target = await file.OpenWriteAsync())
            await using (var source = File.OpenRead(temp))
                await source.CopyToAsync(target);
            File.Delete(temp);
            StatusText = $"Отчёт сохранён: {file.Name}";
        }
        catch (Exception ex) { ShowErrors([$"Не удалось сохранить отчёт: {ex.Message}"]); }
    }

    /// <summary>The selection table belongs in the report only when the checked section is one of its candidates.</summary>
    bool SelectionMatches(CheckResult r) => lastSelection?.Outcomes.Any(o => Fingerprint(o.Candidate.Project) == Fingerprint(r.Project)) == true;

    static string Fingerprint(SectionProject p) => System.Text.Json.JsonSerializer.Serialize(new
    {
        p.Kind, Geometry = p.Kind == SectionKind.Pile ? (object)p.Pile : p.Wall, p.ConcreteClass, p.RebarClass, p.TransverseClass,
        p.SustainedLoadFactor, p.Conditions, p.Loads,
    });

    static string Safe(string name) => string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}
