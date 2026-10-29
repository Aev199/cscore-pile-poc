using SectionCalc;
using Xunit;

// Arbitrary section: any outline with holes, any bars.
public class CustomSectionTests
{
    /// <summary>The verified ARBAT 400×600 wall section entered as an arbitrary section with the same bars.</summary>
    static (SectionProject Wall, SectionProject Custom) Pair()
    {
        var wall = new SectionProject { Kind = SectionKind.Wall };
        wall.Wall = new WallGeometry { WidthMm = 400, ThicknessMm = 600, CoverMm = 42, StirrupDiameterMm = 8, MinClearSpacingMm = 20,
            PositiveFace = new WallFace { BarCount = 3, BarDiameterMm = 20 }, NegativeFace = new WallFace { BarCount = 3, BarDiameterMm = 20 } };
        wall.Loads = [new LoadRow { Name = "T10", N = -250, Mx = 140, My = 80, Q = 60, SustainedShare = 0.5 }];
        wall.Conditions.ElementLengthM = 3; wall.Conditions.StaticallyDeterminate = true;
        var custom = Selection.Clone(wall);
        custom.Kind = SectionKind.Custom;
        custom.Custom = new CustomGeometry
        {
            Shape = ShapeKind.Rectangle, WidthMm = 400, HeightMm = 600, Arrangement = BarArrangement.Manual, MinClearSpacingMm = 20,
            Bars = RequestBuilder.Layout(wall).Bars.Select(b => new BarSpec(b.XMm, b.YMm, b.DiameterMm)).ToList(),
        };
        return (wall, custom);
    }

    static double Max(CheckResult r, string id) => r.Rows.Where(x => x.CheckId == id).Max(x => x.Utilization ?? 0);

    [Fact]
    public void RectangleWithTheWallBarsGivesTheWallResult()
    {
        var (wall, custom) = Pair();
        var a = Engine.Check(wall); var b = Engine.Check(custom);
        foreach (var id in new[] { "normal_member_strength", "slenderness_x", "slenderness_y", "crack_width_long", "crack_width_short" })
            Assert.Equal(Max(a, id), Max(b, id), 3);
        Assert.Equal(a.EaXMm, b.EaXMm);
        Assert.Equal(a.EaYMm, b.EaYMm);
    }

    [Fact]
    public void ShearOfARectangleMatchesTheWall()
    {
        var (wall, custom) = Pair();
        foreach (var p in new[] { wall, custom })
        {
            p.Conditions.ShearCheck = true; p.Conditions.ShearModelConfirmed = true; p.Conditions.StirrupLegs = 2; p.Conditions.ShearPitchMm = 150;
        }
        custom.Custom.StirrupDiameterMm = 8;
        var a = Engine.Check(wall); var b = Engine.Check(custom);
        Assert.Equal(Max(a, "shear_inclined"), Max(b, "shear_inclined"), 3);
        Assert.True(Max(b, "shear_inclined") > 0);
    }

    [Fact]
    public void ForcesActAtTheCentroidOfAnOffsetPolygon()
    {
        // The same rectangle drawn away from the origin: the result does not change.
        var (_, custom) = Pair();
        var moved = Selection.Clone(custom);
        moved.Custom.Shape = ShapeKind.Polygon;
        moved.Custom.Vertices = [new(1000, 2000), new(1400, 2000), new(1400, 2600), new(1000, 2600)];
        foreach (var b in moved.Custom.Bars) { b.X += 1200; b.Y += 2300; }
        Assert.Equal(Max(Engine.Check(custom), "normal_member_strength"), Max(Engine.Check(moved), "normal_member_strength"), 3);
    }

    [Fact]
    public void PropertiesOfTemplates()
    {
        var tee = new CustomGeometry { Shape = ShapeKind.Tee, HeightMm = 600, FlangeWidthMm = 800, FlangeThicknessMm = 150, WebMm = 250 };
        var t = tee.Properties();
        Assert.Equal(800 * 150 + 250 * 450, t.AreaMm2, 6);
        // Centroid above the middle (flange on top): ȳ = Σ A·y / A.
        Assert.Equal((800 * 150 * 225 + 250 * 450 * -75.0) / (800 * 150 + 250 * 450), t.CyMm, 6);
        var box = new CustomGeometry { Shape = ShapeKind.Box, WidthMm = 400, HeightMm = 600, WallMm = 100 }.Properties();
        Assert.Equal(400 * 600 - 200 * 400, box.AreaMm2, 6);
        Assert.Equal((400 * Math.Pow(600, 3) - 200 * Math.Pow(400, 3)) / 12, box.IxMm4, 0);
        var ring = new CustomGeometry { Shape = ShapeKind.Ring, DiameterMm = 600, InnerDiameterMm = 400 }.Properties();
        Assert.True(Math.Abs(ring.AreaMm2 / (Math.PI * (600 * 600 - 400 * 400) / 4) - 1) < 1e-3);
    }

    [Fact]
    public void ContourArrangement()
    {
        // Inset 300×500: corners and intermediate bars at a spacing not above 200 → 2 + 3 segments per side pair.
        var rect = new CustomGeometry { WidthMm = 400, HeightMm = 600, AxisDistanceMm = 50, BarDiameterMm = 20, MaxSpacingMm = 200 };
        var bars = rect.ContourBars();
        Assert.Equal(10, bars.Count);
        Assert.All(bars, b => Assert.True(Math.Abs(Math.Abs(b.X) - 150) < 1e-6 || Math.Abs(Math.Abs(b.Y) - 250) < 1e-6));
        Assert.Null(RequestBuilder.CustomLayout(rect).Problem);
        // Thin box wall: one row in the middle of the wall.
        var box = new CustomGeometry { Shape = ShapeKind.Box, WidthMm = 600, HeightMm = 600, WallMm = 100, AxisDistanceMm = 50, BarDiameterMm = 16, MaxSpacingMm = 250 };
        Assert.All(box.ContourBars(), b => Assert.True(Math.Abs(Math.Abs(b.X) - 250) < 1e-6 || Math.Abs(Math.Abs(b.Y) - 250) < 1e-6));
        Assert.Null(RequestBuilder.CustomLayout(box).Problem);
        // Thick ring: two rows of bars.
        var ring = new CustomGeometry { Shape = ShapeKind.Ring, DiameterMm = 1200, InnerDiameterMm = 600, AxisDistanceMm = 60, BarDiameterMm = 20, MaxSpacingMm = 200 };
        var radii = ring.ContourBars().Select(b => Math.Round(Math.Sqrt(b.X * b.X + b.Y * b.Y))).Distinct().OrderBy(r => r).ToList();
        Assert.Equal([360.0, 540.0], radii);
        // Tee: corners of the inset outline, including the re-entrant ones.
        var tee = new CustomGeometry { Shape = ShapeKind.Tee, HeightMm = 600, FlangeWidthMm = 800, FlangeThicknessMm = 150, WebMm = 250, AxisDistanceMm = 50 };
        Assert.Null(RequestBuilder.CustomLayout(tee).Problem);
    }

    [Fact]
    public void BarOutsideConcreteOrInHoleIsReported()
    {
        var g = new CustomGeometry { Shape = ShapeKind.Box, WidthMm = 600, HeightMm = 600, WallMm = 150, Arrangement = BarArrangement.Manual,
            Bars = [new(-250, -250, 20), new(250, 250, 20), new(0, 0, 20)] };
        Assert.Contains("выходит из бетона", RequestBuilder.CustomLayout(g).Problem ?? "null");
        g.Bars[2] = new(250, -250, 20);
        Assert.Null(RequestBuilder.CustomLayout(g).Problem);
        g.Shape = ShapeKind.Polygon; g.Vertices = [new(0, 0), new(100, 100), new(100, 0), new(0, 100)];
        Assert.Contains("пересекает", RequestBuilder.CustomLayout(g).Problem);
    }

    [Fact]
    public void HollowTeeAndRingAreCalculatedAndSelected()
    {
        var p = SectionProject.Example(SectionKind.Custom);
        p.Custom = new CustomGeometry { Shape = ShapeKind.Ring, DiameterMm = 800, InnerDiameterMm = 500, AxisDistanceMm = 60, BarDiameterMm = 20, MaxSpacingMm = 200 };
        var r = Engine.Check(p);
        Assert.Contains(r.Rows, x => x.CheckId == "normal_member_strength" && x.Utilization > 0);
        // Tee with a flange thick enough for two rows at a = 60.
        p.Custom.Shape = ShapeKind.Tee; p.Custom.FlangeThicknessMm = 200;
        p.Selection.Diameters = [12, 16, 20, 25];
        p.Selection.CustomSpacings = [150, 250];
        var s = Selection.Run(p);
        Assert.True(s.Best is not null, string.Join("\n", s.Outcomes.Select(o => $"{o.Candidate.Description} {o.Verdict} {o.Governing?.Title} {o.Governing?.Utilization} {o.Error}")) + $" rejected {s.GeometricallyRejected}");
        Assert.Contains("шаг", s.Best!.Candidate.Description);
        string path = Path.Combine(Path.GetTempPath(), $"custom-{Guid.NewGuid():N}.docx");
        ReportWriter.Write(path, Engine.Check(s.Best.Candidate.Project), s);
        Assert.True(new FileInfo(path).Length > 10_000);
        File.Delete(path);
    }

    [Fact]
    public void ProjectRoundTrip()
    {
        var p = SectionProject.Example(SectionKind.Custom);
        p.Custom.Shape = ShapeKind.Polygon; p.Custom.Arrangement = BarArrangement.Manual;
        p.Custom.Bars = [new(1, 2, 16), new(3, 4, 20)];
        var q = ProjectStore.Deserialize(ProjectStore.Serialize(p));
        Assert.Equal(SectionKind.Custom, q.Kind);
        Assert.Equal(ShapeKind.Polygon, q.Custom.Shape);
        Assert.Equal(20, q.Custom.Bars[1].DiameterMm);
        Assert.Equal(4, q.Custom.Vertices.Count);
    }
}
