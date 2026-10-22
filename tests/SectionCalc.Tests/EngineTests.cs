using SectionCalc;
using Xunit;

public class EngineTests
{
    // ARBAT report 400x600 T2, already reproduced through the manual check
    // (docs/research/data/arbat-seven-full-2026-10-19/comparison.csv): 0.8697 / 1.7392 / 1.3044.
    static SectionProject Arbat400x600()
    {
        var p = new SectionProject { Kind = SectionKind.Wall };
        p.Wall = new WallGeometry { WidthMm = 400, ThicknessMm = 600, CoverMm = 42, StirrupDiameterMm = 8, MinClearSpacingMm = 20,
            PositiveFace = new WallFace { BarCount = 3, BarDiameterMm = 20 }, NegativeFace = new WallFace { BarCount = 3, BarDiameterMm = 20 } };
        p.Loads = [new LoadRow { Name = "T2", N = 0, Mx = 180 }];
        var c = p.Conditions;
        c.Eccentricity = EccentricityMode.Sp63; c.ElementLengthM = 3; c.StaticallyDeterminate = true;
        c.SecondOrder = SecondOrderMode.Cscore;
        return p;
    }

    [Fact]
    public void ReproducesVerifiedArbatComparison()
    {
        var r = Engine.Check(Arbat400x600());
        double Max(string id) => r.Rows.Where(x => x.CheckId == id).Max(x => x.Utilization ?? 0);
        Assert.Equal(0.8697, Max("normal_member_strength"), 4);
        Assert.Equal(1.7392, Max("crack_width_long"), 4);
        Assert.Equal(1.3044, Max("crack_width_short"), 4);
        Assert.Equal(Verdict.Failed, r.Verdict);
        Assert.Equal("crack_width_long", r.Governing!.CheckId);
    }

    // Same report, T10: biaxial, compression, k = 0.5 (ARBAT My/Mz → our Mx/My).
    [Fact]
    public void ReproducesArbatSustainedShareAndNcr()
    {
        var p = Arbat400x600();
        p.Loads = [new LoadRow { Name = "T10", N = -250, Mx = 140, My = 80, SustainedShare = 0.5 }];
        var r = Engine.Check(p);
        double Max(string id) => r.Rows.Where(x => x.CheckId == id).Max(x => x.Utilization ?? 0);
        Assert.Equal(0.7141, Max("normal_member_strength"), 3);
        Assert.Equal(1.0581, Max("crack_width_short"), 3);
        Assert.Equal(0.7923, Max("crack_width_long"), 3);
        Assert.Equal(0.0181, Max("member_stability"), 3);
    }

    [Fact]
    public void LoadFactorGivesNormativeActionsForCracks()
    {
        var p = Arbat400x600();
        p.Loads = [new LoadRow { Name = "T2", N = 0, Mx = 180 * 1.1, LoadFactor = 1.1 }];
        var r = Engine.Check(p);
        Assert.Equal(1.7392, r.Rows.Where(x => x.CheckId == "crack_width_long").Max(x => x.Utilization ?? 0), 4);
        Assert.True(r.Rows.Where(x => x.CheckId == "normal_member_strength").Max(x => x.Utilization ?? 0) > 0.8697 * 1.05);
    }

    // Refined ψs = the engine's own cracked-section σs,crc: the "native" column of the ARBAT comparison.
    [Fact]
    public void RefinedCrackMethodGivesNativeWidths()
    {
        var p = Arbat400x600();
        p.Conditions.CrackMethod = CrackMethod.Refined;
        var r = Engine.Check(p);
        double Max(string id) => r.Rows.Where(x => x.CheckId == id).Max(x => x.Utilization ?? 0);
        Assert.Equal(1.2771, Max("crack_width_long"), 3);
        Assert.Equal(0.9578, Max("crack_width_short"), 3);
        Assert.Equal(0.8697, Max("normal_member_strength"), 4);
    }

    [Fact]
    public void RefinedCrackMethodKeepsSp63SplitForPartlySustainedLoad()
    {
        var p = Arbat400x600();
        p.Loads = [new LoadRow { Name = "T10", N = -250, Mx = 140, My = 80, SustainedShare = 0.5 }];
        var conservative = Engine.Check(p);
        p.Conditions.CrackMethod = CrackMethod.Refined;
        var refined = Engine.Check(p);
        var crack = refined.Service.Single().Crack!.Value;
        double a1 = crack.GetProperty("a_crc_1_mm").GetDouble(), a2 = crack.GetProperty("a_crc_2_mm").GetDouble(), a3 = crack.GetProperty("a_crc_3_mm").GetDouble();
        double Width(CheckResult r, string id) => r.Rows.Single(x => x.CheckId == id).Details!.Value.GetProperty("width_mm").GetDouble();
        Assert.Equal(a1, Width(refined, "crack_width_long"), 9);
        Assert.Equal(a1 + a2 - a3, Width(refined, "crack_width_short"), 9);
        Assert.Equal(a1 / 1.4, a3, 9);
        Assert.True(Width(refined, "crack_width_long") < Width(conservative, "crack_width_long"));
        Assert.True(Width(refined, "crack_width_short") < Width(conservative, "crack_width_short"));
    }

    [Fact]
    public void ShortTermAndSeismicRaiseResistances()
    {
        var p = Arbat400x600();
        p.Loads = [new LoadRow { Name = "основное", Mx = 180 }, new LoadRow { Name = "кратк", Mx = 180, ShortTerm = true },
                   new LoadRow { Name = "сейсм", Mx = 180, Seismic = true }, new LoadRow { Name = "особое", Mx = 180, Special = true }];
        p.Conditions.ReliabilityUls = 1.1;
        var r = Engine.Check(p);
        double K(string name) => r.Rows.Where(x => x.CheckId == "normal_member_strength" && x.CaseId.StartsWith(name)).Max(x => x.Utilization ?? 0);
        Assert.True(K("кратк") < K("основное"));
        Assert.True(K("сейсм") < K("основное") / 1.1);
        Assert.True(K("особое") < K("основное") / 1.1);
        // Seismic (without the option) and special combinations do not take part in the crack checks.
        Assert.DoesNotContain(r.Rows, x => x.CheckId.StartsWith("crack_width") && (x.CaseId.StartsWith("сейсм") || x.CaseId.StartsWith("особое")));
        Assert.Contains(r.Rows, x => x.CheckId.StartsWith("crack_width") && x.CaseId == "кратк");
        Assert.Contains(r.Rows, x => x.Combination.Contains("сейсмическое"));
        Assert.Contains(r.Notes, n => n.Contains("m_tr"));
    }

    [Fact]
    public void SeismicMaterialsUseTheTrFactor()
    {
        var p = Arbat400x600();
        var normal = MaterialTables.Uls(p, new CombinationKind(false, false, false));
        var seismic = MaterialTables.Uls(p, new CombinationKind(false, true, false));
        var inclined = MaterialTables.Uls(p, new CombinationKind(false, true, false), inclined: true);
        Assert.Equal(normal.Rb * 1.2, seismic.Rb, 9);
        Assert.Equal(normal.Rs * 1.2, seismic.Rs, 9);
        Assert.Equal(normal.Rsw, inclined.Rsw, 9);
        var special = MaterialTables.Uls(p, new CombinationKind(false, false, true));
        Assert.Equal(MaterialTables.Concrete(p.ConcreteClass).RbSer, special.Rb, 9);
    }

    [Fact]
    public void NoCracksCategoryAndSlenderness()
    {
        var p = Arbat400x600();
        p.Loads = [new LoadRow { Name = "1", N = -100, Mx = 20 }];
        p.Conditions.CrackCategory = CrackCategory.NoCracks;
        p.Conditions.ElementLengthM = 40;
        var r = Engine.Check(p);
        Assert.Contains(r.Rows, x => x.CheckId == "no_cracks");
        Assert.DoesNotContain(r.Rows, x => x.CheckId.StartsWith("crack_width"));
        var slender = Assert.Single(r.Rows, x => x.CheckId == "slenderness_y");
        Assert.Equal(40000 / (400 / Math.Sqrt(12)) / 200, slender.Utilization!.Value, 6);
    }

    [Fact]
    public void FailedStrengthSkipsCracksAndReportsIt()
    {
        var p = Arbat400x600();
        p.Loads[0].Mx = 400;
        var r = Engine.Check(p);
        Assert.Equal(Verdict.Failed, r.Verdict);
        Assert.Contains(r.Rows, x => x.CheckId == "crack_width_long" && x.Status == "not_evaluated");
    }

    [Fact]
    public void InvalidInputGivesReadableMessages()
    {
        var p = Arbat400x600();
        p.Wall.PositiveFace.BarCount = 30;
        p.Loads.Clear();
        var problems = RequestBuilder.Validate(p);
        Assert.Contains(problems, m => m.Contains("не помещаются"));
        Assert.Contains(problems, m => m.Contains("сочетани", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SelectionReturnsLightestPassingFirstInAreaOrder()
    {
        var p = new SectionProject { Kind = SectionKind.Pile };
        p.Pile.DiameterMm = 600; p.Pile.BarCount = 6; p.Pile.BarDiameterMm = 12;
        p.Loads = [new LoadRow { Name = "U", N = -800, Mx = 120, LoadFactor = 1.33 }];
        p.Conditions.SecondOrder = SecondOrderMode.SectionOnly; p.Conditions.Eccentricity = EccentricityMode.InLoads;
        p.Selection = new SelectionSettings { PileCountMin = 6, PileCountMax = 12, PileCountStep = 2, Diameters = [12, 16, 20] };
        var r = Selection.Run(p);
        var areas = r.Outcomes.Select(o => o.Candidate.SteelAreaMm2).ToList();
        Assert.Equal(areas.Order(), areas);
        var best = Assert.IsType<SelectionOutcome>(r.Best);
        Assert.All(r.Outcomes.TakeWhile(o => o != best), o => Assert.NotEqual(Verdict.Passed, o.Verdict));
        Assert.True(best.Governing!.Utilization <= 1);
    }

    [Fact]
    public void ProjectRoundTripsThroughFile()
    {
        var p = SectionProject.Example(SectionKind.Wall);
        var copy = ProjectStore.Deserialize(ProjectStore.Serialize(p));
        Assert.Equal(ProjectStore.Serialize(p), ProjectStore.Serialize(copy));
        Assert.Contains("\"Kind\": \"Wall\"", ProjectStore.Serialize(p));
    }

    [Fact]
    public void CoverReferencesDescribeTheSameBars()
    {
        var a = new PileGeometry { DiameterMm = 880, CoverMm = 50, CoverReference = CoverReference.Transverse, SpiralDiameterMm = 12, BarDiameterMm = 32, BarCount = 16 };
        var b = new PileGeometry { DiameterMm = 880, CoverMm = 62, CoverReference = CoverReference.Bar, SpiralDiameterMm = 12, BarDiameterMm = 32, BarCount = 16 };
        var c = new PileGeometry { DiameterMm = 880, CoverMm = 78, CoverReference = CoverReference.Axis, SpiralDiameterMm = 12, BarDiameterMm = 32, BarCount = 16 };
        Assert.Equal(78, a.AxisDistanceMm, 9);
        Assert.Equal(78, b.AxisDistanceMm, 9);
        Assert.Equal(78, c.AxisDistanceMm, 9);
        Assert.Equal(RequestBuilder.PileLayout(a).Bars[3].XMm, RequestBuilder.PileLayout(b).Bars[3].XMm, 9);

        // ARBAT a1 = 50 mm is the clear cover to the bars: the 400×600 comparison used 42 mm to an Ø8 stirrup.
        var w = Arbat400x600().Wall;
        var arbat = new WallGeometry { WidthMm = 400, ThicknessMm = 600, CoverMm = 50, CoverReference = CoverReference.Bar, StirrupDiameterMm = 8,
            MinClearSpacingMm = 20, PositiveFace = w.PositiveFace, NegativeFace = w.NegativeFace };
        Assert.Equal(RequestBuilder.WallLayout(w).Bars.Select(x => (x.XMm, x.YMm)), RequestBuilder.WallLayout(arbat).Bars.Select(x => (x.XMm, x.YMm)));

        var p = Arbat400x600();
        p.Pile.CoverReference = CoverReference.Bar; p.Pile.CoverMm = 5; p.Pile.SpiralDiameterMm = 10; p.Kind = SectionKind.Pile;
        Assert.Contains(RequestBuilder.Validate(p), m => m.Contains("спираль"));
    }
}
