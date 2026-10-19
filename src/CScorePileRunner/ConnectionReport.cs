using System.Text.Json;
using System.Text.Json.Serialization;
using CScore;
using CScore.Sp63;
using CScore.Sp63Shear;

// Strength/eta from CScore; crack estimates explicitly identify the selected method.
internal static class ConnectionReport
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
    private static string Text(JsonElement e, string key) => e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
    private static bool True(JsonElement e, string key) => e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.True;
    private static double? Number(JsonElement e, string key) => e.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out double d) && double.IsFinite(d) ? d : null;

    internal static object Build(BatchInput input, Candidate candidate, List<object> normal,
        object[]? sls, string slsGate)
    {
        var context = input.ConnectionContext!;
        var rows = new List<object>();
        void Add(string id, string caseId, string status, string method,
            double? ratio = null, string reason = "", object? details = null,
            string basis = "native_section") => rows.Add(new {
                check_id = id, case_id = caseId, status, utilization = ratio,
                engine_method = method.StartsWith("Application.") ? method : "CScore." + method, reason, details, basis,
                verified_for_design = false });
        void Ratio(string id, string caseId, double? ratio, string method,
            object? details = null, string basis = "native_section")
        {
            if (ratio is not double r || !double.IsFinite(r) || r < 0)
                Add(id, caseId, "indeterminate", method, reason: "native_result_not_finite", details: details, basis: basis);
            else Add(id, caseId, r <= 1 ? "passed" : "failed", method, r, details: details, basis: basis);
        }
        var nativeNormal = normal.Select(Json).ToDictionary(e => Text(e, "id"));
        var memberUsable = context.SecondOrderMode != "cscore_member";
        bool memberFailure = false;
        CrossSection? section = null;
        try { section = Program.MakeSection(input, candidate); }
        catch { /* Individual checks below remain explicitly indeterminate. */ }

        foreach (var load in input.Loads)
        {
            RodEtaWiring.Result? memberActions = null;
            var n = nativeNormal[load.Id];
            Ratio("normal_strength", load.Id,
                True(n, "converged") && Text(n, "error") == "" ? Number(n, "utilization") : null,
                "LimitForceSolver.AllFactor");
            var mode = context.SecondOrderMode;
            if (mode == "already_in_loads" && context.EccentricityInLoads)
            {
                Add("member_stability", load.Id, "accounted_in_loads", "Sp63.RodEtaWiring",
                    reason: "second_order_confirmed_in_source_not_independently_checked", basis: "user_declared");
                Ratio("normal_member_strength", load.Id,
                    True(n, "converged") && Text(n,"error")=="" ? Number(n,"utilization") : null,
                    "LimitForceSolver.AllFactor", basis: "second_order_in_source_user_declared");
            }
            else if (mode != "cscore_member" || !context.EccentricityInLoads)
            {
                string reason = mode is "cscore_member" or "already_in_loads" ? "confirm_eccentricity_in_source"
                    : mode == "section_only" ? "explicit_section_only_scope" : "second_order_context_missing";
                Add("member_stability", load.Id, "not_evaluated", "Sp63.RodEtaWiring", reason: reason);
                Add("normal_member_strength", load.Id, "not_evaluated", "LimitForceSolver.AllFactor", reason: reason);
                if (mode == "cscore_member") memberUsable = false;
            }
            else
            {
                try
                {
                    if (section is null) throw new InvalidOperationException("section_unavailable");
                    var adjusted = Adjust(section, context, load.NKn, load.MxKnm, load.MyKnm);
                    memberActions = adjusted;
                    bool stable = adjusted.X.Stable && adjusted.Y.Stable;
                    LimitForceResult? memberStrength = stable
                        ? LimitForceSolver.ForCrossSection(section, CalcType.C, ten: false)
                            .AllFactor(load.NKn, adjusted.MxEff, adjusted.MyEff) : null;
                    Add("member_stability", load.Id, stable ? "stable" : "unstable", "Sp63.RodEtaWiring.Apply",
                        details: EtaDetails(adjusted), basis: "native_member");
                    memberUsable = stable && !memberFailure;
                    if (!stable)
                    {
                        memberFailure = true;
                        Add("normal_member_strength", load.Id, "not_evaluated", "LimitForceSolver.AllFactor",
                            reason: "native_member_unstable", basis: "native_member");
                    }
                    else
                    {
                        var r = memberStrength!;
                        Ratio("normal_member_strength", load.Id, Valid(r) ? r.Utilization : null,
                            "LimitForceSolver.AllFactor", details: new {
                                n_kn = load.NKn, mx_knm = adjusted.MxEff, my_knm = adjusted.MyEff }, basis: "native_member");
                        memberFailure |= !Valid(r) || r.Utilization > 1;
                    }
                }
                catch (Exception ex)
                {
                    memberUsable = false; memberFailure = true;
                    Add("member_stability", load.Id, "indeterminate", "Sp63.RodEtaWiring.Apply", reason: ex.Message);
                    Add("normal_member_strength", load.Id, "indeterminate", "LimitForceSolver.AllFactor", reason: ex.Message);
                }
            }
            Shear(input, candidate, load.ExternalActions(), section, context, memberActions, Add, Ratio);
        }
        if (input.SlsProbe is null)
        {
            Add("sls_member_stability","","not_evaluated","Sp63.RodEtaWiring",reason:"SLS_loads_missing");
            foreach (string id in new[] { "crack_formation", "crack_width_long", "crack_width_short" })
                Add(id, "", "not_evaluated", "CrackWidthSolver.Compute", reason: "SLS_loads_missing");
        }
        else
        {
            var serviceRows = sls?.Select(Json).ToDictionary(e => Text(e,"id"));
            foreach (var load in input.SlsProbe.Loads)
            {
                JsonElement? result = serviceRows?.GetValueOrDefault(load.Id);
                string reason = slsGate;
                string stabilityStatus = context.SecondOrderMode=="already_in_loads" ? "accounted_in_loads" : "not_evaluated";
                string stabilityReason = context.SecondOrderMode=="already_in_loads"
                    ? "second_order_confirmed_in_source_not_independently_checked" : "second_order_context_missing";
                object? stabilityDetails = null;
                string basis = context.SecondOrderMode == "already_in_loads"
                    ? "second_order_in_source_user_declared" : "native_section";
                if (context.SecondOrderMode == "cscore_member" && sls is not null)
                {
                    result = null; reason = "member_strength_not_confirmed";
                    stabilityReason = sls is null ? slsGate : reason;
                    if (memberUsable && !memberFailure && section is not null)
                    {
                        try
                        {
                            var a = Adjust(section, context, load.NKn, load.MxTotalKnm, load.MyTotalKnm);
                            stabilityDetails = EtaDetails(a);
                            stabilityStatus = a.X.Stable && a.Y.Stable ? "stable" : "unstable";
                            stabilityReason = stabilityStatus=="unstable" ? "native_SLS_member_unstable" : "";
                            if (stabilityStatus=="unstable") reason = stabilityReason;
                            else
                            {
                                var moments = CrackWidthEta.ScaleLongTotal(
                                    load.MxLongKnm, load.MxTotalKnm, load.MyLongKnm, load.MyTotalKnm,
                                    a.MxEff, a.MyEff);
                                var adjusted = new SlsLoad { Id = load.Id, NKn = load.NKn,
                                    MxLongKnm = moments.MxLongEff, MxTotalKnm = moments.MxTotalEff,
                                    MyLongKnm = moments.MyLongEff, MyTotalKnm = moments.MyTotalEff };
                                result = Json(Program.RunCrackProbe(input,candidate,new[] { adjusted })[0]);
                                basis = "native_member_CrackWidthEta.ScaleLongTotal";
                            }
                        }
                        catch (Exception ex) { reason = "native_SLS_member_error:" + ex.Message;
                            stabilityStatus="indeterminate";stabilityReason=reason; }
                    }
                }
                Add("sls_member_stability",load.Id,stabilityStatus,
                    stabilityStatus is "stable" or "unstable" or "indeterminate" ? "Sp63.RodEtaWiring.Apply" : "Sp63.RodEtaWiring",
                    reason:stabilityReason,details:stabilityDetails,basis:basis);
                if (result is not JsonElement r)
                {
                    foreach (string id in new[] { "crack_formation", "crack_width_long", "crack_width_short" })
                        Add(id,load.Id,"not_evaluated","CrackWidthSolver.Compute",reason:reason,basis:basis);
                }
                else if (Text(r,"status") != "research_calculated" || !True(r,"crc_converged"))
                {
                    foreach (string id in new[] { "crack_formation", "crack_width_long", "crack_width_short" })
                        Add(id,load.Id,"indeterminate","CrackWidthSolver.Compute",
                            reason:Text(r,"error") is { Length: > 0 } error ? error : "native_SLS_indeterminate",basis:basis);
                }
                else
                {
                    Add("crack_formation",load.Id,"calculated","CrackWidthSolver.Compute",
                        details:new { cracked = True(r,"cracked"), mcrc_knm = Number(r,"mcrc_knm") },basis:basis);
                    bool conservative = Text(r,"preliminary_method") == "arbat_conservative";
                    foreach (string duration in new[] { "long", "short" })
                    {
                        var ratio = Number(r,"preliminary_utilization_" + duration);
                        var details = new { width_mm = Number(r,"preliminary_width_" + duration + "_mm"),
                            native_width_mm = Number(r,"width_" + duration + "_mm"),
                            native_utilization = Number(r,"utilization_" + duration),
                            method = Text(r,"preliminary_method"),
                            estimate_basis = Text(r,"preliminary_basis"),
                            decision = Text(r,"preliminary_decision"),
                            review_band = Number(r,"preliminary_review_band"),
                            review_reason = Text(r,"preliminary_review_reason") };
                        string method = conservative ? "Application.ArbatConservativeWithCScore" : "CrackWidthSolver.Compute";
                        if (conservative || Text(r,"preliminary_review_reason") != "")
                        {
                            string decision = Text(r,"preliminary_decision");
                            string status = decision == "engineering_review" ? decision
                                : ratio > 1 ? "preliminary_exceeds_limit" : "preliminary_below_limit";
                            Add("crack_width_" + duration,load.Id,status,method,ratio,
                                details:details,basis:conservative ? "cscore_based_conservative_estimate" : basis);
                        }
                        else Ratio("crack_width_" + duration,load.Id,ratio,method,details,basis);
                    }
                }
            }
        }
        Add("anchorage","","not_evaluated","Sp63Shear.ShearInclinedChecker",
            reason:"anchorage_factor_is_input_not_an_anchorage_length_check");
        Add("detailing","","not_evaluated","Sp63Shear.ShearInclinedChecker",
            reason:"reinforcement_detailing_stage_deferred");
        return new { version = 1, verified_for_design = false, checks = rows };
    }

    private static bool Valid(LimitForceResult r) => r.Converged && double.IsFinite(r.Factor)
        && r.Factor > 0 && double.IsFinite(r.Utilization) && r.Utilization >= 0
        && Math.Abs(r.Factor*r.Utilization-1) <= .01;

    private static RodEtaWiring.Result Adjust(CrossSection section, ConnectionContext c, double n,double mx,double my)
        => RodEtaWiring.Apply(section,n,mx,my,c.L0XM!.Value,c.L0YM!.Value,c.Psi,c.Psi,
            iterative:false,jointSolve:(_,_)=>throw new InvalidOperationException("unexpected_iterative_eta_request"));

    private static object EtaDetails(RodEtaWiring.Result r) => new {
        mx_eff_knm = Finite(r.MxEff), my_eff_knm = Finite(r.MyEff),
        eta_x = Finite(r.X.Eta), eta_y = Finite(r.Y.Eta),
        ncr_x_kn = Finite(r.X.Ncr), ncr_y_kn = Finite(r.Y.Ncr),
        l0_x_m = r.X.L0, l0_y_m = r.Y.L0,
        radius_fallback_x = r.X.RadiusFromBoundingBox, radius_fallback_y = r.Y.RadiusFromBoundingBox };

    private static void Shear(BatchInput input,Candidate candidate,LoadCase load,CrossSection? section,
        ConnectionContext c, RodEtaWiring.Result? memberActions,
        Action<string,string,string,string,double?,string,object?,string> add,
        Action<string,string,double?,string,object?,string> ratio)
    {
        const string method = "Sp63Shear.ShearInclinedChecker.Check";
        void Missing(string status,string reason,string source = method)
        {
            foreach (string id in new[] { "shear_strip","shear_inclined","inclined_moment" })
                add(id,load.Id,status,source,null,reason,null,"native_section");
        }
        bool circular = input.ResearchRectangle is null;
        if (circular && Math.Abs(load.MyKnm)>Sp63ShearApplicability.ForceZeroTolerance)
        {
            Missing("not_applicable","circular_biaxial_moment_not_supported"); return;
        }
        var equivalent = circular ? c.CircularShear : null;
        bool concreteOnly = equivalent?.ConcreteOnly == true;
        double? equivalentB = equivalent is null ? null : .885*input.Pile.DiameterMm/1000;
        if (equivalent is not null && (!equivalent.Ready
            || !concreteOnly && input.ShearScreening?.ConstructiveConfirmed!=true))
        {
            Missing("not_evaluated","spiral_equivalence_not_confirmed_or_incomplete"); return;
        }
        var applicability = Sp63ShearApplicability.Evaluate(new Sp63ShearApplicabilityInput(
            equivalent is null ? Sp63ShearApplicabilityMode.StandardAuto : Sp63ShearApplicabilityMode.StandardEquivalent,
            !circular,equivalentB,equivalent?.Ready??false,0,0,false));
        if (!applicability.HasNormativeVerdict)
        {
            Missing("not_applicable",string.Join("; ",applicability.Reasons),"Sp63Shear.Sp63ShearApplicability.Evaluate"); return;
        }
        var t = input.ShearScreening;
        if ((!concreteOnly && t is not { Enabled:true }) || load.QKn is not double q || !double.IsFinite(q))
        {
            Missing("not_evaluated","transverse_geometry_or_Q_missing"); return;
        }
        try
        {
            if (section is null) throw new InvalidOperationException("section_unavailable");
            if (c.SecondOrderMode == "cscore_member")
            {
                if (memberActions is not RodEtaWiring.Result a)
                {
                    Missing("not_evaluated",c.EccentricityInLoads
                        ? "native_member_actions_unavailable" : "confirm_eccentricity_in_source"); return;
                }
                if (!a.X.Stable || !a.Y.Stable)
                {
                    Missing("not_evaluated","native_member_unstable"); return;
                }
                if (!double.IsFinite(a.X.Eta) || !double.IsFinite(a.Y.Eta)
                    || a.X.Eta<1 || a.Y.Eta<1
                    || !double.IsFinite(a.MxEff) || !double.IsFinite(a.MyEff))
                {
                    Missing("not_evaluated","native_member_actions_unavailable"); return;
                }
                // SP63 8.1.2/8.1.15 amplify eccentricity for normal-section N/M.
                // Q in 8.1.32/8.1.33 remains the external shear; 8.1.34 supplies phiN.
                // For 8.1.35 the native checker evaluates M(Point0) of the supplied
                // force profile. Normal-section eta does not replace that profile.
            }
            object forceContext = new { second_order_mode=c.SecondOrderMode,
                actions_basis=c.SecondOrderMode=="cscore_member" ? "source_actions_SP63_8_1_32_34"
                    : "source_actions",
                n_kn=load.NKn,mx_knm=load.MxKnm,my_knm=load.MyKnm,q_kn=q,
                eta_applied_to_q=false,
                inclined_moment_basis="CScore.ConstantProfile.M(Point0)",
                eta_applied_to_inclined_moment=false,
                normal_mx_eff_knm=memberActions is RodEtaWiring.Result x ? Finite(x.MxEff) : null,
                normal_my_eff_knm=memberActions is RodEtaWiring.Result y ? Finite(y.MyEff) : null,
                eta=memberActions is RodEtaWiring.Result actions ? EtaDetails(actions) : null };
            var pair = InclinedSectionGeometryPair.Resolve(section,ShearPlane.Vy,CalcType.C);
            var side = pair.For(load.MxKnm);
            object? equivalenceDetails = null;
            string shearBasis = "research_constant_profile";
            double? fixedB=null,fixedH0=null;
            double helixProjection=1, efficiency=1;
            if (equivalent is not null)
            {
                fixedB=equivalentB;
                fixedH0=equivalentB-equivalent.AMm!.Value/1000;
                if (!(fixedH0>0))
                    throw new InvalidOperationException("invalid_equivalent_concrete_geometry");
                shearBasis="user_equivalent_concrete_only_constant_profile";
                if (equivalent.TwoLegs)
                {
                    // A transverse bar must fit around the longitudinal ring inside concrete.
                    if (2*candidate.BarAxisRadiusMm+candidate.BarDiameterMm+2*t!.DiameterMm
                        >=input.Pile.DiameterMm)
                        throw new InvalidOperationException("invalid_equivalent_spiral_geometry");
                    shearBasis="user_equivalent_two_legs_constant_profile";
                }
                else if (!concreteOnly)
                {
                double winding=equivalent.WindingDiameterMm!.Value;
                if (winding+ t!.DiameterMm>=input.Pile.DiameterMm
                    || winding<=t.DiameterMm
                    || winding/2-t.DiameterMm/2+1e-6
                        <candidate.BarAxisRadiusMm+candidate.BarDiameterMm/2)
                    throw new InvalidOperationException("invalid_equivalent_spiral_geometry");
                // Geometric projection of the helix tangent, Orr et al. (2010), eq. 9.
                // The circular efficiency is an attributed ENGINEER INPUT, never a
                // built-in SP63 coefficient or an automatic two-straight-leg assumption.
                helixProjection=1/Math.Sqrt(1+Math.Pow(t.SpacingMm/(Math.PI*winding),2));
                efficiency=equivalent.CircularEfficiency!.Value;
                shearBasis="user_equivalent_spiral_constant_profile";
                }
            }
            double b=fixedB??side.B,h0=fixedH0??side.H0;
            if (!(b>0 && h0>0)) throw new InvalidOperationException("invalid_native_shear_geometry");
            double area = !concreteOnly && t!.ConstructiveConfirmed ? t.EffectiveLegsPerCage*t.CageCount
                *Math.PI*Math.Pow(t.DiameterMm/1000,2)/4*efficiency*helixProjection : 0;
            if (equivalent is not null)
                equivalenceDetails=new { method=equivalent.Method, b_m=b,h0_m=h0,
                    a_mm=equivalent.AMm,winding_diameter_mm=equivalent.WindingDiameterMm,
                    concrete_only=concreteOnly,
                    two_conditional_legs=equivalent.TwoLegs,
                    circular_efficiency=concreteOnly || equivalent.TwoLegs ? (double?)null : efficiency,
                    helix_projection=concreteOnly || equivalent.TwoLegs ? (double?)null : helixProjection,
                    effective_asw_mm2=area*1e6,efficiency_basis=equivalent.EfficiencyBasis,
                    confirmed=equivalent.Confirmed,
                    limitation=concreteOnly ? "concrete_component_only_historical_geometry_requires_engineer_confirmation"
                        : "historical_geometry_and_user_spiral_equivalence_not_independently_verified_SP63_or_ARBAT" };
            bool moment = c.InclinedMomentRequested && c.AnchorageConfirmed
                && c.AnchorageFactor.HasValue && c.SupportPositiveM is > 0 && c.SupportNegativeM is > 0;
            var p = new ShearInclinedInput(b,h0,side.Rb,side.Rbt,
                concreteOnly ? 0 : t!.RswKpa*area/(t.SpacingMm/1000),
                concreteOnly ? 0 : t!.SpacingMm/1000,side.Ns,
                ElementKind.Other,c.AnchorageFactor??1,0,0,0,Array.Empty<double>(),moment,null,
                FixedB:fixedB,FixedH0:fixedH0);
            var results = new[] {-1,1}.Select(d => ShearInclinedChecker.Check(p,
                new ConstantProfile(q,load.MxKnm,load.NKn,
                    (d==1 ? c.SupportPositiveM : c.SupportNegativeM)??0),pair,d)).ToArray();
            foreach (var item in new[] { ("shear_strip","8.55"),("shear_inclined","8.56"),("inclined_moment","8.63") })
            {
                if (item.Item1=="inclined_moment" && !moment)
                {
                    add(item.Item1,load.Id,"not_evaluated",method,null,
                        !c.InclinedMomentRequested ? "inclined_moment_not_requested"
                            : "confirm_anchorage_and_section_support_distance",
                        new { force_context=forceContext },shearBasis); continue;
                }
                var checks=results.SelectMany(r=>r.Details).Where(d=>d.Formula==item.Item2).ToArray();
                var zeroCapacity=checks.FirstOrDefault(d=>d.Allowable==0
                    && double.IsFinite(d.Applied)&&d.Applied>0&&double.IsPositiveInfinity(d.Ratio));
                if (zeroCapacity is not null)
                {
                    add(item.Item1,load.Id,"failed",method,null,"native_zero_capacity",new {
                        formula=zeroCapacity.Formula,applied=zeroCapacity.Applied,allowable=zeroCapacity.Allowable,
                        units=item.Item1=="inclined_moment" ? "kN*m" : "kN",
                        equivalence=equivalenceDetails,
                        force_context=forceContext,
                        variables=zeroCapacity.Variables.Where(v=>double.IsFinite(v.Value)).ToDictionary(v=>v.Key,v=>v.Value)
                    },shearBasis);
                    continue;
                }
                var valid=checks.Where(d=>double.IsFinite(d.Ratio)&&d.Ratio>=0).ToArray();
                var worst=valid.OrderByDescending(d=>d.Ratio).FirstOrDefault();
                bool complete=checks.Length==2 && valid.Length==2;
                // A known failed native direction remains a failure even when
                // the opposite direction has no projection. Incomplete can never pass.
                double? use = complete || valid.Any(d=>d.Ratio>1) ? worst!.Ratio : null;
                ratio(item.Item1,load.Id,use,method,worst is null ? null : new {
                    formula = worst.Formula, directions_complete = complete,
                    applied=worst.Applied,allowable=worst.Allowable,
                    units=item.Item1=="inclined_moment" ? "kN*m" : "kN",
                    equivalence = equivalenceDetails,
                    force_context = forceContext,
                    missing_directions = results.Select((r,i)=>(Result:r,Direction:i==0 ? -1 : 1))
                        .Where(r=>!r.Result.Details.Any(d=>d.Formula==item.Item2&&double.IsFinite(d.Ratio)&&d.Ratio>=0))
                        .Select(r=>r.Direction).ToArray(),
                    variables = worst.Variables.Where(v=>double.IsFinite(v.Value)).ToDictionary(v=>v.Key,v=>v.Value),
                    warnings = results.SelectMany(r=>r.Warnings).Distinct().ToArray()
                },shearBasis);
            }
        }
        catch (Exception ex) { Missing("indeterminate","native_shear_error:"+ex.Message); }
    }
}

internal sealed class ConnectionContext
{
    [JsonPropertyName("circular_shear")] public CircularShearEquivalence? CircularShear { get; init; }
    [JsonPropertyName("second_order_mode")] public string SecondOrderMode { get; init; } = "unspecified";
    [JsonPropertyName("l0_x_m")] public double? L0XM { get; init; }
    [JsonPropertyName("l0_y_m")] public double? L0YM { get; init; }
    [JsonPropertyName("psi")] public double Psi { get; init; } = 1;
    [JsonPropertyName("inclined_moment_requested")] public bool InclinedMomentRequested { get; init; }
    [JsonPropertyName("anchorage_confirmed")] public bool AnchorageConfirmed { get; init; }
    [JsonPropertyName("anchorage_factor")] public double? AnchorageFactor { get; init; }
    [JsonPropertyName("support_distance_positive_m")] public double? SupportPositiveM { get; init; }
    [JsonPropertyName("support_distance_negative_m")] public double? SupportNegativeM { get; init; }
    [JsonPropertyName("eccentricity_in_loads")] public bool EccentricityInLoads { get; init; }

    internal void Validate()
    {
        CircularShear?.Validate();
        if (SecondOrderMode is not ("unspecified" or "already_in_loads" or "cscore_member" or "section_only")
            || !double.IsFinite(Psi) || Psi<0 || Psi>1
            || L0XM is double x && (!double.IsFinite(x)||x<=0)
            || L0YM is double y && (!double.IsFinite(y)||y<=0)
            || SecondOrderMode=="cscore_member" && (!L0XM.HasValue||!L0YM.HasValue)
            || AnchorageFactor is double k && (!double.IsFinite(k)||k<=0||k>1)
            || AnchorageConfirmed && !AnchorageFactor.HasValue
            || SupportPositiveM is double s && (!double.IsFinite(s)||s<0)
            || SupportNegativeM is double sn && (!double.IsFinite(sn)||sn<0))
            throw new ArgumentException("Invalid CScore connection context");
    }
}

internal sealed class CircularShearEquivalence
{
    [JsonPropertyName("method")] public string Method { get; init; } = "";
    [JsonPropertyName("a_mm")] public double? AMm { get; init; }
    [JsonPropertyName("winding_diameter_mm")] public double? WindingDiameterMm { get; init; }
    [JsonPropertyName("circular_efficiency")] public double? CircularEfficiency { get; init; }
    [JsonPropertyName("efficiency_basis")] public string EfficiencyBasis { get; init; } = "";
    [JsonPropertyName("confirmed")] public bool Confirmed { get; init; }
    internal bool ConcreteOnly => Method == "vniigs_1984_concrete_only";
    internal bool TwoLegs => Method == "vniigs_1984_two_legs";
    internal bool Ready => Confirmed && AMm.HasValue
        && (ConcreteOnly || TwoLegs || WindingDiameterMm.HasValue && CircularEfficiency.HasValue)
        && !string.IsNullOrWhiteSpace(EfficiencyBasis);
    internal void Validate()
    {
        if (Method is not ("vniigs_1984_user_spiral" or "vniigs_1984_concrete_only" or "vniigs_1984_two_legs")
            || (ConcreteOnly || TwoLegs) && (WindingDiameterMm.HasValue || CircularEfficiency.HasValue)
            || EfficiencyBasis is null || EfficiencyBasis.Length>1000
            || AMm is double a && (!double.IsFinite(a)||a<0)
            || WindingDiameterMm is double d && (!double.IsFinite(d)||d<=0)
            || CircularEfficiency is double f && (!double.IsFinite(f)||f<0||f>1)
            || Confirmed && string.IsNullOrWhiteSpace(EfficiencyBasis))
            throw new ArgumentException("Invalid attributed circular spiral equivalence");
    }
}
