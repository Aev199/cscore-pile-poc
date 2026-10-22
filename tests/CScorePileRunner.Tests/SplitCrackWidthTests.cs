using System.Reflection;
using System.Text.Json;
using Xunit;

// SP63 8.2.6 split for partly sustained actions: acrc1/acrc3 from N_l, M_l,
// acrc2 from N, M, each state through the agreed fully sustained estimate.
public class SplitCrackWidthTests
{
    const string Request = """
    {"schema_version":1,"pile":{"diameter_mm":600,"mesh_resolution":41,"polygon_sides":96},
     "materials":{"concrete_rb_kpa":15300,"concrete_rbt_kpa":1035,"concrete_e_kpa":32500000,
      "concrete_ec1":-0.0015,"concrete_ec2":-0.0035,"concrete_et1":8e-05,"concrete_et2":0.00015,
      "rebar_rs_kpa":435000,"rebar_e_kpa":200000000,"rebar_ec2":-0.025,"rebar_et2":0.025},
     "loads":[{"id":"U","n_kn":0,"mx_knm":1,"my_knm":0}],
     "candidates":[{"id":"C","bar_count":8,"bar_diameter_mm":20,"rotation_deg":0,"bar_axis_radius_mm":240.0}],
     "sls_probe":{"short_materials":MAT,"long_materials":MAT,"phi2":0.5,"acrc_ult_long_mm":0.3,
      "acrc_ult_short_mm":0.4,"preliminary_method":"METHOD","review_band":0.03,"loads":[LOAD]}}
    """;
    const string Service = """
    {"concrete_rb_kpa":22000,"concrete_rbt_kpa":1750,"concrete_e_kpa":32500000,"concrete_ec1":-0.0015,
     "concrete_ec2":-0.0035,"concrete_et1":8e-05,"concrete_et2":0.00015,"rebar_rs_kpa":500000,
     "rebar_e_kpa":200000000,"rebar_ec2":-0.025,"rebar_et2":0.025}
    """;

    static JsonElement Probe(string load, string method = "arbat_conservative")
    {
        var json = Request.Replace("MAT", Service).Replace("METHOD", method).Replace("LOAD", load);
        var assembly = Assembly.Load("CScorePileRunner");
        var type = assembly.GetType("BatchInput", true)!;
        var input = JsonSerializer.Deserialize(json, type)!;
        var candidates = (System.Collections.IEnumerable)type.GetProperty("Candidates")!.GetValue(input)!;
        var result = assembly.GetType("Program")!.GetMethod("RunCrackProbe", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [input, candidates.Cast<object>().Single(), null]);
        return JsonSerializer.SerializeToElement(result)[0];
    }

    static double Number(JsonElement e, string key) => e.GetProperty(key).GetDouble();

    [Fact]
    public void EqualSustainedAxialForceKeepsFullySustainedResult()
    {
        var omitted = Probe("""{"id":"S","n_kn":-250,"mx_total_knm":160,"my_total_knm":0,"mx_long_knm":160,"my_long_knm":0}""");
        var explicitN = Probe("""{"id":"S","n_kn":-250,"n_long_kn":-250,"mx_total_knm":160,"my_total_knm":0,"mx_long_knm":160,"my_long_knm":0}""");
        Assert.Equal("research_calculated", omitted.GetProperty("status").GetString());
        Assert.Equal(omitted.GetRawText(), explicitN.GetRawText());
    }

    [Fact]
    public void PartlySustainedActionCombinesSustainedAndTotalStates()
    {
        var split = Probe("""{"id":"S","n_kn":-250,"n_long_kn":-125,"mx_total_knm":160,"my_total_knm":0,"mx_long_knm":80,"my_long_knm":0}""");
        var total = Probe("""{"id":"T","n_kn":-250,"mx_total_knm":160,"my_total_knm":0,"mx_long_knm":160,"my_long_knm":0}""");
        var sustained = Probe("""{"id":"L","n_kn":-125,"mx_total_knm":80,"my_total_knm":0,"mx_long_knm":80,"my_long_knm":0}""");
        Assert.Equal("research_calculated", split.GetProperty("status").GetString());
        Assert.True(sustained.GetProperty("cracked").GetBoolean());
        Assert.StartsWith("sp63_8_2_6_split_sustained_", split.GetProperty("preliminary_basis").GetString());
        double a1 = Number(sustained, "preliminary_width_long_mm"), a2 = Number(total, "preliminary_width_long_mm") / 1.4;
        Assert.Equal(-125, Number(split, "n_long_kn"));
        Assert.Equal(a1, Number(split, "preliminary_width_long_mm"), 12);
        Assert.Equal(a1 + a2 - a1 / 1.4, Number(split, "preliminary_width_short_mm"), 12);
        Assert.True(Number(split, "preliminary_utilization_long") >= Number(split, "utilization_long"));
        Assert.True(Number(split, "preliminary_utilization_short") >= Number(split, "utilization_short"));
    }

    [Fact]
    public void SustainedBelowCrackingMomentUsesTotalStatePsiOnFormedCracks()
    {
        var split = Probe("""{"id":"S","n_kn":0,"mx_total_knm":160,"my_total_knm":0,"mx_long_knm":40,"my_long_knm":0}""");
        var total = Probe("""{"id":"T","n_kn":0,"mx_total_knm":160,"my_total_knm":0,"mx_long_knm":160,"my_long_knm":0}""");
        Assert.True(Number(total, "mcrc_knm") > 40);
        Assert.Equal("sp63_8_2_6_split_sustained_below_mcrc_total_state_psi_s", split.GetProperty("preliminary_basis").GetString());
        // Nearly linear cracked section at N=0: about the total-state width times M_l/M.
        double a1 = Number(split, "preliminary_width_long_mm");
        double proportional = Number(total, "preliminary_width_long_mm") * 40 / 160;
        Assert.InRange(a1 / proportional, 0.97, 1.03);
        Assert.Equal(0, Number(split, "width_long_mm"));
    }

    [Fact]
    public void NativeRefinedRejectsSeparateSustainedAxialForce()
    {
        var row = Probe("""{"id":"S","n_kn":-250,"n_long_kn":-125,"mx_total_knm":160,"my_total_knm":0,"mx_long_knm":80,"my_long_knm":0}""", "native_refined");
        Assert.Equal("indeterminate", row.GetProperty("status").GetString());
        Assert.EndsWith("native_refined_requires_single_axial_force", row.GetProperty("error").GetString());
    }

    [Fact]
    public void NativeRefinedSplitUsesSp63SplitWithNativeStates()
    {
        const string load = """{"id":"S","n_kn":-250,"n_long_kn":-125,"mx_total_knm":160,"my_total_knm":0,"mx_long_knm":80,"my_long_knm":0}""";
        var refined = Probe(load, "native_refined_split");
        var conservative = Probe(load);
        Assert.Equal("research_calculated", refined.GetProperty("status").GetString());
        Assert.StartsWith("sp63_8_2_6_", refined.GetProperty("preliminary_basis").GetString());
        double a1 = Number(refined, "a_crc_1_mm"), a2 = Number(refined, "a_crc_2_mm"), a3 = Number(refined, "a_crc_3_mm");
        Assert.Equal(a1, Number(refined, "preliminary_width_long_mm"), 12);
        Assert.Equal(a1 + a2 - a3, Number(refined, "preliminary_width_short_mm"), 12);
        // The total state is CScore's own width: acrc2 = native total width / 1.4.
        var total = refined.GetProperty("total_state");
        Assert.Equal(Number(total, "width_long_mm") / 1.4, a2, 12);
        Assert.True(Number(refined, "preliminary_width_short_mm") <= Number(conservative, "preliminary_width_short_mm"));
    }

    [Fact]
    public void NativeRefinedSplitMatchesNativeRefinedWhenFullySustained()
    {
        const string load = """{"id":"S","n_kn":-250,"mx_total_knm":160,"my_total_knm":0,"mx_long_knm":160,"my_long_knm":0}""";
        var split = Probe(load, "native_refined_split");
        var native = Probe(load, "native_refined");
        Assert.Equal(Number(native, "preliminary_width_long_mm"), Number(split, "preliminary_width_long_mm"), 12);
        Assert.Equal(Number(native, "preliminary_width_short_mm"), Number(split, "preliminary_width_short_mm"), 12);
    }
}
