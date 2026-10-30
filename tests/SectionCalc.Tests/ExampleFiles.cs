using SectionCalc;
using Xunit;

// Shipped examples: SECTION_APP_EXAMPLES=examples/section-app dotnet test --filter ExampleFiles
public class ExampleFiles
{
    internal static SectionProject Pile()
    {
        var p = SectionProject.Example(SectionKind.Pile);
        p.Conditions.ShearCheck = true; p.Conditions.ShearModelConfirmed = true;
        p.Conditions.ElementLengthM = 8; p.Conditions.MuX = 0.7; p.Conditions.MuY = 0.7;
        p.Loads.Add(new LoadRow { Name = "3", N = -1300, Mx = 330, Q = 160, LoadFactor = 1, SustainedShare = 0.6, Seismic = true });
        return p;
    }

    internal static SectionProject Wall()
    {
        var p = SectionProject.Example(SectionKind.Wall);
        p.Concrete.Gb3 = 0.85;
        p.Conditions.ShearCheck = true; p.Conditions.ShearModelConfirmed = true;
        p.Conditions.ReliabilityUls = 1.1; p.Conditions.ReliabilitySls = 1.1;
        p.Loads.Add(new LoadRow { Name = "3", N = -700, Mx = 300, Q = 150, LoadFactor = 1, SustainedShare = 0.5, ShortTerm = true });
        p.Loads.Add(new LoadRow { Name = "4", N = -1000, Mx = 380, Q = 170, LoadFactor = 1, SustainedShare = 0.6, Special = true });
        return p;
    }

    /// <summary>Arbitrary section: a tee with the bars by the contour and the shear check.</summary>
    internal static SectionProject Tee()
    {
        var p = SectionProject.Example(SectionKind.Custom);
        p.Title = "Ригель таврового сечения";
        p.Custom = new CustomGeometry { Shape = ShapeKind.Tee, HeightMm = 700, FlangeWidthMm = 900, FlangeThicknessMm = 200, WebMm = 300,
            AxisDistanceMm = 50, BarDiameterMm = 20, MaxSpacingMm = 200, StirrupDiameterMm = 10 };
        p.Conditions.ShearCheck = true; p.Conditions.ShearModelConfirmed = true; p.Conditions.StirrupLegs = 2; p.Conditions.ShearPitchMm = 100;
        p.Loads = [new LoadRow { Name = "1", N = -600, Mx = 320, Q = 260, LoadFactor = 1.15, SustainedShare = 0.7 },
            new LoadRow { Name = "2", N = 0, Mx = -180, Q = 150, LoadFactor = 1.15, SustainedShare = 0.7 }];
        return p;
    }

    [Fact]
    public void ExamplesCheckAndWrite()
    {
        string? dir = Environment.GetEnvironmentVariable("SECTION_APP_EXAMPLES");
        foreach (var (name, p) in new[] { ("bns-800", Pile()), ("svg-600", Wall()), ("tavr-700", Tee()) })
        {
            Assert.Empty(RequestBuilder.Validate(p));
            var r = Engine.Check(p);
            Assert.NotEqual(Verdict.Incomplete, r.Verdict);
            if (dir is not { Length: > 0 }) continue;
            Directory.CreateDirectory(dir);
            ProjectStore.Save(p, Path.Combine(dir, name + ".section.json"));
            ReportWriter.Write(Path.Combine(dir, name switch { "bns-800" => "report-bns.docx", "svg-600" => "report-svg.docx", _ => "report-custom.docx" }), r, null, new DateTime(2026, 10, 20));
        }
    }
}
