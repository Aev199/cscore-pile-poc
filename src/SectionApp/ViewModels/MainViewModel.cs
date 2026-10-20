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
        nameof(SelectedLoad), nameof(AxisText), nameof(L0Text), nameof(HasGoverning), nameof(CombinationHeaders),
        nameof(ProfileMode), nameof(IsSectionMode), nameof(IsProfileMode), nameof(LimitedWidth), nameof(InputTab), nameof(SeismicUsed), nameof(SpecialUsed), nameof(SpecialDesign),
    ];

    CancellationTokenSource? cancel;
    CheckResult? lastResult;
    SelectionResult? lastSelection;
    bool loading;

    public MainViewModel()
    {
        Profile = new ProfileViewModel(this);
        Load(SectionProject.Example(SectionKind.Pile));
    }

    /// <summary>Module «Эпюры (PLAXIS)»; it takes the section from this view model.</summary>
    public ProfileViewModel Profile { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSectionMode), nameof(IsProfileMode))] bool profileMode;
    public bool IsSectionMode => !ProfileMode;
    public bool IsProfileMode => ProfileMode;
    partial void OnProfileModeChanged(bool value) { if (value) Profile.RefreshForces(); }

    [RelayCommand] void ShowSection() => ProfileMode = false;
    [RelayCommand] void ShowProfile() => ProfileMode = true;

    /// <summary>Set by the window: file dialogs need the top-level storage provider.</summary>
    public Func<IStorageProvider?>? Storage { get; set; }

    public IReadOnlyList<string> ConcreteClasses { get; } = MaterialTables.Concretes.Select(c => c.Name).ToList();
    public IReadOnlyList<string> RebarClasses { get; } = MaterialTables.Rebars.Select(r => r.Name).ToList();
    public IReadOnlyList<string> SecondOrderOptions { get; } =
        ["Учитывать (п. 8.1.15)", "Учтено в усилиях", "Не учитывать"];
    public IReadOnlyList<string> CrackCategoryOptions { get; } = ["Ограниченное раскрытие трещин", "Трещины не допускаются"];
    public IReadOnlyList<string> CrackRequirementOptions { get; } = ["Сохранность арматуры", "Ограничение проницаемости"];
    public IReadOnlyList<string> CrackMethodOptions { get; } = ["Консервативный", "Уточнённый"];

    /// <summary>Set by the window: clipboard text for "paste from Excel".</summary>
    public Func<Task<string?>>? ClipboardText { get; set; }

    // ---- section ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWall), nameof(KindTitle))] bool isPile = true;
    public bool IsWall => !IsPile;
    public string KindTitle => IsPile ? "Буронабивная свая" : "Стена в грунте";
    [ObservableProperty] string title = "";
    [ObservableProperty] string pileDiameter = "", pileCover = "", pileSpiral = "", pileBarCount = "", pileBarDiameter = "";
    [ObservableProperty] string wallWidth = "", wallThickness = "", wallCover = "", wallStirrup = "";
    [ObservableProperty] string positiveCount = "", positiveDiameter = "", negativeCount = "", negativeDiameter = "";
    [ObservableProperty] string minClearSpacing = "";
    public IReadOnlyList<string> CoverReferenceOptions { get; } = ["до продольных стержней (в свету)", "до спирали / хомута", "до оси продольных стержней"];
    [ObservableProperty] int coverReferenceIndex;
    [ObservableProperty] string axisText = "";

    static CoverReference CoverOf(int index) => index switch { 1 => CoverReference.Transverse, 2 => CoverReference.Axis, _ => CoverReference.Bar };
    static int IndexOf(CoverReference r) => r switch { CoverReference.Transverse => 1, CoverReference.Axis => 2, _ => 0 };

    // ---- materials ----
    [ObservableProperty] string concreteClass = "B30", rebarClass = "A500", transverseClass = "A240";
    [ObservableProperty] string gb1 = "", gb2 = "", gb3 = "", gb5 = "", hardening = "", gammaS = "", gammaSw = "";

    // ---- element and design conditions ----
    [ObservableProperty, NotifyPropertyChangedFor(nameof(L0Text))] string elementLength = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(L0Text))] string muX = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(L0Text))] string muY = "";
    public string L0Text => TryNumber(ElementLength, out var l) && TryNumber(MuX, out var mx) && TryNumber(MuY, out var my)
        ? $"l₀ в плоскости Mx = {S(l * mx)} м, в плоскости My = {S(l * my)} м" : "l₀ = μ · L";
    [ObservableProperty] string slendernessLimit = "";
    [ObservableProperty] bool staticallyDeterminate;
    [ObservableProperty] bool eccentricitySp63 = true;
    [ObservableProperty] string eaX = "", eaY = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EtaByCscore))] int secondOrderIndex;
    public bool EtaByCscore => SecondOrderIndex == 0;
    [ObservableProperty] string reliabilityUls = "", reliabilitySls = "";

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
    [ObservableProperty] string specialGb = "", specialGs = "", specialGsw = "", specialAdditional = "";
    public bool SeismicUsed => Loads.Any(l => l.Seismic);
    public bool SpecialUsed => Loads.Any(l => l.Special);
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
    public ObservableCollection<CombinationRowViewModel> CombinationRows { get; } = [];
    [ObservableProperty] string[] combinationHeaders = ["Прочность", "N/Ncr", "Q", "acrc, прод.", "acrc, непрод."];
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
    [ObservableProperty] string verdictText = "Расчёт не выполнен";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VerdictOk), nameof(VerdictBad), nameof(VerdictWarn), nameof(VerdictInfo))]
    string verdictTone = "info";
    public bool VerdictOk => VerdictTone == "ok";
    public bool VerdictBad => VerdictTone == "bad";
    public bool VerdictWarn => VerdictTone == "warn";
    public bool VerdictInfo => VerdictTone == "info";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasGoverning))] string governingText = "";
    public bool HasGoverning => GoverningText.Length > 0;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasInputErrors))] string inputErrors = "";
    public bool HasInputErrors => InputErrors.Length > 0;
    [ObservableProperty] string selectionSummary = "";
    [ObservableProperty] bool hasSelection;
    [ObservableProperty] int selectedTab;
    [ObservableProperty] int inputTab;
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
        RefreshSketch();
    }

    void RefreshSketch()
    {
        var p = TryBuild(out var errors, quiet: true);
        InputErrors = string.Join("\n", errors);
        if (p is null) return;
        var layout = RequestBuilder.Layout(p);
        SteelText = $"As = {layout.SteelAreaMm2.ToString("0", Ru)} мм²" + (layout.Problem is string problem ? $" · {problem}" : "");
        AxisText = p.Kind == SectionKind.Pile
            ? $"От грани до оси стержней {p.Pile.AxisDistanceMm.ToString("0.#", Ru)} мм, до спирали {p.Pile.SpiralCoverMm.ToString("0.#", Ru)} мм"
            : $"От грани до оси стержней {p.Wall.AxisDistanceMm(p.Wall.PositiveFace).ToString("0.#", Ru)} мм («+»), {p.Wall.AxisDistanceMm(p.Wall.NegativeFace).ToString("0.#", Ru)} мм («−»)";
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
            CoverReferenceIndex = IndexOf(IsPile ? p.Pile.CoverReference : p.Wall.CoverReference);
            ConcreteClass = p.ConcreteClass; RebarClass = p.RebarClass; TransverseClass = p.TransverseClass;
            var f = p.Concrete;
            Gb1 = S(f.Gb1); Gb2 = S(f.Gb2); Gb3 = S(f.Gb3); Gb5 = S(f.Gb5); Hardening = S(f.HardeningFactor);
            GammaS = S(p.LongitudinalFactor); GammaSw = S(p.TransverseFactor);
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
            SelectionCountMin = S(IsPile ? s.PileCountMin : s.WallCountMin); SelectionCountMax = S(IsPile ? s.PileCountMax : s.WallCountMax);
            SelectionCountStep = S(s.PileCountStep); SelectionSymmetric = s.WallSymmetric;
            SelectionDiameters = string.Join(", ", s.Diameters.Select(d => S(d)));
            Loads.Clear();
            foreach (var l in p.Loads) Loads.Add(LoadRowViewModel.From(l, this));
            SelectedLoad = Loads.FirstOrDefault();
        }
        finally { loading = false; }
        OnPropertyChanged(nameof(SeismicUsed)); OnPropertyChanged(nameof(SpecialUsed));
        ClearResults();
        RefreshSketch();
    }

    /// <summary>Project from the fields; null with readable messages when a value is not a number.</summary>
    internal SectionProject? TryBuild(out List<string> errors, bool quiet = false)
    {
        var e = new List<string>();
        double D(string text, string label)
        {
            if (TryNumber(text, out var v)) return v;
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
            ConcreteClass = ConcreteClass, RebarClass = RebarClass, TransverseClass = TransverseClass,
            Concrete = new ConcreteFactors
            {
                Gb1 = D(Gb1, "γb1"), Gb2 = D(Gb2, "γb2"), Gb3 = D(Gb3, "γb3"), Gb5 = D(Gb5, "γb5"), HardeningFactor = D(Hardening, "Коэффициент упрочнения"),
            },
            LongitudinalFactor = D(GammaS, "γs продольной"), TransverseFactor = D(GammaSw, "γs поперечной"),
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
        if (IsPile)
            p.Pile = new PileGeometry
            {
                DiameterMm = D(PileDiameter, "Диаметр D"), CoverMm = D(PileCover, "Защитный слой"), CoverReference = CoverOf(CoverReferenceIndex),
                SpiralDiameterMm = D(PileSpiral, "Ø навивки"),
                BarCount = I(PileBarCount, "Число стержней"), BarDiameterMm = D(PileBarDiameter, "Ø стержней"), MinClearSpacingMm = clear,
            };
        else
            p.Wall = new WallGeometry
            {
                WidthMm = D(WallWidth, "Ширина b"), ThicknessMm = D(WallThickness, "Толщина h"), CoverMm = D(WallCover, "Защитный слой"),
                CoverReference = CoverOf(CoverReferenceIndex),
                StirrupDiameterMm = D(WallStirrup, "Ø хомута"), MinClearSpacingMm = clear,
                PositiveFace = new WallFace { BarCount = I(PositiveCount, "Стержней на грани «+»"), BarDiameterMm = D(PositiveDiameter, "Ø на грани «+»") },
                NegativeFace = new WallFace { BarCount = I(NegativeCount, "Стержней на грани «−»"), BarDiameterMm = D(NegativeDiameter, "Ø на грани «−»") },
            };
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

    /// <summary>Number in Russian or invariant notation ("1,15" or "1.15"); spaces as thousand separators.</summary>
    internal static bool TryNumber(string? text, out double value)
    {
        value = 0;
        if (text is null) return false;
        string t = text.Trim().Replace("\u00A0", "").Replace(" ", "").Replace(',', '.').Replace('−', '-');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    // ---- commands ----

    [RelayCommand] void SetPile() => SwitchKind(true);
    [RelayCommand] void SetWall() => SwitchKind(false);

    void SwitchKind(bool pile)
    {
        if (IsPile == pile) return;
        var current = TryBuild(out _, quiet: true);
        var example = SectionProject.Example(pile ? SectionKind.Pile : SectionKind.Wall);
        if (current is not null)
        {
            example.Loads = current.Loads; example.Conditions = current.Conditions; example.ConcreteClass = current.ConcreteClass;
            example.RebarClass = current.RebarClass; example.TransverseClass = current.TransverseClass; example.Concrete = current.Concrete;
            example.LongitudinalFactor = current.LongitudinalFactor; example.TransverseFactor = current.TransverseFactor;
            example.Seismic = current.Seismic; example.Special = current.Special;
        }
        Load(example);
    }

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
        // A new row takes γf, kдл and the flags of the selected one: combinations usually share them.
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
        StatusText = "Моменты Mx и My переставлены во всех сочетаниях";
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
            StatusText = x.Label.Length > 0 ? $"Подбор: {x.Label} ({x.Done + 1}/{x.Total})" : "Подбор завершён";
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
        GoverningText = ReportWriter.GoverningText(r);
        SummaryRows.Clear();
        foreach (var row in ReportWriter.SummaryRows(r)) SummaryRows.Add(CheckRowViewModel.From(row, r));
        CombinationRows.Clear();
        foreach (var line in ReportWriter.Combinations(r)) CombinationRows.Add(new CombinationRowViewModel(line));
        CombinationHeaders = ReportWriter.CombinationHeaders(r);
        StatusText = $"Расчёт выполнен · {DateTime.Now:HH:mm}";
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
        if (s.GeometricallyRejected > 0) SelectionSummary += $" Не помещаются в сечении: {s.GeometricallyRejected}.";
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
        SummaryRows.Clear(); CombinationRows.Clear(); SelectionRows.Clear();
        VerdictText = "Расчёт не выполнен"; VerdictTone = "info"; SelectionSummary = "";
        GoverningText = "";
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
        p.Concrete, p.LongitudinalFactor, p.TransverseFactor, p.Seismic, p.Special, p.Conditions, p.Loads,
    });

    static string Safe(string name) => string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}
