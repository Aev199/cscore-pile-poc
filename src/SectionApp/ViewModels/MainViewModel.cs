using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SectionCalc;

namespace SectionApp.ViewModels;

/// <summary>One concrete outline template in the shape picker.</summary>
public sealed record ShapeOption(ShapeKind Kind, string Name, string Icon);

/// <summary>A tile of the section picker: the bored pile, the diaphragm wall or an outline template of any section.</summary>
/// <summary>A recent project file in the drop-down at «Открыть».</summary>
public sealed record RecentFile(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

public sealed record SectionTile(SectionKind Kind, int Shape, string Name, string Icon, string? Caption = null);

public partial class MainViewModel : ObservableObject, INotifyDataErrorInfo
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    static readonly HashSet<string> NonInputs =
    [
        nameof(Sketch), nameof(IsBusy), nameof(StatusText), nameof(Progress), nameof(ShowProgress), nameof(HasResult),
        nameof(ResultIsStale), nameof(VerdictText), nameof(VerdictTone), nameof(GoverningText), nameof(InputErrors),
        nameof(HasInputErrors), nameof(SelectionSummary), nameof(HasSelection), nameof(SteelText), nameof(SelectedTab),
        nameof(VerdictOk), nameof(VerdictBad), nameof(VerdictWarn), nameof(VerdictInfo), nameof(IsWall), nameof(IsPile), nameof(IsCustom),
        nameof(IsGeotech), nameof(KindTitle), nameof(EtaByCscore), nameof(SelectedLoad), nameof(SelectedBar), nameof(SelectedVertex),
        nameof(AxisText), nameof(L0Text), nameof(HasGoverning), nameof(CombinationHeaders), nameof(ProfileMode), nameof(IsSectionMode),
        nameof(IsProfileMode), nameof(TileIndex), nameof(HasRecent), nameof(CoefficientsText), nameof(ShowFactorColumns), nameof(CommonLoadFactor), nameof(CommonSustained), nameof(LimitedWidth), nameof(SeismicUsed), nameof(SpecialUsed),
        nameof(SpecialDesign), nameof(AutoCalc), nameof(PropertiesText), nameof(IsContour), nameof(IsManual), nameof(IsPolygon),
        nameof(ShowRect), nameof(ShowDiameter), nameof(ShowInner), nameof(ShowFlanges), nameof(ShowBoxWall), nameof(ContourText),
        nameof(ShowSeismic), nameof(CanCheck), nameof(ShowHeight),
    ];

    CancellationTokenSource? cancel;
    readonly SemaphoreSlim engine = new(1, 1);
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(450) };
    int runId;
    CheckResult? lastResult;
    SelectionResult? lastSelection;
    bool loading;
    SectionKind lastGeotech = SectionKind.Pile;

    readonly SessionStore? session;

    public MainViewModel() : this(null) { }

    /// <param name="session">Remembers the last section and the recent files (the application; tests run without it).</param>
    public MainViewModel(SessionStore? session)
    {
        this.session = session;
        Profile = new ProfileViewModel(this);
        debounce.Tick += async (_, _) => { debounce.Stop(); await AutoCheck(); };
        // The work of the last run continues; the first run starts with an example.
        Load(session?.LastSection() ?? SectionProject.Example(SectionKind.Custom));
        RefreshRecent();
    }

    /// <summary>Recent project files (full paths) for the drop-down at «Открыть».</summary>
    public ObservableCollection<RecentFile> RecentFiles { get; } = [];
    public bool HasRecent => RecentFiles.Count > 0;

    void RefreshRecent()
    {
        RecentFiles.Clear();
        foreach (var f in session?.RecentFiles ?? []) RecentFiles.Add(new RecentFile(f));
        OnPropertyChanged(nameof(HasRecent));
    }

    /// <summary>Called by the window when it closes: the section is there on the next start.</summary>
    public void SaveSession()
    {
        if (session is not null && TryBuild(out _, quiet: true) is { } p) session.SaveLastSection(p);
    }

    [RelayCommand]
    void OpenRecent(RecentFile? file)
    {
        if (file is null) return;
        try
        {
            Load(ProjectStore.Deserialize(File.ReadAllText(file.Path)));
            ProfileMode = false;
            session?.Remember(file.Path);
            RefreshRecent();
            StatusText = $"Открыт {file.Name}";
        }
        catch (Exception ex) { ShowErrors([$"Не удалось открыть файл: {ex.Message}"]); }
    }

    async Task<IStorageFolder?> StartFolder(IStorageProvider storage) =>
        session?.LastFolder is { } dir && Directory.Exists(dir) ? await storage.TryGetFolderFromPathAsync(dir) : null;

    /// <summary>Module «Эпюры (PLAXIS)»; it takes the section from this view model.</summary>
    public ProfileViewModel Profile { get; }

    // ---- two modes: one section (any outline, the pile and the wall among the templates) / PLAXIS diagrams ----

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSectionMode), nameof(IsProfileMode))]
    bool profileMode;
    public bool IsSectionMode => !ProfileMode;
    public bool IsProfileMode => ProfileMode;
    partial void OnProfileModeChanged(bool value) { if (value) Profile.RefreshForces(); }

    [RelayCommand] void ShowSection() => ProfileMode = false;
    [RelayCommand]
    void ShowProfile()
    {
        // The PLAXIS module designs a pile or a wall: an arbitrary section gives way to the last of them.
        if (IsCustom) { Kind = lastGeotech; StatusText = $"Эпюры PLAXIS — для БНС и СВГ: сечение «{KindTitle}»"; }
        ProfileMode = true;
    }

    /// <summary>Opens a saved file in its program (the report in Word); the window sets it, tests leave it empty.</summary>
    public Action<string>? OpenFile { get; set; }

    /// <summary>Set by the window: file dialogs need the top-level storage provider.</summary>
    public Func<IStorageProvider?>? Storage { get; set; }

    public IReadOnlyList<string> ConcreteClasses { get; } = MaterialTables.Concretes.Select(c => c.Name).ToList();
    public IReadOnlyList<string> RebarClasses { get; } = MaterialTables.Rebars.Select(r => r.Name).ToList();
    public IReadOnlyList<string> SecondOrderOptions { get; } = ["η по п. 8.1.15", "учтено в усилиях", "не учитывать"];
    public IReadOnlyList<string> EccentricityOptions { get; } = ["по СП 63 / заданный", "учтён в моментах"];
    public IReadOnlyList<string> SchemeOptions { get; } = ["стат. неопределимая", "стат. определимая"];
    public IReadOnlyList<string> CrackCategoryOptions { get; } = ["ограниченное раскрытие", "трещины не допускаются"];
    public IReadOnlyList<string> CrackRequirementOptions { get; } = ["сохранность арматуры", "ограничение проницаемости"];
    public IReadOnlyList<string> CrackMethodOptions { get; } = ["консервативный", "уточнённый"];
    public IReadOnlyList<string> SpecialOptions { get; } = ["нормативные", "расчётные"];
    public IReadOnlyList<string> ArrangementOptions { get; } = ["По контуру", "Таблицей"];

    /// <summary>Pictograms: 24 × 24 path data.</summary>
    public IReadOnlyList<ShapeOption> ShapeOptions { get; } =
    [
        new(ShapeKind.Rectangle, "Прямоугольник", "M6,2 H18 V22 H6 Z"),
        new(ShapeKind.Circle, "Круг", "M12,2 A10,10 0 1 1 11.99,2 Z"),
        new(ShapeKind.Ring, "Кольцо", "M12,2 A10,10 0 1 1 11.99,2 Z M12,7 A5,5 0 1 0 12.01,7 Z"),
        new(ShapeKind.Tee, "Тавр", "M2,2 H22 V7 H15 V22 H9 V7 H2 Z"),
        new(ShapeKind.IBeam, "Двутавр", "M3,2 H21 V6 H14.5 V18 H21 V22 H3 V18 H9.5 V6 H3 Z"),
        new(ShapeKind.Box, "Коробка", "M4,2 H20 V22 H4 Z M8,6 V18 H16 V6 Z"),
        new(ShapeKind.Polygon, "Многоугольник (вершины)", "M3,20 L6,4 L15,2 L21,10 L17,22 Z"),
    ];

    /// <summary>The section picker: the pile and the wall first, then the outlines of any section.</summary>
    public IReadOnlyList<SectionTile> Tiles => tiles ??=
    [
        new(SectionKind.Pile, -1, "Буронабивная свая (БНС): кольцо стержней, спираль, Q по приведённому сечению",
            "M12,2 A10,10 0 1 1 11.99,2 Z M12,5.5 A1.2,1.2 0 1 1 11.99,5.5 Z M18.5,12 A1.2,1.2 0 1 1 18.49,12 Z M12,18.5 A1.2,1.2 0 1 1 11.99,18.5 Z M5.5,12 A1.2,1.2 0 1 1 5.49,12 Z", "БНС"),
        new(SectionKind.Wall, -1, "Стена в грунте (СВГ): стержни у граней «+» и «−», ряды, хомуты",
            "M2,6 H22 V18 H2 Z M5,8.5 A1,1 0 1 1 4.99,8.5 Z M12,8.5 A1,1 0 1 1 11.99,8.5 Z M19,8.5 A1,1 0 1 1 18.99,8.5 Z M5,15.5 A1,1 0 1 1 4.99,15.5 Z M12,15.5 A1,1 0 1 1 11.99,15.5 Z M19,15.5 A1,1 0 1 1 18.99,15.5 Z", "СВГ"),
        .. ShapeOptions.Select((o, i) => new SectionTile(SectionKind.Custom, i, o.Name, o.Icon)),
    ];
    IReadOnlyList<SectionTile>? tiles;

    /// <summary>Selected tile: the kind of the section and, for any section, its outline.</summary>
    public int TileIndex
    {
        get => Kind switch { SectionKind.Pile => 0, SectionKind.Wall => 1, _ => 2 + ShapeIndex };
        set
        {
            if (value < 0 || value >= Tiles.Count || value == TileIndex) return;
            var t = Tiles[value];
            if (t.Kind == SectionKind.Custom) ShapeIndex = t.Shape;
            Kind = t.Kind;
            OnPropertyChanged();
        }
    }

    /// <summary>Set by the window: clipboard text for "paste from Excel".</summary>
    public Func<Task<string?>>? ClipboardText { get; set; }

    // ---- section kind ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPile), nameof(IsWall), nameof(IsCustom), nameof(IsGeotech), nameof(KindTitle), nameof(TileIndex))]
    SectionKind kind = SectionKind.Custom;
    partial void OnKindChanged(SectionKind value)
    {
        if (value != SectionKind.Custom) lastGeotech = value;
        if (loading) return;
        // A default name follows the kind; a name given by the user stays.
        if (DefaultTitles.Contains(Title.Trim())) Title = SectionProject.Example(value).Title;
        ClearResults();
        InputsChanged();
    }

    /// <summary>Bored pile; setting false selects the wall (the PLAXIS module sets it by its scenario).</summary>
    static readonly HashSet<string> DefaultTitles = [.. Enum.GetValues<SectionKind>().Select(k => SectionProject.Example(k).Title), "", "Новое сечение"];

    public bool IsPile { get => Kind == SectionKind.Pile; set => Kind = value ? SectionKind.Pile : SectionKind.Wall; }
    public bool IsWall => Kind == SectionKind.Wall;
    public bool IsCustom => Kind == SectionKind.Custom;
    public bool IsGeotech => !IsCustom;
    public string KindTitle => Kind switch { SectionKind.Pile => "Буронабивная свая", SectionKind.Wall => "Стена в грунте", _ => "Сечение" };

    [ObservableProperty] string title = "";
    [ObservableProperty] string pileDiameter = "", pileCover = "", pileSpiral = "", pileBarCount = "", pileBarDiameter = "";
    [ObservableProperty] string wallWidth = "", wallThickness = "", wallCover = "", wallStirrup = "";
    [ObservableProperty] string positiveCount = "", positiveDiameter = "", negativeCount = "", negativeDiameter = "";
    [ObservableProperty] string minClearSpacing = "";
    [ObservableProperty] string positiveRows = "1", negativeRows = "1", rowGap = "25";
    public IReadOnlyList<string> CoverReferenceOptions { get; } = ["до стержней в свету", "до спирали / хомута", "до оси стержней"];
    [ObservableProperty] int coverReferenceIndex;
    [ObservableProperty] string axisText = "";

    static CoverReference CoverOf(int index) => index switch { 1 => CoverReference.Transverse, 2 => CoverReference.Axis, _ => CoverReference.Bar };

    // ---- arbitrary section ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsPolygon), nameof(ShowRect), nameof(ShowDiameter), nameof(ShowInner),
        nameof(ShowFlanges), nameof(ShowBoxWall), nameof(ShowHeight), nameof(TileIndex))]
    int shapeIndex;
    ShapeKind Shape => ShapeOptions[Math.Clamp(ShapeIndex, 0, ShapeOptions.Count - 1)].Kind;
    public bool IsPolygon => Shape == ShapeKind.Polygon;
    public bool ShowRect => Shape is ShapeKind.Rectangle or ShapeKind.Box;
    public bool ShowDiameter => Shape is ShapeKind.Circle or ShapeKind.Ring;
    public bool ShowInner => Shape == ShapeKind.Ring;
    public bool ShowFlanges => Shape is ShapeKind.Tee or ShapeKind.IBeam;
    public bool ShowBoxWall => Shape == ShapeKind.Box;
    public bool ShowHeight => ShowRect || ShowFlanges;
    [ObservableProperty] string customWidth = "", customHeight = "", customDiameter = "", customInner = "";
    [ObservableProperty] string flangeWidth = "", flangeThickness = "", webWidth = "", boxWall = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsContour), nameof(IsManual))] int arrangementIndex;
    public bool IsContour => ArrangementIndex == 0;
    public bool IsManual => ArrangementIndex == 1;
    [ObservableProperty] string barAxis = "", barDiameter = "", barSpacing = "", customStirrup = "";
    [ObservableProperty] string contourText = "";
    [ObservableProperty] string propertiesText = "";
    public ObservableCollection<VertexRowViewModel> Vertices { get; } = [];
    public ObservableCollection<BarRowViewModel> Bars { get; } = [];
    [ObservableProperty] VertexRowViewModel? selectedVertex;
    [ObservableProperty] BarRowViewModel? selectedBar;

    // ---- materials ----
    [ObservableProperty] string concreteClass = "B30", rebarClass = "A500", transverseClass = "A240";
    [ObservableProperty] string gb1 = "", gb2 = "", gb3 = "", gb5 = "", hardening = "", gammaS = "", gammaSw = "";
    [ObservableProperty] bool rscByTable = true;

    /// <summary>The working-condition factors in one line: the values that differ from 1 (γb1 always).</summary>
    public string CoefficientsText
    {
        get
        {
            var parts = new List<string> { $"γb1 = {Gb1}" };
            foreach (var (name, value) in new[] { ("γb2", Gb2), ("γb3", Gb3), ("γb5", Gb5), ("γs", GammaS), ("γsw", GammaSw), ("к Eb", Hardening) })
                if (!(TryNumber(value, out var v) && Math.Abs(v - 1) < 1e-9)) parts.Add($"{name} = {value}");
            return string.Join("; ", parts) + (parts.Count == 1 ? "; прочие 1" : "");
        }
    }

    // ---- element and design conditions ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(L0Text))] string elementLength = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(L0Text))] string muX = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(L0Text))] string muY = "";
    public string L0Text => TryNumber(ElementLength, out var l) && TryNumber(MuX, out var mx) && TryNumber(MuY, out var my)
        ? $"l₀,x = {S(l * mx)} м, l₀,y = {S(l * my)} м" : "l₀ = μ·L";
    [ObservableProperty] string slendernessLimit = "";
    [ObservableProperty] bool staticallyDeterminate;
    [ObservableProperty] bool eccentricitySp63 = true;
    [ObservableProperty] string eaX = "", eaY = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EtaByCscore))] int secondOrderIndex;
    public bool EtaByCscore => SecondOrderIndex == 0;
    [ObservableProperty] string reliabilityUls = "", reliabilitySls = "";
    public int EccentricityIndex { get => EccentricitySp63 ? 0 : 1; set { EccentricitySp63 = value == 0; OnPropertyChanged(); } }
    public int SchemeIndex { get => StaticallyDeterminate ? 1 : 0; set { StaticallyDeterminate = value == 1; OnPropertyChanged(); } }
    partial void OnEccentricitySp63Changed(bool value) => OnPropertyChanged(nameof(EccentricityIndex));
    partial void OnStaticallyDeterminateChanged(bool value) => OnPropertyChanged(nameof(SchemeIndex));

    // ---- cracks ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LimitedWidth))] int crackCategoryIndex;
    public bool LimitedWidth => CrackCategoryIndex == 0;
    [ObservableProperty] int crackRequirementIndex;
    [ObservableProperty] string crackLimitLong = "", crackLimitShort = "";
    [ObservableProperty] int crackMethodIndex;

    /// <summary>SP 63 8.2.6: 0.3 / 0.4 mm for reinforcement preservation, 0.2 / 0.3 mm for limited permeability.</summary>
    partial void OnCrackRequirementIndexChanged(int value)
    {
        if (loading) return;
        CrackLimitLong = value == 1 ? "0,2" : "0,3";
        CrackLimitShort = value == 1 ? "0,3" : "0,4";
    }

    // ---- seismic and special combinations ----
    [ObservableProperty] string seismicNormal = "", seismicInclined = "";
    [ObservableProperty] bool seismicCracks;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SpecialDesign))] bool specialNormative = true;
    public bool SpecialDesign => !SpecialNormative;
    public int SpecialIndex { get => SpecialNormative ? 0 : 1; set { SpecialNormative = value == 0; OnPropertyChanged(); } }
    partial void OnSpecialNormativeChanged(bool value) => OnPropertyChanged(nameof(SpecialIndex));
    [ObservableProperty] string specialGb = "", specialGs = "", specialGsw = "", specialAdditional = "";
    public bool SeismicUsed => Loads.Any(l => l.Seismic);
    public bool SpecialUsed => Loads.Any(l => l.Special);
    /// <summary>The group of seismic and special settings opens by itself once such a combination appears.</summary>
    [ObservableProperty] bool showSeismic;
    [ObservableProperty] bool shearCheck;
    [ObservableProperty] string shearPitch = "", stirrupLegs = "", pileShearA = "";
    [ObservableProperty] bool shearConfirmed;
    [ObservableProperty] bool inclinedMoment;
    [ObservableProperty] string anchorageFactor = "", supportDown = "", supportUp = "";

    // ---- selection ----
    [ObservableProperty] string selectionCountMin = "", selectionCountMax = "", selectionCountStep = "";
    [ObservableProperty] bool selectionSymmetric = true;
    [ObservableProperty] string selectionDiameters = "", selectionSpacings = "";

    public ObservableCollection<LoadRowViewModel> Loads { get; } = [];

    /// <summary>γf and kдл of all the rows at once (they are usually the same); empty when the rows differ.</summary>
    public string CommonLoadFactor
    {
        get => Common(l => l.LoadFactor);
        set => SetAll(l => l.LoadFactor = value);
    }
    public string CommonSustained
    {
        get => Common(l => l.SustainedShare);
        set => SetAll(l => l.SustainedShare = value);
    }
    bool settingAll;
    void SetAll(Action<LoadRowViewModel> set)
    {
        settingAll = true;
        try { foreach (var l in Loads) set(l); }
        finally { settingAll = false; }
        FactorsChanged();
    }
    string Common(Func<LoadRowViewModel, string> f)
    {
        var values = Loads.Select(l => TryNumber(f(l), out var v) ? v : double.NaN).Distinct().ToList();
        return values.Count == 1 && double.IsFinite(values[0]) ? S(values[0]) : "";
    }
    /// <summary>The γf and kдл columns: shown when the rows differ or on demand.</summary>
    [ObservableProperty] bool showFactorColumns;
    internal void FactorsChanged()
    {
        OnPropertyChanged(nameof(CommonLoadFactor)); OnPropertyChanged(nameof(CommonSustained));
        if (!settingAll && Loads.Count > 0 && (CommonLoadFactor.Length == 0 || CommonSustained.Length == 0)) ShowFactorColumns = true;
    }
    public ObservableCollection<CheckRowViewModel> SummaryRows { get; } = [];
    public ObservableCollection<CombinationRowViewModel> CombinationRows { get; } = [];
    [ObservableProperty] string[] combinationHeaders = ["Прочность", "N/Ncr", "Q", "acrc, прод.", "acrc, непрод."];
    public ObservableCollection<SelectionRowViewModel> SelectionRows { get; } = [];

    // ---- state ----
    [ObservableProperty] Bitmap? sketch;
    [ObservableProperty] string steelText = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SelectCommand), nameof(ExportReportCommand))] bool isBusy;
    /// <summary>Recalculate by itself shortly after every change of the input.</summary>
    [ObservableProperty] bool autoCalc = true;
    partial void OnAutoCalcChanged(bool value) { if (value && (!HasResult || ResultIsStale)) debounce.Start(); }
    [ObservableProperty] string statusText = "";
    [ObservableProperty] double progress;
    [ObservableProperty] bool showProgress;
    [ObservableProperty] bool hasResult;
    [ObservableProperty] bool resultIsStale;
    [ObservableProperty] string verdictText = "Нет результата";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VerdictOk), nameof(VerdictBad), nameof(VerdictWarn), nameof(VerdictInfo))]
    string verdictTone = "info";
    public bool VerdictOk => VerdictTone == "ok";
    public bool VerdictBad => VerdictTone == "bad";
    public bool VerdictWarn => VerdictTone == "warn";
    public bool VerdictInfo => VerdictTone == "info";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasGoverning))] string governingText = "";
    public bool HasGoverning => GoverningText.Length > 0;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasInputErrors), nameof(CanCheck))] string inputErrors = "";
    public bool HasInputErrors => InputErrors.Length > 0;
    public bool CanCheck => !HasInputErrors;
    [ObservableProperty] string selectionSummary = "";
    [ObservableProperty] bool hasSelection;
    [ObservableProperty] int selectedTab;
    [ObservableProperty] LoadRowViewModel? selectedLoad;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!loading && e.PropertyName is { } name && !NonInputs.Contains(name)) InputsChanged();
    }

    internal void InputsChanged()
    {
        if (loading) return;
        if (HasResult) ResultIsStale = true;
        OnPropertyChanged(nameof(SeismicUsed)); OnPropertyChanged(nameof(SpecialUsed));
        if (SeismicUsed || SpecialUsed) ShowSeismic = true;
        OnPropertyChanged(nameof(CoefficientsText));
        FactorsChanged();
        RefreshSketch();
        debounce.Stop();
        if (AutoCalc && !HasInputErrors) debounce.Start();
    }

    void RefreshSketch()
    {
        var p = TryBuild(out var errors, quiet: true);
        InputErrors = string.Join("\n", errors);
        if (p is null) return;
        var layout = RequestBuilder.Layout(p);
        var gross = Geometry.Gross(p);
        SteelText = $"As = {layout.SteelAreaMm2.ToString("0", Ru)} мм²   μs = {(layout.SteelAreaMm2 / gross.AreaMm2 * 100).ToString("0.00", Ru)} %   n = {layout.Bars.Count}";
        AxisText = p.Kind switch
        {
            SectionKind.Pile => $"до оси {p.Pile.AxisDistanceMm.ToString("0.#", Ru)} мм, до спирали {p.Pile.SpiralCoverMm.ToString("0.#", Ru)} мм",
            SectionKind.Wall => $"до оси {p.Wall.AxisDistanceMm(p.Wall.PositiveFace).ToString("0.#", Ru)} мм («+»), {p.Wall.AxisDistanceMm(p.Wall.NegativeFace).ToString("0.#", Ru)} мм («−»)",
            _ => "",
        };
        if (p.Kind == SectionKind.Custom && p.Custom.OutlineProblems().Count == 0)
        {
            var g = p.Custom.Properties();
            PropertiesText = $"A = {(g.AreaMm2 / 1e6).ToString("0.####", Ru)} м²   ц.т. ({g.CxMm.ToString("0.#", Ru)}; {g.CyMm.ToString("0.#", Ru)}) мм\n"
                + $"Ix = {(g.IxMm4 / 1e12).ToString("0.#####", Ru)} м⁴   Iy = {(g.IyMm4 / 1e12).ToString("0.#####", Ru)} м⁴\n"
                + $"ix = {g.RadiusXMm.ToString("0", Ru)} мм   iy = {g.RadiusYMm.ToString("0", Ru)} мм";
            ContourText = IsContour ? $"{layout.Bars.Count} Ø{p.Custom.BarDiameterMm.ToString("0.#", Ru)}" : "";
        }
        else PropertiesText = $"A = {(gross.AreaMm2 / 1e6).ToString("0.####", Ru)} м²   i = {gross.IxMm.ToString("0", Ru)} / {gross.IyMm.ToString("0", Ru)} мм";
        using var stream = new MemoryStream(SectionSketch.Png(p, 640, 520, 24));
        Sketch = new Bitmap(stream);
    }

    // ---- project <-> fields ----

    internal void Load(SectionProject p)
    {
        loading = true;
        try
        {
            Kind = p.Kind; Title = p.Title;
            PileDiameter = S(p.Pile.DiameterMm); PileCover = S(p.Pile.CoverMm); PileSpiral = S(p.Pile.SpiralDiameterMm);
            PileBarCount = S(p.Pile.BarCount); PileBarDiameter = S(p.Pile.BarDiameterMm);
            WallWidth = S(p.Wall.WidthMm); WallThickness = S(p.Wall.ThicknessMm); WallCover = S(p.Wall.CoverMm); WallStirrup = S(p.Wall.StirrupDiameterMm);
            PositiveCount = S(p.Wall.PositiveFace.BarCount); PositiveDiameter = S(p.Wall.PositiveFace.BarDiameterMm);
            NegativeCount = S(p.Wall.NegativeFace.BarCount); NegativeDiameter = S(p.Wall.NegativeFace.BarDiameterMm);
            PositiveRows = S(p.Wall.PositiveFace.Rows); NegativeRows = S(p.Wall.NegativeFace.Rows); RowGap = S(p.Wall.PositiveFace.RowGapMm);
            // One convention for the cover: clear distance to the longitudinal bars (SP 63); earlier files are converted.
            var pc = Geometry.TransverseCover(p.Pile.CoverMm, p.Pile.CoverReference, p.Pile.SpiralDiameterMm, p.Pile.BarDiameterMm) + p.Pile.SpiralDiameterMm;
            var wc = Geometry.TransverseCover(p.Wall.CoverMm, p.Wall.CoverReference, p.Wall.StirrupDiameterMm, p.Wall.PositiveFace.BarDiameterMm) + p.Wall.StirrupDiameterMm;
            PileCover = S(pc); WallCover = S(wc);
            MinClearSpacing = S(p.Kind switch { SectionKind.Pile => p.Pile.MinClearSpacingMm, SectionKind.Wall => p.Wall.MinClearSpacingMm, _ => p.Custom.MinClearSpacingMm });
            CoverReferenceIndex = 0;
            var g = p.Custom;
            ShapeIndex = Math.Max(0, ShapeOptions.ToList().FindIndex(o => o.Kind == g.Shape));
            CustomWidth = S(g.WidthMm); CustomHeight = S(g.HeightMm); CustomDiameter = S(g.DiameterMm); CustomInner = S(g.InnerDiameterMm);
            FlangeWidth = S(g.FlangeWidthMm); FlangeThickness = S(g.FlangeThicknessMm); WebWidth = S(g.WebMm); BoxWall = S(g.WallMm);
            ArrangementIndex = g.Arrangement == BarArrangement.Manual ? 1 : 0;
            BarAxis = S(g.AxisDistanceMm); BarDiameter = S(g.BarDiameterMm); BarSpacing = S(g.MaxSpacingMm); CustomStirrup = S(g.StirrupDiameterMm);
            Vertices.Clear();
            foreach (var v in g.Vertices) Vertices.Add(new VertexRowViewModel(this, S(v.X), S(v.Y)));
            Bars.Clear();
            foreach (var b in g.Bars) Bars.Add(new BarRowViewModel(this, S(b.X), S(b.Y), S(b.DiameterMm)));
            ConcreteClass = p.ConcreteClass; RebarClass = p.RebarClass; TransverseClass = p.TransverseClass;
            var f = p.Concrete;
            Gb1 = S(f.Gb1); Gb2 = S(f.Gb2); Gb3 = S(f.Gb3); Gb5 = S(f.Gb5); Hardening = S(f.HardeningFactor);
            GammaS = S(p.LongitudinalFactor); GammaSw = S(p.TransverseFactor); RscByTable = p.RscByTable;
            var c = p.Conditions;
            ElementLength = S(c.ElementLengthM); MuX = S(c.MuX); MuY = S(c.MuY); SlendernessLimit = S(c.SlendernessLimit);
            StaticallyDeterminate = c.StaticallyDeterminate;
            EccentricitySp63 = c.Eccentricity == EccentricityMode.Sp63; EaX = S(c.EaXMm); EaY = S(c.EaYMm);
            SecondOrderIndex = c.SecondOrder switch { SecondOrderMode.Cscore => 0, SecondOrderMode.InLoads => 1, _ => 2 };
            ReliabilityUls = S(c.ReliabilityUls); ReliabilitySls = S(c.ReliabilitySls);
            CrackCategoryIndex = c.CrackCategory == CrackCategory.NoCracks ? 1 : 0; CrackRequirementIndex = c.PermeabilityLimits ? 1 : 0;
            CrackLimitLong = S(c.CrackLimitLongMm); CrackLimitShort = S(c.CrackLimitShortMm);
            CrackMethodIndex = c.CrackMethod == CrackMethod.Refined ? 1 : 0;
            SeismicNormal = S(p.Seismic.NormalSectionFactor); SeismicInclined = S(p.Seismic.InclinedSectionFactor); SeismicCracks = p.Seismic.CheckCracks;
            var sp = p.Special;
            SpecialNormative = sp.NormativeProperties; SpecialGb = S(sp.ConcreteFactor); SpecialGs = S(sp.LongitudinalFactor);
            SpecialGsw = S(sp.TransverseFactor); SpecialAdditional = S(sp.AdditionalFactor);
            ShearCheck = c.ShearCheck; ShearPitch = S(c.ShearPitchMm); StirrupLegs = S(c.StirrupLegs); PileShearA = S(c.PileShearAMm);
            ShearConfirmed = c.ShearModelConfirmed; InclinedMoment = c.InclinedMoment;
            AnchorageFactor = S(c.AnchorageFactor); SupportDown = S(c.SupportDistanceDownM); SupportUp = S(c.SupportDistanceUpM);
            var s = p.Selection;
            SelectionCountMin = S(p.Kind == SectionKind.Pile ? s.PileCountMin : s.WallCountMin); SelectionCountMax = S(p.Kind == SectionKind.Pile ? s.PileCountMax : s.WallCountMax);
            SelectionCountStep = S(s.PileCountStep); SelectionSymmetric = s.WallSymmetric;
            SelectionDiameters = string.Join(", ", s.Diameters.Select(d => S(d)));
            SelectionSpacings = string.Join(", ", s.CustomSpacings.Select(d => S(d)));
            Loads.Clear();
            foreach (var l in p.Loads) Loads.Add(LoadRowViewModel.From(l, this));
            SelectedLoad = Loads.FirstOrDefault();
            ShowSeismic = p.Loads.Any(l => l.Seismic || l.Special);
            ShowFactorColumns = p.Loads.Select(l => (l.LoadFactor, l.SustainedShare)).Distinct().Count() > 1;
        }
        finally { loading = false; }
        OnPropertyChanged(nameof(SeismicUsed)); OnPropertyChanged(nameof(SpecialUsed));
        ClearResults();
        InputsChanged();
    }

    /// <summary>
    /// Project from the fields; null with readable messages when a value is not a number. The geometry of every kind
    /// is kept (a saved file reopens in either mode); only the active one reports its errors.
    /// </summary>
    internal SectionProject? TryBuild(out List<string> errors, bool quiet = false)
    {
        var e = new List<string>();
        var fields = new Dictionary<string, string>();
        bool report = true;
        // The field of a bad number is marked in the form: its property is the argument expression ("PileDiameter").
        void Bad(string message, string field)
        {
            e.Add(message);
            if (field.All(char.IsLetterOrDigit)) fields.TryAdd(field, message);
        }
        double D(string text, string label, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(text))] string field = "")
        {
            if (TryNumber(text, out var v)) return v;
            if (report) Bad($"«{label}»: введите число", field);
            return 0;
        }
        int I(string text, string label, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(text))] string field = "")
        {
            if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
            if (report) Bad($"«{label}»: введите целое число", field);
            return 0;
        }
        var p = new SectionProject
        {
            Kind = Kind, Title = string.IsNullOrWhiteSpace(Title) ? KindTitle : Title.Trim(),
            ConcreteClass = ConcreteClass, RebarClass = RebarClass, TransverseClass = TransverseClass,
            Concrete = new ConcreteFactors
            {
                Gb1 = D(Gb1, "γb1"), Gb2 = D(Gb2, "γb2"), Gb3 = D(Gb3, "γb3"), Gb5 = D(Gb5, "γb5"), HardeningFactor = D(Hardening, "Коэффициент к Eb"),
            },
            LongitudinalFactor = D(GammaS, "γs продольной"), TransverseFactor = D(GammaSw, "γs поперечной"), RscByTable = RscByTable,
            Seismic = new SeismicSettings
            {
                NormalSectionFactor = D(SeismicNormal, "m_tr нормальных сечений"), InclinedSectionFactor = D(SeismicInclined, "m_tr наклонных сечений"),
                CheckCracks = SeismicCracks,
            },
            Special = new SpecialSettings
            {
                NormativeProperties = SpecialNormative, ConcreteFactor = D(SpecialGb, "Особое: γb"), LongitudinalFactor = D(SpecialGs, "Особое: γs"),
                TransverseFactor = D(SpecialGsw, "Особое: γsw"), AdditionalFactor = D(SpecialAdditional, "Особое: понижающий коэффициент"),
            },
        };
        double clear = D(MinClearSpacing, "Мин. просвет");

        report = Kind == SectionKind.Pile;
        p.Pile = new PileGeometry
        {
            DiameterMm = D(PileDiameter, "Диаметр D"), CoverMm = D(PileCover, "Защитный слой"), CoverReference = CoverOf(CoverReferenceIndex),
            SpiralDiameterMm = D(PileSpiral, "Ø навивки"),
            BarCount = I(PileBarCount, "Число стержней"), BarDiameterMm = D(PileBarDiameter, "Ø стержней"), MinClearSpacingMm = clear,
        };
        report = Kind == SectionKind.Wall;
        p.Wall = new WallGeometry
        {
            WidthMm = D(WallWidth, "Ширина b"), ThicknessMm = D(WallThickness, "Толщина h"), CoverMm = D(WallCover, "Защитный слой"),
            CoverReference = CoverOf(CoverReferenceIndex),
            StirrupDiameterMm = D(WallStirrup, "Ø хомута"), MinClearSpacingMm = clear,
            PositiveFace = new WallFace { BarCount = I(PositiveCount, "Стержней на грани «+»"), BarDiameterMm = D(PositiveDiameter, "Ø на грани «+»"),
                Rows = I(PositiveRows, "Рядов у грани «+»"), RowGapMm = D(RowGap, "Зазор между рядами") },
            NegativeFace = new WallFace { BarCount = I(NegativeCount, "Стержней на грани «−»"), BarDiameterMm = D(NegativeDiameter, "Ø на грани «−»"),
                Rows = I(NegativeRows, "Рядов у грани «−»"), RowGapMm = D(RowGap, "Зазор между рядами") },
        };
        report = Kind == SectionKind.Custom;
        var shape = Shape;
        bool Used(params ShapeKind[] kinds) => kinds.Contains(shape);
        bool r0 = report;
        p.Custom = new CustomGeometry
        {
            Shape = shape, MinClearSpacingMm = clear,
            Arrangement = IsManual ? BarArrangement.Manual : BarArrangement.Contour,
        };
        var g = p.Custom;
        report = r0 && Used(ShapeKind.Rectangle, ShapeKind.Box); g.WidthMm = D(CustomWidth, "b");
        report = r0 && Used(ShapeKind.Rectangle, ShapeKind.Box, ShapeKind.Tee, ShapeKind.IBeam); g.HeightMm = D(CustomHeight, "h");
        report = r0 && Used(ShapeKind.Circle, ShapeKind.Ring); g.DiameterMm = D(CustomDiameter, "D");
        report = r0 && Used(ShapeKind.Ring); g.InnerDiameterMm = D(CustomInner, "d");
        report = r0 && Used(ShapeKind.Tee, ShapeKind.IBeam);
        g.FlangeWidthMm = D(FlangeWidth, "bf"); g.FlangeThicknessMm = D(FlangeThickness, "hf"); g.WebMm = D(WebWidth, "bw");
        report = r0 && Used(ShapeKind.Box); g.WallMm = D(BoxWall, "t");
        report = r0 && Used(ShapeKind.Polygon);
        g.Vertices = Vertices.Select((v, i) => new Vertex(D(v.X, $"Вершина {i + 1}: x"), D(v.Y, $"Вершина {i + 1}: y"))).ToList();
        report = r0 && IsContour;
        g.AxisDistanceMm = D(BarAxis, "a до оси"); g.BarDiameterMm = D(BarDiameter, "Ø стержней"); g.MaxSpacingMm = D(BarSpacing, "Шаг не более");
        report = r0 && IsManual;
        g.Bars = Bars.Select((b, i) => new BarSpec(D(b.X, $"Стержень {i + 1}: x"), D(b.Y, $"Стержень {i + 1}: y"), D(b.D, $"Стержень {i + 1}: Ø"))).ToList();
        report = r0 && ShearCheck; g.StirrupDiameterMm = D(CustomStirrup, "Ø хомутов");
        report = true;

        var c = p.Conditions;
        c.ElementLengthM = D(ElementLength, "Длина элемента"); c.MuX = D(MuX, "μ в плоскости Mx"); c.MuY = D(MuY, "μ в плоскости My");
        c.SlendernessLimit = D(SlendernessLimit, "Предельная гибкость");
        c.StaticallyDeterminate = StaticallyDeterminate;
        c.Eccentricity = EccentricitySp63 ? EccentricityMode.Sp63 : EccentricityMode.InLoads;
        if (EccentricitySp63) { c.EaXMm = D(EaX, "ea в плоскости Mx"); c.EaYMm = D(EaY, "ea в плоскости My"); }
        c.SecondOrder = SecondOrderIndex switch { 0 => SecondOrderMode.Cscore, 1 => SecondOrderMode.InLoads, _ => SecondOrderMode.SectionOnly };
        c.ReliabilityUls = D(ReliabilityUls, "γn по прочности"); c.ReliabilitySls = D(ReliabilitySls, "γn по трещиностойкости");
        c.CrackCategory = LimitedWidth ? CrackCategory.LimitedWidth : CrackCategory.NoCracks;
        c.PermeabilityLimits = CrackRequirementIndex == 1;
        c.CrackLimitLongMm = D(CrackLimitLong, "Предел продолжительного раскрытия");
        c.CrackLimitShortMm = D(CrackLimitShort, "Предел непродолжительного раскрытия");
        c.CrackMethod = CrackMethodIndex == 1 ? CrackMethod.Refined : CrackMethod.Conservative;
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
        else if (IsWall) { s.WallCountMin = I(SelectionCountMin, "Подбор: от"); s.WallCountMax = I(SelectionCountMax, "Подбор: до"); s.WallSymmetric = SelectionSymmetric; }
        s.Diameters = Numbers(SelectionDiameters, "Подбор: диаметры");
        report = IsCustom && IsContour;
        s.CustomSpacings = Numbers(SelectionSpacings, "Подбор: шаги");
        report = true;
        foreach (var row in Loads) p.Loads.Add(row.ToModel(e));
        SetFieldErrors(fields);
        errors = e.Distinct().ToList();
        if (errors.Count > 0) return null;
        errors.AddRange(RequestBuilder.Validate(p));
        return quiet || errors.Count == 0 ? p : null;

        List<double> Numbers(string text, string label, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(text))] string field = "") =>
            text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Select(t => D(t, label, field)).ToList();
    }

    static string S(double v) => v.ToString("0.###", Ru);

    // ---- errors at the fields (red frame and the message in the field) ----

    Dictionary<string, string> fieldErrors = [];
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
    bool INotifyDataErrorInfo.HasErrors => fieldErrors.Count > 0;
    public System.Collections.IEnumerable GetErrors(string? propertyName) =>
        propertyName is not null && fieldErrors.TryGetValue(propertyName, out var m) ? new[] { m } : Array.Empty<string>();

    void SetFieldErrors(Dictionary<string, string> next)
    {
        var changed = fieldErrors.Keys.Union(next.Keys).Where(k => fieldErrors.GetValueOrDefault(k) != next.GetValueOrDefault(k)).ToList();
        fieldErrors = next;
        foreach (var k in changed) ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(k));
    }

    /// <summary>Number in Russian or invariant notation ("1,15" or "1.15"); spaces as thousand separators.</summary>
    internal static bool TryNumber(string? text, out double value)
    {
        value = 0;
        if (text is null) return false;
        string t = text.Trim().Replace(" ", "").Replace(" ", "").Replace(',', '.').Replace('−', '-');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    // ---- commands ----

    [RelayCommand] void SetPile() => Kind = SectionKind.Pile;
    [RelayCommand] void SetWall() => Kind = SectionKind.Wall;

    [RelayCommand] void SetArrangement(string index) => ArrangementIndex = int.Parse(index, CultureInfo.InvariantCulture);

    // ---- forces grid ----

    string NextName()
    {
        int n = Loads.Count + 1;
        while (Loads.Any(l => l.Name.Trim() == n.ToString(CultureInfo.InvariantCulture))) n++;
        return n.ToString(CultureInfo.InvariantCulture);
    }

    LoadRow Template() => SelectedLoad?.ToModel([]) ?? Loads.LastOrDefault()?.ToModel([]) ?? new LoadRow();

    [RelayCommand]
    void AddLoad()
    {
        // A new row takes γf, kдл and the kind of the selected one: combinations usually share them.
        var t = Template();
        var row = LoadRowViewModel.From(new LoadRow { Name = NextName(), LoadFactor = t.LoadFactor, SustainedShare = t.SustainedShare,
            ShortTerm = t.ShortTerm, Seismic = t.Seismic, Special = t.Special }, this);
        Loads.Add(row); SelectedLoad = row;
        InputsChanged();
    }

    [RelayCommand]
    void DuplicateLoad()
    {
        if (SelectedLoad is null) return;
        var copy = SelectedLoad.ToModel([]);
        copy.Name = NextName();
        var row = LoadRowViewModel.From(copy, this);
        Loads.Insert(Loads.IndexOf(SelectedLoad) + 1, row); SelectedLoad = row;
        InputsChanged();
    }

    [RelayCommand]
    void RemoveLoad()
    {
        if (SelectedLoad is not { } row) return;
        int i = Loads.IndexOf(row);
        Loads.Remove(row);
        SelectedLoad = Loads.Count == 0 ? null : Loads[Math.Min(i, Loads.Count - 1)];
        InputsChanged();
    }

    [RelayCommand]
    void ClearLoads() { Loads.Clear(); SelectedLoad = null; InputsChanged(); }

    /// <summary>ARBAT "change the force plane": Mx ↔ My for every combination.</summary>
    [RelayCommand]
    void SwapPlane()
    {
        foreach (var row in Loads) row.SwapPlane();
        StatusText = "Mx и My переставлены";
    }

    [RelayCommand]
    async Task PasteLoads()
    {
        string? text = ClipboardText is null ? null : await ClipboardText();
        PasteText(text ?? "");
    }

    /// <summary>
    /// Rows copied from Excel (tab, semicolon separated): [name] N Mx My Q [γf] [kдл]. Lines without
    /// numbers (headers) are skipped; missing γf / kдл are taken from the selected row.
    /// </summary>
    internal int PasteText(string text)
    {
        var t = Template();
        int added = 0, skipped = 0;
        foreach (var line in text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0))
        {
            var cells = line.Split(line.Contains('\t') ? '\t' : ';').Select(c => c.Trim()).ToList();
            string? name = null;
            if (cells.Count > 0 && !TryNumber(cells[0], out _)) { name = cells[0]; cells.RemoveAt(0); }
            var values = new List<double>();
            foreach (var cell in cells)
            {
                if (cell.Length == 0) { values.Add(double.NaN); continue; }
                if (!TryNumber(cell, out var v)) break;
                values.Add(v);
            }
            if (values.Count == 0 || values.All(double.IsNaN)) { skipped++; continue; }
            double V(int i, double fallback) => i < values.Count && !double.IsNaN(values[i]) ? values[i] : fallback;
            var row = new LoadRow
            {
                Name = string.IsNullOrWhiteSpace(name) ? NextName() : name!,
                N = V(0, 0), Mx = V(1, 0), My = V(2, 0), Q = V(3, 0), LoadFactor = V(4, t.LoadFactor), SustainedShare = V(5, t.SustainedShare),
                ShortTerm = t.ShortTerm, Seismic = t.Seismic, Special = t.Special,
            };
            if (Loads.Any(l => l.Name.Trim() == row.Name.Trim())) row.Name = NextName();
            Loads.Add(LoadRowViewModel.From(row, this));
            added++;
        }
        StatusText = added > 0 ? $"Вставлено сочетаний: {added}" + (skipped > 0 ? $", пропущено строк: {skipped}" : "")
            : "В буфере обмена нет строк с усилиями";
        if (added > 0) { SelectedLoad = Loads[^1]; InputsChanged(); }
        return added;
    }

    // ---- vertex and bar tables (arbitrary section) ----

    [RelayCommand]
    void AddVertex()
    {
        var last = Vertices.LastOrDefault();
        var row = new VertexRowViewModel(this, last?.X ?? "0", last?.Y ?? "0");
        Vertices.Insert(SelectedVertex is null ? Vertices.Count : Vertices.IndexOf(SelectedVertex) + 1, row);
        SelectedVertex = row; InputsChanged();
    }

    [RelayCommand]
    void RemoveVertex()
    {
        if (SelectedVertex is not { } row) return;
        int i = Vertices.IndexOf(row);
        Vertices.Remove(row);
        SelectedVertex = Vertices.Count == 0 ? null : Vertices[Math.Min(i, Vertices.Count - 1)];
        InputsChanged();
    }

    [RelayCommand]
    async Task PasteVertices()
    {
        var rows = Pairs(ClipboardText is null ? null : await ClipboardText(), 2);
        if (rows.Count < 3) { StatusText = "В буфере нет вершин: нужны строки «x y», не менее трёх"; return; }
        Vertices.Clear();
        foreach (var r in rows) Vertices.Add(new VertexRowViewModel(this, S(r[0]), S(r[1])));
        StatusText = $"Вставлено вершин: {rows.Count}";
        InputsChanged();
    }

    [RelayCommand]
    void AddBar()
    {
        var t = SelectedBar ?? Bars.LastOrDefault();
        var row = new BarRowViewModel(this, t?.X ?? "0", t?.Y ?? "0", t?.D ?? BarDiameter);
        Bars.Insert(SelectedBar is null ? Bars.Count : Bars.IndexOf(SelectedBar) + 1, row);
        SelectedBar = row; InputsChanged();
    }

    [RelayCommand]
    void RemoveBar()
    {
        if (SelectedBar is not { } row) return;
        int i = Bars.IndexOf(row);
        Bars.Remove(row);
        SelectedBar = Bars.Count == 0 ? null : Bars[Math.Min(i, Bars.Count - 1)];
        InputsChanged();
    }

    [RelayCommand]
    async Task PasteBars()
    {
        var rows = Pairs(ClipboardText is null ? null : await ClipboardText(), 2);
        if (rows.Count == 0) { StatusText = "В буфере нет стержней: нужны строки «x y [Ø]»"; return; }
        PasteBarRows(rows);
    }

    internal void PasteBarRows(List<double[]> rows)
    {
        Bars.Clear();
        foreach (var r in rows) Bars.Add(new BarRowViewModel(this, S(r[0]), S(r[1]), r.Length > 2 ? S(r[2]) : BarDiameter));
        ArrangementIndex = 1;
        StatusText = $"Вставлено стержней: {rows.Count}";
        InputsChanged();
    }

    /// <summary>The contour arrangement as an editable table: one click to move or add single bars.</summary>
    [RelayCommand]
    void ContourToTable()
    {
        if (TryBuild(out _, quiet: true) is not { } p) return;
        loading = true;
        Bars.Clear();
        foreach (var b in p.Custom.ContourBars()) Bars.Add(new BarRowViewModel(this, S(b.X), S(b.Y), S(b.DiameterMm)));
        loading = false;
        ArrangementIndex = 1;
    }

    /// <summary>Lines of at least <paramref name="min"/> numbers (Excel, CAD lists, "x y" or "x;y"); other lines are skipped.</summary>
    static List<double[]> Pairs(string? text, int min) =>
        (text ?? "").Split('\n').Select(l => l.Trim().Split(['\t', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            .Select(cells => cells.Select(c => TryNumber(c, out var v) ? v : double.NaN).TakeWhile(double.IsFinite).ToArray())
            .Where(v => v.Length >= min).ToList();

    // ---- calculation ----

    async Task AutoCheck()
    {
        if (!AutoCalc || selecting || ProfileMode) return;
        if (TryBuild(out _) is not { } p) return;
        await RunCheck(p);
    }

    [RelayCommand]
    async Task Check()
    {
        debounce.Stop();
        var p = TryBuild(out var errors);
        if (p is null) { ShowErrors(errors); return; }
        await RunCheck(p);
    }

    Task RunCheck(SectionProject p) => Run("Расчёт…", token => Engine.Check(p, token), Show);

    bool CanRun() => !IsBusy;
    bool selecting;

    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task Select()
    {
        // The adopted bars are replaced by the candidates; Selection.Run validates everything else.
        var p = TryBuild(out var errors, quiet: true);
        if (p is null) { ShowErrors(errors); return; }
        var progress = new Progress<(int Done, int Total, string Label)>(x =>
        {
            Progress = x.Total == 0 ? 0 : 100.0 * x.Done / x.Total;
            StatusText = x.Label.Length > 0 ? $"Подбор: {x.Label} ({x.Done + 1}/{x.Total})" : "Подбор завершён";
        });
        selecting = true;
        try { await Run("Подбор армирования…", token => Selection.Run(p, progress, token), ShowSelection); }
        finally { selecting = false; }
    }

    [RelayCommand] void Cancel() => cancel?.Cancel();

    /// <summary>
    /// One calculation at a time: a newer run cancels the older one, and only the newest shows its result
    /// (the engine itself runs one request at a time).
    /// </summary>
    async Task Run<T>(string message, Func<CancellationToken, T> work, Action<T> done)
    {
        cancel?.Cancel();
        var cts = new CancellationTokenSource();
        cancel = cts;
        int id = ++runId;
        IsBusy = true; ShowProgress = true; Progress = 0; StatusText = message;
        try
        {
            var token = cts.Token;
            var result = await Task.Run(async () =>
            {
                await engine.WaitAsync(token);
                try { return work(token); }
                finally { engine.Release(); }
            }, token);
            if (id == runId) done(result);
        }
        catch (OperationCanceledException) { if (id == runId) StatusText = "Остановлено"; }
        catch (Exception ex) { if (id == runId) { StatusText = "Ошибка расчёта"; ShowErrors([ex.Message]); } }
        finally
        {
            if (id == runId) { IsBusy = false; ShowProgress = false; cancel = null; }
            cts.Dispose();
        }
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
        GoverningText = ReportWriter.GoverningText(r);
        SummaryRows.Clear();
        foreach (var row in ReportWriter.SummaryRows(r)) SummaryRows.Add(CheckRowViewModel.From(row, r));
        CombinationRows.Clear();
        foreach (var line in ReportWriter.Combinations(r)) CombinationRows.Add(new CombinationRowViewModel(line));
        CombinationHeaders = ReportWriter.CombinationHeaders(r);
        StatusText = $"Рассчитано {DateTime.Now:HH:mm:ss}";
    }

    void ShowSelection(SelectionResult s)
    {
        lastSelection = s;
        SelectionRows.Clear();
        foreach (var o in s.Outcomes) SelectionRows.Add(new SelectionRowViewModel(o, o == s.Best, this));
        HasSelection = true; SelectedTab = 1;
        SelectionSummary = s.Best is SelectionOutcome best
            ? $"Минимальное армирование: {best.Candidate.Description}, As = {best.Candidate.SteelAreaMm2.ToString("0", Ru)} мм²."
            : "В заданном диапазоне подходящих вариантов нет.";
        if (s.GeometricallyRejected > 0) SelectionSummary += $" Не помещаются: {s.GeometricallyRejected}.";
        StatusText = "Подбор завершён";
    }

    internal async Task Apply(SelectionOutcome outcome)
    {
        var keepSelection = lastSelection;
        debounce.Stop();
        Load(outcome.Candidate.Project);
        debounce.Stop();
        lastSelection = keepSelection;
        HasSelection = keepSelection is not null;
        if (keepSelection is not null)
        {
            SelectionRows.Clear();
            foreach (var o in keepSelection.Outcomes) SelectionRows.Add(new SelectionRowViewModel(o, o == keepSelection.Best, this));
        }
        await Check();
        SelectedTab = 0;
    }

    void ClearResults()
    {
        lastResult = null; lastSelection = null; HasResult = false; ResultIsStale = false; HasSelection = false;
        SummaryRows.Clear(); CombinationRows.Clear(); SelectionRows.Clear();
        VerdictText = "Нет результата"; VerdictTone = "info"; SelectionSummary = "";
        GoverningText = "";
    }

    // ---- files ----

    static readonly FilePickerFileType ProjectType = new("Проект сечения") { Patterns = ["*.section.json", "*.json"] };
    static readonly FilePickerFileType WordType = new("Документ Word") { Patterns = ["*.docx"] };

    [RelayCommand]
    void New()
    {
        // A fresh section of the current kind; the geometry of the other mode stays.
        var current = TryBuild(out _, quiet: true);
        var p = SectionProject.Example(Kind);
        if (current is not null) { p.Pile = Kind == SectionKind.Pile ? p.Pile : current.Pile; p.Wall = Kind == SectionKind.Wall ? p.Wall : current.Wall;
            p.Custom = Kind == SectionKind.Custom ? p.Custom : current.Custom; }
        Load(p);
    }

    [RelayCommand]
    async Task Open()
    {
        if (Storage?.Invoke() is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Открыть проект", FileTypeFilter = [ProjectType],
            SuggestedStartLocation = await StartFolder(storage) });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            Load(ProjectStore.Deserialize(await reader.ReadToEndAsync()));
            ProfileMode = false;
            if (files[0].TryGetLocalPath() is { } path) { session?.Remember(path); RefreshRecent(); }
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
            SuggestedStartLocation = await StartFolder(storage),
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(ProjectStore.Serialize(p));
        if (file.TryGetLocalPath() is { } path) { session?.Remember(path); RefreshRecent(); }
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
            SuggestedStartLocation = await StartFolder(storage),
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
            // The report opens in Word at once.
            if (file.TryGetLocalPath() is { } path)
            {
                session?.RememberFolder(path);
                OpenFile?.Invoke(path);
            }
        }
        catch (Exception ex) { ShowErrors([$"Не удалось сохранить отчёт: {ex.Message}"]); }
    }

    /// <summary>The selection table belongs in the report only when the checked section is one of its candidates.</summary>
    bool SelectionMatches(CheckResult r) => lastSelection?.Outcomes.Any(o => Fingerprint(o.Candidate.Project) == Fingerprint(r.Project)) == true;

    static string Fingerprint(SectionProject p) => System.Text.Json.JsonSerializer.Serialize(new
    {
        p.Kind, Geometry = p.Kind switch { SectionKind.Pile => (object)p.Pile, SectionKind.Wall => p.Wall, _ => p.Custom }, p.ConcreteClass, p.RebarClass,
        p.TransverseClass, p.Concrete, p.LongitudinalFactor, p.TransverseFactor, p.RscByTable, p.Seismic, p.Special, p.Conditions, p.Loads,
    });

    static string Safe(string name) => string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}
