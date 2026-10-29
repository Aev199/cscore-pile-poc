using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SectionCalc;

namespace SectionApp.ViewModels;

/// <summary>One editable force combination (a row of the forces grid, ARBAT "simple form").</summary>
public partial class LoadRowViewModel : ObservableObject
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    readonly MainViewModel owner;
    bool loading;

    LoadRowViewModel(MainViewModel owner) => this.owner = owner;

    [ObservableProperty] string name = "";
    [ObservableProperty] string n = "0", mx = "0", my = "0", q = "0", loadFactor = "1", sustainedShare = "1";
    [ObservableProperty] bool shortTerm;
    [ObservableProperty] bool seismic;
    [ObservableProperty] bool special;

    // A combination is either seismic or special (SP 14 / SP 296), never both.
    partial void OnSeismicChanged(bool value) { if (value) Special = false; OnPropertyChanged(nameof(KindIndex)); }
    partial void OnSpecialChanged(bool value) { if (value) Seismic = false; OnPropertyChanged(nameof(KindIndex)); }
    partial void OnShortTermChanged(bool value) => OnPropertyChanged(nameof(KindIndex));

    /// <summary>Kind of the combination in one list instead of three flags.</summary>
    public static IReadOnlyList<string> KindOptions { get; } = ["основное", "кратковр.", "сейсм.", "сейсм. кратк.", "особое", "особое кратк."];
    public IReadOnlyList<string> Kinds => KindOptions;
    public int KindIndex
    {
        get => (Seismic ? 2 : Special ? 4 : 0) + (ShortTerm ? 1 : 0);
        set
        {
            if (value < 0 || value == KindIndex) return;
            bool wasLoading = loading;
            loading = true;
            Seismic = value is 2 or 3; Special = value is 4 or 5; ShortTerm = value % 2 == 1;
            loading = wasLoading;
            OnPropertyChanged();
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!loading) owner.InputsChanged();
    }

    public static LoadRowViewModel From(LoadRow l, MainViewModel owner)
    {
        var row = new LoadRowViewModel(owner) { loading = true };
        row.Name = l.Name; row.N = S(l.N); row.Mx = S(l.Mx); row.My = S(l.My); row.Q = S(l.Q);
        row.LoadFactor = S(l.LoadFactor); row.SustainedShare = S(l.SustainedShare);
        row.ShortTerm = l.ShortTerm; row.Seismic = l.Seismic; row.Special = l.Special && !l.Seismic;
        row.loading = false;
        return row;
    }

    public LoadRow ToModel(List<string> errors)
    {
        string label = string.IsNullOrWhiteSpace(Name) ? "без названия" : Name.Trim();
        double D(string text, string field)
        {
            if (MainViewModel.TryNumber(text, out var v)) return v;
            errors.Add($"Сочетание «{label}», {field}: введите число"); return 0;
        }
        return new LoadRow
        {
            Name = Name.Trim(), N = D(N, "N"), Mx = D(Mx, "Mx"), My = D(My, "My"), Q = D(Q, "Q"),
            LoadFactor = D(LoadFactor, "γf"), SustainedShare = D(SustainedShare, "kдл"),
            ShortTerm = ShortTerm, Seismic = Seismic, Special = Special,
        };
    }

    internal void SwapPlane()
    {
        (Mx, My) = (My, Mx);
    }

    static string S(double v) => v.ToString("0.###", Ru);
}

/// <summary>One vertex of a polygon outline (arbitrary section), mm.</summary>
public partial class VertexRowViewModel(MainViewModel owner, string x, string y) : ObservableObject
{
    [ObservableProperty] string x = x;
    [ObservableProperty] string y = y;
    protected override void OnPropertyChanged(PropertyChangedEventArgs e) { base.OnPropertyChanged(e); owner.InputsChanged(); }
}

/// <summary>One bar of the bar table (arbitrary section): axis coordinates and diameter, mm.</summary>
public partial class BarRowViewModel(MainViewModel owner, string x, string y, string d) : ObservableObject
{
    [ObservableProperty] string x = x;
    [ObservableProperty] string y = y;
    [ObservableProperty] string d = d;
    protected override void OnPropertyChanged(PropertyChangedEventArgs e) { base.OnPropertyChanged(e); owner.InputsChanged(); }
}

/// <summary>One combination in the results table: worst K per check type; values above 1 are highlighted.</summary>
public sealed class CombinationRowViewModel(CombinationLine line)
{
    public string Name => line.Name;
    public string C0 => line.Cells[0];
    public string C1 => line.Cells[1];
    public string C2 => line.Cells[2];
    public string C3 => line.Cells[3];
    public string C4 => line.Cells[4];
    public bool B0 => line.Exceeded[0];
    public bool B1 => line.Exceeded[1];
    public bool B2 => line.Exceeded[2];
    public bool B3 => line.Exceeded[3];
    public bool B4 => line.Exceeded[4];
}

/// <summary>One result line: readable title, combination, K and a colour tone.</summary>
public sealed class CheckRowViewModel
{
    public required string Title { get; init; }
    public required string Combination { get; init; }
    public required string KText { get; init; }
    public required string Status { get; init; }
    /// <summary>ok | bad | warn | muted — matches the styles of the design code.</summary>
    public required string Tone { get; init; }
    /// <summary>Utilisation bar fill, 0…1 of the bar width (K = 1 at 80 %).</summary>
    public double Fill { get; init; }
    public double FillWidth => Fill * 48;
    public string Tip => $"{Status}; {Combination}";
    public bool IsOk => Tone == "ok";
    public bool IsBad => Tone == "bad";
    public bool IsWarn => Tone == "warn";
    public bool IsMuted => Tone == "muted";

    public static CheckRowViewModel From(CheckRow row, CheckResult result)
    {
        string tone = row.IsFailure ? "bad" : row.IsReview ? "warn"
            : row.Status is "passed" or "preliminary_below_limit" or "stable" ? "ok" : "muted";
        return new CheckRowViewModel
        {
            Title = row.Title, Combination = row.Combination, KText = ReportWriter.UtilizationText(row, result),
            Status = ReportWriter.StatusText(row, result), Tone = tone,
            Fill = row.Utilization is double u ? Math.Clamp(u * 0.8, 0, 1) : 0,
        };
    }
}

public partial class SelectionRowViewModel(SelectionOutcome outcome, bool isBest, MainViewModel owner) : ObservableObject
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public string Description => outcome.Candidate.Description;
    public string Area => outcome.Candidate.SteelAreaMm2.ToString("0", Ru);
    public string Governing => outcome.Error ?? outcome.Governing?.Title ?? "—";
    public string KText => ReportWriter.Utilization(outcome.Governing?.Utilization);
    public bool IsBest => isBest;
    public string Result => outcome.Error is not null ? "ошибка" : isBest ? "минимальный" : outcome.Verdict switch
    {
        Verdict.Passed => "проходит", Verdict.Failed => "не проходит", Verdict.Review => "на границе", _ => "неполный",
    };
    public string Tone => outcome.Verdict switch { Verdict.Passed => "ok", Verdict.Failed => "bad", _ => "warn" };
    public bool CanApply => outcome.Verdict == Verdict.Passed;
    public bool IsOk => Tone == "ok";
    public bool IsBad => Tone == "bad";
    public bool IsWarn => Tone == "warn";

    [RelayCommand] Task Apply() => owner.Apply(outcome);

    public void Refresh() => OnPropertyChanged(string.Empty);
}
