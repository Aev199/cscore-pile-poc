using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SectionCalc;

namespace SectionApp.ViewModels;

/// <summary>One editable force combination; empty sustained fields mean "equal to the total".</summary>
public partial class LoadRowViewModel : ObservableObject
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    readonly MainViewModel owner;
    bool loading;

    LoadRowViewModel(MainViewModel owner) => this.owner = owner;

    public IReadOnlyList<string> BasisOptions { get; } = ["ULS", "SLS"];
    [ObservableProperty] string name = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsSls), nameof(IsUls))] int basisIndex;
    public bool IsSls => BasisIndex == 1;
    public bool IsUls => !IsSls;
    [ObservableProperty] string n = "0", mx = "0", my = "0", q = "0", nLong = "", mxLong = "", myLong = "";

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!loading && e.PropertyName is not (nameof(IsSls) or nameof(IsUls))) owner.InputsChanged();
    }

    [RelayCommand] void Remove() => owner.RemoveLoad(this);

    public static LoadRowViewModel From(LoadRow l, MainViewModel owner)
    {
        var row = new LoadRowViewModel(owner) { loading = true };
        row.Name = l.Name; row.BasisIndex = l.Basis == LoadBasis.Sls ? 1 : 0;
        row.N = S(l.N); row.Mx = S(l.Mx); row.My = S(l.My); row.Q = S(l.Q);
        row.NLong = l.NLong is double nl ? S(nl) : ""; row.MxLong = l.MxLong is double ml ? S(ml) : ""; row.MyLong = l.MyLong is double yl ? S(yl) : "";
        row.loading = false;
        return row;
    }

    public LoadRow ToModel(List<string> errors)
    {
        string label = string.IsNullOrWhiteSpace(Name) ? "без названия" : Name.Trim();
        double D(string text, string field)
        {
            if (double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v)) return v;
            errors.Add($"Сочетание «{label}», {field}: введите число"); return 0;
        }
        double? Optional(string text, string field) => string.IsNullOrWhiteSpace(text) ? null : D(text, field);
        return new LoadRow
        {
            Name = Name.Trim(), Basis = IsSls ? LoadBasis.Sls : LoadBasis.Uls,
            N = D(N, "N"), Mx = D(Mx, "Mx"), My = D(My, "My"), Q = IsUls ? D(Q, "Q") : 0,
            NLong = IsSls ? Optional(NLong, "Nдл") : null, MxLong = IsSls ? Optional(MxLong, "Mx,дл") : null, MyLong = IsSls ? Optional(MyLong, "My,дл") : null,
        };
    }

    static string S(double v) => v.ToString("0.###", Ru);
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
    public double FillWidth => Fill * 64;
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
    public string Result => outcome.Error is not null ? "ошибка" : isBest ? "лучший вариант" : outcome.Verdict switch
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
