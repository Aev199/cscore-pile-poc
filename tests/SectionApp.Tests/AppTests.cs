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

    /// <summary>Synthetic PLAXIS 2D Output of a 15 m wall (kN, m), three phases.</summary>
    static SectionCalc.Profile.PlaxisExport DemoExport()
    {
        var e = new SectionCalc.Profile.PlaxisExport
        {
            Info = new() { UnitForce = "kN", UnitLength = "m", Version = "22.01", Title = "Демо" },
            Objects = [new() { Id = 0, Kind = "Plate", Name = "Plate_1" }],
            Phases = [new() { Id = 1, Name = "Phase_1", Title = "Выемка 1" }, new() { Id = 2, Name = "Phase_2", Title = "Выемка 2" }, new() { Id = 3, Name = "Phase_3", Title = "Дно" }],
            Columns = ["x", "y", "n", "m", "q"],
        };
        foreach (var ph in e.Phases)
        {
            double k = ph.Id / 3.0, w = 1.3 * Math.PI / 15;
            var nodes = new List<double[]>();
            for (double z = 0; z <= 15 + 1e-9; z += 0.25)
                nodes.Add([5, -z, -25 * z * (0.5 + 0.5 * k), k * 280 * Math.Sin(w * z), k * 280 * w * Math.Cos(w * z)]);
            e.Data.Add(new() { Object = 0, Phase = ph.Id, Nodes = nodes });
        }
        return e;
    }

    [AvaloniaFact]
    public async Task ProfileModuleGroupOfPilesFrom3D()
    {
        var (window, vm) = Open(pile: true);
        vm.ShowProfileCommand.Execute(null);
        var p = vm.Profile;
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "examples", "profile"))) root = Path.GetDirectoryName(root)!;
        p.UseExport(SectionCalc.Profile.PlaxisBridge.Parse(File.ReadAllText(Path.Combine(root, "examples", "profile", "pile-group-3d.plaxis.json"))));
        Assert.True(p.Is3D);
        Assert.False(p.ShowPileSpacing);
        Assert.Contains("группа 4 свай", p.SourceText);
        p.StationStep = "3";
        p.Confirmed = true;
        await p.CalculateCommand.ExecuteAsync(null);
        Assert.True(p.HasResult, p.Errors);
        Assert.Contains("группы из 4 свай", p.Summary);
        Assert.Contains("оголовкам", p.Summary);
        Snapshot(window, "12-profile-group");

        // Each pile on its own, then three types (heads and lengths give three classes here).
        var (gn, gm) = (SectionCalc.Profile.PileTyping.GridN, SectionCalc.Profile.PileTyping.GridM);
        (SectionCalc.Profile.PileTyping.GridN, SectionCalc.Profile.PileTyping.GridM) = (3, 4);
        try
        {
            p.ModeIndex = 2;
            p.TypeCount = "3";
            await p.CalculateCommand.ExecuteAsync(null);
            Assert.True(p.HasResult, p.Errors);
            Assert.Equal(3, p.Types.Count);
            Assert.Contains("типов", p.Summary);
            Assert.NotNull(p.StationSketch);
            Assert.Contains("Тип 1", p.StationText);
            Assert.Equal("Сохранить таблицу…", p.ExportText);
            p.SelectedType = p.Types[^1];
            Snapshot(window, "14-profile-typing");
        }
        finally { (SectionCalc.Profile.PileTyping.GridN, SectionCalc.Profile.PileTyping.GridM) = (gn, gm); }
    }

    [AvaloniaFact]
    public async Task ProfileModuleBuildsReinforcementDiagram()
    {
        var (window, vm) = Open(pile: false);
        vm.ShowProfileCommand.Execute(null);
        Assert.True(vm.IsProfileMode);
        var p = vm.Profile;
        p.UseExport(DemoExport());
        Assert.True(p.HasPhases);
        Assert.NotNull(p.Chart);
        p.StationStep = "3";
        p.AddCutCommand.Execute(null);
        var cut = Assert.Single(p.PeakCuts);
        cut.Top = "-5"; cut.Bottom = "-7"; cut.Shear = true;
        await p.CalculateCommand.ExecuteAsync(null);
        Assert.Contains("Сверьте", p.Errors);
        p.Confirmed = true;
        Snapshot(window, "10-profile-forces");
        await p.CalculateCommand.ExecuteAsync(null);
        Assert.True(p.HasResult, p.Errors);
        Assert.Equal(6, p.Stations.Count);
        Assert.Contains("правая", p.Summary);
        Assert.NotNull(p.SelectedStation);
        Assert.NotNull(p.StationSketch);
        Assert.Contains("→", p.StationText);
                Snapshot(window, "11-profile-result");
        var json = System.Text.Json.Nodes.JsonNode.Parse(SectionCalc.Profile.RevitExport.Json(p.Result!))!;
        Assert.Equal("wall", (string)json["element"]!["type"]!);
        if (Environment.GetEnvironmentVariable("SECTION_APP_EXAMPLES") is { Length: > 0 } dir)
        {
            // Exactly the layout of plaxis_export.py output.
            var d = DemoExport();
            var o = new System.Text.Json.Nodes.JsonObject
            {
                ["info"] = new System.Text.Json.Nodes.JsonObject { ["UnitForce"] = d.Info.UnitForce, ["UnitLength"] = d.Info.UnitLength, ["Version"] = d.Info.Version, ["Title"] = d.Info.Title },
                ["objects"] = new System.Text.Json.Nodes.JsonArray(d.Objects.Select(x => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["id"] = x.Id, ["kind"] = x.Kind, ["name"] = x.Name }).ToArray()),
                ["phases"] = new System.Text.Json.Nodes.JsonArray(d.Phases.Select(x => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["id"] = x.Id, ["name"] = x.Name, ["title"] = x.Title }).ToArray()),
                ["columns"] = new System.Text.Json.Nodes.JsonArray("x", "y", "n", "m", "q"),
                ["data"] = new System.Text.Json.Nodes.JsonArray(d.Data.Select(x => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
                {
                    ["object"] = x.Object, ["phase"] = x.Phase,
                    ["nodes"] = new System.Text.Json.Nodes.JsonArray(x.Nodes.Select(n => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonArray(n.Select(v => (System.Text.Json.Nodes.JsonNode)Math.Round(v, 4)).ToArray())).ToArray()),
                }).ToArray()),
            };
            File.WriteAllText(Path.Combine(dir, "svg-demo.plaxis.json"), o.ToJsonString());
            File.WriteAllText(Path.Combine(dir, "svg-demo.reinforcement.json"), SectionCalc.Profile.RevitExport.Json(p.Result!));
        }
        // The given section checked along the wall.
        p.ModeIndex = 1;
        await p.CalculateCommand.ExecuteAsync(null);
        Assert.True(p.HasResult, p.Errors);
        Assert.True(p.Result!.IsCheck);
        Assert.Contains("Проверка сечения", p.Summary);
        Assert.Equal("Проверка сечения", p.TableTitle);
        Snapshot(window, "13-profile-check");
        p.ModeIndex = 0;
        // The scenario decides the section kind.
        p.ScenarioIndex = 1;
        Assert.True(vm.IsPile);
        Assert.True(p.ShowPileSpacing);
        p.ScenarioIndex = 0;
        Assert.False(vm.IsPile);
                p.PileSpacing = "abc";
        Assert.True(p.ResultIsStale || p.HasErrors);
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
