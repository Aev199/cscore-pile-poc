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
            var steps = new List<string>();
            var e = await PlaxisBridge.FromInput(python, 10000, "secret", log: steps.Add);
            Assert.Contains(steps, x => x.Contains("Output: подключение"));
            Assert.Contains("Огибающая", File.ReadAllText(PlaxisBridge.LogPath));
            Assert.Equal(["Plate_1"], e.Selection);
            Assert.Equal(2, e.Objects.Count);
            Assert.True(e.IsEnvelope);
            Assert.Equal(["Phase_1"], e.Phases.Select(p => p.Name));
            Assert.Equal(["InitialPhase", "Phase_1"], e.Phases[0].Covers);
            Assert.Equal("Safety", e.ExcludedPhases.Single().CalcType);
            var p = new ProfileProject { Section = SectionProject.Example(SectionKind.Wall) };
            p.UseSource(e);
            var f = ForceProfile.Build(p);
            Assert.True(f.IsEnvelope);
            Assert.Equal(0, f.TopLevel, 9);
            Assert.Equal(-10, f.BottomLevel, 9);
            // Envelope at −10 m: M from 500 to 1000 kN·m/m, N from −220 to −110 kN/m (wall b = 1 m).
            var bottom = f.Envelope.Last();
            Assert.Equal(1000, bottom.MMax, 6); Assert.Equal(500, bottom.MMin, 6);
            Assert.Equal(-220, bottom.NMin, 6); Assert.Equal(-110, bottom.NMax, 6);
            p.Settings.StationStepM = 5;
            var rsu = f.Stations(p)[^1].Rsu;
            Assert.Equal(4, rsu.Count);
            Assert.Contains(rsu, r => r.Mx == 1000 && r.N == -220);
            // Extremes over the whole interval −7,5…−10 m: Nmax = −85 at −7,5 m.
            Assert.Contains(rsu, r => r.Mx == 1000 && Math.Abs(r.N + 85) < 1e-9);
            Assert.All(rsu, r => Assert.Equal(100, r.Q, 6));
            p.Settings.Confirmed = true;
            var reinforcement = RequiredReinforcement.Run(p);
            Assert.All(reinforcement.Stations, st => Assert.Contains(st.Status, new[] { "ok", "minimum", "exceeded" }));
            Assert.Contains("огибающая", reinforcement.Stations[^1].Combination);
            Assert.Equal("envelope", (string)System.Text.Json.Nodes.JsonNode.Parse(RevitExport.Json(reinforcement))!["source"]!["forces"]!);
            var wrong = await Assert.ThrowsAsync<InvalidOperationException>(() => PlaxisBridge.FromInput(python, 10000, "wrong"));
            Assert.Contains("Input", wrong.Message);
        }
        finally { Environment.SetEnvironmentVariable("PYTHONPATH", old); }
    }

    [Fact]
    public void WallRowsLayout()
    {
        var w = new WallGeometry { WidthMm = 1000, ThicknessMm = 800, CoverMm = 50, CoverReference = CoverReference.Bar, StirrupDiameterMm = 12,
            PositiveFace = new WallFace { BarCount = 5, BarDiameterMm = 32, Rows = 3, RowGapMm = 32 },
            NegativeFace = new WallFace { BarCount = 5, BarDiameterMm = 20, Rows = 1 } };
        var layout = RequestBuilder.WallLayout(w);
        Assert.Null(layout.Problem);
        Assert.Equal(20, layout.Bars.Count);
        var ys = layout.Bars.Where(b => b.YMm > 0).Select(b => b.YMm).Distinct().OrderByDescending(y => y).ToList();
        // Rows at 50 + 16, + 64, + 128 mm from the face.
        Assert.Equal([400 - 66, 400 - 130, 400 - 194], ys);
        Assert.Equal(3 * 5 * Math.PI * 32 * 32 / 4 + 5 * Math.PI * 20 * 20 / 4, layout.SteelAreaMm2, 6);
        Assert.Equal(66 + 64, w.CentroidMm(w.PositiveFace), 9);
        w.PositiveFace.Rows = 4;
        Assert.NotNull(RequestBuilder.WallLayout(w).Problem);
    }

    [Fact]
    public void PeakCutsByChordAndLimit()
    {
        var p = Wall();
        var f0 = ForceProfile.Build(p);
        double peak = f0.Envelope.Max(e => e.MMax);
        double level = f0.Envelope.MaxBy(e => e.MMax)!.Level;
        p.Settings.PeakCuts = [new PeakCut { TopLevel = level + 1, BottomLevel = level - 1, Moment = true, Method = PeakCutMethod.Chord }];
        var f1 = ForceProfile.Build(p);
        double edges = Math.Max(f0.Envelope.Where(e => Math.Abs(e.Level - (level + 1)) < 0.13).Max(e => e.MMax),
            f0.Envelope.Where(e => Math.Abs(e.Level - (level - 1)) < 0.13).Max(e => e.MMax));
        Assert.True(f1.Envelope.Max(e => e.MMax) <= edges + 1e-6);
        Assert.Equal(peak, f1.OriginalEnvelope.Max(e => e.MMax), 9);
        // Q is untouched by a moment-only cut.
        Assert.Equal(f0.Envelope.Max(e => e.QAbsMax), f1.Envelope.Max(e => e.QAbsMax), 9);

        p.Settings.PeakCuts = [new PeakCut { TopLevel = 0, BottomLevel = -15, Moment = true, Shear = true, Method = PeakCutMethod.Limit, MomentLimit = 200, ShearLimit = 50 }];
        var f2 = ForceProfile.Build(p);
        Assert.Equal(200, f2.Envelope.Where(e => e.Level < -0.01 && e.Level > -14.99).Max(e => e.MMax), 9);
        Assert.True(f2.Envelope.Where(e => e.Level < -0.01 && e.Level > -14.99).Max(e => e.QAbsMax) <= 50 + 1e-9);
    }

    [Fact]
    public void AxialForceCanBeIgnored()
    {
        var p = Wall();
        p.Settings.IgnoreAxialForce = true;
        var stations = ForceProfile.Build(p).Stations(p);
        Assert.All(stations.SelectMany(s => s.Rsu), r => Assert.Equal(0, r.N));
    }

    [Fact]
    public void RequiredAreaDoesNotDependOnTheGivenBars()
    {
        static ProfileProject Make(int bars)
        {
            var p = Wall();
            p.Settings.StationStepM = 7.5;
            p.Section.Wall.PositiveFace.BarCount = bars; p.Section.Wall.NegativeFace.BarCount = bars;
            return p;
        }
        var a = RequiredReinforcement.Run(Make(4));
        var b = RequiredReinforcement.Run(Make(9));
        Assert.Equal(a.Stations.Select(s => Math.Round(s.AsRightMm2, 3)), b.Stations.Select(s => Math.Round(s.AsRightMm2, 3)));
        Assert.Equal(a.Stations.Select(s => Math.Round(s.AsLeftMm2, 3)), b.Stations.Select(s => Math.Round(s.AsLeftMm2, 3)));
        Assert.True(a.Stations[1].AsRightMm2 > a.Stations[0].AsRightMm2);
    }

    [Fact]
    public void MomentSignHintIsAdopted()
    {
        var p = Wall();
        var e = WallExport();
        e.MomentSign = new MomentSignHint { Sign = -1, Agreement = 0.95, Phase = "Phase_3" };
        p.UseSource(e);
        Assert.Equal(-1, p.Settings.MomentSign);
        e.MomentSign = new MomentSignHint { Sign = 1, Agreement = 0.3 };
        p.UseSource(e);
        Assert.Equal(-1, p.Settings.MomentSign);
        e.MomentSign = new MomentSignHint { Sign = 1, Agreement = 0.9, Excavation = "left" };
        p.UseSource(e);
        Assert.Equal(-1, p.Settings.ExcavationSide);
        var json = System.Text.Json.Nodes.JsonNode.Parse(RevitExport.Json(new ReinforcementProfile(p, ForceProfile.Build(p), [])))!;
        Assert.Equal("left", (string)json["faces"]!["excavation"]!);
        Assert.Contains("(котлован)", (string)json["faces"]!["left"]!);
    }

    /// <summary>
    /// PLAXIS 3D, kN and m: two piles of a group as embedded beams. EmbeddedBeam_1 (two Output segments) has its head
    /// at 0 and 12 m long; EmbeddedBeam_2 has its head at −1 m, 10 m long, and twice the moments.
    /// </summary>
    internal static PlaxisExport GroupExport(bool envelope)
    {
        var export = new PlaxisExport
        {
            Info = new PlaxisInfo { UnitForce = "kN", UnitLength = "m", Version = "2024.1", Title = "Котлован 3D", Dimension = "3D" },
            Objects = [new PlaxisObject { Id = 0, Kind = "EmbeddedBeam", Name = "EmbeddedBeam_1_1", Pile = "EmbeddedBeam_1" },
                       new PlaxisObject { Id = 1, Kind = "EmbeddedBeam", Name = "EmbeddedBeam_1_2", Pile = "EmbeddedBeam_1" },
                       new PlaxisObject { Id = 2, Kind = "EmbeddedBeam", Name = "EmbeddedBeam_2_1", Pile = "EmbeddedBeam_2" }],
            Phases = [new PlaxisPhase { Id = 1, Name = "Phase_1" }, new PlaxisPhase { Id = 2, Name = "Phase_2" }],
            Columns = [.. envelope ? PlaxisExport.EnvelopeColumns3D : PlaxisExport.PhaseColumns3D],
            Mode = envelope ? "envelope" : null,
        };
        void Add(int obj, double head, double from, double to, double x, double k)
        {
            foreach (var phase in export.Phases)
            {
                var nodes = new List<double[]>();
                for (double t = from; t <= to + 1e-9; t += 0.5)
                {
                    // M2 = 30, M3 = 40 → resultant 50 kN·m at the head, decreasing to zero at 8 m below it.
                    double m = Math.Max(0, 1 - t / 8) * k * phase.Id, n = -100 * k, q = 5 * k;
                    nodes.Add(envelope
                        ? [x, 2, head - t, n * 1.2, n * 0.8, -30 * m, 30 * m, -40 * m * 0.5, 40 * m, -3 * q, 3 * q, -4 * q, 4 * q]
                        : [x, 2, head - t, n, 30 * m, -40 * m, 3 * q, -4 * q]);
                }
                export.Data.Add(new PlaxisSeries { Object = obj, Phase = phase.Id, Nodes = nodes });
            }
        }
        Add(0, 0, 0, 6, 1, 1);
        Add(1, 0, 6, 12, 1, 1);
        Add(2, -1, 0, 10, 4, 2);
        return export;
    }

    [Fact]
    public void PileGroupFrom3DGetsOneReinforcement()
    {
        var p = new ProfileProject { Title = "Группа", Section = SectionProject.Example(SectionKind.Pile) };
        var e = PlaxisBridge.Parse(System.Text.Json.JsonSerializer.Serialize(GroupExport(envelope: false),
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        Assert.True(e.Is3D);
        p.UseSource(e);
        p.Settings.PileSpacingM = 99; // not used in 3D: forces are per pile
        var f = ForceProfile.Build(p);
        Assert.True(f.IsGroup);
        Assert.Equal(["EmbeddedBeam_1", "EmbeddedBeam_2"], f.Piles.Select(x => x.Name));
        // Heads aligned: pile 2 is moved up by 1 m, the profile runs from 0 to −12.
        Assert.Equal(1, f.Piles[1].Shift, 9);
        Assert.Equal(0, f.TopLevel, 9); Assert.Equal(-12, f.BottomLevel, 9);
        // Resultant √(30² + 40²) = 50 per unit; pile 2, phase 2: 50 × 2 × 2 = 200 kN·m at the head.
        Assert.Equal(200, f.Envelope[0].MMax, 6);
        Assert.Equal(5 * 5 * 2, f.Envelope[0].QAbsMax, 6);
        Assert.Equal(-200, f.Envelope[0].NMin, 6);
        // Each phase alone: phase 1 gives half the moment of phase 2.
        var extremes = ForceProfile.PhaseExtremes(p);
        Assert.Equal(100, extremes[1].MAbs, 6); Assert.Equal(200, extremes[2].MAbs, 6);

        p.Settings.StationStepM = 6; p.Settings.Confirmed = true;
        var stations = f.Stations(p);
        Assert.Contains(stations[0].Rsu, r => r.Name.StartsWith("EmbeddedBeam_2, Phase_2") && Math.Abs(r.Mx - 200) < 1e-6);
        var r = RequiredReinforcement.Run(p);
        Assert.Equal(3, r.Stations.Count);
        Assert.Contains("EmbeddedBeam_2", r.Stations[0].Combination);
        Assert.True(r.Stations[0].AsRightMm2 >= r.Stations[2].AsRightMm2);
        var json = System.Text.Json.Nodes.JsonNode.Parse(RevitExport.Json(r))!;
        Assert.Equal("PLAXIS 3D", (string)json["source"]!["program"]!);
        var group = json["element"]!["group"]!;
        Assert.Equal("head", (string)group["level_reference"]!);
        Assert.Equal(-1, (double)group["piles"]![1]!["head_level_m"]!, 9);
        Assert.Equal(-11, (double)group["piles"]![1]!["tip_level_m"]!, 9);
        Assert.Null(json["element"]!["pile"]!["spacing_m"]);

        // Without alignment the model elevations are kept: pile 2 starts at −1.
        p.Settings.AlignPileHeads = false;
        var g = ForceProfile.Build(p);
        Assert.Equal(0, g.Piles[1].Shift, 9);
        Assert.Equal(100, g.Envelope[0].MMax, 6);
    }

    [Fact]
    public void PileGroupEnvelopesCombineM2AndM3OnTheSafeSide()
    {
        var p = new ProfileProject { Section = SectionProject.Example(SectionKind.Pile) };
        p.UseSource(GroupExport(envelope: true));
        var f = ForceProfile.Build(p);
        Assert.True(f.IsEnvelope);
        // Largest |M2| = 30, |M3| = 40 per unit: 50 × 2 × 2 = 200; N from −240 to −160 (pile 2).
        Assert.Equal(200, f.Envelope[0].MMax, 6);
        Assert.Equal(-240, f.Envelope[0].NMin, 6);
        p.Settings.StationStepM = 6;
        var rsu = f.Stations(p)[0].Rsu;
        Assert.Contains(rsu, r => r.Name.Contains("EmbeddedBeam_2 (из 2 свай)"));
        Assert.Contains(rsu, r => Math.Abs(r.Mx - 200) < 1e-6);
    }

    [Fact]
    public void InclinedPileIsRejected()
    {
        var p = new ProfileProject { Section = SectionProject.Example(SectionKind.Pile) };
        var e = GroupExport(envelope: false);
        e.Data[0].Nodes[^1][0] += 0.2;
        p.UseSource(e);
        var ex = Assert.Throws<InvalidOperationException>(() => ForceProfile.Build(p));
        Assert.Contains("EmbeddedBeam_1", ex.Message);
    }

    [Fact]
    public void RetainingPilesFromPlateAreForcesPerMetreTimesSpacing()
    {
        var p = new ProfileProject { Section = SectionProject.Example(SectionKind.Pile) };
        p.UseSource(WallExport());
        Assert.Equal(ProfileScenario.Wall, p.Settings.Scenario);
        // Plate with a pile section: the scenario must be chosen.
        Assert.Contains("СВГ", Assert.Throws<InvalidOperationException>(() => ForceProfile.Build(p)).Message);
        p.Settings.Scenario = ProfileScenario.RetainingPiles;
        p.Settings.PileSpacingM = 1.5;
        var f = ForceProfile.Build(p);
        Assert.False(f.IsWall);
        // Up to 280 kN·m/m × 1,5 m, the pile takes |M|.
        Assert.InRange(f.Envelope.Max(e => e.MMax), 280 * 1.5 * 0.99, 280 * 1.5);
        p.Settings.StationStepM = 15;
        Assert.All(f.Stations(p).SelectMany(st => st.Rsu), r => Assert.True(r.Mx >= 0));
        // A 3D export needs the 3D scenario and the other way round.
        p.Settings.Scenario = ProfileScenario.Piles3D;
        Assert.Contains("2D", Assert.Throws<InvalidOperationException>(() => ForceProfile.Build(p)).Message);
    }

    [Fact]
    public void GivenSectionIsCheckedAlongTheElement()
    {
        var p = Wall();
        p.Settings.StationStepM = 5;
        p.Settings.Mode = ProfileMode.Check;
        var w = p.Section.Wall;
        w.PositiveFace.BarCount = 5; w.PositiveFace.BarDiameterMm = 12;
        w.NegativeFace.BarCount = 5; w.NegativeFace.BarDiameterMm = 12;
        var weak = RequiredReinforcement.Check(p);
        Assert.True(weak.IsCheck);
        Assert.Equal(Math.Round(5 * Math.PI * 36, 3), Math.Round(weak.Stations[0].AsRightMm2, 3));
        Assert.Contains(weak.Stations, st => st.Status == "exceeded" && st.K > 1);
        w.PositiveFace.BarDiameterMm = 32; w.NegativeFace.BarDiameterMm = 32; w.PositiveFace.BarCount = 10; w.NegativeFace.BarCount = 10;
        var strong = RequiredReinforcement.Check(p);
        Assert.All(strong.Stations.Where(st => st.K is not null), st => Assert.True(st.K <= 1, $"{st.Station.Level}: {st.K} {st.Governing}"));
        var json = System.Text.Json.Nodes.JsonNode.Parse(RevitExport.Json(strong))!;
        Assert.Equal("check", (string)json["mode"]!);
        Assert.Equal("wall_2d", (string)json["scenario"]!);
    }
}
