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
    /// <summary>Extremes of this phase (branch) alone: which one drives the envelope.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasExtremes))] string extremes = "";
    public bool HasExtremes => Extremes.Length > 0;
    partial void OnIncludeChanged(bool value) { Setting.Include = value; changed(); }
    partial void OnKindIndexChanged(int value)
    {
        Setting.ShortTerm = value == 1; Setting.Seismic = value == 2; Setting.Special = value == 3;
        changed();
    }
}

/// <summary>One peak cut zone in the editor.</summary>
public partial class PeakCutViewModel : ObservableObject
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    readonly Action changed;
    readonly Action<PeakCutViewModel> remove;

    public PeakCutViewModel(PeakCut c, Action changed, Action<PeakCutViewModel> remove)
    {
        this.changed = changed; this.remove = remove;
        top = c.TopLevel.ToString("0.###", Ru); bottom = c.BottomLevel.ToString("0.###", Ru);
        moment = c.Moment; shear = c.Shear; methodIndex = c.Method == PeakCutMethod.Limit ? 1 : 0;
        momentLimit = c.MomentLimit.ToString("0.#", Ru); shearLimit = c.ShearLimit.ToString("0.#", Ru);
    }

    public static IReadOnlyList<string> MethodOptions { get; } = ["по хорде", "до значения"];
    public IReadOnlyList<string> Methods => MethodOptions;
    [ObservableProperty] string top, bottom, momentLimit, shearLimit;
    [ObservableProperty] bool moment, shear;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsLimit))] int methodIndex;
    public bool IsLimit => MethodIndex == 1;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(IsLimit)) changed();
    }

    [RelayCommand] void Remove() => remove(this);

    public PeakCut? ToModel(List<string> e, int index)
    {
        double D(string text, string label)
        {
            if (MainViewModel.TryNumber(text, out var v)) return v;
            e.Add($"Срезка {index}: «{label}» — введите число"); return 0;
        }
        var c = new PeakCut
        {
            TopLevel = D(Top, "от отметки"), BottomLevel = D(Bottom, "до отметки"), Moment = Moment, Shear = Shear,
            Method = IsLimit ? PeakCutMethod.Limit : PeakCutMethod.Chord,
            MomentLimit = IsLimit && Moment ? D(MomentLimit, "предел M") : 0, ShearLimit = IsLimit && Shear ? D(ShearLimit, "предел Q") : 0,
        };
        if (!Moment && !Shear) e.Add($"Срезка {index}: отметьте M и/или Q");
        if (Math.Abs(c.TopLevel - c.BottomLevel) < 1e-6) e.Add($"Срезка {index}: отметки совпадают");
        if (IsLimit && (Moment && c.MomentLimit < 0 || Shear && c.ShearLimit < 0)) e.Add($"Срезка {index}: предел не может быть отрицательным");
        return c;
    }
}

/// <summary>One pile type in the list: number, colour, piles, zones, steel and K.</summary>
public sealed class TypeRowViewModel(PileType t)
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    public PileType Type => t;
    public Avalonia.Media.IBrush Color { get; } = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromUInt32(
        (uint)TypingChart.Color(t.Number).Alpha << 24 | (uint)TypingChart.Color(t.Number).Red << 16 | (uint)TypingChart.Color(t.Number).Green << 8 | TypingChart.Color(t.Number).Blue));
    public string Title => $"Т{t.Number} · {t.Members.Count} св. · {t.Head.ToString("0.00", Ru)}…{t.Tip.ToString("0.00", Ru)}";
    public string Bars => string.Join(" → ", t.Zones.Select(z => z.Bars.ToString()));
    public string Detail => $"{t.SteelKg / Math.Max(1, t.Members.Count):0} кг/св. · K {ReportWriter.Utilization(t.K)}" + (t.Locked ? " · закреплён" : "");
    public string Number => $"Т{t.Number}";
    public int Count => t.Members.Count;
    public string Levels => $"{t.Head.ToString("0.00", Ru)}…{t.Tip.ToString("0.00", Ru)}";
    public string Spiral => string.Join(" → ", t.Zones.Select(z => z.SpiralMm is double d ? $"Ø{d:0}" : "—").Distinct());
    public string Steel => (t.SteelKg / Math.Max(1, t.Members.Count)).ToString("0", Ru);
    public string K => ReportWriter.Utilization(t.K);
    public string Locked => t.Locked ? "да" : "";
    public bool IsBad => t.K is null or > 1;
    public string Tip => string.Join(", ", t.Members.Select(m => m.Info.Name));
}

/// <summary>One pile in the table: its type, own demand and how much the type gives it.</summary>
public sealed class PileRowViewModel(PileDemand p, PileType t)
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    public PileDemand Pile => p;
    public string Name => p.Info.Name;
    public string Type => $"Т{t.Number}";
    public int TypeNumber => t.Number;
    public string Levels => $"{p.Info.Head.ToString("0.00", Ru)}…{p.Info.Tip.ToString("0.00", Ru)}";
    public string AsMax => (p.As.Max() / 100).ToString("0.0", Ru);
    /// <summary>Stations where this pile sets the envelope of its type (above the minimum).</summary>
    public int Governs => p.As.Select((a, i) => (a, i)).Count(x => x.i < t.As.Length && x.a >= t.As[x.i] * 0.999 && x.a > p.As.Min() * 1.01);
    public string GovernsText => Governs > 0 ? $"{Governs} сеч." : "";
    /// <summary>Steel of the type cage over the pile's own required steel.</summary>
    public string Excess
    {
        get
        {
            double own = t.OwnSteelKg / Math.Max(1, t.Members.Count);
            return own > 0 ? $"+{(t.SteelKg / t.Members.Count / OwnOf() - 1) * 100:0} %" : "";
        }
    }
    double OwnOf()
    {
        double kg = 0;
        for (int s = 0; s < p.Stations.Count; s++) kg += p.As[s] * 1e-6 * (p.Stations[s].Top - p.Stations[s].Bottom) * 7850;
        return Math.Max(kg, 1e-9);
    }
}

public sealed class StationRowViewModel(StationResult s, bool wall)
{
    public StationResult Result => s;
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
        inputPort = settings.PlaxisInputPort.ToString(CultureInfo.InvariantCulture);
        LoadSettings();
    }

    public Func<IStorageProvider?>? Storage { get; set; }

    public static IReadOnlyList<string> ForceUnits { get; } = ["kN", "N", "MN", "tf"];
    public static IReadOnlyList<string> LengthUnits { get; } = ["m", "mm", "cm"];
    public IReadOnlyList<string> ForceUnitOptions => ForceUnits;
    public IReadOnlyList<string> LengthUnitOptions => LengthUnits;
    public IReadOnlyList<string> MomentSignOptions { get; } = ["правую грань (бóльший X)", "левую грань (меньший X)"];
    public IReadOnlyList<string> ExcavationOptions { get; } = ["не задано", "справа (бóльший X)", "слева (меньший X)"];
    public IReadOnlyList<string> ScenarioOptions { get; } = ["СВГ — PLAXIS 2D, на 1 п.м × захватка", "Сваи ограждения — PLAXIS 2D, на 1 п.м × шаг", "Сваи — PLAXIS 3D, на сваю"];
    public IReadOnlyList<string> ForcesOptions { get; } = ["Огибающие PLAXIS", "Усилия выбранных стадий"];
    public IReadOnlyList<string> ModeOptions { get; } = ["Подбор армирования", "Проверка сечения", "Типизация свай (3D): каждая отдельно → типы"];

    // ---- connection ----
    [ObservableProperty] string pythonPath;
    [ObservableProperty] string port;
    [ObservableProperty] string inputPort;
    [ObservableProperty] bool showOutputDirect;
    /// <summary>0 — Output envelopes per branch; 1 — forces of every stage (pick the stages in the list).</summary>
    [ObservableProperty] int forcesIndex;
    [ObservableProperty] string password = "";
    public ObservableCollection<PlaxisObjectViewModel> Objects { get; } = [];
    public ObservableCollection<PhaseRowViewModel> Phases { get; } = [];
    [ObservableProperty] string sourceText = "Усилия не загружены";
    [ObservableProperty] bool hasObjects;
    [ObservableProperty] bool hasPhases;
    public string RightHeader => !IsWall ? "As" : ExcavationIndex switch { 1 => "As котл.", 2 => "As грунт", _ => "As прав." };
    public string LeftHeader => ExcavationIndex switch { 1 => "As грунт", 2 => "As котл.", _ => "As лев." };

    // ---- settings ----
    [ObservableProperty] string forceUnit = "kN", lengthUnit = "m";
    [ObservableProperty] string pileSpacing = "", panelWidth = "", stationStep = "", minPercent = "", serviceDivisor = "", sustainedShare = "";
    [ObservableProperty] int momentSignIndex;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(RightHeader), nameof(LeftHeader))] int excavationIndex;
    [ObservableProperty] string excavationText = "";
    [ObservableProperty] bool alignPileHeads = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsWall), nameof(Is3D), nameof(ShowPileSpacing), nameof(RightHeader))] int scenarioIndex;
    public ProfileScenario Scenario => (ProfileScenario)ScenarioIndex;
    public bool Is3D => Scenario == ProfileScenario.Piles3D;
    public bool ShowPileSpacing => Scenario == ProfileScenario.RetainingPiles;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCheck), nameof(IsTyping), nameof(IsStations), nameof(TableTitle), nameof(ExportText), nameof(HasTyping), nameof(HasNoTyping))] int modeIndex;
    public bool IsCheck => ModeIndex == 1;
    public bool IsTyping => ModeIndex == 2;
    public bool IsStations => !IsTyping;
    public string TableTitle => IsTyping ? "Типы свай" : IsCheck ? "Проверка сечения" : "Требуемое армирование";
    public string ExportText => IsTyping ? "Сохранить таблицу…" : "Сохранить для Revit…";
    [ObservableProperty] string typeCount = "3";
    public ObservableCollection<TypeRowViewModel> Types { get; } = [];
    public ObservableCollection<PileRowViewModel> PileRows { get; } = [];
    [ObservableProperty] TypeRowViewModel? selectedType;
    PileDemands? demands;
    string? demandsKey;
    TypingResult? typing;
    /// <summary>Types checked by the engine, by their piles: an edit re-checks only the changed types.</summary>
    readonly Dictionary<string, PileType> verified = [];
    /// <summary>The typing result for the plan, the cages and the curve.</summary>
    public TypingResult? Typing => typing;
    public bool HasTyping => typing is not null && IsTyping;
    public bool HasNoTyping => !HasTyping;
    [ObservableProperty] bool showSettings = true;
    [ObservableProperty] int selectedTypeNumber;
    [ObservableProperty] IReadOnlyList<int> selectedPiles = [];
    [ObservableProperty] int curveCount;
    [ObservableProperty] int colorModeIndex;
    public IReadOnlyList<string> ColorModes { get; } = ["Цвет — тип", "Цвет — требуемая As"];
    [ObservableProperty] string selectionText = "";
    public ObservableCollection<string> TypeTargets { get; } = [];
    [ObservableProperty] int targetIndex;
    [ObservableProperty] PileRowViewModel? selectedPileRow;
    bool syncing;
    [ObservableProperty] string momentSignText = "";
    [ObservableProperty] bool ignoreAxialForce;
    [ObservableProperty] bool skipInactiveZeroN = true;
    [ObservableProperty] string zeroNText = "";
    public ObservableCollection<PeakCutViewModel> PeakCuts { get; } = [];
    [ObservableProperty] bool confirmed;
    public bool IsWall => Scenario == ProfileScenario.Wall;
    /// <summary>The Q check of the section module: with it the required Asw/sw is found along the element.</summary>
    public bool ShearCheck
    {
        get => main.ShearCheck;
        set { if (main.ShearCheck == value) return; main.ShearCheck = value; OnPropertyChanged(); Changed(); }
    }
    /// <summary>Constructive requirements of the transverse bars confirmed: without it Q is not checked.</summary>
    public bool ShearConfirmed
    {
        get => main.ShearConfirmed;
        set { if (main.ShearConfirmed == value) return; main.ShearConfirmed = value; OnPropertyChanged(); Changed(); }
    }
    public string ShearText => $"Шаг {main.ShearPitch} мм" + (IsWall ? $", ветвей {main.StirrupLegs}" : ", спираль") + " — в режиме «Сечение» → Расчёт";
    [ObservableProperty] string sectionText = "";

    // ---- state ----
    [ObservableProperty] Bitmap? chart;
    public ObservableCollection<StationRowViewModel> Stations { get; } = [];
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(LoadSelectionCommand), nameof(LoadForcesCommand), nameof(CalculateCommand), nameof(ExportRevitCommand), nameof(MovePilesCommand), nameof(NewTypeCommand), nameof(ToggleLockCommand), nameof(Open3DCommand))]
    bool isBusy;
    [ObservableProperty] double progress;
    [ObservableProperty] string status = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasErrors))] string errors = "";
    public bool HasErrors => Errors.Length > 0;
    [ObservableProperty] bool hasResult;
    [ObservableProperty] bool resultIsStale;
    [ObservableProperty] string summary = "";
    /// <summary>The station shown in the section: bars for its required area (or the given bars in the check).</summary>
    [ObservableProperty] StationRowViewModel? selectedStation;
    [ObservableProperty] Bitmap? stationSketch;
    [ObservableProperty] string stationText = "";

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!loading && e.PropertyName is nameof(ForceUnit) or nameof(LengthUnit) or nameof(PileSpacing) or nameof(StationStep)
                or nameof(MinPercent) or nameof(ServiceDivisor) or nameof(SustainedShare) or nameof(MomentSignIndex) or nameof(Confirmed)
                or nameof(IgnoreAxialForce) or nameof(ExcavationIndex) or nameof(AlignPileHeads) or nameof(PanelWidth) or nameof(ModeIndex)
                or nameof(TypeCount) or nameof(SkipInactiveZeroN))
            Changed();
    }

    void LoadSettings()
    {
        loading = true;
        var s = project.Settings;
        ForceUnit = s.ForceUnit; LengthUnit = s.LengthUnit; PileSpacing = S(s.PileSpacingM); StationStep = S(s.StationStepM);
        MinPercent = S(s.MinPercent); ServiceDivisor = S(s.ServiceDivisor); SustainedShare = S(s.SustainedShare);
        MomentSignIndex = s.MomentSign > 0 ? 0 : 1; Confirmed = s.Confirmed; IgnoreAxialForce = s.IgnoreAxialForce;
        ExcavationIndex = Excavation(s.ExcavationSide); AlignPileHeads = s.AlignPileHeads; SkipInactiveZeroN = s.SkipInactiveZeroN;
        ScenarioIndex = (int)s.Scenario; ModeIndex = (int)s.Mode; TypeCount = s.TypeCount.ToString(CultureInfo.InvariantCulture);
        PanelWidth = S(s.PanelWidthM > 0 ? s.PanelWidthM : project.Section.Wall.WidthMm / 1000);
        PeakCuts.Clear();
        foreach (var c in s.PeakCuts) PeakCuts.Add(new PeakCutViewModel(c, Changed, RemoveCut));
        loading = false;
    }

    static string S(double v) => v.ToString("0.###", Ru);
    static string Whole(double v) => Math.Round(v) == 0 ? "0" : v.ToString("0", Ru);
    /// <summary>The scenario decides the section kind: a wall or a round pile in the section module.</summary>
    partial void OnScenarioIndexChanged(int value)
    {
        main.IsPile = Scenario != ProfileScenario.Wall;
        OnPropertyChanged(nameof(ShearText));
        if (!loading) Changed();
    }

    static int Excavation(int side) => side switch { 1 => 1, -1 => 2, _ => 0 };

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
        s.MomentSign = MomentSignIndex == 0 ? 1 : -1; s.Confirmed = Confirmed; s.IgnoreAxialForce = IgnoreAxialForce;
        s.ExcavationSide = ExcavationIndex switch { 1 => 1, 2 => -1, _ => 0 }; s.AlignPileHeads = AlignPileHeads; s.SkipInactiveZeroN = SkipInactiveZeroN;
        s.Scenario = Scenario; s.Mode = (ProfileMode)ModeIndex;
        if (IsTyping)
        {
            if (int.TryParse(TypeCount.Trim(), out int k) && k >= 1) s.TypeCount = k; else e.Add("Число типов — целое число от 1");
            if (Scenario != ProfileScenario.Piles3D) e.Add("Типизация — для свай из PLAXIS 3D (сценарий «Сваи — PLAXIS 3D»)");
        }
        if (IsWall) { s.PanelWidthM = D(PanelWidth, "Ширина захватки"); if (s.PanelWidthM <= 0) e.Add("Ширина захватки должна быть больше 0"); }
        else if (Scenario == ProfileScenario.RetainingPiles && s.PileSpacingM <= 0) e.Add("Шаг свай должен быть больше 0");
        s.PeakCuts = PeakCuts.Select((c, i) => c.ToModel(e, i + 1)).OfType<PeakCut>().ToList();
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
        // Diaphragm wall: the section is one panel, its width b is the panel width.
        if (IsWall && project.Settings.PanelWidthM > 0) project.Section.Wall.WidthMm = project.Settings.PanelWidthM * 1000;
        SectionText = SectionSummary(project.Section);
        if (result is not null && Key() != resultKey) ResultIsStale = true;
        try
        {
            forces = ForceProfile.Build(project);
            Errors = "";
            ZeroNText = forces.InactiveZeroN.Count == 0 ? ""
                : $"Nmax огибающей = 0 по всей длине: {forces.InactiveZeroN.Count} (свая, ветка) — стадии до устройства сваи; угол «N = 0 с Mmax» не учтён";
            var extremes = ForceProfile.PhaseExtremes(project);
            foreach (var row in Phases)
                row.Extremes = extremes.TryGetValue(row.Setting.Id, out var x)
                    ? $"N {Whole(x.NMin)}…{Whole(x.NMax)} кН · |M| ≤ {Whole(x.MAbs)} · |Q| ≤ {Whole(x.QAbs)}" : "нет усилий выбранных элементов";
            Draw();
        }
        catch (InvalidOperationException ex) { Errors = ex.Message; Chart = null; }
    }

    void Draw()
    {
        if (forces is null) return;
        // Typing has its own plan and cages; here the diagrams along the element.
        using var stream = new MemoryStream(ProfileChart.Png(forces, ResultIsStale ? null : result, 1000, 1000, 16));
        Chart = new Bitmap(stream);
    }

    /// <summary>Settings and data the pile demands depend on (not the number of types).</summary>
    string DemandKey()
    {
        var s = project.Settings;
        var (count, mode) = (s.TypeCount, s.Mode);
        (s.TypeCount, s.Mode) = (0, ProfileMode.Typing);
        string key = Key();
        (s.TypeCount, s.Mode) = (count, mode);
        return key;
    }

    async Task CalculateTyping()
    {
        var p = project;
        int count = p.Settings.TypeCount;
        // Types locked by the engineer stay (with the same demands).
        var locked = typing is not null && demands is not null && demandsKey == DemandKey()
            ? typing.Types.Where(t => t.Locked).Select(t => (IReadOnlyList<int>)t.Members.Select(m => m.Pile).ToList()).ToList() : [];
        await Busy("Типизация свай…", async token =>
        {
            string dkey = DemandKey();
            if (demands is null || demandsKey != dkey)
            {
                var progress = new Progress<(int Done, int Total)>(x =>
                {
                    Progress = 100.0 * x.Done / x.Total;
                    Status = $"Таблицы требуемой арматуры (расчёт ядра): {x.Done} из {x.Total}";
                });
                demands = await Task.Run(() => PileTyping.Demand(p, progress, token), token);
                demandsKey = dkey;
                verified.Clear();
                locked = [];
            }
            var d = demands;
            var grouped = await Task.Run(() => PileTyping.Group(d, count, locked), token);
            await Apply(grouped, token);
        });
    }

    /// <summary>
    /// A new partition (automatic or edited): types with the same piles keep their checked zones, the others are
    /// checked by the engine.
    /// </summary>
    async Task Apply(TypingResult r, CancellationToken token)
    {
        r = r with { Types = r.Types.Select(t => verified.TryGetValue(t.Key, out var v) ? v with { Number = t.Number, Locked = t.Locked } : t).ToList() };
        var check = new Progress<(int Done, int Total)>(x =>
        {
            Progress = 100.0 * x.Done / x.Total;
            Status = $"Проверка зон ядром: {x.Done} из {x.Total}";
        });
        r = await Task.Run(() => PileTyping.Verify(r, check, token, t => !t.Verified), token);
        foreach (var t in r.Types) verified[t.Key] = t;
        typing = r;
        result = null;
        resultKey = Key();
        ShowTyping();
    }

    void ShowTyping()
    {
        if (typing is null) return;
        int keep = SelectedTypeNumber;
        Stations.Clear(); SelectedStation = null;
        Types.Clear(); PileRows.Clear(); TypeTargets.Clear();
        foreach (var t in typing.Types)
        {
            Types.Add(new TypeRowViewModel(t));
            TypeTargets.Add($"Т{t.Number}");
            foreach (var m in t.Members) PileRows.Add(new PileRowViewModel(m, t));
        }
        HasResult = true; ResultIsStale = false;
        var d = typing.Demands;
        double? k = typing.Types.Any(t => t.K is null) ? null : typing.Types.Max(t => t.K);
        Summary = $"{d.Piles.Count} свай → {typing.Types.Count} типов: сталь {typing.SteelKg / 1000:0.00} т; "
            + $"каркас на каждую сваю — {typing.SeparateSteelKg / 1000:0.00} т (типизация +{typing.Overspend * 100:0.0} %); "
            + $"проверка зон ядром: Kmax {ReportWriter.Utilization(k)}"
            + (d.Piles.Any(x => x.Exceeded.Any(e => e)) ? ". Есть сечения, где не хватает и полного кольца Ø40" : "")
            + (typing.Note is null ? "" : ". " + typing.Note);
        Status = $"Типизация: {typing.Types.Count} типов (таблицы: {d.TablePoints} точек)";
        syncing = true;
        CurveCount = typing.Types.Count;
        syncing = false;
        OnPropertyChanged(nameof(Typing)); OnPropertyChanged(nameof(HasTyping)); OnPropertyChanged(nameof(HasNoTyping));
        SelectedTypeNumber = typing.Types.Any(t => t.Number == keep) ? keep : typing.Types.FirstOrDefault()?.Number ?? 0;
        SelectedPiles = SelectedPiles.Where(i => i < d.Piles.Count).ToList();
        ShowSettings = false;
    }

    partial void OnSelectedTypeNumberChanged(int value)
    {
        if (syncing) return;
        syncing = true;
        SelectedType = Types.FirstOrDefault(t => t.Type.Number == value);
        TargetIndex = Math.Max(0, Types.ToList().FindIndex(t => t.Type.Number == value));
        syncing = false;
        ShowTypeDetails();
    }

    partial void OnSelectedTypeChanged(TypeRowViewModel? value)
    {
        if (syncing || value is null) return;
        SelectedTypeNumber = value.Type.Number;
    }

    partial void OnSelectedPilesChanged(IReadOnlyList<int> value)
    {
        SelectionText = value.Count == 0 ? "Сваи не выбраны: щелчок или рамка на плане" : $"Выбрано свай: {value.Count}";
        if (syncing || value.Count != 1) return;
        syncing = true;
        SelectedPileRow = PileRows.FirstOrDefault(r => r.Pile.Pile == value[0]);
        syncing = false;
    }

    partial void OnSelectedPileRowChanged(PileRowViewModel? value)
    {
        if (syncing || value is null) return;
        syncing = true;
        SelectedPiles = [value.Pile.Pile];
        SelectedTypeNumber = value.TypeNumber;
        syncing = false;
        SelectedType = Types.FirstOrDefault(t => t.Type.Number == value.TypeNumber);
        ShowTypeDetails();
    }

    /// <summary>The steel curve: a click sets the number of types and gathers again.</summary>
    partial void OnCurveCountChanged(int value)
    {
        if (syncing || value <= 0 || typing is null || IsBusy) return;
        TypeCount = value.ToString(CultureInfo.InvariantCulture);
        CalculateCommand.Execute(null);
    }

    void ShowTypeDetails()
    {
        var t = typing?.Types.FirstOrDefault(x => x.Number == SelectedTypeNumber);
        if (t is null) { StationSketch = null; StationText = ""; return; }
        var strongest = t.Zones.MaxBy(z => z.Bars.AreaMm2)!;
        var section = ProjectStore.Deserialize(ProjectStore.Serialize(typing!.Demands.Project.Section));
        section.Pile.BarCount = strongest.Bars.Count; section.Pile.BarDiameterMm = strongest.Bars.DiameterMm;
        if (strongest.SpiralMm is double sd) section.Pile.SpiralDiameterMm = sd;
        using (var stream = new MemoryStream(SectionSketch.Png(section, 520, 420, 22))) StationSketch = new Bitmap(stream);
        StationText = $"Тип {t.Number}{(t.Locked ? " (закреплён)" : "")}: {string.Join(", ", t.Members.Select(m => m.Info.Name))}\n"
            + string.Join("\n", t.Zones.Select(z => $"{z.Top.ToString("0.00", Ru)}…{z.Bottom.ToString("0.00", Ru)}: {z.Bars}"
                + (z.SpiralMm is double d ? $", спираль Ø{d:0} шаг {typing.Demands.Project.Section.Conditions.ShearPitchMm:0}" : "")
                + (z.K is double k ? $", K {ReportWriter.Utilization(k)}" + (k > 1 ? $" ({z.Governing})" : "") : ", K не определён")));
    }

    // ---- manual editing of the types ----

    List<List<int>> Partition() => typing!.Types.Select(t => t.Members.Select(m => m.Pile).ToList()).ToList();

    async Task Edit(string what, Func<List<List<int>>, List<bool>, string?> change)
    {
        if (typing is null || demands is null) return;
        if (ResultIsStale) { Errors = "Данные изменены — выполните расчёт заново"; return; }
        var groups = Partition();
        var locked = typing.Types.Select(t => t.Locked).ToList();
        if (change(groups, locked) is string problem) { Errors = problem; return; }
        // Empty types disappear, the order of the others is kept.
        var keep = groups.Select((g, i) => (g, l: locked[i])).Where(x => x.g.Count > 0).ToList();
        TypingResult r;
        try { r = PileTyping.Build(demands, keep.Select(x => (IReadOnlyList<int>)x.g).ToList(), keep.Select(x => x.l).ToList()) with { Curve = typing.Curve }; }
        catch (InvalidOperationException ex) { Errors = ex.Message; return; }
        await Busy(what, token => Apply(r, token));
    }

    bool CanEdit() => !IsBusy;

    /// <summary>The selected piles into the type chosen in the list.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    Task MovePiles() => Edit("Перенос свай…", (groups, locked) =>
    {
        if (SelectedPiles.Count == 0) return "Выберите сваи на плане или в таблице";
        if (TargetIndex < 0 || TargetIndex >= groups.Count) return "Выберите тип";
        var target = groups[TargetIndex];
        int cls = demands!.Piles[target[0]].Class;
        if (SelectedPiles.Any(i => demands.Piles[i].Class != cls)) return "В один тип — только сваи с одинаковыми отметками оголовка и низа";
        foreach (var g in groups) g.RemoveAll(i => SelectedPiles.Contains(i));
        target.AddRange(SelectedPiles);
        return null;
    });

    /// <summary>The selected piles into a new type (splitting a type).</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    Task NewType() => Edit("Новый тип…", (groups, locked) =>
    {
        if (SelectedPiles.Count == 0) return "Выберите сваи на плане или в таблице";
        if (SelectedPiles.Select(i => demands!.Piles[i].Class).Distinct().Count() > 1) return "В один тип — только сваи с одинаковыми отметками оголовка и низа";
        foreach (var g in groups) g.RemoveAll(i => SelectedPiles.Contains(i));
        groups.Add([.. SelectedPiles]);
        locked.Add(false);
        return null;
    });

    /// <summary>Lock the selected type: the automatic gathering keeps it as it is.</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    Task ToggleLock() => Edit("Закрепление типа…", (groups, locked) =>
    {
        int i = typing!.Types.ToList().FindIndex(t => t.Number == SelectedTypeNumber);
        if (i < 0) return "Выберите тип";
        locked[i] = !locked[i];
        return null;
    });

    static string TypingCsv(TypingResult r)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Тип;Свая;X, м;Y, м;Оголовок, м;Низ, м");
        foreach (var t in r.Types)
            foreach (var m in t.Members)
                sb.AppendLine(string.Join(";", $"Т{t.Number}", m.Info.Name, m.Info.X.ToString("0.000", inv), m.Info.Y.ToString("0.000", inv),
                    m.Info.Head.ToString("0.000", inv), m.Info.Tip.ToString("0.000", inv)));
        sb.AppendLine();
        sb.AppendLine("Тип;Свай;Зона от, м;Зона до, м;Стержней;Ø, мм;As, см²;Спираль Ø, мм;K");
        foreach (var t in r.Types)
            foreach (var z in t.Zones)
                sb.AppendLine(string.Join(";", $"Т{t.Number}", t.Members.Count, z.Top.ToString("0.00", inv), z.Bottom.ToString("0.00", inv),
                    z.Bars.Count, z.Bars.DiameterMm.ToString("0", inv), (z.Bars.AreaMm2 / 100).ToString("0.00", inv),
                    z.SpiralMm?.ToString("0", inv) ?? "", z.K?.ToString("0.000", inv) ?? ""));
        return sb.ToString();
    }

    string Key() => ProjectStore.Serialize(project.Section) + JsonSerializer.Serialize(project.Settings) + JsonSerializer.Serialize(project.Phases);

    static string SectionSummary(SectionProject p)
    {
        static string Face(WallFace f) => (f.Rows > 1 ? $"{f.Rows}×" : "") + $"{f.BarCount}Ø{f.BarDiameterMm:0}";
        return p.Kind == SectionKind.Pile
            ? $"Сечение: БНС Ø{p.Pile.DiameterMm:0}, {p.Pile.BarCount}Ø{p.Pile.BarDiameterMm:0}, {p.ConcreteClass}, {p.RebarClass}"
            : $"Сечение: СВГ b×h = {p.Wall.WidthMm:0}×{p.Wall.ThicknessMm:0} (b — захватка), грань «+» {Face(p.Wall.PositiveFace)}, "
              + $"грань «−» {Face(p.Wall.NegativeFace)}, {p.ConcreteClass}, {p.RebarClass}";
    }

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

    int? PortNumber() => PortOf(Port, "Output");

    int? PortOf(string text, string program)
    {
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) && p is > 0 and < 65536) return p;
        Errors = $"Порт {program} — целое число от 1 до 65535"; return null;
    }

    void SaveSettings(int? input = null, int? output = null)
    {
        var s = AppSettings.Load();
        s.PlaxisPython = PythonPath;
        if (input is int i) s.PlaxisInputPort = i;
        if (output is int o) s.PlaxisPort = o;
        AppSettings.Save(s);
    }

    /// <summary>The pile or wall selected in PLAXIS Input; Input opens Output, Safety phases are left out.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task LoadSelection()
    {
        if (PortOf(InputPort, "Input") is not int port) return;
        await Busy("PLAXIS: выделение в Input, результаты из Output…", async token =>
        {
            // Progress of the script: the current step, so that a slow or stuck PLAXIS call is seen.
            var log = new Progress<string>(line => { lastStep = line; Status = "PLAXIS " + line; });
            // An Output left open by the previous load is reused: opening it again loads the whole model.
            int? reuse = int.TryParse(Port, NumberStyles.Integer, CultureInfo.InvariantCulture, out int last) ? last : null;
            var export = await PlaxisBridge.FromInput(PythonPath, port, Password, perPhase: ForcesIndex == 1, cancel: token,
                log: ((IProgress<string>)log).Report, reuseOutputPort: reuse);
            if (export.OutputPort is int used) Port = used.ToString(CultureInfo.InvariantCulture);
            SaveSettings(input: port, output: export.OutputPort);
            Adopt(export);
            Status = $"Загружено: {string.Join(", ", export.Selection)}";
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task Connect()
    {
        if (PortNumber() is not int port) return;
        await Busy("Подключение к PLAXIS Output…", async token =>
        {
            catalog = await PlaxisBridge.Catalog(PythonPath, port, Password, token);
            SaveSettings(output: port);
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
        loading = true;
        ScenarioIndex = (int)project.Settings.Scenario;
        loading = false;
        main.IsPile = Scenario != ProfileScenario.Wall;
        MomentSignIndex = project.Settings.MomentSign > 0 ? 0 : 1;
        ExcavationIndex = Excavation(project.Settings.ExcavationSide);
        ExcavationText = s.MomentSign?.Excavation is "right" or "left"
            ? $"Предложено по перемещениям стены (фаза {s.MomentSign.Phase}): стена смещается в сторону котлована"
            : "Укажите сторону котлована — от неё зависят названия граней в результатах и в JSON";
        MomentSignText = s.MomentSign is { } h
            ? $"По прогибу стены в фазе {h.Phase}: M > 0 растягивает {(h.Sign > 0 ? "правую" : "левую")} грань (совпадение {h.Agreement * 100:0} % узлов)"
                + (h.Agreement < 0.5 ? " — ненадёжно, проверьте по Output" : "")
            : "Не определено по прогибу — проверьте по эпюре Output";
        Phases.Clear();
        foreach (var ph in project.Phases)
        {
            var src = s.Phases.FirstOrDefault(x => x.Id == ph.Id);
            string label = src?.Label ?? ph.Name;
            if (src is { Covers.Count: > 1 }) label += $" · огибающая {src.Covers.Count} стадий";
            if (src?.CalcType is { Length: > 0 } calc && !calc.Contains("Plastic", StringComparison.OrdinalIgnoreCase)) label += $" · {calc}";
            Phases.Add(new PhaseRowViewModel(ph, label, Changed));
        }
        HasPhases = Phases.Count > 0;
        int piles = s.Objects.Select(o => o.Pile).Distinct().Count();
        string selected = s.Is3D && piles > 1 ? $"группа {piles} свай: {string.Join(", ", s.Selection)}"
            : s.Selection.Count > 0 ? $"{string.Join(", ", s.Selection)} ({s.Objects.Count} уч.)" : string.Join(", ", s.Objects.Select(o => o.Name));
        SourceText = $"{selected} · " + (s.IsEnvelope ? $"огибающие Output, веток {s.Phases.Count}" : $"фаз {s.Phases.Count}") + $" · PLAXIS {(s.Is3D ? "3D " : "")}{s.Info.Version} · {s.Info.UnitForce}, {s.Info.UnitLength}"
            + (s.ExcludedPhases.Count > 0 ? $"\nИсключены (Safety): {string.Join(", ", s.ExcludedPhases.Select(p => p.Name))}" : "")
            + (s.InactivePhases.Count > 0 ? $"\nЭлемент не активен: {string.Join(", ", s.InactivePhases)}" : "");
    }

    // ---- calculation ----

    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task Calculate()
    {
        RefreshForces();
        if (forces is null) { if (Errors.Length == 0) Errors = "Загрузите усилия PLAXIS"; return; }
        if (!project.Settings.Confirmed) { Errors = "Сверьте с Output единицы, масштаб и знак момента и отметьте «Сверено»"; return; }
        if (IsTyping) { await CalculateTyping(); return; }
        var p = project;
        bool check = IsCheck;
        string what = check ? "Проверка сечения" : "Подбор армирования";
        await Busy(what + "…", async token =>
        {
            var progress = new Progress<(int Done, int Total)>(x =>
            {
                Progress = 100.0 * x.Done / x.Total;
                Status = $"{what}: {x.Done} из {x.Total} сечений";
            });
            string key = Key();
            result = await Task.Run(() => check ? RequiredReinforcement.Check(p, progress, token) : RequiredReinforcement.Run(p, progress, token), token);
            resultKey = key;
            typing = null; Types.Clear(); PileRows.Clear();
            OnPropertyChanged(nameof(Typing)); OnPropertyChanged(nameof(HasTyping)); OnPropertyChanged(nameof(HasNoTyping));
            ShowResult();
        });
    }

    partial void OnSelectedStationChanged(StationRowViewModel? value)
    {
        if (value is null || result is null) { StationSketch = null; StationText = ""; return; }
        var st = value.Result;
        string level = $"Отм. {st.Station.Level.ToString("0.00", Ru)}";
        SectionProject shown;
        if (result.IsCheck)
        {
            shown = result.Project.Section;
            StationText = $"{level}: заданное сечение, K = {ReportWriter.Utilization(st.K)}";
        }
        else
        {
            var choice = BarSuggestion.Choose(result.Project.Section, st.AsRightMm2, st.AsLeftMm2);
            shown = choice.Section;
            string need = result.Project.Section.Kind == SectionKind.Wall
                ? $"«+» {st.AsRightMm2 / 100:0.0}, «−» {st.AsLeftMm2 / 100:0.0}" : $"{st.AsRightMm2 / 100:0.0}";
            StationText = $"{level}: требуется {need} см² → {choice.Text}" + (choice.Fits ? "" : " — не помещается");
        }
        using var stream = new MemoryStream(SectionSketch.Png(shown, 520, 420, 22));
        StationSketch = new Bitmap(stream);
    }

    void ShowResult()
    {
        if (result is null) return;
        bool wall = result.Project.Section.Kind == SectionKind.Wall;
        Stations.Clear();
        foreach (var s in result.Stations) Stations.Add(new StationRowViewModel(s, wall));
        // The governing station first: the largest area (in the check — the largest K).
        SelectedStation = result.IsCheck ? Stations.MaxBy(r => r.Result.K ?? 0) : Stations.MaxBy(r => r.Result.AsTotalMm2);
        HasResult = true; ResultIsStale = false;
        var st = result.Stations;
        string As(double mm2) => (mm2 / 100).ToString("0.0", Ru);
        var f = result.Forces;
        int side = result.Project.Settings.ExcavationSide;
        string Face(int face) => side == 0 ? (face > 0 ? "правая" : "левая") : side == face ? "со стороны котлована" : "со стороны грунта";
        if (result.IsCheck)
        {
            // The given section: the largest K along the element and where it is.
            var worst = st.Where(s => s.K is not null).MaxBy(s => s.K);
            Summary = worst is null ? "Проверка: нет усилий"
                : $"Проверка сечения{(f.IsGroup ? $" (группа {f.Piles.Count} свай)" : "")}: Kmax {ReportWriter.Utilization(worst.K)} на отм. {worst.Station.Level.ToString("0.00", Ru)} — "
                  + (worst.K <= 1 ? "сечение обеспечено" : "НЕ обеспечено") + $" ({worst.Governing})";
        }
        else
            Summary = wall
                ? $"As,max: {Face(1)} {As(st.Max(s => s.AsRightMm2))} см², {Face(-1)} {As(st.Max(s => s.AsLeftMm2))} см²"
                : f.IsGroup ? $"Единое армирование группы из {f.Piles.Count} свай: As,max {As(st.Max(s => s.AsRightMm2))} см²"
                : $"As,max {As(st.Max(s => s.AsRightMm2))} см²";
        if (f.Piles.Any(x => Math.Abs(x.Shift) > 1e-6))
            Summary += $". * Эпюры свай совмещены по оголовкам (отметки сваи {f.Piles.MaxBy(x => x.Head)!.Name})";
        if (!result.IsCheck && st.Any(s => s.AswCm2PerM is not null)) Summary += $"; Asw/sw,max {st.Max(s => s.AswCm2PerM ?? 0).ToString("0.00", Ru)} см²/м";
        int bad = st.Count(s => s.IsBad());
        if (bad > 0) Summary += $". Не обеспечено или не определено: {bad} сеч.";
        Status = result.IsCheck ? "Проверка выполнена" : "Подбор выполнен";
        Draw();
    }

    void ClearResult()
    {
        result = null; HasResult = false; ResultIsStale = false; Stations.Clear(); Summary = ""; SelectedStation = null;
        typing = null; demands = null; Types.Clear(); PileRows.Clear(); verified.Clear();
        OnPropertyChanged(nameof(Typing)); OnPropertyChanged(nameof(HasTyping)); OnPropertyChanged(nameof(HasNoTyping));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task ExportRevit()
    {
        if (typing is not null && !ResultIsStale)
        {
            if (Storage?.Invoke() is not { } st) return;
            var csv = await st.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Типы свай", SuggestedFileName = Safe(project.Title) + ".types.csv", DefaultExtension = "csv",
                FileTypeChoices = [new FilePickerFileType("CSV (Excel)") { Patterns = ["*.csv"] }],
            });
            if (csv is null) return;
            await using var cs = await csv.OpenWriteAsync();
            // UTF-8 with BOM: Excel opens the Cyrillic text right.
            await using var cw = new StreamWriter(cs, new System.Text.UTF8Encoding(true));
            await cw.WriteAsync(TypingCsv(typing));
            Status = $"Сохранено: {csv.Name}";
            return;
        }
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

    [RelayCommand]
    void AddCut()
    {
        double top = forces?.TopLevel ?? 0;
        PeakCuts.Add(new PeakCutViewModel(new PeakCut { TopLevel = Math.Round(top - 1, 2), BottomLevel = Math.Round(top - 2, 2) }, Changed, RemoveCut));
        Changed();
    }

    void RemoveCut(PeakCutViewModel c) { PeakCuts.Remove(c); Changed(); }

    /// <summary>All stages on or off: with the stage forces, tick only the stages wanted.</summary>
    [RelayCommand]
    void AllPhases(string? on)
    {
        bool value = on != "0";
        foreach (var row in Phases) row.Include = value;
    }

    string? lastStep;

    /// <summary>Opens a file in its program (the 3D view in the browser); replaced in tests.</summary>
    public Func<string, bool> OpenFile { get; set; } = path =>
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); return true; }
        catch (Exception) { return false; }
    };
    (TypingResult Typing, Dictionary<(int Type, int Zone), ZoneCapacity> Caps)? capacities;

    /// <summary>
    /// 3D view of the piles in the browser: diagrams of N, M, Q and reinforcement along each pile; with the typing —
    /// the material diagram (Mult of the cage zones against M).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    async Task Open3D()
    {
        if (project.Source is null || forces is null) { Errors = "Загрузите усилия PLAXIS"; return; }
        var p = project;
        var r = HasTyping && !ResultIsStale ? typing : null;
        await Busy("3D-вид…", async token =>
        {
            Dictionary<(int Type, int Zone), ZoneCapacity>? caps = null;
            if (r is not null)
            {
                if (capacities is { } known && ReferenceEquals(known.Typing, r)) caps = known.Caps;
                else
                {
                    var progress = new Progress<(int Done, int Total)>(x =>
                    {
                        Progress = 100.0 * x.Done / x.Total;
                        Status = $"Эпюра материалов: Mult зон {x.Done} из {x.Total}";
                    });
                    caps = await Task.Run(() => SceneExport.Capacities(r, progress, token), token);
                    capacities = (r, caps);
                }
            }
            string html = await Task.Run(() => SceneExport.Html(SceneExport.Data(p, r, caps)), token);
            string path = Path.Combine(Path.GetTempPath(), $"section-app-3d-{DateTime.Now:yyyyMMdd-HHmmss}.html");
            await File.WriteAllTextAsync(path, html, token);
            LastScene = path;
            Status = OpenFile(path) ? "3D-вид открыт в браузере" : $"Откройте в браузере: {path}";
        });
    }

    /// <summary>The last 3D page written.</summary>
    public string? LastScene { get; private set; }

    async Task Busy(string message, Func<CancellationToken, Task> work)
    {
        cancel = new CancellationTokenSource();
        IsBusy = true; Progress = 0; Status = message; Errors = ""; lastStep = null;
        try { await work(cancel.Token); }
        catch (OperationCanceledException)
        {
            Status = "Остановлено";
            if (lastStep is not null) Errors = $"Остановлено на шаге: {lastStep}\nЖурнал: {PlaxisBridge.LogPath}";
        }
        catch (AggregateException ex)
        {
            // Parallel work: the distinct messages of the failed parts.
            Status = "Ошибка";
            Errors = string.Join("\n", ex.Flatten().InnerExceptions.Select(x => x.Message).Distinct());
        }
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
    public int PlaxisInputPort { get; set; } = 10000;

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
