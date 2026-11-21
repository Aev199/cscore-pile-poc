using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SectionApp.ViewModels;

public static class Converters
{
    public static readonly IValueConverter BoldIf =
        new FuncValueConverter<bool, FontWeight>(bold => bold ? FontWeight.SemiBold : FontWeight.Normal);
}
