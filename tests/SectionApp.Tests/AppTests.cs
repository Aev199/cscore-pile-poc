using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SectionApp;
using SectionApp.ViewModels;
using SectionApp.Views;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

// Real engine, real window: the user workflow from input to results.
public class AppTests
{
    static (MainWindow, MainViewModel) Open(bool pile = true)
    {
        var vm = new MainViewModel();
        if (!pile) vm.SetWallCommand.Execute(null);
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    static void Snapshot(MainWindow window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        if (Environment.GetEnvironmentVariable("SECTION_APP_SCREENSHOTS") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            window.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png"));
        }
    }

    [AvaloniaFact]
    public async Task PileCheckShowsVerdictAndRows()
    {
        var (window, vm) = Open();
        Snapshot(window, "01-pile-start");
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.HasResult, vm.InputErrors);
        Assert.NotEmpty(vm.DetailRows);
        Assert.NotEmpty(vm.SummaryRows);
        Assert.Contains(vm.VerdictTone, new[] { "ok", "bad", "warn" });
        Snapshot(window, "02-pile-checked");
        vm.PileBarCount = "16";
        Assert.True(vm.ResultIsStale);
    }

    [AvaloniaFact]
    public async Task WallSelectionAppliesLightestPassingVariant()
    {
        var (window, vm) = Open(pile: false);
        vm.SelectionCountMin = "3"; vm.SelectionCountMax = "7"; vm.SelectionDiameters = "16, 20, 25";
        await vm.SelectCommand.ExecuteAsync(null);
        Assert.True(vm.HasSelection, vm.InputErrors);
        var best = Assert.Single(vm.SelectionRows, r => r.IsBest);
        Snapshot(window, "03-wall-selection");
        await best.ApplyCommand.ExecuteAsync(null);
        Assert.True(vm.HasResult);
        Assert.Equal("ok", vm.VerdictTone);
        Assert.Equal(0, vm.SelectedTab);
        Snapshot(window, "04-wall-applied");
    }

    [AvaloniaFact]
    public async Task InvalidNumberIsReportedNotCalculated()
    {
        var (window, vm) = Open();
        vm.PileDiameter = "восемьсот";
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.False(vm.HasResult);
        Assert.Contains("Диаметр D", vm.InputErrors);
        Snapshot(window, "05-input-error");
    }
}
