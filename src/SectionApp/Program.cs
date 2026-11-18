using Avalonia;
using System.Reflection;
using System.Runtime.InteropServices;

namespace SectionApp;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? marker = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(a => a.Key == "StartupMarker")?.Value;
        if (!CanStart(marker))
        {
            const string message = "Не удалось получить доступ к файлу разрешения запуска. Проверьте подключение к корпоративной сети и права доступа.";
            if (OperatingSystem.IsWindows()) MessageBoxW(IntPtr.Zero, message, "Сечения ЖБ", 0x10);
            else Console.Error.WriteLine(message);
            return 1;
        }
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
