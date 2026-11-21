using Avalonia;
using System.Reflection;

namespace SectionApp;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? marker = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(a => a.Key == "StartupMarker")?.Value;
        if (!CanStart(marker)) return 1;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    internal static bool CanStart(string? marker)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(marker)) return false;
        if (!marker.StartsWith(@"\\", StringComparison.Ordinal) || marker.StartsWith(@"\\?\", StringComparison.Ordinal)
            || marker.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
        var parts = marker[2..].Split('\\');
        return parts.Length >= 3 && parts.All(p => p.Length > 0 && p != "." && p != "..") && File.Exists(marker);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
