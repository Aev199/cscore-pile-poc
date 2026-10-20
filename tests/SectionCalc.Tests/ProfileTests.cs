using System.Diagnostics;
using SectionCalc;
using SectionCalc.Profile;
using Xunit;
using Xunit.Abstractions;

public class ProfileTests(ITestOutputHelper output)
{
    /// <summary>
    /// Synthetic Output of PLAXIS 2D: a vertical plate x = 5 m from level 0 to −15 m in N and mm, nodes every 0.25 m.
    /// Phases: three excavation stages; the moment grows with the stage, the axial force with the depth.
    /// </summary>
    internal static PlaxisExport WallExport(string kind = "Plate", double depth = 15)
    {
        var export = new PlaxisExport
        {
            Info = new PlaxisInfo { UnitForce = "N", UnitLength = "mm", Version = "22.01", Title = "Котлован" },
            Objects = [new PlaxisObject { Id = 0, Kind = kind, Name = kind + "_1" }],
            Phases = [new PlaxisPhase { Id = 1, Name = "Phase_1", Title = "Выемка 1" }, new PlaxisPhase { Id = 2, Name = "Phase_2", Title = "Выемка 2" },
                      new PlaxisPhase { Id = 3, Name = "Phase_3", Title = "Дно" }],
            Columns = ["x", "y", "n", "m", "q"],
        };
        foreach (var phase in export.Phases)
        {
            double k = phase.Id / 3.0;
            var nodes = new List<double[]>();
            for (double z = 0; z <= depth + 1e-9; z += 0.25)
            {
                // kN/m and kN·m/m: a span moment and an opposite moment at the toe.
                double w = 1.3 * Math.PI / depth;
                double m = k * 280 * Math.Sin(w * z), q = k * 280 * w * Math.Cos(w * z);
                double n = -25 * z * (0.5 + 0.5 * k);
                // Project units N and mm: N per mm, N·mm per mm.
                nodes.Add([5000, -z * 1000, n, m * 1000, q]);
            }
            export.Data.Add(new PlaxisSeries { Object = 0, Phase = phase.Id, Nodes = nodes });
        }
        return export;
    }

    internal static ProfileProject Wall()
    {
        var p = new ProfileProject { Title = "СВГ-1" };
        p.Section = SectionProject.Example(SectionKind.Wall);
        p.Section.Wall.WidthMm = 1000; p.Section.Wall.ThicknessMm = 600;
        p.Section.Conditions.MuX = 0.7; p.Section.Conditions.MuY = 0.7;
        p.UseSource(WallExport());
        p.Settings.Confirmed = true;
        p.Settings.StationStepM = 1.5;
        return p;
    }

    [Fact]
    public void ForcesAreConvertedToKiloNewtonsPerSectionWidth()
    {
        var p = Wall();
        p.Section.Wall.WidthMm = 2000;
        var f = ForceProfile.Build(p);
        Assert.Equal(0, f.TopLevel, 9);
        Assert.Equal(-15, f.BottomLevel, 9);
        var peak = f.Envelope.MaxBy(e => e.MMax)!;
        // Phase 3 peak ≈ 260 kN·m/m near mid-depth, times b = 2 m.
        Assert.Equal(2 * 280, peak.MMax, 0);
        Assert.Equal(-25 * 15 * 2, f.Envelope.Last().NMin, 6);
        Assert.Equal(0.001 * 2, f.ForceScale, 12);
    }

    [Fact]
    public void StationsCoverTheIntervalAndPickRsu()
    {
        var p = Wall();
        var f = ForceProfile.Build(p);
        var stations = f.Stations(p);
        Assert.Equal(11, stations.Count);
        Assert.Equal(0, stations[0].Level, 9);
        Assert.Equal(-15, stations[^1].Level, 9);
        var mid = stations.MinBy(s => Math.Abs(s.Level + 7.5))!;
        var mmax = mid.Rsu.Single(r => r.Name.Contains("Mmax"));
        Assert.StartsWith("Phase_3", mmax.Name);
        // Within ±0.75 m of the station the largest moment is taken, not the value at the station.
        Assert.True(mmax.Mx >= f.Samples.Where(x => x.Phase == 3 && Math.Abs(x.Level + 7.5) < 0.76).Max(x => x.M) - 1e-9);
        Assert.All(mid.Rsu, r => Assert.True(r.Q >= 0));
        p.Settings.MomentSign = -1;
        Assert.Equal(-mmax.Mx, f.Stations(p).MinBy(s => Math.Abs(s.Level + 7.5))!.Rsu.Single(r => r.Name.Contains("Mmax")).Mx, 9);
    }

    [Fact]
    public void ElementsOnDifferentVerticalsAreRejected()
    {
        var p = Wall();
        p.Source!.Data[1].Nodes[3][0] = 5100;
        Assert.Contains("вертикали", Assert.Throws<InvalidOperationException>(() => ForceProfile.Build(p)).Message);
    }

    [Fact]
    public void RequiredAreaIsTheSmallestThatPasses()
    {
        var p = Wall();
        var sw = Stopwatch.StartNew();
        var r = RequiredReinforcement.Run(p);
        output.WriteLine($"{r.Stations.Count} stations, {sw.Elapsed.TotalSeconds:0.0} s, {r.Stations.Sum(s => s.Evaluations)} engine checks");
        foreach (var s in r.Stations)
            output.WriteLine($"{s.Station.Level,6:0.00}  right {s.AsRightMm2 / 100,6:0.00}  left {s.AsLeftMm2 / 100,6:0.00} cm²  {s.Status,-9} K={s.K:0.000} {s.Governing} | {s.Combination} | {s.Evaluations} {s.Note}");
        Assert.All(r.Stations, s => Assert.Contains(s.Status, new[] { "ok", "minimum" }));
        var peak = r.Stations.MaxBy(s => s.AsRightMm2)!;
        Assert.InRange(peak.Station.Level, -7.5, -4);
        Assert.Equal("ok", peak.Status);
        // The toe moment is opposite: the left face needs more than the minimum there.
        Assert.True(r.Stations[^1].AsLeftMm2 > r.Stations[0].AsLeftMm2 * 1.5);

        // Check the peak: with the found areas every check passes, with 5 % less on the right face something fails.
        var basis = ProjectStore.Deserialize(ProjectStore.Serialize(p.Section));
        basis.Conditions.ElementLengthM = 15;
        basis.Loads = peak.Station.Rsu.ToList();
        double K(double right)
        {
            var q = ProjectStore.Deserialize(ProjectStore.Serialize(basis));
            q.Wall.PositiveFace.BarDiameterMm = Math.Sqrt(4 * right / (q.Wall.PositiveFace.BarCount * Math.PI));
            q.Wall.NegativeFace.BarDiameterMm = Math.Sqrt(4 * peak.AsLeftMm2 / (q.Wall.NegativeFace.BarCount * Math.PI));
            return Engine.Check(q).Rows.Where(x => x.CheckId is "normal_member_strength" or "crack_width_long" or "crack_width_short")
                .Max(x => x.Utilization) ?? 0;
        }
        Assert.True(K(peak.AsRightMm2) <= 1);
        Assert.True(K(peak.AsRightMm2 * 0.95) > 0.97);
    }

    [Fact]
    public void PileFromEmbeddedBeamRowAndRevitJson()
    {
        var p = new ProfileProject { Title = "БНС-1", Section = SectionProject.Example(SectionKind.Pile) };
        p.UseSource(WallExport("EmbeddedBeamRow", depth: 12));
        p.Settings.PileSpacingM = 2.4;
        p.Settings.StationStepM = 6;
        p.Settings.MinPercent = 0.2;
        Assert.False(p.Settings.Confirmed);
        var r = RequiredReinforcement.Run(p);
        Assert.Equal(3, r.Stations.Count);
        Assert.All(r.Stations, st => Assert.Equal(0, st.AsLeftMm2));
        // Per pile: 280 kN·m/m × 2.4 m spacing.
        Assert.InRange(r.Forces.Envelope.Max(e => e.MMax), 280 * 2.4 * 0.99, 280 * 2.4);
        var mid = r.Stations[1];
        Assert.True(mid.AsRightMm2 > 0.2 / 100 * Math.PI * 800 * 800 / 4);

        var json = System.Text.Json.Nodes.JsonNode.Parse(RevitExport.Json(r, new DateTime(2026, 10, 20)))!;
        Assert.Equal(RevitExport.Format, (string)json["format"]!);
        Assert.Equal("pile", (string)json["element"]!["type"]!);
        Assert.Equal(0, (double)json["element"]!["top_level_m"]!, 9);
        Assert.Equal(-12, (double)json["element"]!["bottom_level_m"]!, 9);
        var stations = json["stations"]!.AsArray();
        Assert.Equal(3, stations.Count);
        Assert.Equal(Math.Round(mid.AsRightMm2 / 100, 2), (double)stations[1]!["as_cm2"]!, 9);
        Assert.Equal(6, (double)stations[1]!["depth_m"]!, 9);
        Assert.Null(stations[1]!["as_right_cm2"]);
        Assert.Equal("main", (string)json["source"]!["phases"]![0]!["kind"]!);
        Assert.True(json["envelope"]!.AsArray().Count > 40);
    }

    [Fact]
    public void ProfileProjectRoundTripsWithSource()
    {
        var p = Wall();
        p.Phases[0].Include = false; p.Phases[2].Seismic = true;
        var copy = ProfileProject.Deserialize(p.Serialize());
        Assert.Equal(p.Serialize(), copy.Serialize());
        Assert.Equal(3, copy.Source!.Data.Count);
        // A new export keeps the phase settings by name.
        copy.UseSource(WallExport());
        Assert.False(copy.Phases[0].Include);
        Assert.True(copy.Phases[2].Seismic);
        Assert.False(copy.Settings.Confirmed);
    }

    [Fact]
    public void ExporterOutputParses()
    {
        string json = """{"info":{"UnitForce":"kN","UnitLength":"m","Version":"22.01","Title":"T"},"objects":[{"id":0,"kind":"Plate","name":"Plate_1"}],"phases":[{"id":1,"name":"Phase_1","title":"A"}],"columns":["x","y","n","m","q"],"data":[{"object":0,"phase":1,"nodes":[[5,0,-1,2,3],[5,-1,-2,3,4]]}]}""";
        var e = PlaxisBridge.Parse(json);
        Assert.Equal("Plate", e.Objects[0].Kind);
        Assert.Equal(-1, e.Data[0].Nodes[1][1]);
        Assert.Throws<InvalidDataException>(() => PlaxisBridge.Parse(json.Replace("[5,-1,-2,3,4]", "[5,-1,-2,3]")));

        // Read through Input: selection, calculation types, Safety and inactive phases.
        string input = json.Replace("\"title\":\"A\"}", "\"title\":\"A\",\"calc_type\":\"Plastic\"}")
            .Replace("]}]}", "]}],\"selection\":[\"Plate_1\"],\"excluded_phases\":[{\"name\":\"Phase_9\",\"title\":\"FS\",\"calc_type\":\"Safety\"}],\"inactive_phases\":[\"InitialPhase\"]}");
        var i = PlaxisBridge.Parse(input);
        Assert.Equal("Plastic", i.Phases[0].CalcType);
        Assert.Equal(["Plate_1"], i.Selection);
        Assert.Equal("Safety", i.ExcludedPhases.Single().CalcType);
        Assert.Equal(["InitialPhase"], i.InactivePhases);
    }

    [Fact]
    public void DemoExportInExamplesParsesWithUnits()
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "examples"))) root = Path.GetDirectoryName(root)!;
        var e = PlaxisBridge.Parse(File.ReadAllText(Path.Combine(root, "examples", "profile", "svg-demo.plaxis.json")));
        Assert.Equal("kN", e.Info.UnitForce);
        Assert.Equal("m", e.Info.UnitLength);
        Assert.Equal(3, e.Phases.Count);
        Assert.Equal("Выемка 1", e.Phases[0].Title);
    }

    /// <summary>The whole chain with a real Python and a stand-in plxscripting (tests/fake_plxscripting).</summary>
    [Fact]
    public async Task InputSelectionThroughPython()
    {
        string python = new[] { "/usr/bin/python3", "/usr/local/bin/python3" }.FirstOrDefault(File.Exists) ?? "";
        if (python.Length == 0) return;
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "tests", "fake_plxscripting"))) root = Path.GetDirectoryName(root)!;
        string old = Environment.GetEnvironmentVariable("PYTHONPATH") ?? "";
        Environment.SetEnvironmentVariable("PYTHONPATH", Path.Combine(root, "tests", "fake_plxscripting"));
        try
        {
            var e = await PlaxisBridge.FromInput(python, 10000, "secret");
            Assert.Equal(["Plate_1"], e.Selection);
            Assert.Equal(2, e.Objects.Count);
            Assert.Equal(["Phase_1"], e.Phases.Select(p => p.Name));
            Assert.Equal("Safety", e.ExcludedPhases.Single().CalcType);
            var p = new ProfileProject { Section = SectionProject.Example(SectionKind.Wall) };
            p.UseSource(e);
            var f = ForceProfile.Build(p);
            Assert.Equal(0, f.TopLevel, 9);
            Assert.Equal(-10, f.BottomLevel, 9);
            var wrong = await Assert.ThrowsAsync<InvalidOperationException>(() => PlaxisBridge.FromInput(python, 10000, "wrong"));
            Assert.Contains("Input", wrong.Message);
        }
        finally { Environment.SetEnvironmentVariable("PYTHONPATH", old); }
    }
}
