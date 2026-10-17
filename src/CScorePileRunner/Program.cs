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
                object[]? shearValues = input.ShearScreening?.Enabled == true
                    ? RunShearScreening(input, candidate) : null;
                object[]? fullProfileValues = input.ShearProfile is not null
                    ? RunShearProfiles(input, candidate) : null;
                records.Add(new { id = candidate.Id, loads = values,
                    sls_gate = slsGate, sls_probe = slsValues, shear_screening = shearValues,
                    shear_profiles = fullProfileValues });
            }
            Console.Out.Write(JsonSerializer.Serialize(new
            {
                schema_version = 1,
                engine = "OpenCS.CScore.LimitForceSolver",
                engine_commit = "4ae6aa077be5b29145930ec093ba1c9eb7776222",
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

    // Exploratory whole-phase response for a SINGLE CONSTANT reinforcement layout.
    // This is NOT a full check of a wall with zones, real supports or anchorage.
    // Input Q follows an explicitly confirmed M'=Q convention, never guessed.
    private static object[] RunShearProfiles(BatchInput input, Candidate candidate)
    {
        var batch = input.ShearProfile!;
        var transverse = input.ShearScreening!;
        return batch.Profiles.Select(phase =>
        {
            try
            {
                if (input.ResearchRectangle is null)
                    throw new InvalidOperationException("not_rectangular");
                var section = MakeSection(input, candidate);
                var geometry = InclinedSectionGeometryPair.Resolve(section, ShearPlane.Vy, CalcType.C);
                double areaM2 = transverse.ConstructiveConfirmed
                    ? transverse.EffectiveLegsPerCage * transverse.CageCount
                        * Math.PI * Math.Pow(transverse.DiameterMm / 1000.0, 2) / 4.0
                    : 0.0;
                double qsw = transverse.RswKpa * areaM2 / (transverse.SpacingMm / 1000.0);
                var samples = phase.Samples.Select(v =>
                    new ForceSample(v.SM, v.QKn, v.MKnm, v.NKn)).ToArray();
                var profile = new SampledProfile(
                    samples, supportDistanceFromStart: 0.0,
                    supportDistanceFromEnd: 0.0,
                    supportAtStart: false, supportAtEnd: false);
                // Upstream silently switches to LINEAR M on segments that fail its
                // stricter 1% derivative consistency check. Do not silently accept.
                if (profile.Warnings.Count != 0)
                    throw new InvalidOperationException("upstream_profile_M_Q_mismatch");
                var reference = geometry.For(samples[0].M);
                var shearInput = new ShearInclinedInput(
                    B: reference.B, H0: reference.H0,
                    Rb: reference.Rb, Rbt: reference.Rbt, Qsw: qsw,
                    Sw: transverse.SpacingMm / 1000.0, Ns: reference.Ns,
                    Kind: ElementKind.Other, AnchorageFactor: 1.0,
                    StationStep: 0.0, ProjectionStep: 0.0,
                    MomentZoneLength: 0.0, BarCutoffs: Array.Empty<double>(),
                    CheckMoment: false, PhiNOverride: null);
                var outcome = ShearInclinedChecker.Check(
                    shearInput, profile, geometry, direction: 0);
                var details = outcome.Details
                    .Where(x => x.Formula is "8.55" or "8.56").ToList();
                if (details.Count != 2 ||
                    details.Any(x => !double.IsFinite(x.Ratio) || x.Ratio < 0.0))
                    throw new InvalidOperationException("incomplete_phase_shear_check");
                double eta = details.Max(x => x.Ratio);
                var worst = details.OrderByDescending(x => x.Ratio).First();
                var worstStation = worst.Variables.TryGetValue("s", out var s)
                    && double.IsFinite(s) ? (double?)s : null;
                return (object)new
                {
                    id = phase.Id,
                    status = eta <= 1.0 ? "research_profile_below_one"
                        : "research_profile_exceeds_one",
                    utilization = (double?)eta,
                    governing_formula = worst.Formula,
                    governing_s_m = worstStation,
                    sampled_stations = outcome.Stations.Count,
                    max_abs_q_kn = profile.MaxAbsQ(
                        profile.StationRange.Min, profile.StationRange.Max),
                    qsw_kn_per_m = qsw,
                    support_model = "none",
                    reinforcement_model = "constant_background_only",
                    note = "No verified supports, zone changes, bar cutoffs, anchorage or inclined moment",
                    verified_for_design = false
                };
            }
            catch (Exception ex)
            {
                return (object)new
                {
                    id = phase.Id, status = "indeterminate",
                    utilization = (double?)null,
                    error = "profile_shear_error:" + ex.GetType().Name + ":" + ex.Message,
                    support_model = "none",
                    verified_for_design = false
                };
            }
        }).ToArray();
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
                    // Supplement ONLY the upstream early-return case where
                    // the sustained moment is under Mcrc but the total moment
                    // crosses Mcrc. The upstream Compute() returns zero before
                    // calculating the total force. Repeat it with FULL action
                    // as both arguments and take its Acrc2 transient term.
                    // This is an independent research reconstruction, not a
                    // rewrite or verification of the author's solver.
                    if (!r.Cracked && r.CrcConverged
                        && double.IsFinite(r.Mcrc) && r.Mcrc >= 0
                        && mLong <= r.Mcrc && mTotal > r.Mcrc + 1e-8)
                    {
                        // The cracking-face direction must stay the same.
                        // A rotating or reversed load would require separate
                        // cracking threshold computations for every direction.
                        double cross = load.MxLongKnm * load.MyTotalKnm
                                     - load.MyLongKnm * load.MxTotalKnm;
                        double dot = load.MxLongKnm * load.MxTotalKnm
                                   + load.MyLongKnm * load.MyTotalKnm;
                        if (mLong > 1e-9
                            && (dot <= 0 || Math.Abs(cross) > 1e-8 * mLong * mTotal))
                            throw new InvalidOperationException(
                                "short_only_requires_proportional_nonreversing_moments");

                        var full = solver.Compute(load.NKn,
                            load.MxTotalKnm, load.MxTotalKnm,
                            load.MyTotalKnm, load.MyTotalKnm);
                        bool validFull = full.Cracked && full.CrcConverged
                            && full.PlaneLong.HasValue
                            && double.IsFinite(full.Mcrc) && full.Mcrc >= 0
                            && Math.Abs(full.Mcrc - r.Mcrc) <= 0.05
                            && double.IsFinite(full.Acrc2) && full.Acrc2 > 0
                            && double.IsFinite(full.AcrcLong) && full.AcrcLong > 0
                            && double.IsFinite(full.AcrcShort) && full.AcrcShort > 0
                            && double.IsFinite(full.PsiS2) && full.PsiS2 > 0
                            && double.IsFinite(full.SigmaS) && full.SigmaS >= 0;
                        // For equal total and sustained forces, Acrc2 is the
                        // phi1=1.0 transient term, while AcrcLong has phi1=1.4.
                        bool consistentShortTerm = validFull
                            && Math.Abs(full.Acrc2 - full.AcrcLong / 1.4)
                                <= 1e-7 + 1e-6 * full.Acrc2;
                        if (!consistentShortTerm)
                            throw new InvalidOperationException(
                                "short_only_supplement_failed_convergence_or_psi_identity");

                        double nativeShortWidth = full.Acrc2;
                        double screeningShortWidth = probe.PreliminaryMethod == "native_refined"
                            ? nativeShortWidth
                            : Math.Max(nativeShortWidth, nativeShortWidth / full.PsiS2);
                        if (!double.IsFinite(screeningShortWidth)
                            || screeningShortWidth < 0)
                            throw new InvalidOperationException(
                                "short_only_invalid_conservative_width");

                        // Because ARBAT sometimes predicts long cracks even
                        // below this preliminary CScore Mcrc, we cannot
                        // classify this reconstructed case as a prelim PASS.
                        // Always route it to explicit engineering review.
                        double shortUse = screeningShortWidth / probe.AcrcUltShortMm;
                        string shortOnlyDecision = shortUse > 1.0 + probe.ReviewBand
                            ? "preliminary_exceeds_limit" : "engineering_review";
                        return (object)new
                        {
                            id = load.Id,
                            status = "research_calculated",
                            error = (string?)null,
                            preliminary_method = probe.PreliminaryMethod,
                            preliminary_basis = "short_only_supplemental_full_load_solver",
                            preliminary_review_reason = "cracked_only_under_total_load_unvalidated_mcrc_threshold",
                            preliminary_width_long_mm = (double?)0.0,
                            preliminary_utilization_long = (double?)0.0,
                            preliminary_width_short_mm = (double?)screeningShortWidth,
                            preliminary_utilization_short = (double?)shortUse,
                            preliminary_governing_utilization = (double?)shortUse,
                            preliminary_decision = shortOnlyDecision,
                            preliminary_review_band = probe.ReviewBand,
                            preliminary_is_final_approval = false,
                            cracked = (bool?)true,
                            cracked_long = false,
                            cracked_total = true,
                            crack_solver_path = "supplemental_full_load_Acrc2",
                            raw_long_branch_cracked = false,
                            crc_converged = full.CrcConverged,
                            mcrc_knm = (double?)full.Mcrc,
                            sigma_rebar_kpa = (double?)null,
                            sigma_rebar_total_kpa = (double?)full.SigmaS,
                            sigma_rebar_at_crack_kpa = (double?)null,
                            compatibility_hypothesis = "none_short_only",
                            compatibility_error = "no_sustained_cracks_to_reconstruct",
                            sigma_rebar_precrack_kpa = (double?)null,
                            psi_s_precrack_hypothesis = (double?)null,
                            width_long_precrack_hypothesis_mm = (double?)null,
                            utilization_long_precrack_hypothesis = (double?)null,
                            psi_s = (double?)null,
                            psi_s2 = (double?)full.PsiS2,
                            crack_spacing_m = double.IsFinite(full.Ls) ? (double?)full.Ls : null,
                            tensile_rebar_area_m2 = double.IsFinite(full.AsTens) ? (double?)full.AsTens : null,
                            equivalent_rebar_diameter_m = double.IsFinite(full.DsEq) ? (double?)full.DsEq : null,
                            tensile_concrete_area_m2 = double.IsFinite(full.Abt) ? (double?)full.Abt : null,
                            effective_height_m = double.IsFinite(full.H0) ? (double?)full.H0 : null,
                            phi1_long_times_phi2 = 1.4 * probe.Phi2,
                            phi2 = probe.Phi2,
                            a_crc_1_mm = (double?)0.0,
                            a_crc_2_mm = (double?)full.Acrc2,
                            a_crc_3_mm = (double?)0.0,
                            width_long_mm = (double?)0.0,
                            screening_psi_one_basis = "SP63_8.2.15_psi_s_one_transient_only",
                            width_long_psi_one_mm = (double?)0.0,
                            utilization_long_psi_one = (double?)0.0,
                            width_short_mm = (double?)nativeShortWidth,
                            utilization_long = (double?)0.0,
                            utilization_short = (double?)(nativeShortWidth / probe.AcrcUltShortMm),
                            verified_for_design = false
                        };
                    }

                    // An ARBAT-oriented RESEARCH contrast: pre-cracking rebar
                    // stress (historical OpenCS technique, superseded upstream).
                    // We keep upstream's post-crack algorithm entirely unchanged.
                    // For arbitrary SLS load splits this contrast is not valid:
                    // our reference dataset uses identical sustained/total forces.
                    bool sameMoments = Math.Abs(load.MxLongKnm - load.MxTotalKnm) < 1e-9
                        && Math.Abs(load.MyLongKnm - load.MyTotalKnm) < 1e-9;
                    double? sigmaPreKpa = null, psiPre = null, widthPreMm = null,
                        utilizationPre = null;
                    string? preError = sameMoments ? null : "sustained_total_moments_differ";
                    if (sameMoments && r.Cracked && r.CrcConverged && r.PlaneLong.HasValue
                        && double.IsFinite(r.PsiS) && r.PsiS > 1e-12
                        && double.IsFinite(r.SigmaS) && r.SigmaS > 0
                        && double.IsFinite(r.AcrcLong) && r.AcrcLong >= 0)
                    {
                        try
                        {
                            double mxDir = Math.Abs(load.MxLongKnm) > 1e-12
                                ? load.MxLongKnm : (Math.Abs(load.MxTotalKnm) > 1e-12
                                    ? load.MxTotalKnm : 1.0);
                            double myDir = Math.Abs(load.MxLongKnm) > 1e-12
                                || Math.Abs(load.MyLongKnm) > 1e-12
                                ? load.MyLongKnm : load.MyTotalKnm;
                            var cracking = new CrackingSolver(section, CalcType.N,
                                solverTol: 0.5,
                                tensionZone: CrackingSolver.LoadedTensionZone(
                                    section, load.NKn, mxDir, myDir,
                                    nAtZeroMoment: load.NKn));
                            var crc = cracking.CrackingMoment(load.NKn, mxDir, myDir);
                            double mag = Math.Sqrt(crc.Mx * crc.Mx + crc.My * crc.My);
                            if (!crc.Converged || !crc.StrainPlane.HasValue
                                || Math.Abs(mag - r.Mcrc) > 0.05)
                            {
                                preError = "precrack_threshold_not_reproduced";
                            }
                            else
                            {
                                double sigma = 0.0;
                                foreach (var (area, ka) in section.EnumerateAreas(crc.StrainPlane.Value))
                                {
                                    if (area.Material?.Type != MatType.ReSteelF)
                                        continue;
                                    var diagrams = area.Material.GetDiagramms(
                                        area.DiagrammType, 0.85, 0.05);
                                    if (diagrams is null
                                        || !diagrams.TryGetValue(CalcType.N, out var diag))
                                        continue;
                                    foreach (var bar in area.Fibers)
                                    {
                                        if (bar.TypeFiber != FiberType.point)
                                            continue;
                                        var eps = ka.e0 + ka.ky * bar.Y + ka.kz * bar.X;
                                        if (eps > 0)
                                        {
                                            var stress = diag.Sig(eps, out _);
                                            if (double.IsFinite(stress) && stress > sigma)
                                                sigma = stress;
                                        }
                                    }
                                }
                                double psi = Math.Clamp(1.0 - 0.8 * sigma / r.SigmaS,
                                    0.2, 1.0);
                                double trialWidth = r.AcrcLong * psi / r.PsiS;
                                if (!double.IsFinite(trialWidth) || trialWidth < 0)
                                    preError = "invalid_compatibility_width";
                                else
                                {
                                    sigmaPreKpa = sigma;
                                    psiPre = psi;
                                    widthPreMm = trialWidth;
                                    utilizationPre = trialWidth / probe.AcrcUltLongMm;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            preError = "precrack_study_error:" + ex.GetType().Name;
                        }
                    }
                    else if (sameMoments && !r.Cracked)
                    {
                        preError = "no_crack_in_upstream_result";
                    }
                    else if (sameMoments)
                    {
                        preError = "insufficient_upstream_sls_state";
                    }
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
                    // SECONDARY DIAGNOSTIC ONLY. CrackWidthSolver's internal
                    // SolveDamped is private and may silently return a zero
                    // result from multiple stages. For a zero return AFTER
                    // the cracking threshold, independently attempt a
                    // public StrainSolver equilibrium at the same service
                    // load and diagram. This must NOT replace or approve
                    // the upstream crack result, even if it converges.
                    bool? independentEquilibriumConverged = null;
                    int? independentEquilibriumIterations = null;
                    double? independentEquilibriumResidual = null;
                    string independentEquilibriumStatus = "not_necessary";
                    if (!r.Cracked && r.CrcConverged
                        && double.IsFinite(r.Mcrc) && r.Mcrc >= 0
                        && mLong > r.Mcrc + 1e-8)
                    {
                        independentEquilibriumStatus = "attempted";
                        try
                        {
                            var equilibrium = new StrainSolver(
                                section, CalcType.N, ten: false, ca: true,
                                tol: 0.5, maxIter: 80, centralJacobian: true);
                            var independentPlane = equilibrium.Solve(
                                load.NKn, load.MxLongKnm, load.MyLongKnm);
                            bool planeFinite = double.IsFinite(independentPlane.e0)
                                && double.IsFinite(independentPlane.ky)
                                && double.IsFinite(independentPlane.kz);
                            bool resultValid = planeFinite
                                && double.IsFinite(equilibrium.Residual)
                                && equilibrium.Residual >= 0;
                            independentEquilibriumConverged =
                                resultValid && equilibrium.Converged;
                            independentEquilibriumIterations =
                                equilibrium.Iterations;
                            independentEquilibriumResidual = resultValid
                                ? equilibrium.Residual : null;
                            independentEquilibriumStatus =
                                !resultValid ? "invalid_independent_solution"
                                : equilibrium.Converged
                                    ? "independent_equilibrium_converged"
                                    : "independent_equilibrium_not_converged";
                        }
                        catch (Exception ex)
                        {
                            independentEquilibriumStatus =
                                "independent_equilibrium_exception:"
                                + ex.GetType().Name;
                        }
                    }
                    // SP63 8.2.15 permits psi_s=1 for a conservative initial
                    // screening. It preserves CScore's sigma_s, Es, ls, phi's
                    // and changes ONLY psi_s, with no ARBAT fitting.
                    // Cracked cases only: never turn an uncertain upstream
                    // zero-width fallback into a valid upper bound.
                    double? widthPsiOne = validated && r.Cracked
                        && double.IsFinite(r.PsiS) && r.PsiS > 0
                        ? r.AcrcLong / r.PsiS : null;
                    if (widthPsiOne.HasValue
                        && (!double.IsFinite(widthPsiOne.Value) || widthPsiOne.Value < 0))
                        widthPsiOne = null;
                    string? error = validated ? null :
                        (!r.Cracked && r.CrcConverged
                             && double.IsFinite(r.Mcrc) && mLong > r.Mcrc
                            ? "upstream_post_threshold_zero_unresolved_stage"
                            : "indeterminate_crack_solver_or_upstream_silent_fallback");
                    // Selection here governs ONLY preliminary SLS screening.
                    // 'arbat_conservative' does NOT imply we reproduced ARBAT:
                    // the pre-crack stress approximation is not normatively
                    // established for arbitrary loads or geometries.
                    // Keep the native refined result as a separate, unchanged option.
                    double? primaryWidth = null, primaryShortWidth = null;
                    string primaryBasis = "not_computed";
                    if (validated)
                    {
                        if (!r.Cracked)
                        {
                            primaryWidth = 0.0;
                            primaryShortWidth = 0.0;
                            primaryBasis = "uncracked_checked_both_long_and_total_thresholds";
                        }
                        else if (probe.PreliminaryMethod == "native_refined")
                        {
                            primaryWidth = r.AcrcLong;
                            primaryShortWidth = r.AcrcShort;
                            primaryBasis = "native_refined_postcrack";
                        }
                        else
                        {
                            // Long: ARBAT-oriented pre-crack reference stress,
                            // if applicable, but never below native CScore.
                            if (widthPreMm.HasValue && preError is null)
                            {
                                primaryWidth = Math.Max(widthPreMm.Value, r.AcrcLong);
                                primaryBasis = "precrack_compatibility_envelope_with_native";
                            }
                            else if (widthPsiOne.HasValue)
                            {
                                primaryWidth = Math.Max(widthPsiOne.Value, r.AcrcLong);
                                primaryBasis = "psi_s_one_fallback_compat_not_applicable";
                            }

                            // Exact same sustained and total N-M tuple:
                            // SP63 three-component short width = a1+a2-a3
                            // and with identical load/diagram, a2 == a3.
                            // The short width MUST equal the long width, also
                            // for the pre-crack compatibility estimate. This
                            // holds only if the native solve independently
                            // confirms equal widths. Otherwise fail closed.
                            if (sameMoments)
                            {
                                double nativeWidthTolerance = Math.Max(1e-9,
                                    1e-8 * Math.Max(Math.Abs(r.AcrcLong),
                                                    Math.Abs(r.AcrcShort)));
                                if (primaryWidth.HasValue
                                    && Math.Abs(r.AcrcLong - r.AcrcShort)
                                        <= nativeWidthTolerance)
                                {
                                    primaryShortWidth = primaryWidth.Value;
                                    primaryBasis += "_fully_sustained";
                                }
                                else
                                {
                                    primaryBasis += "_native_short_long_inconsistent";
                                }
                            }
                            else
                            {
                                // Partial sustained action: no independently
                                // validated pre-crack transformation for the
                                // second transient term. Use psi_s=1 for all
                                // three native SP63 components and retain an
                                // envelope against unmodified native CScore.
                                double? shortPsiOne = null;
                                if (double.IsFinite(r.PsiS) && r.PsiS > 0.0
                                    && double.IsFinite(r.PsiS2) && r.PsiS2 > 0.0
                                    && double.IsFinite(r.Acrc1) && double.IsFinite(r.Acrc2)
                                    && double.IsFinite(r.Acrc3))
                                {
                                    double estimate = (r.Acrc1 - r.Acrc3) / r.PsiS
                                                      + r.Acrc2 / r.PsiS2;
                                    if (double.IsFinite(estimate) && estimate >= 0.0)
                                        shortPsiOne = estimate;
                                }
                                if (shortPsiOne.HasValue)
                                    primaryShortWidth = Math.Max(shortPsiOne.Value,
                                                                r.AcrcShort);
                            }
                        }
                    }

                    double? primaryUtilization = primaryWidth.HasValue
                        ? primaryWidth.Value / probe.AcrcUltLongMm : null;
                    double? primaryShortUtilization = primaryShortWidth.HasValue
                        ? primaryShortWidth.Value / probe.AcrcUltShortMm : null;
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
                        secondary_equilibrium_status = independentEquilibriumStatus,
                        secondary_equilibrium_converged = independentEquilibriumConverged,
                        secondary_equilibrium_iterations = independentEquilibriumIterations,
                        secondary_equilibrium_residual = independentEquilibriumResidual,
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
                        // Alternative uses PRE-crack plane only for ψs,
                        // not for the governing CScore SLS result.
                        compatibility_hypothesis = "precrack_sigma_s_crc_legacy_opencs_not_verified_arbat",
                        compatibility_error = preError,
                        sigma_rebar_precrack_kpa = validated ? sigmaPreKpa : null,
                        psi_s_precrack_hypothesis = validated ? psiPre : null,
                        width_long_precrack_hypothesis_mm = validated ? widthPreMm : null,
                        utilization_long_precrack_hypothesis = validated ? utilizationPre : null,
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
                        screening_psi_one_basis = "SP63_8.2.15_psi_s_equal_one_no_upstream_changes",
                        width_long_psi_one_mm = widthPsiOne,
                        utilization_long_psi_one = widthPsiOne.HasValue
                            ? (double?)(widthPsiOne.Value / probe.AcrcUltLongMm) : null,
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
            if (!rectMode || !double.IsFinite(shear.DiameterMm) || shear.DiameterMm <= 0
                || !double.IsFinite(shear.SpacingMm) || shear.SpacingMm <= 0
                || !double.IsFinite(shear.RswKpa) || shear.RswKpa <= 0 || shear.RswKpa > 300_000
                || shear.EffectiveLegsPerCage < 1 || shear.EffectiveLegsPerCage > 30
                || shear.CageCount < 1 || shear.CageCount > 3
                || input.Loads.Any(l => l.QKn is not double q || !double.IsFinite(q)))
                throw new ArgumentException("Invalid shear screening geometry, steel or Q");
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
                        .Any(v => v.Second.SM - v.First.SM <= 1e-9)))
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
                || (probe.PreliminaryMethod != "arbat_conservative"
                    && probe.PreliminaryMethod != "native_refined")
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
    [JsonPropertyName("preliminary_method")] public string PreliminaryMethod { get; init; } = "arbat_conservative";
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
