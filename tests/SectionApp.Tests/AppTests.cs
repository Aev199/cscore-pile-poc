using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SectionApp;
using SectionCalc;
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
        Assert.Equal(2, vm.CombinationRows.Count);
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
    public async Task PastedSeismicCombinationIsCheckedWithItsFactors()
    {
        var (window, vm) = Open();
        int added = vm.PasteText("№\tN, кН\tMx\tMy\tQ\tγf\tkдл\r\nС1\t-1 200\t210,5\t0\t100\t1,2\t0,6\r\n");
        Assert.Equal(1, added);
        var row = vm.Loads[^1];
        Assert.Equal("С1", row.Name);
        Assert.Equal("1,2", row.LoadFactor);
        row.Seismic = true;
        row.Special = true;
        Assert.False(row.Seismic);
        row.Seismic = true;
        Assert.False(row.Special);
        Assert.True(vm.SeismicUsed);
        vm.InputTab = 4;
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.HasResult, vm.InputErrors);
        Assert.Contains(vm.CombinationRows, r => r.Name == "С1 · сейсмическое");
        Snapshot(window, "06-seismic");
        vm.SwapPlaneCommand.Execute(null);
        Assert.Equal("210,5", row.My);
        Assert.True(vm.ResultIsStale);
    }

    [AvaloniaFact]
    public void TabsShowAllSettings()
    {
        var (window, vm) = Open(pile: false);
        vm.InputTab = 1; Snapshot(window, "07-materials");
        vm.InputTab = 2; Snapshot(window, "08-design");
        vm.InputTab = 3;
        vm.CrackRequirementIndex = 1;
        Assert.Equal("0,2", vm.CrackLimitLong);
        Assert.Equal("0,3", vm.CrackLimitShort);
        Snapshot(window, "09-cracks");
        var p = vm.TryBuild(out var errors);
        Assert.True(p is not null, string.Join("\n", errors));
        Assert.True(p!.Conditions.PermeabilityLimits);
        vm.CrackMethodIndex = 1;
        Assert.Equal(CrackMethod.Refined, vm.TryBuild(out _)!.Conditions.CrackMethod);
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
        window.Close();
    }
}
