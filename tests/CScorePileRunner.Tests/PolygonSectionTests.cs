using System.Reflection;
using System.Text.Json.Nodes;
using Xunit;

// A general outline (with holes) in place of the width × height rectangle.
public class PolygonSectionTests
{
    const string Bars = """[{"x_mm":-150,"y_mm":-250,"diameter_mm":25},{"x_mm":150,"y_mm":-250,"diameter_mm":25},{"x_mm":-150,"y_mm":250,"diameter_mm":25},{"x_mm":150,"y_mm":250,"diameter_mm":25}]""";

    static JsonNode Request(string geometry, string load = """{"id":"U","n_kn":-1500,"mx_knm":150,"my_knm":20}""") => JsonNode.Parse($$"""
        {"schema_version":1,"pile":{"diameter_mm":0,"mesh_resolution":41,"polygon_sides":0},
         "materials":{"concrete_rb_kpa":17000,"concrete_rbt_kpa":1150,"concrete_e_kpa":32500000,
          "concrete_ec1":-0.0015,"concrete_ec2":-0.0035,"concrete_et1":0.00008,"concrete_et2":0.00015,
          "rebar_rs_kpa":435000,"rebar_e_kpa":200000000,"rebar_ec2":-0.025,"rebar_et2":0.025},
         "research_rectangle":{{geometry}},
         "loads":[{{load}}],
         "candidates":[{"id":"S","bar_count":4,"bar_diameter_mm":25,"bar_axis_radius_mm":0}]}
        """)!;

    static double Factor(JsonNode request)
    {
        string json = (string)Assembly.Load("CScorePileRunner").GetType("Program")!
            .GetMethod("Execute", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, ["", request.ToJsonString()])!;
        var load = JsonNode.Parse(json)!["cases"]![0]!["loads"]![0]!;
        Assert.True((bool)load["converged"]!, (string?)load["error"]);
        return (double)load["factor"]!;
    }

    const string Rectangle = """[{"x_mm":-200,"y_mm":-300},{"x_mm":200,"y_mm":-300},{"x_mm":200,"y_mm":300},{"x_mm":-200,"y_mm":300}]""";

    [Fact]
    public void OutlineOfTheRectangleGivesTheRectangleResult()
    {
        double rect = Factor(Request($$"""{"width_mm":400,"height_mm":600,"mesh_resolution":41,"bars":{{Bars}}}"""));
        // Clockwise outline: the orientation does not matter.
        string reversed = """[{"x_mm":-200,"y_mm":300},{"x_mm":200,"y_mm":300},{"x_mm":200,"y_mm":-300},{"x_mm":-200,"y_mm":-300}]""";
        double poly = Factor(Request($$"""{"width_mm":400,"height_mm":600,"mesh_resolution":41,"bars":{{Bars}},"outline":{{reversed}}}"""));
        Assert.Equal(rect, poly, 3);
    }

    [Fact]
    public void HoleTakesItsConcreteOutOfAxialResistance()
    {
        const string axial = """{"id":"U","n_kn":-1000,"mx_knm":0,"my_knm":0}""";
        double solid = Factor(Request($$"""{"width_mm":400,"height_mm":600,"mesh_resolution":41,"bars":{{Bars}},"outline":{{Rectangle}}}""", axial));
        string hole = """[[{"x_mm":-100,"y_mm":-150},{"x_mm":100,"y_mm":-150},{"x_mm":100,"y_mm":150},{"x_mm":-100,"y_mm":150}]]""";
        double hollow = Factor(Request($$"""{"width_mm":400,"height_mm":600,"mesh_resolution":41,"bars":{{Bars}},"outline":{{Rectangle}},"holes":{{hole}}}""", axial));
        // Pure compression: N = Rb·Ab + Rsc·As (the hole is 1/4 of the concrete).
        double steel = 4 * Math.PI * 25 * 25 / 4 * 435 / 1000, concrete = 0.4 * 0.6 * 17000;
        Assert.Equal(solid / hollow, (concrete + steel) / (0.75 * concrete + steel), 2);
    }

    [Fact]
    public void BarInTheHoleIsRejected()
    {
        string hole = """[[{"x_mm":-190,"y_mm":200},{"x_mm":190,"y_mm":200},{"x_mm":190,"y_mm":290},{"x_mm":-190,"y_mm":290}]]""";
        var ex = Assert.Throws<TargetInvocationException>(() =>
            Factor(Request($$"""{"width_mm":400,"height_mm":600,"mesh_resolution":41,"bars":{{Bars}},"outline":{{Rectangle}},"holes":{{hole}}}""")));
        Assert.Contains("polygon", ex.InnerException!.Message);
    }

    [Fact]
    public void TriangleIsAccepted()
    {
        string triangle = """[{"x_mm":-300,"y_mm":-300},{"x_mm":300,"y_mm":-300},{"x_mm":0,"y_mm":400}]""";
        string bars = """[{"x_mm":-150,"y_mm":-250,"diameter_mm":20},{"x_mm":150,"y_mm":-250,"diameter_mm":20},{"x_mm":0,"y_mm":250,"diameter_mm":20}]""";
        Assert.True(Factor(Request($$"""{"width_mm":600,"height_mm":700,"mesh_resolution":41,"bars":{{bars}},"outline":{{triangle}}}""",
            """{"id":"U","n_kn":-500,"mx_knm":30,"my_knm":0}""")) > 0);
    }
}
