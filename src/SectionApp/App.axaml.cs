using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SectionApp.ViewModels;
using SectionApp.Views;

namespace SectionApp;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel(SessionStore.Default)
            {
                OpenFile = path =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
                    catch { /* no program for the file: it is saved anyway */ }
                },
            };
            var window = new MainWindow { DataContext = vm };
            window.Closing += (_, _) => vm.SaveSession();
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
