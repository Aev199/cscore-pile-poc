// Exploratory adapter to OpenCS CScore, pinned at 4ae6aa077be5b29145930ec093ba1c9eb7776222.
// IMPORTANT: ULS checks are incomplete; experimental SLS crack probe is diagnostic only and unverified.
using System.Text.Json;
using System.Text.Json.Serialization;
using CScore;
using CScore.Sp63Shear;

internal static class Program
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    static int Main(string[] args)
    {
        try
        {
            if (args.SequenceEqual(new[] { "--prepare-loads" })) return PrepareLoads();
            if (args.Length != 0) throw new ArgumentException("Unsupported runner command");
            var input = JsonSerializer.Deserialize<BatchInput>(Console.In.ReadToEnd(), ReadOptions)
                ?? throw new ArgumentException("Пустой JSON");
            Validate(input);
            input.ConnectionContext?.Validate();
            var records = new List<object>();
            foreach (var candidate in input.Candidates)
            {
                var values = new List<object>();
                bool ulsAnyFailed = false;
                bool ulsAnyIndeterminate = false;
                // All ULS action tuples are checked before any serviceability work.
                // ULS is a normal SECTION check only; no member/shear acceptance.
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
                                      result.Utilization >= 0.0
                                      && double.IsFinite(result.Factor * result.Utilization)
                                      && Math.Abs(result.Factor * result.Utilization - 1.0) <= 0.01;
                            if (!ok)
                                ulsAnyIndeterminate = true;
                            else if (result.Utilization > 1.0)
                                ulsAnyFailed = true;
                            // ARBAT's radial N-M strength-domain factor corresponds to
                            // AllFactor in our benchmark. MomentFactor at fixed N
                            // is provided as a separate diagnostic measure.
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
                            ulsAnyIndeterminate = true;
                            values.Add(new { id = load.Id, converged = false,
                                factor = (double?)null, utilization = (double?)null,
                                error = "solver_error:" + ex.GetType().Name + ":" + ex.Message });
                        }
                    }
                }
                catch (Exception ex)
                {
                    ulsAnyIndeterminate = true;
                    foreach (var load in input.Loads)
                        values.Add(new { id = load.Id, converged = false,
                            factor = (double?)null, utilization = (double?)null,
                            error = "section_error:" + ex.GetType().Name + ":" + ex.Message });
                }
                // In the normal product workflow, SLS is NOT evaluated for
                // a section already known to fail ULS, or where ULS is unknown.
                // The research matrix explicitly opts into research_all to
                // preserve independent crack-benchmark cases (including dummy ULS).
                // Fail and indeterminate are different engineering diagnoses.
                string slsGate = input.SlsProbe is null ? "not_requested"
                    : input.SlsExecutionPolicy == "research_all" ? "calculated_research_override"
                    : ulsAnyIndeterminate ? "skipped_uls_indeterminate"
                    : ulsAnyFailed ? "skipped_uls_fail"
                    : "calculated_uls_pass";
                object[]? slsValues =
                    slsGate is "calculated_research_override" or "calculated_uls_pass"
                        ? RunCrackProbe(input, candidate) : null;
                object[]? shearValues = input.ShearScreening?.Enabled == true && input.ResearchRectangle is not null
                    ? RunShearScreening(input, candidate) : null;
                object[]? fullProfileValues = input.ShearProfile is not null
                    ? RunShearProfiles(input, candidate) : null;
                records.Add(new { id = candidate.Id, loads = values,
                    sls_gate = slsGate, sls_probe = slsValues, shear_screening = shearValues,
                    shear_profiles = fullProfileValues,
                    connection_report = input.ConnectionContext is null ? null
                        : ConnectionReport.Build(input,candidate,values,slsValues,slsGate) });
            }
            Console.Out.Write(JsonSerializer.Serialize(new
            {
                schema_version = 1,
                engine = "OpenCS.CScore.LimitForceSolver",
                engine_commit = "4ae6aa077be5b29145930ec093ba1c9eb7776222",
                engine_patches = new[] { "sp63_phi_n_amendment2_v1" },
                scope = input.ShearScreening?.Enabled == true
                    ? "uls_ndm_and_local_shear_research_screen_not_design_verified"
                    : "uls_ndm_section_only_without_eta_shear_cracks",
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

    private static int PrepareLoads()
    {
        var input = JsonSerializer.Deserialize<LoadPreparationInput>(Console.In.ReadToEnd(), ReadOptions)
            ?? throw new ArgumentException("Missing load preparation data");
        if (!double.IsFinite(input.DiameterMm) || input.DiameterMm <= 0
            || !double.IsFinite(input.LengthMm) || input.LengthMm <= 0
            || input.Loads.Count == 0 || input.Loads.Count > 1000)
            throw new ArgumentException("Invalid eccentricity geometry or loads");
        double ea = CScore.Sp63.Normal.Sp63MemberContext.AccidentalEccentricity(
            input.LengthMm / 1000.0, input.DiameterMm / 1000.0);
        var loads = new List<object>();
        var audit = new List<object>();
        foreach (var l in input.Loads)
        {
            if (!double.IsFinite(l.NKn) || !double.IsFinite(l.MxKnm) || !double.IsFinite(l.MyKnm))
                throw new ArgumentException("Nonfinite load");
            double mx = l.MxKnm, my = l.MyKnm;
            if (l.NKn < 0)
            {
                double magnitude = Math.Sqrt(mx * mx + my * my);
                var direction = magnitude > 1e-12 ? (mx / magnitude, my / magnitude)
                    : input.ZeroMomentAxis switch {
                        "mx_positive" => (1.0, 0.0), "mx_negative" => (-1.0, 0.0),
                        "my_positive" => (0.0, 1.0), "my_negative" => (0.0, -1.0),
                        _ => throw new ArgumentException("zero_moment_axis required") };
                double e = CScore.Sp63.Normal.Sp63MemberContext.EffectiveEccentricity(
                    magnitude / Math.Abs(l.NKn), ea,
                    CScore.Sp63.Normal.Sp63StructuralScheme.StaticallyDeterminate);
                mx = Math.Abs(l.NKn) * e * direction.Item1;
                my = Math.Abs(l.NKn) * e * direction.Item2;
            }
            if (!double.IsFinite(mx) || !double.IsFinite(my))
                throw new ArgumentException("Nonfinite prepared moments");
            var original = new { id = l.Id, n_kn = l.NKn, mx_knm = l.MxKnm, my_knm = l.MyKnm };
            var effective = new { id = l.Id, n_kn = l.NKn, mx_knm = mx, my_knm = my };
            loads.Add(effective);
            audit.Add(new { id = l.Id, original, effective,
                delta_mx_knm = mx - l.MxKnm, delta_my_knm = my - l.MyKnm,
                ea_mm = l.NKn < 0 ? ea * 1000.0 : 0.0 });
        }
        Console.Out.Write(JsonSerializer.Serialize(new { loads, audit, ea_mm = ea * 1000.0 }));
        return 0;
    }

    // A native infinite ratio is a known failure only when its capacity is exactly zero.
    private static bool IsZeroCapacityFailure(CScore.CheckDetail d) =>
        d.Allowable == 0 && double.IsFinite(d.Applied) && d.Applied > 0
        && double.IsPositiveInfinity(d.Ratio);

    // Local Q diagnostic, NOT a complete inclined-section verification.
    // ConstantProfile is an upstream-supported model for one isolated force tuple,
    // but it does not reconstruct reactions, applied distributed loads or bar cutoffs.
    // A positive result must NEVER be described as normative approval.
    private static object[] RunShearScreening(BatchInput input, Candidate candidate)
    {
        var options = input.ShearScreening!;
        return input.Loads.Select(load =>
        {
            try
            {
                if (input.ResearchRectangle is null)
                    throw new InvalidOperationException("circular_shear_requires_independent_method");
                if (load.QKn is not double q || !double.IsFinite(q))
                    throw new InvalidOperationException("missing_or_invalid_Q");
                var section = MakeSection(input, candidate);
                var geometry = InclinedSectionGeometryPair.Resolve(section, ShearPlane.Vy, CalcType.C);
                var side = geometry.For(load.MxKnm);
                if (!(side.B > 0 && side.H0 > 0))
                    throw new InvalidOperationException("invalid_automatic_shear_geometry");
                // Active branches per *physical* cage; never infer from longitudinal bars.
                double effectiveLegs = options.ConstructiveConfirmed
                    ? options.EffectiveLegsPerCage * options.CageCount : 0.0;
                double areaM2 = effectiveLegs * Math.PI
                    * Math.Pow(options.DiameterMm / 1000.0, 2) / 4.0;
                double qsw = options.RswKpa * areaM2 / (options.SpacingMm / 1000.0);
                var parameters = new ShearInclinedInput(
                    B: side.B, H0: side.H0, Rb: side.Rb, Rbt: side.Rbt,
                    Qsw: qsw, Sw: options.SpacingMm / 1000.0,
                    Ns: side.Ns, Kind: ElementKind.Other,
                    AnchorageFactor: 1.0, StationStep: 0.0, ProjectionStep: 0.0,
                    MomentZoneLength: 0.0, BarCutoffs: Array.Empty<double>(),
                    CheckMoment: false, PhiNOverride: null);
                var profile = new ConstantProfile(
                    q: q, m: load.MxKnm, n: load.NKn, supportDistance: 0.0);
                var outcome = ShearInclinedChecker.Check(parameters, profile, geometry, 0);
                var exact = outcome.Details.Where(x => x.Formula is "8.55" or "8.56").ToList();
                var zero = exact.FirstOrDefault(IsZeroCapacityFailure);
                if (zero is not null)
                    return (object)new { id = load.Id, status = "research_screen_fail",
                        utilization = (double?)null, reason = "native_zero_capacity",
                        governing_formula = zero.Formula, applied = zero.Applied, allowable = zero.Allowable,
                        verified_for_design = false };
                if (exact.Count != 2 || exact.Any(x => !double.IsFinite(x.Ratio) || x.Ratio < 0))
                    throw new InvalidOperationException("incomplete_or_nonfinite_shear_checks");
                double util = exact.Max(x => x.Ratio);
                var worst = exact.OrderByDescending(x => x.Ratio).First();
                var station = outcome.Stations.FirstOrDefault();
                return (object)new {
                    id = load.Id,
                    status = util <= 1.0 ? "research_screen_pass" : "research_screen_fail",
                    utilization = (double?)util,
                    governing_formula = worst.Formula,
                    q_kn = (double?)q,
                    qb_kn = station is not null && double.IsFinite(station.Qb)
                        ? (double?)station.Qb : null,
                    qsw_kn = station is not null && double.IsFinite(station.Qsw)
                        ? (double?)station.Qsw : null,
                    critical_c_m = station is not null && double.IsFinite(station.CriticalC)
                        ? (double?)station.CriticalC : null,
                    active_qsw_kn_per_m = qsw,
                    b_m = side.B, h0_m = side.H0,
                    rb_kpa = side.Rb, rbt_kpa = side.Rbt,
                    phi_n = station is not null && double.IsFinite(station.PhiN)
                        ? (double?)station.PhiN : null,
                    support_model = "constant_profile_no_support",
                    note = options.ConstructiveConfirmed
                        ? "Unverified link geometry and local Q profile; no inclined moment check"
                        : "Constructive 10.3 not confirmed; stirrup contribution ignored",
                    verified_for_design = false
                };
            }
            catch (Exception ex)
            {
                return (object)new {
                    id = load.Id, status = "indeterminate",
                    utilization = (double?)null,
                    error = "shear_screen_error:" + ex.GetType().Name + ":" + ex.Message,
                    verified_for_design = false
                };
            }
        }).ToArray();
    }

    // Direct upstream profile check. Pinned CScore accepts one constant qsw;
    // variable physical pitch is unsupported, never approximated by this adapter.
    private static object[] RunShearProfiles(BatchInput input, Candidate candidate)
    {
        var batch = input.ShearProfile!;
        var transverse = input.ShearScreening!;
        return batch.Profiles.Select(phase =>
        {
            try
            {
                var section = MakeSection(input, candidate);
                var geometry = InclinedSectionGeometryPair.Resolve(section, ShearPlane.Vy, CalcType.C);
                var profile = new SampledProfile(
                    phase.Samples.Select(v => new ForceSample(v.SM, v.QKn, v.MKnm, v.NKn)).ToArray(),
                    supportDistanceFromStart: 0.0, supportDistanceFromEnd: 0.0,
                    supportAtStart: false, supportAtEnd: false);
                if (profile.Warnings.Count != 0)
                    throw new InvalidOperationException("upstream_profile_M_Q_mismatch");
                var pitches = batch.PitchBands!.Select(b => b.SpacingMm).Distinct().ToArray();
                if (pitches.Length != 1 || pitches[0] != transverse.SpacingMm)
                    throw new InvalidOperationException("CScore_requires_uniform_pitch");
                double area = transverse.ConstructiveConfirmed
                    ? transverse.EffectiveLegsPerCage * transverse.CageCount
                        * Math.PI * Math.Pow(transverse.DiameterMm / 1000.0, 2) / 4.0 : 0.0;
                double qsw = transverse.RswKpa * area / (transverse.SpacingMm / 1000.0);
                var reference = geometry.For(phase.Samples[0].MKnm);
                var parameters = new ShearInclinedInput(
                    B: reference.B, H0: reference.H0, Rb: reference.Rb, Rbt: reference.Rbt,
                    Qsw: qsw, Sw: transverse.SpacingMm / 1000.0, Ns: reference.Ns,
                    Kind: ElementKind.Other, AnchorageFactor: 1.0,
                    StationStep: 0.0, ProjectionStep: 0.0, MomentZoneLength: 0.0,
                    BarCutoffs: Array.Empty<double>(), CheckMoment: false, PhiNOverride: null);
                var runs = new[] { -1, 1 }.Select(direction => (
                    Direction: direction,
                    Outcome: ShearInclinedChecker.Check(parameters, profile, geometry, direction))).ToArray();
                var zero = runs.SelectMany(r => r.Outcome.Details)
                    .Where(d => d.Formula is "8.55" or "8.56").FirstOrDefault(IsZeroCapacityFailure);
                if (zero is not null)
                    return (object)new { id = phase.Id, status = "research_profile_exceeds_one",
                        utilization = (double?)null, reason = "native_zero_capacity",
                        governing_formula = zero.Formula, applied = zero.Applied, allowable = zero.Allowable,
                        support_model = "none", reinforcement_model = "cscore_native_uniform_pitch",
                        uniform_pitch_mm = transverse.SpacingMm, verified_for_design = false };
                foreach (var run in runs)
                {
                    var checks = run.Outcome.Details.Where(d => d.Formula is "8.55" or "8.56").ToArray();
                    if (checks.Length != 2 || checks.Any(d => !double.IsFinite(d.Ratio) || d.Ratio < 0))
                        throw new InvalidOperationException("incomplete_upstream_shear_result");
                }
                var worst = runs.SelectMany(run => run.Outcome.Details
                    .Where(d => d.Formula is "8.55" or "8.56")
                    .Select(d => (Detail: d, run.Direction))).OrderByDescending(x => x.Detail.Ratio).First();
                var critical = runs.SelectMany(run => run.Outcome.Details.Where(d => d.Formula == "8.56")
                    .Select(d => (Detail: d, run.Direction))).OrderByDescending(x => x.Detail.Ratio).First();
                double eta = worst.Detail.Ratio;
                return (object)new {
                    id = phase.Id,
                    status = eta <= 1 ? "research_profile_below_one" : "research_profile_exceeds_one",
                    utilization = (double?)eta, governing_formula = worst.Detail.Formula,
                    governing_s_m = worst.Detail.Variables["s"],
                    critical_shear_s_m = critical.Detail.Variables["s"],
                    critical_shear_c_m = critical.Detail.Variables["C"],
                    critical_shear_direction = critical.Direction,
                    critical_shear_eta = critical.Detail.Ratio,
                    sampled_stations = runs.Sum(r => r.Outcome.Stations.Count),
                    max_abs_q_kn = profile.MaxAbsQ(profile.StationRange.Min, profile.StationRange.Max),
                    qsw_kn_per_m = qsw, uniform_pitch_mm = transverse.SpacingMm,
                    warnings = runs.SelectMany(r => r.Outcome.Warnings).Distinct().ToArray(),
                    support_model = "none", reinforcement_model = "cscore_native_uniform_pitch",
                    inclined_moment_status = "not_evaluated",
                    note = "Native CScore constant-pitch profile; supports, anchorage and inclined moment not verified.",
                    verified_for_design = false
                };
            }
            catch (Exception ex)
            {
                return (object)new { id = phase.Id, status = "indeterminate", utilization = (double?)null,
                    error = "profile_shear_error:" + ex.GetType().Name + ":" + ex.Message,
                    support_model = "none", verified_for_design = false };
            }
        }).ToArray();
    }

    // Research probe: explicit service properties and separate total / sustained bending
    // moments. CScore.CrackWidthSolver accepts only ONE axial N, so the probe uses
    // the same axial action for sustained and total loads. No ULS verdict is affected.
    internal static object[] RunCrackProbe(BatchInput input, Candidate candidate, IReadOnlyList<SlsLoad>? loadsOverride = null)
    {
        var probe = input.SlsProbe!;
        try
        {
            var section = MakeSection(input, candidate);
            return (loadsOverride ?? probe.Loads).Select(load =>
            {
                try
                {
                    var solver = new CrackWidthSolver(
                        section,
                        calcCrc: CalcType.N,
                        calcService: CalcType.N,
                        // SP63 6.1.26: crack opening uses the SHORT-TERM concrete
                        // constitutive diagram even for sustained actions.
                        // Upstream Example47 explicitly uses N for both parts.
                        calcServiceLong: CalcType.N,
                        phi2: probe.Phi2,
                        acrcUltLong: probe.AcrcUltLongMm,
                        acrcUltShort: probe.AcrcUltShortMm);
                    var r = solver.Compute(load.NKn,
                        load.MxLongKnm, load.MxTotalKnm,
                        load.MyLongKnm, load.MyTotalKnm);
                    var mLong = Math.Sqrt(load.MxLongKnm * load.MxLongKnm
                                          + load.MyLongKnm * load.MyLongKnm);
                    var mTotal = Math.Sqrt(load.MxTotalKnm * load.MxTotalKnm
                                           + load.MyTotalKnm * load.MyTotalKnm);
                    bool finite = double.IsFinite(r.Mcrc) && r.Mcrc >= 0
                        && double.IsFinite(r.AcrcLong) && r.AcrcLong >= 0
                        && double.IsFinite(r.AcrcShort) && r.AcrcShort >= 0
                        && double.IsFinite(r.UtilLong) && r.UtilLong >= 0
                        && double.IsFinite(r.UtilShort) && r.UtilShort >= 0;
                    // Not cracked can be a valid outcome ONLY with a successful
                    // crack threshold search and a sustained moment below Mcrc.
                    // Otherwise the upstream solver may have returned its
                    // misleading zero-valued fallback object.
                    // A short-term TOTAL force above Mcrc cannot be
                    // categorized uncracked solely because LONG part is below it.
                    // Upstream's early zero return is ambiguous in that case.
                    bool plausibleNoCrack = !r.Cracked
                        && mLong <= r.Mcrc + 1e-9
                        && mTotal <= r.Mcrc + 1e-9;
                    bool validated = r.CrcConverged && finite
                        && (r.Cracked ? r.PlaneLong.HasValue : plausibleNoCrack);
                    string? error = validated ? null : "indeterminate_native_CScore_crack_result";
                    double? primaryWidth = validated ? r.AcrcLong : null;
                    double? primaryShortWidth = validated ? r.AcrcShort : null;
                    string primaryBasis = "native_refined_postcrack";
                    double? primaryUtilization = primaryWidth.HasValue
                        ? r.UtilLong : null;
                    double? primaryShortUtilization = primaryShortWidth.HasValue
                        ? r.UtilShort : null;
                    double? governingUtilization = primaryUtilization.HasValue
                        && primaryShortUtilization.HasValue
                            ? Math.Max(primaryUtilization.Value, primaryShortUtilization.Value)
                            : null;
                    double reviewBand = probe.ReviewBand;
                    string preliminaryDecision = governingUtilization is null
                        ? "indeterminate"
                        : Math.Abs(governingUtilization.Value - 1.0) <= reviewBand
                            ? "engineering_review"
                            : governingUtilization.Value > 1.0
                                ? "preliminary_exceeds_limit"
                                : "preliminary_below_limit";
                    bool preliminaryCalculated = validated && governingUtilization.HasValue;
                    if (!preliminaryCalculated && error is null)
                        error = "preliminary_long_or_short_crack_screen_not_available";
                    return (object)new {
                        id = load.Id,
                        status = preliminaryCalculated ? "research_calculated" : "indeterminate",
                        error,
                        preliminary_method = probe.PreliminaryMethod,
                        preliminary_basis = primaryBasis,
                        crack_solver_path = !validated && !r.Cracked
                            ? "upstream_default_zero_return" : "upstream_default",
                        upstream_post_threshold_zero = !r.Cracked && r.CrcConverged
                            && double.IsFinite(r.Mcrc) && mLong > r.Mcrc,
                        preliminary_width_long_mm = primaryWidth,
                        preliminary_utilization_long = primaryUtilization,
                        preliminary_width_short_mm = primaryShortWidth,
                        preliminary_utilization_short = primaryShortUtilization,
                        preliminary_governing_utilization = governingUtilization,
                        preliminary_decision = preliminaryDecision,
                        preliminary_review_band = reviewBand,
                        preliminary_is_final_approval = false,
                        cracked = (bool?)r.Cracked,
                        crc_converged = r.CrcConverged,
                        mcrc_knm = finite ? (double?)r.Mcrc : null,
                        // Audit diagnostics of SP63 8.2.15 and 8.2.18.
                        // These do not themselves certify a correct crack result.
                        sigma_rebar_kpa = validated && double.IsFinite(r.SigmaS) ? (double?)r.SigmaS : null,
                        sigma_rebar_at_crack_kpa = validated && double.IsFinite(r.SigmaSCrc) ? (double?)r.SigmaSCrc : null,
                        psi_s = validated && double.IsFinite(r.PsiS) ? (double?)r.PsiS : null,
                        psi_s2 = validated && double.IsFinite(r.PsiS2) ? (double?)r.PsiS2 : null,
                        crack_spacing_m = validated && double.IsFinite(r.Ls) ? (double?)r.Ls : null,
                        tensile_rebar_area_m2 = validated && double.IsFinite(r.AsTens) ? (double?)r.AsTens : null,
                        equivalent_rebar_diameter_m = validated && double.IsFinite(r.DsEq) ? (double?)r.DsEq : null,
                        tensile_concrete_area_m2 = validated && double.IsFinite(r.Abt) ? (double?)r.Abt : null,
                        effective_height_m = validated && double.IsFinite(r.H0) ? (double?)r.H0 : null,
                        phi1_long_times_phi2 = 1.4 * probe.Phi2,
                        phi2 = probe.Phi2,
                        a_crc_1_mm = validated && double.IsFinite(r.Acrc1) ? (double?)r.Acrc1 : null,
                        a_crc_2_mm = validated && double.IsFinite(r.Acrc2) ? (double?)r.Acrc2 : null,
                        a_crc_3_mm = validated && double.IsFinite(r.Acrc3) ? (double?)r.Acrc3 : null,
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
            return (loadsOverride ?? probe.Loads).Select(load => (object)new {
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

    internal static CrossSection MakeSection(BatchInput input, Candidate candidate)
    {
        // Explicit rectangular section is a research-only route. The ordinary
        // Python pile selector still sends only circular pile geometries.
        var rectangle = input.ResearchRectangle;
        int mesh = rectangle?.MeshResolution ?? input.Pile.MeshResolution;
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
            if (!double.IsFinite(m.ConcreteEc0) || m.ConcreteEc0>=0
                || !double.IsFinite(m.ConcreteEt0) || m.ConcreteEt0<=0)
                throw new ArgumentException("Invalid physical concrete reference strains");
            return new MaterialChars(mode)
            {
                Type = MatType.Concrete,
                Fc = -m.ConcreteRbKpa,
                Ft = m.ConcreteRbtKpa,
                E = m.ConcreteEKpa,
                // Physical reference strains for phiN are distinct from L2 diagram points.
                Ec0 = m.ConcreteEc0,
                Et0 = m.ConcreteEt0,
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
        if (rectangle is not null)
        {
            double hx = rectangle.WidthMm / 2000.0;
            double hy = rectangle.HeightMm / 2000.0;
            xs.AddRange([-hx, hx, hx, -hx, -hx]);
            ys.AddRange([-hy, -hy, hy, hy, -hy]);
        }
        else
        {
            double radius = input.Pile.DiameterMm / 2000.0;
            for (int i = 0; i < input.Pile.PolygonSides; i++)
            {
                double angle = 2.0 * Math.PI * i / input.Pile.PolygonSides;
                xs.Add(radius * Math.Cos(angle));
                ys.Add(radius * Math.Sin(angle));
            }
            xs.Add(xs[0]);
            ys.Add(ys[0]);
        }
        var contour = new Contour(xs, ys,
            rectangle is null ? "pile-concrete-contour" : "rectangular-research-contour")
            { Type = ContourType.Hull };
        var region = new MaterialArea
        {
            Id = 1, Tag = "concrete", Category = AreaCategory.Region,
            Material = concrete, MaterialId = concrete.Id, DiagrammType = DiagrammType.L2,
            Hull = contour, Contours = [contour], NX = mesh, NY = mesh
        };
        region.ResolveAndBuildDiagramms();
        region.SliceXY(mesh, mesh);

        Fiber[] bars;
        if (rectangle is not null)
        {
            bars = rectangle.Bars.Select(b => Fiber.CreatePoint(
                b.DiameterMm / 1000.0, b.XMm / 1000.0, b.YMm / 1000.0)).ToArray();
        }
        else
        {
            double rotationRadians = candidate.RotationDeg * Math.PI / 180.0;
            double barRadiusM = candidate.BarAxisRadiusMm / 1000.0;
            double barDiameterM = candidate.BarDiameterMm / 1000.0;
            bars = Enumerable.Range(0, candidate.BarCount)
                .Select(i =>
                {
                    double angle = rotationRadians + 2.0 * Math.PI * i / candidate.BarCount;
                    return Fiber.CreatePoint(barDiameterM,
                        barRadiusM * Math.Cos(angle), barRadiusM * Math.Sin(angle));
                }).ToArray();
        }
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
        bool rectMode = input.ResearchRectangle is not null;
        if (!rectMode && (input.Pile.DiameterMm <= 0 || input.Pile.MeshResolution < 10 ||
                          input.Pile.PolygonSides < 32))
            throw new ArgumentException("Некорректная геометрия сваи");
        if (input.ResearchRectangle is { } rect)
        {
            if (!double.IsFinite(rect.WidthMm) || !double.IsFinite(rect.HeightMm)
                || rect.WidthMm <= 0 || rect.HeightMm <= 0
                || rect.MeshResolution < 10 || rect.MeshResolution > 200
                || rect.Bars is null || rect.Bars.Count < 2 || input.Candidates.Count != 1
                || string.IsNullOrWhiteSpace(input.Candidates[0].Id))
                throw new ArgumentException("Invalid rectangular research geometry");
            if (rect.Bars.Any(b => !double.IsFinite(b.XMm) || !double.IsFinite(b.YMm)
                    || !double.IsFinite(b.DiameterMm) || b.DiameterMm <= 0
                    || Math.Abs(b.XMm) + b.DiameterMm / 2 >= rect.WidthMm / 2
                    || Math.Abs(b.YMm) + b.DiameterMm / 2 >= rect.HeightMm / 2))
                throw new ArgumentException("Rebar outside rectangular concrete face");
            for (int i = 0; i < rect.Bars.Count; i++)
                for (int j = i + 1; j < rect.Bars.Count; j++)
                    if (Math.Sqrt(Math.Pow(rect.Bars[i].XMm - rect.Bars[j].XMm, 2)
                                 + Math.Pow(rect.Bars[i].YMm - rect.Bars[j].YMm, 2))
                        <= (rect.Bars[i].DiameterMm + rect.Bars[j].DiameterMm) / 2)
                        throw new ArgumentException("Overlapping rectangular reinforcement bars");
        }
        if (input.ShearScreening is { Enabled: true } shear)
        {
            if ((!rectMode && input.ConnectionContext is null) || !double.IsFinite(shear.DiameterMm) || shear.DiameterMm <= 0
                || !double.IsFinite(shear.SpacingMm) || shear.SpacingMm <= 0
                || !double.IsFinite(shear.RswKpa) || shear.RswKpa <= 0 || shear.RswKpa > 300_000
                || shear.EffectiveLegsPerCage < 1 || shear.EffectiveLegsPerCage > 30
                || shear.CageCount < 1 || shear.CageCount > 3
                || input.Loads.Any(l => l.QKn is not double q || !double.IsFinite(q)))
                throw new ArgumentException("Invalid shear screening geometry, steel or Q");
            if (!rectMode && (shear.CageCount!=1 || shear.EffectiveLegsPerCage!=2))
                throw new ArgumentException("Circular shear uses one spiral, not rectangular cage branches");
        }
        if (input.ShearProfile is { } profiles)
        {
            if (!rectMode || input.ShearScreening is not { Enabled: true }
                || profiles.Scope != "full_phase_profile_research_constant_background_only"
                || !profiles.AxisConfirmed
                || profiles.Orientation is not ("same" or "opposite")
                || profiles.SupportModel != "none"
                || profiles.Profiles is null or { Count: 0 or > 200 }
                || profiles.Profiles.Select(p => p.Id).Distinct().Count() != profiles.Profiles.Count
                || profiles.Profiles.Any(p => string.IsNullOrWhiteSpace(p.Id)
                    || p.Samples is null or { Count: < 2 or > 200 }
                    || p.Samples.Any(s => !double.IsFinite(s.SM)
                        || !double.IsFinite(s.NKn) || !double.IsFinite(s.MKnm)
                        || !double.IsFinite(s.QKn))
                    || p.Samples.Zip(p.Samples.Skip(1))
                        .Any(v => v.Second.SM - v.First.SM <= 1e-9))
                || profiles.PitchBands is null or { Count: 0 or > 201 }
                || profiles.PitchBands.Any(b => !double.IsFinite(b.StartSM)
                    || !double.IsFinite(b.EndSM) || !double.IsFinite(b.SpacingMm)
                    || b.EndSM - b.StartSM <= 1e-9
                    || b.SpacingMm <= 0.0 || b.SpacingMm > 2000.0)
                || Math.Abs(profiles.PitchBands[0].StartSM
                    - profiles.Profiles[0].Samples[0].SM)>1e-8
                || Math.Abs(profiles.PitchBands[^1].EndSM
                    - profiles.Profiles[0].Samples[^1].SM)>1e-8
                || profiles.PitchBands.Zip(profiles.PitchBands.Skip(1))
                    .Any(v => Math.Abs(v.First.EndSM-v.Second.StartSM)>1e-8)
                || profiles.Profiles.Any(p =>
                    Math.Abs(p.Samples[0].SM-profiles.PitchBands[0].StartSM)>1e-8
                    || Math.Abs(p.Samples[^1].SM-profiles.PitchBands[^1].EndSM)>1e-8))
                throw new ArgumentException("Invalid or unconfirmed shear phase profile");
        }
        if (input.SlsExecutionPolicy is not ("uls_pass_only" or "research_all"))
            throw new ArgumentException("Unsupported SLS execution policy");
        if (input.SlsProbe is { } probe)
        {
            if (probe.ShortMaterials is null || probe.LongMaterials is null
                || probe.Loads is null || probe.Loads.Count == 0
                || !double.IsFinite(probe.Phi2) || probe.Phi2 <= 0
                || !double.IsFinite(probe.AcrcUltLongMm) || probe.AcrcUltLongMm <= 0
                || !double.IsFinite(probe.AcrcUltShortMm) || probe.AcrcUltShortMm <= 0
                || probe.PreliminaryMethod != "native_refined"
                || !double.IsFinite(probe.ReviewBand) || probe.ReviewBand <= 0
                || probe.ReviewBand >= 0.25)
                throw new ArgumentException("Explicit service characteristics, loads and limits required");
            ValidateMaterials(probe.ShortMaterials);
            ValidateMaterials(probe.LongMaterials);
            if (probe.Loads.Select(x => x.Id).Distinct().Count() != probe.Loads.Count
                || probe.Loads.Any(x => string.IsNullOrWhiteSpace(x.Id)
                    || new[] {x.NKn, x.MxTotalKnm, x.MyTotalKnm, x.MxLongKnm, x.MyLongKnm}
                        .Any(v => !double.IsFinite(v))))
                throw new ArgumentException("Nonfinite or duplicate SLS force tuples");
        }
        if (!rectMode)
        {
            foreach (var candidate in input.Candidates)
            {
                if (candidate.BarCount < 6 || candidate.BarDiameterMm <= 0 ||
                    candidate.BarAxisRadiusMm <= 0 ||
                    candidate.BarAxisRadiusMm + candidate.BarDiameterMm / 2 >= input.Pile.DiameterMm / 2)
                    throw new ArgumentException("Недопустимый кандидат " + candidate.Id);
            }
        }
    }
}

internal sealed class BatchInput
{
    [JsonPropertyName("connection_context")] public ConnectionContext? ConnectionContext { get; init; }
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    [JsonPropertyName("pile")] public Pile Pile { get; init; } = new();
    [JsonPropertyName("materials")] public Materials Materials { get; init; } = new();
    [JsonPropertyName("loads")] public List<LoadCase> Loads { get; init; } = [];
    [JsonPropertyName("candidates")] public List<Candidate> Candidates { get; init; } = [];
    [JsonPropertyName("sls_probe")] public SlsProbe? SlsProbe { get; init; }
    // Default is safe even when a direct consumer does not specify a policy.
    [JsonPropertyName("sls_execution_policy")] public string SlsExecutionPolicy { get; init; } = "uls_pass_only";
    [JsonPropertyName("research_rectangle")] public ResearchRectangle? ResearchRectangle { get; init; }
    [JsonPropertyName("shear_screening")] public ShearScreening? ShearScreening { get; init; }
    [JsonPropertyName("shear_profile")] public ShearProfileBatch? ShearProfile { get; init; }
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
    // SP63 6.1.14, short-term heavy concrete; explicit overrides remain supported.
    [JsonPropertyName("concrete_ec0")] public double ConcreteEc0 { get; init; } = -.002;
    [JsonPropertyName("concrete_et0")] public double ConcreteEt0 { get; init; } = .0001;
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
    [JsonPropertyName("q_kn")] public double? QKn { get; init; }
}


internal sealed class ShearProfileBatch
{
    [JsonPropertyName("scope")] public string Scope { get; init; } = "";
    [JsonPropertyName("orientation")] public string Orientation { get; init; } = "";
    [JsonPropertyName("axis_confirmed")] public bool AxisConfirmed { get; init; }
    [JsonPropertyName("support_model")] public string SupportModel { get; init; } = "";
    [JsonPropertyName("profiles")] public List<ShearPhase> Profiles { get; init; } = [];
    [JsonPropertyName("pitch_bands")] public List<ShearPitchBand>? PitchBands { get; init; }
}
internal sealed class ShearPitchBand
{
    [JsonRequired, JsonPropertyName("start_s_m")] public double StartSM { get; init; }
    [JsonRequired, JsonPropertyName("end_s_m")] public double EndSM { get; init; }
    [JsonRequired, JsonPropertyName("spacing_mm")] public double SpacingMm { get; init; }
}
internal sealed class ShearPhase
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("samples")] public List<ShearSample> Samples { get; init; } = [];
}
internal sealed class ShearSample
{
    // Reject omitted numbers instead of silently accepting the CLR default 0.
    [JsonRequired, JsonPropertyName("s_m")] public double SM { get; init; }
    [JsonRequired, JsonPropertyName("n_kn")] public double NKn { get; init; }
    [JsonRequired, JsonPropertyName("m_knm")] public double MKnm { get; init; }
    [JsonRequired, JsonPropertyName("q_kn")] public double QKn { get; init; }
}

internal sealed class ShearScreening
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("diameter_mm")] public double DiameterMm { get; init; }
    [JsonPropertyName("spacing_mm")] public double SpacingMm { get; init; }
    [JsonPropertyName("effective_legs_per_cage")] public int EffectiveLegsPerCage { get; init; }
    [JsonPropertyName("cage_count")] public int CageCount { get; init; }
    [JsonPropertyName("rsw_kpa")] public double RswKpa { get; init; }
    [JsonPropertyName("constructive_confirmed")] public bool ConstructiveConfirmed { get; init; }
}

internal sealed class ResearchRectangle
{
    [JsonPropertyName("width_mm")] public double WidthMm { get; init; }
    [JsonPropertyName("height_mm")] public double HeightMm { get; init; }
    [JsonPropertyName("mesh_resolution")] public int MeshResolution { get; init; }
    [JsonPropertyName("bars")] public List<ResearchBar> Bars { get; init; } = [];
}
internal sealed class ResearchBar
{
    [JsonPropertyName("x_mm")] public double XMm { get; init; }
    [JsonPropertyName("y_mm")] public double YMm { get; init; }
    [JsonPropertyName("diameter_mm")] public double DiameterMm { get; init; }
}

internal sealed class SlsProbe
{
    [JsonPropertyName("short_materials")] public Materials? ShortMaterials { get; init; }
    [JsonPropertyName("long_materials")] public Materials? LongMaterials { get; init; }
    [JsonPropertyName("loads")] public List<SlsLoad> Loads { get; init; } = [];
    [JsonPropertyName("phi2")] public double Phi2 { get; init; } = 0.5;
    [JsonPropertyName("acrc_ult_long_mm")] public double AcrcUltLongMm { get; init; } = 0.3;
    [JsonPropertyName("acrc_ult_short_mm")] public double AcrcUltShortMm { get; init; } = 0.4;
    [JsonPropertyName("preliminary_method")] public string PreliminaryMethod { get; init; } = "native_refined";
    [JsonPropertyName("review_band")] public double ReviewBand { get; init; } = 0.03;
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

internal sealed class LoadPreparationInput
{
    [JsonPropertyName("loads")] public List<LoadCase> Loads { get; init; } = new();
    [JsonPropertyName("diameter_mm")] public double DiameterMm { get; init; }
    [JsonPropertyName("length_mm")] public double LengthMm { get; init; }
    [JsonPropertyName("zero_moment_axis")] public string? ZeroMomentAxis { get; init; }
}
