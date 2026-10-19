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
        p.Loads = [new LoadRow { Name = "T2", N = 0, Mx = 180 }, new LoadRow { Name = "T2 SLS", Basis = LoadBasis.Sls, N = 0, Mx = 180 }];
        var c = p.Conditions;
        c.Eccentricity = EccentricityMode.Sp63; c.MemberLengthM = 3; c.StaticallyDeterminate = true;
        c.SecondOrder = SecondOrderMode.Cscore; c.L0xM = 3; c.L0yM = 3; c.SustainedShare = 1;
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
        Assert.Contains(problems, m => m.Contains("ULS"));
    }

    [Fact]
    public void SelectionReturnsLightestPassingFirstInAreaOrder()
    {
        var p = new SectionProject { Kind = SectionKind.Pile };
        p.Pile.DiameterMm = 600; p.Pile.BarCount = 6; p.Pile.BarDiameterMm = 12;
        p.Loads = [new LoadRow { Name = "U", N = -800, Mx = 120 }, new LoadRow { Name = "S", Basis = LoadBasis.Sls, N = -600, Mx = 80 }];
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
}
