using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SectionCalc.Profile;

namespace SectionApp.ViewModels;

/// <summary>Depths below the head; edits are committed explicitly to the selected type.</summary>
public partial class CageZoneRow : ObservableObject
{
    [ObservableProperty] string from = "0";
    [ObservableProperty] string to = "1";
    [ObservableProperty] string count = "12";
    [ObservableProperty] string diameter = "25";
    [ObservableProperty] string spiral = "10";
    [ObservableProperty] string pitch = "200";

    public static CageZoneRow FromZone(TypeZone z, double head, double defaultPitch) => new()
    {
        From = S(head - z.Top), To = S(head - z.Bottom), Count = S(z.Bars.Count), Diameter = S(z.Bars.DiameterMm),
        Spiral = S(z.SpiralMm ?? 10), Pitch = S(z.PitchMm ?? defaultPitch),
    };
    static string S(double x) => x.ToString("0.###", CultureInfo.InvariantCulture);
    public TypeZone ToZone(double head)
    {
        double D(string text)
        {
            if (!MainViewModel.TryNumber(text, out var x) || !double.IsFinite(x))
                throw new InvalidOperationException("Введите числа в таблице зон каркаса");
            return x;
        }
        var n = D(Count);
        if (n < 1 || n > 1000 || n != Math.Truncate(n)) throw new InvalidOperationException("Число стержней — целое от 1 до 1000");
        return new TypeZone(head - D(From), head - D(To), new BarSet(1, (int)n, D(Diameter)), D(Spiral), null, null) { PitchMm = D(Pitch) };
    }
}
