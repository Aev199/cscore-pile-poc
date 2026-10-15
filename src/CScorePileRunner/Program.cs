// Exploratory adapter to OpenCS CScore, pinned at 4ae6aa077be5b29145930ec093ba1c9eb7776222.
// IMPORTANT: ULS checks are incomplete; experimental SLS crack probe is diagnostic only and unverified.
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
                            // ARBAT reports the moment utilization at a FIXED N.
                            // AllFactor scales N and M together, so it is not the same metric.
                            // Calculate a second, independently qualified MomentFactor for comparison.
                            double? momentFactor = null, momentUtilization = null;
                            bool momentConverged = false;
                            string? momentError = null;
                            try
                            {
                                var momentSolver = LimitForceSolver.ForCrossSection(
                                    section, CalcType.C, ten: false);
                                var mr = momentSolver.MomentFactor(load.NKn, load.MxKnm, load.MyKnm);
                                momentConverged = mr.Converged && double.IsFinite(mr.Factor) &&
                                    double.IsFinite(mr.Utilization) && mr.Factor > 0.0 &&
                                    mr.Utilization >= 0.0;
                                if (momentConverged)
                                {
                                    momentFactor = mr.Factor;
                                    momentUtilization = mr.Utilization;
                                }
                                else
                                {
                                    momentError = "moment_no_convergence_or_invalid_result";
                                }
                            }
                            catch (Exception momentEx)
                            {
                                momentError = "moment_solver_error:" + momentEx.GetType().Name +
                                    ":" + momentEx.Message;
                            }
                            values.Add(new
                            {
                                id = load.Id,
                                moment_converged = momentConverged,
                                moment_factor = momentFactor,
                                moment_utilization = momentUtilization,
                                moment_error = momentError,
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
                // SLS probe is fully separate from the ULS decision path.
                // A numerical SLS value is NEVER interpreted as an approved design.
                var slsValues = input.SlsProbe is null
                    ? null : RunCrackProbe(input, candidate);
                records.Add(new { id = candidate.Id, loads = values, sls_probe = slsValues });
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

    // Research probe: explicit service properties and separate total / sustained bending
    // moments. CScore.CrackWidthSolver accepts only ONE axial N, so the probe uses
    // the same axial action for sustained and total loads. No ULS verdict is affected.
    private static object[] RunCrackProbe(BatchInput input, Candidate candidate)
    {
        var probe = input.SlsProbe!;
        try
        {
            var section = MakeSection(input, candidate);
            return probe.Loads.Select(load =>
            {
                try
                {
                    var solver = new CrackWidthSolver(
                        section,
                        calcCrc: CalcType.N,
                        calcService: CalcType.N,
                        calcServiceLong: CalcType.NL,
                        phi2: probe.Phi2,
                        acrcUltLong: probe.AcrcUltLongMm,
                        acrcUltShort: probe.AcrcUltShortMm);
                    var r = solver.Compute(load.NKn,
                        load.MxLongKnm, load.MxTotalKnm,
                        load.MyLongKnm, load.MyTotalKnm);
                    var mLong = Math.Sqrt(load.MxLongKnm * load.MxLongKnm
                                          + load.MyLongKnm * load.MyLongKnm);
                    bool finite = double.IsFinite(r.Mcrc) && r.Mcrc >= 0
                        && double.IsFinite(r.AcrcLong) && r.AcrcLong >= 0
                        && double.IsFinite(r.AcrcShort) && r.AcrcShort >= 0
                        && double.IsFinite(r.UtilLong) && r.UtilLong >= 0
                        && double.IsFinite(r.UtilShort) && r.UtilShort >= 0;
                    // Not cracked can be a valid outcome ONLY with a successful
                    // crack threshold search and a sustained moment below Mcrc.
                    // Otherwise the upstream solver may have returned its
                    // misleading zero-valued fallback object.
                    bool plausibleNoCrack = !r.Cracked
                        && mLong <= r.Mcrc + 1e-9;
                    bool validated = r.CrcConverged && finite
                        && (r.Cracked ? r.PlaneLong.HasValue : plausibleNoCrack);
                    string? error = validated ? null :
                        "indeterminate_crack_solver_or_upstream_silent_fallback";
                    return (object)new {
                        id = load.Id,
                        status = validated ? "research_calculated" : "indeterminate",
                        error,
                        cracked = (bool?)r.Cracked,
                        crc_converged = r.CrcConverged,
                        mcrc_knm = finite ? (double?)r.Mcrc : null,
                        width_long_mm = validated ? (double?)r.AcrcLong : null,
                        width_short_mm = validated ? (double?)r.AcrcShort : null,
                        utilization_long = validated ? (double?)r.UtilLong : null,
                        utilization_short = validated ? (double?)r.UtilShort : null,
                        verified_for_design = false
                    };
                }
                catch (Exception ex)
                {
                    return (object)new {
                        id = load.Id,
                        status = "indeterminate",
                        error = "crack_probe_error:" + ex.GetType().Name + ":" + ex.Message,
                        verified_for_design = false
                    };
                }
            }).Cast<object>().ToArray();
        }
        catch (Exception ex)
        {
            return probe.Loads.Select(load => (object)new {
                id = load.Id,
                status = "indeterminate",
                error = "crack_section_error:" + ex.GetType().Name + ":" + ex.Message,
                verified_for_design = false
            }).ToArray();
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
        var serviceShort = input.SlsProbe?.ShortMaterials ?? material;
        var serviceLong = input.SlsProbe?.LongMaterials ?? material;
        Materials Choose(CalcType mode) => mode switch {
            CalcType.N => serviceShort, CalcType.NL => serviceLong, _ => material
        };

        var concrete = new Material
        {
            Id = 1, Tag = "concrete-user-properties", Type = MatType.Concrete,
            E = material.ConcreteEKpa
        };
        // CScore.Material.GetD2L() builds diagrams for C, CL, N and NL unconditionally.
        // In this exploratory ULS-only runner CL/N/NL are technical placeholders
        // containing copies of C input. They are NOT valid serviceability or
        // long-term material definitions and must never be used for SLS/CL design.
        concrete.MaterialChars = MaterialModes.Select(mode =>
        {
            var m = Choose(mode);
            return new MaterialChars(mode)
            {
                Type = MatType.Concrete,
                Fc = -m.ConcreteRbKpa,
                Ft = m.ConcreteRbtKpa,
                E = m.ConcreteEKpa,
                Ec1Red = m.ConcreteEc1,
                Ec2 = m.ConcreteEc2,
                Et1Red = m.ConcreteEt1,
                Et2 = m.ConcreteEt2
            };
        }).ToList();
        var rebar = new Material
        {
            Id = 2, Tag = "reinforcement-user-properties", Type = MatType.ReSteelF,
            E = material.RebarEKpa
        };
        rebar.MaterialChars = MaterialModes.Select(mode =>
        {
            var m = Choose(mode);
            return new MaterialChars(mode)
            {
                Type = MatType.ReSteelF,
                Fc = -m.RebarRsKpa,
                Ft = m.RebarRsKpa,
                E = m.RebarEKpa,
                Ec2 = m.RebarEc2,
                Et2 = m.RebarEt2
            };
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

    private static void ValidateMaterials(Materials m)
    {
        var positive = new[] {m.ConcreteRbKpa, m.ConcreteRbtKpa, m.ConcreteEKpa,
                              m.RebarRsKpa, m.RebarEKpa, m.ConcreteEt1,
                              m.ConcreteEt2, m.RebarEt2};
        var negative = new[] {m.ConcreteEc1, m.ConcreteEc2, m.RebarEc2};
        if (positive.Any(x => !double.IsFinite(x) || x <= 0)
            || negative.Any(x => !double.IsFinite(x) || x >= 0))
            throw new ArgumentException("Invalid service material characteristics");
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
        if (input.SlsProbe is { } probe)
        {
            if (probe.ShortMaterials is null || probe.LongMaterials is null
                || probe.Loads is null || probe.Loads.Count == 0
                || !double.IsFinite(probe.Phi2) || probe.Phi2 <= 0
                || !double.IsFinite(probe.AcrcUltLongMm) || probe.AcrcUltLongMm <= 0
                || !double.IsFinite(probe.AcrcUltShortMm) || probe.AcrcUltShortMm <= 0)
                throw new ArgumentException("Explicit service characteristics, loads and limits required");
            ValidateMaterials(probe.ShortMaterials);
            ValidateMaterials(probe.LongMaterials);
            if (probe.Loads.Select(x => x.Id).Distinct().Count() != probe.Loads.Count
                || probe.Loads.Any(x => string.IsNullOrWhiteSpace(x.Id)
                    || new[] {x.NKn, x.MxTotalKnm, x.MyTotalKnm, x.MxLongKnm, x.MyLongKnm}
                        .Any(v => !double.IsFinite(v))))
                throw new ArgumentException("Nonfinite or duplicate SLS force tuples");
        }
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
    [JsonPropertyName("sls_probe")] public SlsProbe? SlsProbe { get; init; }
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

internal sealed class SlsProbe
{
    [JsonPropertyName("short_materials")] public Materials? ShortMaterials { get; init; }
    [JsonPropertyName("long_materials")] public Materials? LongMaterials { get; init; }
    [JsonPropertyName("loads")] public List<SlsLoad> Loads { get; init; } = [];
    [JsonPropertyName("phi2")] public double Phi2 { get; init; } = 0.5;
    [JsonPropertyName("acrc_ult_long_mm")] public double AcrcUltLongMm { get; init; } = 0.3;
    [JsonPropertyName("acrc_ult_short_mm")] public double AcrcUltShortMm { get; init; } = 0.4;
}
internal sealed class SlsLoad
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("n_kn")] public double NKn { get; init; }
    [JsonPropertyName("mx_long_knm")] public double MxLongKnm { get; init; }
    [JsonPropertyName("my_long_knm")] public double MyLongKnm { get; init; }
    [JsonPropertyName("mx_total_knm")] public double MxTotalKnm { get; init; }
    [JsonPropertyName("my_total_knm")] public double MyTotalKnm { get; init; }
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
