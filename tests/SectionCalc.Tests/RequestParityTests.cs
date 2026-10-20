using System.Text.Json.Nodes;
using SectionCalc;
using Xunit;

// The application must send CScore exactly what the verified Python single-section path sends.
public class RequestParityTests
{
    static SectionProject Pile()
    {
        var p = new SectionProject { Kind = SectionKind.Pile };
        p.Pile = new PileGeometry { DiameterMm = 900, CoverMm = 55, SpiralDiameterMm = 10, BarCount = 14, BarDiameterMm = 25 };
        p.Loads = [new LoadRow { Name = "ULS:U #1", N = -2000, Mx = 320, My = 40, Q = 150 }];
        Common(p);
        p.Conditions.ShearPitchMm = 150; p.Conditions.PileShearAMm = 90;
        return p;
    }

    static SectionProject Wall()
    {
        var p = new SectionProject { Kind = SectionKind.Wall };
        p.Wall = new WallGeometry { WidthMm = 1000, ThicknessMm = 600, CoverMm = 50, StirrupDiameterMm = 10,
            NegativeFace = new WallFace { BarCount = 4, BarDiameterMm = 20 }, PositiveFace = new WallFace { BarCount = 5, BarDiameterMm = 25 } };
        p.Loads = [new LoadRow { Name = "ULS:U #1", N = -800, Mx = -350, Q = 200 }];
        Common(p);
        p.Conditions.ShearPitchMm = 200; p.Conditions.StirrupLegs = 4;
        p.Conditions.InclinedMoment = true; p.Conditions.AnchorageFactor = 0.8;
        p.Conditions.SupportDistanceDownM = 2.5; p.Conditions.SupportDistanceUpM = 3.5;
        return p;
    }

    static void Common(SectionProject p)
    {
        var c = p.Conditions;
        c.Eccentricity = EccentricityMode.Sp63; c.ElementLengthM = 7; c.StaticallyDeterminate = false;
        c.SecondOrder = SecondOrderMode.Cscore; c.MuX = 1; c.MuY = 6.5 / 7;
        c.ShearCheck = true; c.ShearModelConfirmed = true;
    }

    [Theory]
    [InlineData("pile")]
    [InlineData("wall")]
    public void RequestMatchesVerifiedPythonPath(string name)
    {
        var p = name == "pile" ? Pile() : Wall();
        var source = p.Loads.Select(l => new UlsLoad(l.Name, l.Name, l.N, l.Mx, l.My, l.Q)).ToList();
        var (uls, _, _) = Engine.PrepareUls(p, source);
        // The golden SLS actions were given directly as normative total / sustained values.
        List<SlsAction> sls = name == "pile" ? [new("S #1", -1500, -1100, 240, 180, 30, 20)] : [new("S #1", -600, -600, -260, -200, 0, 0)];
        var materials = MaterialTables.Uls(p, new CombinationKind(false, false, false));
        var ours = JsonNode.Parse(RequestBuilder.Build(p, uls, sls, materials, 0.8).ToJsonString())!;
        var golden = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", $"{name}_request.json")))!;
        // Candidate labels are cosmetic; Python prefixes engine ids with the basis.
        ours["candidates"]![0]!["id"] = golden["candidates"]![0]!["id"]!.GetValue<string>();
        foreach (var load in ours["loads"]!.AsArray()) load!["id"] = "ULS:" + ((string)load["id"]!).Replace("ULS:", "");
        Same(golden, ours, "$");
    }

    static void Same(JsonNode? expected, JsonNode? actual, string path)
    {
        switch (expected)
        {
            case null: Assert.True(actual is null, $"{path}: expected null, got {actual?.ToJsonString()}"); break;
            case JsonObject eo:
                var ao = Assert.IsType<JsonObject>(actual);
                Assert.Equal(eo.Select(kv => kv.Key).Order(), ao.Select(kv => kv.Key).Order());
                foreach (var (key, value) in eo) Same(value, ao[key], $"{path}.{key}");
                break;
            case JsonArray ea:
                var aa = Assert.IsType<JsonArray>(actual);
                Assert.Equal(ea.Count, aa.Count);
                for (int i = 0; i < ea.Count; i++) Same(ea[i], aa[i], $"{path}[{i}]");
                break;
            default:
                var ev = (JsonValue)expected;
                if (ev.TryGetValue<double>(out var e))
                {
                    Assert.True(actual is JsonValue av && av.TryGetValue<double>(out var a)
                        && Math.Abs(a - e) <= 1e-9 * Math.Max(1, Math.Abs(e)), $"{path}: {e} vs {actual?.ToJsonString()}");
                }
                else Assert.Equal(expected.ToJsonString(), actual?.ToJsonString());
                break;
        }
    }
}
