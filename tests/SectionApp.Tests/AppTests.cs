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
    /// <summary>Window in the geotechnical mode (pile or wall) or, with <paramref name="custom"/>, in the arbitrary section mode.</summary>
    static (MainWindow, MainViewModel) Open(bool pile = true, bool custom = false)
    {
        var vm = new MainViewModel { AutoCalc = false };
        if (!custom) { if (pile) vm.SetPileCommand.Execute(null); else vm.SetWallCommand.Execute(null); }
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

    /// <summary>Pumps the dispatcher until the condition holds (the debounced recalculation runs on a timer).</summary>
    static async Task Until(Func<bool> condition, int timeoutMs = 20000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(30);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition(), "condition not reached in time");
    }

    [AvaloniaFact]
    public async Task StartsInArbitraryModeAndRecalculatesByItself()
    {
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1440, Height = 900 };
        window.Show();
        Assert.True(vm.IsSectionMode && vm.IsCustom);
        Assert.True(vm.AutoCalc);
        // No button: the example is calculated on start.
        await Until(() => vm.HasResult && !vm.IsBusy);
        Assert.Equal(2, vm.CombinationRows.Count);
        Snapshot(window, "01-custom-start");
        // A change recalculates without any command.
        vm.CustomHeight = "700";
        Assert.True(vm.ResultIsStale);
        await Until(() => !vm.ResultIsStale && !vm.IsBusy);
        Assert.Contains("700", vm.SteelText + vm.PropertiesText + vm.TryBuild(out _)!.Custom.HeightMm);
    }

    [AvaloniaFact]
    public async Task ArbitraryShapesBarsAndSelection()
    {
        var (window, vm) = Open(custom: true);
        // Tee from the template, bars by the contour.
        vm.ShapeIndex = vm.ShapeOptions.ToList().FindIndex(o => o.Kind == ShapeKind.Tee);
        vm.FlangeThickness = "200";
        Assert.True(vm.ShowFlanges);
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.HasResult, vm.InputErrors);
        Assert.Contains("Ix", vm.PropertiesText);
        Snapshot(window, "02-custom-tee");

        // The contour arrangement goes into the table; one bar is moved by hand.
        vm.ContourToTableCommand.Execute(null);
        Assert.True(vm.IsManual);
        int n = vm.Bars.Count;
        Assert.True(n > 6);
        vm.Bars[0].X = "-1000";
        Assert.Contains("выходит из бетона", vm.InputErrors);
        vm.SelectedBar = vm.Bars[0];
        vm.RemoveBarCommand.Execute(null);
        Assert.Equal(n - 1, vm.Bars.Count);
        Assert.DoesNotContain("выходит", vm.InputErrors);

        // Polygon pasted from a list of coordinates, bars pasted with diameters.
        vm.ShapeIndex = vm.ShapeOptions.ToList().FindIndex(o => o.Kind == ShapeKind.Polygon);
        vm.ClipboardText = () => Task.FromResult<string?>("x\ty\n0\t0\n600\t0\n600\t300\n300\t300\n300\t700\n0\t700\n");
        await vm.PasteVerticesCommand.ExecuteAsync(null);
        Assert.Equal(6, vm.Vertices.Count);
        vm.ClipboardText = () => Task.FromResult<string?>("50 50 20\n550 50 20\n550 250 20\n250 250 16\n250 650 16\n50 650 20\n");
        await vm.PasteBarsCommand.ExecuteAsync(null);
        Assert.Equal(6, vm.Bars.Count);
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.HasResult, vm.InputErrors);
        Snapshot(window, "03-custom-polygon");

        // Selection keeps the positions and tries the diameters.
        vm.SelectionDiameters = "16, 20, 25, 32";
        await vm.SelectCommand.ExecuteAsync(null);
        Assert.True(vm.HasSelection, vm.InputErrors);
        Snapshot(window, "04-custom-selection");

        // The file keeps the arbitrary geometry and the geotechnical one.
        var p = vm.TryBuild(out _, quiet: true)!;
        var q = ProjectStore.Deserialize(ProjectStore.Serialize(p));
        Assert.Equal(SectionKind.Custom, q.Kind);
        Assert.Equal(6, q.Custom.Vertices.Count);
        Assert.Equal(800, q.Pile.DiameterMm);
    }

    [AvaloniaFact]
    public void PileAndWallAreTemplatesOfTheSection()
    {
        var (_, vm) = Open(custom: true);
        vm.CustomWidth = "450";
        // The pile and the wall are the first tiles of the picker; the loads stay.
        int loads = vm.Loads.Count;
        vm.TileIndex = 0;
        Assert.True(vm.IsPile);
        vm.TileIndex = 1;
        Assert.True(vm.IsWall);
        vm.WallThickness = "800";
        Assert.Equal(loads, vm.Loads.Count);
        vm.TileIndex = 2 + vm.ShapeOptions.ToList().FindIndex(o => o.Kind == ShapeKind.Tee);
        Assert.True(vm.IsCustom && vm.ShowFlanges);
        Assert.Equal("450", vm.CustomWidth);
        // PLAXIS designs a pile or a wall: the last of them (the wall) with its thickness.
        vm.ShowProfileCommand.Execute(null);
        Assert.True(vm.IsProfileMode && vm.IsWall);
        Assert.Equal("800", vm.WallThickness);
        // γf and kдл of all rows at once.
        vm.CommonLoadFactor = "1,2";
        Assert.All(vm.Loads, l => Assert.Equal("1,2", l.LoadFactor));
        Assert.False(vm.ShowFactorColumns);
        vm.Loads[0].SustainedShare = "0,5";
        Assert.Equal("", vm.CommonSustained);
        Assert.True(vm.ShowFactorColumns);
        // Factors in one line: the ones that differ from 1.
        vm.Gb3 = "0,85";
        Assert.Contains("γb3 = 0,85", vm.CoefficientsText);
    }

    [AvaloniaFact]
    public void LastSectionAndRecentFilesAreRemembered()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"section-session-{Guid.NewGuid():N}");
        try
        {
            var store = new SessionStore(dir);
            var first = new MainViewModel(store) { AutoCalc = false };
            first.SetWallCommand.Execute(null);
            first.Title = "Стена секции 3";
            first.SaveSession();
            // The next start continues the work.
            var next = new MainViewModel(store) { AutoCalc = false };
            Assert.Equal("Стена секции 3", next.Title);
            Assert.True(next.IsWall);
            Assert.False(next.HasRecent);
            // A file opened or saved goes first into the recent list and opens from it.
            string file = Path.Combine(dir, "pile.section.json");
            ProjectStore.Save(SectionProject.Example(SectionKind.Pile), file);
            store.Remember(file);
            var third = new MainViewModel(store) { AutoCalc = false };
            Assert.Equal("pile.section.json", Assert.Single(third.RecentFiles).Name);
            third.OpenRecentCommand.Execute(third.RecentFiles[0]);
            Assert.True(third.IsPile);
            Assert.Equal(dir, store.LastFolder);
        }
        finally { Directory.Delete(dir, true); }
    }

    [AvaloniaFact]
    public void CoverOfEarlierFilesIsConvertedToClearCover()
    {
        var (_, vm) = Open();
        var p = SectionProject.Example(SectionKind.Pile);
        p.Pile.CoverReference = CoverReference.Transverse; p.Pile.CoverMm = 50; p.Pile.SpiralDiameterMm = 10;
        vm.Load(p);
        Assert.Equal("60", vm.PileCover);
        var q = vm.TryBuild(out _)!;
        Assert.Equal(CoverReference.Bar, q.Pile.CoverReference);
        Assert.Equal(p.Pile.AxisDistanceMm, q.Pile.AxisDistanceMm, 6);
    }

    [AvaloniaFact]
    public async Task PileCheckShowsVerdictAndRows()
    {
        var (window, vm) = Open();
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.HasResult, vm.InputErrors);
        Assert.Equal(2, vm.CombinationRows.Count);
        Assert.NotEmpty(vm.SummaryRows);
        Assert.Contains(vm.VerdictTone, new[] { "ok", "bad", "warn" });
        Snapshot(window, "05-pile-checked");
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
        Snapshot(window, "06-wall-selection");
        await best.ApplyCommand.ExecuteAsync(null);
        Assert.True(vm.HasResult);
        Assert.Equal("ok", vm.VerdictTone);
        Assert.Equal(0, vm.SelectedTab);
        Assert.True(vm.HasSelection);
        Snapshot(window, "07-wall-applied");
    }

    [AvaloniaFact]
    public async Task PastedSeismicCombinationIsCheckedWithItsFactors()
    {
        var (window, vm) = Open();
        Assert.False(vm.ShowSeismic);
        int added = vm.PasteText("№\tN, кН\tMx\tMy\tQ\tγf\tkдл\r\nС1\t-1 200\t210,5\t0\t100\t1,2\t0,6\r\n");
        Assert.Equal(1, added);
        var row = vm.Loads[^1];
        Assert.Equal("С1", row.Name);
        Assert.Equal("1,2", row.LoadFactor);
        row.Seismic = true;
        row.Special = true;
        Assert.False(row.Seismic);
        Assert.Equal(4, row.KindIndex);
        // One list instead of three flags: seismic, short-term.
        row.KindIndex = 3;
        Assert.True(row.Seismic && row.ShortTerm && !row.Special);
        row.KindIndex = 2;
        Assert.False(row.ShortTerm);
        Assert.True(vm.SeismicUsed);
        Assert.True(vm.ShowSeismic);
        await vm.CheckCommand.ExecuteAsync(null);
        Assert.True(vm.HasResult, vm.InputErrors);
        Assert.Contains(vm.CombinationRows, r => r.Name == "С1 · сейсмическое");
        Snapshot(window, "08-seismic");
        vm.SwapPlaneCommand.Execute(null);
        Assert.Equal("210,5", row.My);
        Assert.True(vm.ResultIsStale);
    }

    [AvaloniaFact]
    public void SettingsReachTheProject()
    {
        var (_, vm) = Open(pile: false);
        vm.CrackRequirementIndex = 1;
        Assert.Equal("0,2", vm.CrackLimitLong);
        Assert.Equal("0,3", vm.CrackLimitShort);
        var p = vm.TryBuild(out var errors);
        Assert.True(p is not null, string.Join("\n", errors));
        Assert.True(p!.Conditions.PermeabilityLimits);
        vm.CrackMethodIndex = 1;
        Assert.Equal(CrackMethod.Refined, vm.TryBuild(out _)!.Conditions.CrackMethod);
        vm.SchemeIndex = 1; vm.EccentricityIndex = 1; vm.SpecialIndex = 1;
        p = vm.TryBuild(out _)!;
        Assert.True(p.Conditions.StaticallyDeterminate);
        Assert.Equal(EccentricityMode.InLoads, p.Conditions.Eccentricity);
        Assert.False(p.Special.NormativeProperties);
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
        Assert.True(vm.IsProfileMode);
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

        // Each pile on its own, then three types (piles of any length may share one).
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
            Assert.True(p.HasTyping);
            Assert.False(p.ShowSettings);
            Assert.True(p.Typing!.Curve.Count >= 2);
            Assert.Equal(4, p.PileRows.Count);
            var shared = p.Types.Single(t => t.Type.Members.Count == 2).Type;
            // Split: one pile of the shared type into a type of its own.
            int pile = shared.Members[1].Pile;
            p.SelectedPiles = [pile];
            await p.NewTypeCommand.ExecuteAsync(null);
            Assert.Equal(4, p.Types.Count);
            // Back into the shared type.
            p.TargetIndex = p.Types.ToList().FindIndex(t => t.Type.Members.Any(m => m.Pile == shared.Members[0].Pile));
            p.SelectedPiles = [pile];
            await p.MovePilesCommand.ExecuteAsync(null);
            Assert.Equal(3, p.Types.Count);
            // A pile of another length joins it too: the type shows the range of lengths.
            var other = p.Typing!.Demands.Piles.First(x => shared.Members.All(m => m.Pile != x.Pile) && Math.Abs(x.Length - shared.MaxLength) > 0.1);
            p.TargetIndex = p.Types.ToList().FindIndex(t => t.Type.Members.Any(m => m.Pile == shared.Members[0].Pile));
            p.SelectedPiles = [other.Pile];
            await p.MovePilesCommand.ExecuteAsync(null);
            Assert.True(string.IsNullOrEmpty(p.Errors), p.Errors);
            Assert.Contains(p.Types, t => t.Type.Members.Count == 3 && t.Title.Contains('–'));
            // Lock a type: it stays when gathered again.
            p.SelectedTypeNumber = p.Types[0].Type.Number;
            await p.ToggleLockCommand.ExecuteAsync(null);
            Assert.True(p.Types[0].Type.Locked);
            Assert.All(p.Types, t => Assert.True(t.Type.Verified));
            p.SelectedPiles = [pile];
            p.SelectedTypeNumber = p.Types.Last().Type.Number;
            Snapshot(window, "14-profile-typing");
            // The 3D view with the material diagram: the page is written, not opened in a test.
            string? opened = null;
            p.OpenFile = path => { opened = path; return true; };
            await p.Open3DCommand.ExecuteAsync(null);
            Assert.True(opened is not null, p.Errors);
            string html = File.ReadAllText(opened!);
            Assert.Contains("THREE", html);
            Assert.Contains("\"mult\"", html);
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
        Snapshot(window, "09-input-error");
        window.Close();
    }
}
