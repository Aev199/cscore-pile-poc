using System.Reflection;
using System.Text.Json.Nodes;
using Xunit;

public class MomentCurvatureTests
{
    static JsonNode Request(double? rscKpa = null)
    {
        var data = JsonNode.Parse("""
        {"schema_version":1,"pile":{"diameter_mm":800,"mesh_resolution":41,"polygon_sides":96},
        "materials":{"concrete_rb_kpa":17000,"concrete_rbt_kpa":1150,"concrete_e_kpa":32500000,
          "concrete_ec1":-0.0015,"concrete_ec2":-0.0035,"concrete_et1":0.00008,"concrete_et2":0.00015,
          "rebar_rs_kpa":435000,"rebar_e_kpa":200000000,"rebar_ec2":-0.025,"rebar_et2":0.025},
        "loads":[{"id":"N","n_kn":-6000,"mx_knm":1,"my_knm":0}],
        "candidates":[{"id":"P800","bar_count":12,"bar_diameter_mm":25,"bar_axis_radius_mm":317.5,
          "rotation_deg":0,"steel_area_mm2":5890.486225480862,"clearance_mm":100}]}
        """)!;
        if (rscKpa is double r) data["materials"]!["rebar_rsc_kpa"] = r;
        return data;
    }

    static string Execute(string command, string json) =>
        (string)Assembly.Load("CScorePileRunner").GetType("Program")!
            .GetMethod("Execute", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [command, json])!;

    [Fact]
    public void CurveRisesFromCrackingToYieldToUltimate()
    {
        var data = Request();
        data["moment_curvature"] = JsonNode.Parse("""{"n_kn":-1000,"mx_knm":1,"my_knm":0,"calc_type":"C"}""");
        var r = JsonNode.Parse(Execute("--moment-curvature", data.ToJsonString()))!;
        Assert.Equal("ok", (string)r["status"]!);
        double M(string key) => (double)r[key]!["mx_knm"]!;
        double K(string key) => Math.Abs((double)r[key]!["ky_1m"]!) + Math.Abs((double)r[key]!["kz_1m"]!);
        Assert.True(M("cracking") > 0 && M("cracking") < M("yield") && M("yield") <= M("ultimate") + 1e-6);
        Assert.True(K("cracking") < K("yield") && K("yield") < K("ultimate"));
        Assert.True(r["points"]!.AsArray().Count > 10);
    }

    [Fact]
    public void RscLowersTheSquashLoadByTheSteelShare()
    {
        double Factor(double? rsc)
        {
            var r = JsonNode.Parse(Execute("", Request(rsc).ToJsonString()))!;
            return (double)r["cases"]![0]!["loads"]![0]!["factor"]!;
        }
        // Squash load: the bars at Rsc instead of Rs (12Ø25, 35 MPa less, differential diagram).
        double same = Factor(null), lower = Factor(400000);
        double expected = 5890.486 * 35 / 1000;
        Assert.InRange(same * 6000 - lower * 6000, expected * 0.97, expected * 1.03);
    }
}
