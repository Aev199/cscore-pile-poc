using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SectionCalc;
using SectionCalc.Profile;

namespace SectionApp.ViewModels;

public partial class PlaxisObjectViewModel(PlaxisObject o) : ObservableObject
{
    public PlaxisObject Object { get; } = o;
    public string Label => $"{Object.Name} · {(Object.Kind == "Plate" ? "Plate" : "Embedded beam row")}";
    [ObservableProperty] bool selected;
}

public partial class PhaseRowViewModel : ObservableObject
{
    readonly Action changed;
    public PhaseRowViewModel(PhaseSetting s, string label, Action changed)
    {
        Setting = s; Label = label; this.changed = changed;
        include = s.Include;
        kindIndex = s.Seismic ? 2 : s.Special ? 3 : s.ShortTerm ? 1 : 0;
    }
    public PhaseSetting Setting { get; }
    public string Label { get; }
    public static IReadOnlyList<string> KindOptions { get; } = ["Основное", "Кратковременное", "Сейсмическое", "Особое"];
    public IReadOnlyList<string> Kinds => KindOptions;
    [ObservableProperty] bool include;
    [ObservableProperty] int kindIndex;
    partial void OnIncludeChanged(bool value) { Setting.Include = value; changed(); }
    partial void OnKindIndexChanged(int value)
    {
        Setting.ShortTerm = value == 1; Setting.Seismic = value == 2; Setting.Special = value == 3;
        changed();
    }
}

public sealed class StationRowViewModel(StationResult s, bool wall)
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    public string Level => s.Station.Level.ToString("0.00", Ru);
    public string Right => (s.AsRightMm2 / 100).ToString("0.00", Ru);
    public string Left => wall ? (s.AsLeftMm2 / 100).ToString("0.00", Ru) : "";
    public string Asw => s.AswCm2PerM is double a ? a.ToString("0.00", Ru) : "—";
    public string K => ReportWriter.Utilization(s.K);
    public string Status => s.Status switch
    {
        "ok" => "по расчёту", "minimum" => "минимум", "exceeded" => "не обеспечено", "indeterminate" => "не определено", _ => "нет усилий",
    };
    public string Tip => string.Join(". ", new[] { s.Governing, s.Combination, s.Note }.Where(x => !string.IsNullOrEmpty(x)));
    public bool IsBad => s.Status is "exceeded" or "indeterminate";
}

/// <summary>
/// Module «Эпюры (PLAXIS)»: forces of a whole pile or wall from PLAXIS 2D Output, РСУ per station, required
/// reinforcement along the element and its export for Revit. The section comes from the section module.
/// </summary>
public partial class ProfileViewModel : ObservableObject
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    readonly MainViewModel main;
    ProfileProject project = new();
    ForceProfile? forces;
    ReinforcementProfile? result;
    PlaxisCatalog? catalog;
    /// <summary>Section, settings and phases the shown result was calculated with.</summary>
    string? resultKey;
    CancellationTokenSource? cancel;
    bool loading;

    public ProfileViewModel(MainViewModel main)
    {
        this.main = main;
        var settings = AppSettings.Load();
        pythonPath = settings.PlaxisPython ?? PlaxisBridge.FindPython().FirstOrDefault() ?? "";
        port = settings.PlaxisPort.ToString(CultureInfo.InvariantCulture);
        LoadSettings();
    }

    public Func<IStorageProvider?>? Storage { get; set; }

    public static IReadOnlyList<string> ForceUnits { get; } = ["kN", "N", "MN", "tf"];
    public static IReadOnlyList<string> LengthUnits { get; } = ["m", "mm", "cm"];
    public IReadOnlyList<string> ForceUnitOptions => ForceUnits;
    public IReadOnlyList<string> LengthUnitOptions => LengthUnits;
    public IReadOnlyList<string> MomentSignOptions { get; } = ["правую грань (бóльший X)", "левую грань (меньший X)"];

    // ---- connection ----
    [ObservableProperty] string pythonPath;
    [ObservableProperty] string port;
    [ObservableProperty] string password = "";
    public ObservableCollection<PlaxisObjectViewModel> Objects { get; } = [];
    public ObservableCollection<PhaseRowViewModel> Phases { get; } = [];
    [ObservableProperty] string sourceText = "Усилия не загружены";
    [ObservableProperty] bool hasObjects;
    [ObservableProperty] bool hasPhases;
    public string RightHeader => IsWall ? "As прав." : "As";

    // ---- settings ----
    [ObservableProperty] string forceUnit = "kN", lengthUnit = "m";
    [ObservableProperty] string pileSpacing = "", stationStep = "", minPercent = "", serviceDivisor = "", sustainedShare = "";
    [ObservableProperty] int momentSignIndex;
    [ObservableProperty] bool confirmed;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWall), nameof(RightHeader))] bool isEmbedded;
    public bool IsWall => !IsEmbedded;
    [ObservableProperty] string sectionText = "";

    // ---- state ----
    [ObservableProperty] Bitmap? chart;
    public ObservableCollection<StationRowViewModel> Stations { get; } = [];
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(LoadForcesCommand), nameof(CalculateCommand), nameof(ExportRevitCommand))]
    bool isBusy;
    [ObservableProperty] double progress;
    [ObservableProperty] string status = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasErrors))] string errors = "";
    public bool HasErrors => Errors.Length > 0;
    [ObservableProperty] bool hasResult;
    [ObservableProperty] bool resultIsStale;
    [ObservableProperty] string summary = "";

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!loading && e.PropertyName is nameof(ForceUnit) or nameof(LengthUnit) or nameof(PileSpacing) or nameof(StationStep)
                or nameof(MinPercent) or nameof(ServiceDivisor) or nameof(SustainedShare) or nameof(MomentSignIndex) or nameof(Confirmed))
            Changed();
    }

    void LoadSettings()
    {
        loading = true;
        var s = project.Settings;
        ForceUnit = s.ForceUnit; LengthUnit = s.LengthUnit; PileSpacing = S(s.PileSpacingM); StationStep = S(s.StationStepM);
        MinPercent = S(s.MinPercent); ServiceDivisor = S(s.ServiceDivisor); SustainedShare = S(s.SustainedShare);
        MomentSignIndex = s.MomentSign > 0 ? 0 : 1; Confirmed = s.Confirmed;
        loading = false;
    }

    static string S(double v) => v.ToString("0.###", Ru);

    /// <summary>Settings from the fields into the project; messages for invalid numbers.</summary>
    bool ReadSettings(List<string> e)
    {
        var s = project.Settings;
        double D(string text, string label)
        {
            if (MainViewModel.TryNumber(text, out var v)) return v;
            e.Add($"«{label}»: введите число"); return 0;
        }
        s.ForceUnit = ForceUnit; s.LengthUnit = LengthUnit;
        s.PileSpacingM = D(PileSpacing, "Шаг свай"); s.StationStepM = D(StationStep, "Шаг сечений");
        s.MinPercent = D(MinPercent, "Минимальное армирование"); s.ServiceDivisor = D(ServiceDivisor, "γf для нормативных");
        s.SustainedShare = D(SustainedShare, "kдл");
        s.MomentSign = MomentSignIndex == 0 ? 1 : -1; s.Confirmed = Confirmed;
        if (s.StationStepM <= 0) e.Add("Шаг сечений должен быть больше 0");
        if (s.ServiceDivisor <= 0) e.Add("γf для нормативных усилий должен быть больше 0");
        if (s.MinPercent < 0) e.Add("Минимальное армирование не может быть отрицательным");
        return e.Count == 0;
    }

    void Changed()
    {
        if (loading) return;
        if (HasResult) ResultIsStale = true;
        RefreshForces();
    }

    /// <summary>Envelope chart of the loaded forces with the current settings.</summary>
    internal void RefreshForces()
    {
        var e = new List<string>();
        forces = null;
        if (project.Source is null || !ReadSettings(e)) { Errors = string.Join("\n", e); return; }
        if (main.TryBuild(out var se, quiet: true) is { } section) project.Section = section;
        else { Errors = "Сечение: " + string.Join("; ", se); return; }
        SectionText = SectionSummary(project.Section);
        if (result is not null && Key() != resultKey) ResultIsStale = true;
        try
        {
            forces = ForceProfile.Build(project);
            Errors = "";
            Draw();
        }
        catch (InvalidOperationException ex) { Errors = ex.Message; Chart = null; }
    }

    void Draw()
    {
        if (forces is null) return;
        using var stream = new MemoryStream(ProfileChart.Png(forces, ResultIsStale ? null : result, 1000, 1000, 16));
        Chart = new Bitmap(stream);
    }

    string Key() => ProjectStore.Serialize(project.Section) + JsonSerializer.Serialize(project.Settings) + JsonSerializer.Serialize(project.Phases);

    static string SectionSummary(SectionProject p) => p.Kind == SectionKind.Pile
        ? $"БНС Ø{p.Pile.DiameterMm:0}, {p.Pile.BarCount} стержней, {p.ConcreteClass}, {p.RebarClass}"
        : $"СВГ {p.Wall.WidthMm:0}×{p.Wall.ThicknessMm:0}, {p.Wall.NegativeFace.BarCount} + {p.Wall.PositiveFace.BarCount} стержней, {p.ConcreteClass}, {p.RebarClass}";

    bool CanRun() => !IsBusy;

    // ---- PLAXIS ----

    [RelayCommand]
    async Task BrowsePython()
    {
        if (Storage?.Invoke() is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "python.exe из каталога PLAXIS", FileTypeFilter = [new FilePickerFileType("python.exe") { Patterns = ["python.exe", "*.exe"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is string path) PythonPath = path;
    }

    int? PortNumber()
    {
        if (int.TryParse(Port.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) && p is > 0 and < 65536) return p;
        Errors = "Порт Output — целое число от 1 до 65535"; return null;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task Connect()
    {
        if (PortNumber() is not int port) return;
        await Busy("Подключение к PLAXIS Output…", async token =>
        {
            catalog = await PlaxisBridge.Catalog(PythonPath, port, Password, token);
            AppSettings.Save(new AppSettings { PlaxisPython = PythonPath, PlaxisPort = port });
            Objects.Clear();
            foreach (var o in catalog.Objects) Objects.Add(new PlaxisObjectViewModel(o));
            HasObjects = Objects.Count > 0;
            Status = $"PLAXIS {catalog.Info.Version} · {catalog.Info.Title} · элементов {catalog.Objects.Count}, фаз {catalog.Phases.Count}";
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task LoadForces()
    {
        if (catalog is null) { Errors = "Сначала подключитесь к PLAXIS Output"; return; }
        if (PortNumber() is not int port) return;
        var selected = Objects.Where(o => o.Selected).Select(o => o.Object.Id).ToList();
        if (selected.Count == 0) { Errors = "Выберите элементы одной вертикали"; return; }
        await Busy("Чтение результатов PLAXIS…", async token =>
        {
            var export = await PlaxisBridge.Extract(PythonPath, port, Password, selected, catalog.Phases.Select(p => p.Id).ToList(), token);
            Adopt(export);
        });
    }

    [RelayCommand]
    async Task OpenExport()
    {
        if (Storage?.Invoke() is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выгрузка PLAXIS или эпюра", FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            string json = await reader.ReadToEndAsync();
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.TryGetProperty("Section", out _)) { Open(ProfileProject.Deserialize(json)); Status = $"Открыт {files[0].Name}"; return; }
            }
            Adopt(PlaxisBridge.Parse(json));
            Status = $"Загружена выгрузка {files[0].Name}";
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { Errors = "Не удалось прочитать файл: " + ex.Message; }
    }

    void Adopt(PlaxisExport export)
    {
        project.UseSource(export);
        ShowSource();
        ClearResult();
        RefreshForces();
    }

    internal void Open(ProfileProject p)
    {
        project = p;
        main.Load(p.Section);
        LoadSettings();
        ShowSource();
        ClearResult();
        RefreshForces();
    }

    void ShowSource()
    {
        loading = true;
        var s = project.Source!;
        ForceUnit = project.Settings.ForceUnit; LengthUnit = project.Settings.LengthUnit; Confirmed = project.Settings.Confirmed;
        loading = false;
        IsEmbedded = s.Objects.Any(o => o.Kind != "Plate");
        Phases.Clear();
        foreach (var ph in project.Phases)
            Phases.Add(new PhaseRowViewModel(ph, s.Phases.FirstOrDefault(x => x.Id == ph.Id)?.Label ?? ph.Name, Changed));
        HasPhases = Phases.Count > 0;
        SourceText = $"{string.Join(", ", s.Objects.Select(o => o.Name))} · фаз {s.Phases.Count} · PLAXIS {s.Info.Version} · единицы {s.Info.UnitForce}, {s.Info.UnitLength}";
    }

    // ---- calculation ----

    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task Calculate()
    {
        RefreshForces();
        if (forces is null) { if (Errors.Length == 0) Errors = "Загрузите усилия PLAXIS"; return; }
        if (!project.Settings.Confirmed) { Errors = "Сверьте с Output единицы, масштаб и знак момента и отметьте «Сверено»"; return; }
        var p = project;
        await Busy("Подбор армирования…", async token =>
        {
            var progress = new Progress<(int Done, int Total)>(x =>
            {
                Progress = 100.0 * x.Done / x.Total;
                Status = $"Подбор армирования: {x.Done} из {x.Total} сечений";
            });
            string key = Key();
            result = await Task.Run(() => RequiredReinforcement.Run(p, progress, token), token);
            resultKey = key;
            ShowResult();
        });
    }

    void ShowResult()
    {
        if (result is null) return;
        bool wall = result.Project.Section.Kind == SectionKind.Wall;
        Stations.Clear();
        foreach (var s in result.Stations) Stations.Add(new StationRowViewModel(s, wall));
        HasResult = true; ResultIsStale = false;
        var st = result.Stations;
        string As(double mm2) => (mm2 / 100).ToString("0.0", Ru);
        Summary = wall
            ? $"As,max: правая {As(st.Max(s => s.AsRightMm2))} см², левая {As(st.Max(s => s.AsLeftMm2))} см²"
            : $"As,max {As(st.Max(s => s.AsRightMm2))} см²";
        if (st.Any(s => s.AswCm2PerM is not null)) Summary += $"; Asw/sw,max {st.Max(s => s.AswCm2PerM ?? 0).ToString("0.00", Ru)} см²/м";
        int bad = st.Count(s => s.IsBad());
        if (bad > 0) Summary += $". Не обеспечено или не определено: {bad} сеч.";
        Status = "Подбор выполнен";
        Draw();
    }

    void ClearResult()
    {
        result = null; HasResult = false; ResultIsStale = false; Stations.Clear(); Summary = "";
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task ExportRevit()
    {
        if (result is null || ResultIsStale) { Errors = "Выполните подбор армирования"; return; }
        if (Storage?.Invoke() is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Эпюра армирования для Revit", SuggestedFileName = Safe(project.Title) + ".reinforcement.json", DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("Эпюра армирования") { Patterns = ["*.reinforcement.json", "*.json"] }],
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(RevitExport.Json(result));
        Status = $"Сохранено: {file.Name}";
    }

    [RelayCommand]
    async Task SaveProject()
    {
        if (project.Source is null) { Errors = "Нет данных для сохранения"; return; }
        if (main.TryBuild(out _, quiet: true) is { } section) project.Section = section;
        ReadSettings([]);
        if (Storage?.Invoke() is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Сохранить эпюру", SuggestedFileName = Safe(project.Title) + ".profile.json", DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("Эпюра (усилия PLAXIS и настройки)") { Patterns = ["*.profile.json", "*.json"] }],
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(project.Serialize());
        Status = $"Сохранено: {file.Name}";
    }

    [RelayCommand] void Cancel() => cancel?.Cancel();

    async Task Busy(string message, Func<CancellationToken, Task> work)
    {
        cancel = new CancellationTokenSource();
        IsBusy = true; Progress = 0; Status = message; Errors = "";
        try { await work(cancel.Token); }
        catch (OperationCanceledException) { Status = "Остановлено"; }
        catch (Exception ex) { Status = "Ошибка"; Errors = ex.Message; }
        finally { IsBusy = false; cancel.Dispose(); cancel = null; }
    }

    static string Safe(string name) => string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    /// <summary>For tests and the demo: use an export without PLAXIS.</summary>
    internal void UseExport(PlaxisExport export) => Adopt(export);
    internal void Confirm() => Confirmed = true;
    internal Task RunCalculation() => Calculate();
    internal ReinforcementProfile? Result => result;
}

static class StationResultExtensions
{
    public static bool IsBad(this StationResult s) => s.Status is "exceeded" or "indeterminate";
}

/// <summary>Per-user settings: the PLAXIS Python and the Output port.</summary>
public sealed class AppSettings
{
    public string? PlaxisPython { get; set; }
    public int PlaxisPort { get; set; } = 10001;

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SectionApp", "settings.json");

    public static AppSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
