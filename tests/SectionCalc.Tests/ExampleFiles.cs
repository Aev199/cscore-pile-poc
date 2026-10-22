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

    [Fact]
    public void ExamplesCheckAndWrite()
    {
        string? dir = Environment.GetEnvironmentVariable("SECTION_APP_EXAMPLES");
        foreach (var (name, p) in new[] { ("bns-800", Pile()), ("svg-600", Wall()) })
        {
            Assert.Empty(RequestBuilder.Validate(p));
            var r = Engine.Check(p);
            Assert.NotEqual(Verdict.Incomplete, r.Verdict);
            if (dir is not { Length: > 0 }) continue;
            Directory.CreateDirectory(dir);
            ProjectStore.Save(p, Path.Combine(dir, name + ".section.json"));
            ReportWriter.Write(Path.Combine(dir, name == "bns-800" ? "report-bns.docx" : "report-svg.docx"), r, null, new DateTime(2026, 10, 20));
        }
    }
}
