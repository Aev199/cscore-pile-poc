using System.Reflection;
using System.Text.Json;
using Xunit;

public class ZeroShearCapacityTests
{
    [Theory]
    [InlineData("RunShearScreening", "research_screen_fail")]
    [InlineData("RunShearProfiles", "research_profile_exceeds_one")]
    public void NativeZeroResistanceIsKnownFailure(string method, string status)
    {
        const string json = """
        {"pile":{"diameter_mm":800,"mesh_resolution":21,"polygon_sides":48},
         "materials":{"concrete_rb_kpa":15300,"concrete_rbt_kpa":1035,"concrete_e_kpa":32500000,
          "concrete_ec1":-0.0015,"concrete_ec2":-0.0035,"concrete_et1":0.00008,"concrete_et2":0.00015,
          "rebar_rs_kpa":435000,"rebar_e_kpa":200000000,"rebar_ec2":-0.025,"rebar_et2":0.025},
         "research_rectangle":{"width_mm":800,"height_mm":800,"mesh_resolution":21,"bars":[
          {"x_mm":-300,"y_mm":-300,"diameter_mm":25},{"x_mm":300,"y_mm":300,"diameter_mm":25}]},
         "loads":[{"id":"U","n_kn":-500000,"mx_knm":40,"my_knm":0,"q_kn":20}],
         "shear_screening":{"enabled":true,"diameter_mm":10,"spacing_mm":200,
          "effective_legs_per_cage":2,"cage_count":1,"rsw_kpa":300000,"constructive_confirmed":true},
         "shear_profile":{"profiles":[{"id":"U","samples":[
          {"s_m":0,"n_kn":-500000,"m_knm":40,"q_kn":20},
          {"s_m":2,"n_kn":-500000,"m_knm":80,"q_kn":20}]}],
          "pitch_bands":[{"start_s_m":0,"end_s_m":2,"spacing_mm":200}]},
         "candidates":[{"id":"R","bar_count":2,"bar_diameter_mm":25,"bar_axis_radius_mm":300}]}
        """;
        var assembly=Assembly.Load("CScorePileRunner");
        var type=assembly.GetType("BatchInput",true)!;
        var input=JsonSerializer.Deserialize(json,type)!;
        var candidates=(System.Collections.IEnumerable)type.GetProperty("Candidates")!.GetValue(input)!;
        var result=assembly.GetType("Program")!.GetMethod(method,BindingFlags.NonPublic|BindingFlags.Static)!
            .Invoke(null,[input,candidates.Cast<object>().Single()]);
        var row=JsonSerializer.SerializeToElement(result)[0];
        Assert.Equal(status,row.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null,row.GetProperty("utilization").ValueKind);
        Assert.Equal("native_zero_capacity",row.GetProperty("reason").GetString());
        Assert.Equal(0,row.GetProperty("allowable").GetDouble());
        Assert.True(row.GetProperty("applied").GetDouble()>0);
    }
}
