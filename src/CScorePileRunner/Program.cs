// Exploratory adapter to OpenCS CScore, pinned at 4ae6aa077be5b29145930ec093ba1c9eb7776222.
// IMPORTANT: This is NOT a complete SP63 RC design verification. No eta, shear or crack width.
using System.Text.Json;
using System.Text.Json.Serialization;
using CScore;

internal static class Program
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    static int Main()
    {
        try
        {
            var input = JsonSerializer.Deserialize<BatchInput>(Console.In.ReadToEnd(), ReadOptions)
                ?? throw new ArgumentException("Пустой JSON");
            Validate(input);
            var records = new List<object>();
            foreach (var candidate in input.Candidates)
            {
                var values = new List<object>();
                // Each input/candidate is independently checked; errors are never converted to 'pass'.
                try
                {
                    var section = MakeSection(input, candidate);
                    foreach (var load in input.Loads)
                    {
                        try
                        {
                            // IMPORTANT: etaDisabled; downstream report must not claim member verification.
                            // 'ten: false' is the ULS NDM convention in upstream CScore tests.
                            var solver = LimitForceSolver.ForCrossSection(
                                section, CalcType.C, ten: false);
                            var result = solver.AllFactor(load.NKn, load.MxKnm, load.MyKnm);
                            bool ok = result.Converged && double.IsFinite(result.Factor) &&
                                      double.IsFinite(result.Utilization) && result.Factor > 0.0 &&
                                      result.Utilization >= 0.0;
                            values.Add(new
                            {
                                id = load.Id,
                                converged = ok,
                                factor = ok ? (double?)result.Factor : null,
                                utilization = ok ? (double?)result.Utilization : null,
                                error = ok ? (string?)null : "no_convergence_or_invalid_result"
                            });
                        }
                        catch (Exception ex)
                        {
                            values.Add(new { id = load.Id, converged = false,
                                factor = (double?)null, utilization = (double?)null,
                                error = "solver_error:" + ex.GetType().Name + ":" + ex.Message });
                        }
                    }
                }
                catch (Exception ex)
                {
                    foreach (var load in input.Loads)
                        values.Add(new { id = load.Id, converged = false,
                            factor = (double?)null, utilization = (double?)null,
                            error = "section_error:" + ex.GetType().Name + ":" + ex.Message });
                }
                records.Add(new { id = candidate.Id, loads = values });
            }
            Console.Out.Write(JsonSerializer.Serialize(new
            {
                schema_version = 1,
                engine = "OpenCS.CScore.LimitForceSolver",
                engine_commit = "4ae6aa077be5b29145930ec093ba1c9eb7776222",
                scope = "uls_ndm_section_only_without_eta_shear_cracks",
                cases = records
            }));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 2;
        }
    }

    // CScore eagerly creates material diagrams for all four internal calculation states.
    private static readonly CalcType[] MaterialModes =
        [CalcType.C, CalcType.CL, CalcType.N, CalcType.NL];

    private static CrossSection MakeSection(BatchInput input, Candidate candidate)
    {
        double radiusM = input.Pile.DiameterMm / 2000.0;
        double barRadiusM = candidate.BarAxisRadiusMm / 1000.0;
        double barDiameterM = candidate.BarDiameterMm / 1000.0;
        int count = candidate.BarCount;
        int polygonSides = input.Pile.PolygonSides;
        int mesh = input.Pile.MeshResolution;
        var material = input.Materials;

        var concrete = new Material
        {
            Id = 1, Tag = "concrete-user-properties", Type = MatType.Concrete,
            E = material.ConcreteEKpa
        };
        // CScore.Material.GetD2L() builds diagrams for C, CL, N and NL unconditionally.
        // In this exploratory ULS-only runner CL/N/NL are technical placeholders
        // containing copies of C input. They are NOT valid serviceability or
        // long-term material definitions and must never be used for SLS/CL design.
        concrete.MaterialChars = MaterialModes.Select(mode => new MaterialChars(mode)
        {
            Type = MatType.Concrete,
            Fc = -material.ConcreteRbKpa,
            Ft = material.ConcreteRbtKpa,
            E = material.ConcreteEKpa,
            Ec1Red = material.ConcreteEc1,
            Ec2 = material.ConcreteEc2,
            Et1Red = material.ConcreteEt1,
            Et2 = material.ConcreteEt2
        }).ToList();
        var rebar = new Material
        {
            Id = 2, Tag = "reinforcement-user-properties", Type = MatType.ReSteelF,
            E = material.RebarEKpa
        };
        rebar.MaterialChars = MaterialModes.Select(mode => new MaterialChars(mode)
        {
            Type = MatType.ReSteelF,
            Fc = -material.RebarRsKpa,
            Ft = material.RebarRsKpa,
            E = material.RebarEKpa,
            Ec2 = material.RebarEc2,
            Et2 = material.RebarEt2
        }).ToList();

        var xs = new List<double>();
        var ys = new List<double>();
        for (int i = 0; i < polygonSides; i++)
        {
            double angle = 2.0 * Math.PI * i / polygonSides;
            xs.Add(radiusM * Math.Cos(angle));
            ys.Add(radiusM * Math.Sin(angle));
        }
        xs.Add(xs[0]);
        ys.Add(ys[0]);
        var contour = new Contour(xs, ys, "pile-concrete-contour") { Type = ContourType.Hull };
        var region = new MaterialArea
        {
            Id = 1, Tag = "concrete", Category = AreaCategory.Region,
            Material = concrete, MaterialId = concrete.Id, DiagrammType = DiagrammType.L2,
            Hull = contour, Contours = [contour], NX = mesh, NY = mesh
        };
        region.ResolveAndBuildDiagramms();
        region.SliceXY(mesh, mesh);

        double rotationRadians = candidate.RotationDeg * Math.PI / 180.0;
        var bars = Enumerable.Range(0, count)
            .Select(i =>
            {
                double angle = rotationRadians + 2.0 * Math.PI * i / count;
                return Fiber.CreatePoint(barDiameterM,
                    barRadiusM * Math.Cos(angle), barRadiusM * Math.Sin(angle));
            }).ToArray();
        var rebarRegion = MaterialArea.CreateRebarArea(bars, rebar, DiagrammType.L2, region);
        return new CrossSection { Tag = candidate.Id, Areas = [region, rebarRegion] };
    }

    private static void Validate(BatchInput input)
    {
        if (input.SchemaVersion != 1 || input.Pile is null || input.Materials is null ||
            input.Loads is null || input.Candidates is null ||
            input.Loads.Count == 0 || input.Candidates.Count == 0)
            throw new ArgumentException("Некорректная схема JSON");
        if (input.Pile.DiameterMm <= 0 || input.Pile.MeshResolution < 10 ||
            input.Pile.PolygonSides < 32)
            throw new ArgumentException("Некорректная геометрия сваи");
        foreach (var candidate in input.Candidates)
        {
            if (candidate.BarCount < 6 || candidate.BarDiameterMm <= 0 ||
                candidate.BarAxisRadiusMm <= 0 ||
                candidate.BarAxisRadiusMm + candidate.BarDiameterMm / 2 >= input.Pile.DiameterMm / 2)
                throw new ArgumentException("Недопустимый кандидат " + candidate.Id);
        }
    }
}

internal sealed class BatchInput
{
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("pile")] public Pile Pile { get; init; } = new();
    [JsonPropertyName("materials")] public Materials Materials { get; init; } = new();
    [JsonPropertyName("loads")] public List<LoadCase> Loads { get; init; } = [];
    [JsonPropertyName("candidates")] public List<Candidate> Candidates { get; init; } = [];
}

internal sealed class Pile
{
    [JsonPropertyName("diameter_mm")] public double DiameterMm { get; init; }
    [JsonPropertyName("mesh_resolution")] public int MeshResolution { get; init; }
    [JsonPropertyName("polygon_sides")] public int PolygonSides { get; init; }
}

internal sealed class Materials
{
    [JsonPropertyName("concrete_rb_kpa")] public double ConcreteRbKpa { get; init; }
    [JsonPropertyName("concrete_rbt_kpa")] public double ConcreteRbtKpa { get; init; }
    [JsonPropertyName("concrete_e_kpa")] public double ConcreteEKpa { get; init; }
    [JsonPropertyName("concrete_ec1")] public double ConcreteEc1 { get; init; }
    [JsonPropertyName("concrete_ec2")] public double ConcreteEc2 { get; init; }
    [JsonPropertyName("concrete_et1")] public double ConcreteEt1 { get; init; }
    [JsonPropertyName("concrete_et2")] public double ConcreteEt2 { get; init; }
    [JsonPropertyName("rebar_rs_kpa")] public double RebarRsKpa { get; init; }
    [JsonPropertyName("rebar_e_kpa")] public double RebarEKpa { get; init; }
    [JsonPropertyName("rebar_ec2")] public double RebarEc2 { get; init; }
    [JsonPropertyName("rebar_et2")] public double RebarEt2 { get; init; }
}

internal sealed class LoadCase
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("n_kn")] public double NKn { get; init; }
    [JsonPropertyName("mx_knm")] public double MxKnm { get; init; }
    [JsonPropertyName("my_knm")] public double MyKnm { get; init; }
}

internal sealed class Candidate
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("bar_count")] public int BarCount { get; init; }
    [JsonPropertyName("bar_diameter_mm")] public double BarDiameterMm { get; init; }
    [JsonPropertyName("rotation_deg")] public double RotationDeg { get; init; }
    [JsonPropertyName("bar_axis_radius_mm")] public double BarAxisRadiusMm { get; init; }
    [JsonPropertyName("steel_area_mm2")] public double SteelAreaMm2 { get; init; }
    [JsonPropertyName("clearance_mm")] public double ClearanceMm { get; init; }
}
