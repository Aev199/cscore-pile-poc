using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CScore;
using CScore.Sp63Shear;
using Xunit;

public class MaterialReferenceStrainTests
{
    private static CrossSection Section(double? ec0=null, double? et0=null)
    {
        var data=JsonNode.Parse("""
        {"schema_version":1,"pile":{"diameter_mm":800,"mesh_resolution":41,"polygon_sides":96},
        "materials":{"concrete_rb_kpa":15300,"concrete_rbt_kpa":1035,"concrete_e_kpa":32500000,
          "concrete_ec1":-0.0015,"concrete_ec2":-0.0035,"concrete_et1":0.00008,"concrete_et2":0.00015,
          "rebar_rs_kpa":435000,"rebar_e_kpa":200000000,"rebar_ec2":-0.025,"rebar_et2":0.025},
        "loads":[{"id":"U","n_kn":-800,"mx_knm":180,"my_knm":0}],
        "candidates":[{"id":"P800","bar_count":12,"bar_diameter_mm":25,"bar_axis_radius_mm":317.5,
          "rotation_deg":0,"steel_area_mm2":5890.486225480862,"clearance_mm":100}]}
        """)!;
        if(ec0.HasValue)data["materials"]!["concrete_ec0"]=ec0.Value;
        if(et0.HasValue)data["materials"]!["concrete_et0"]=et0.Value;
        var assembly=Assembly.Load("CScorePileRunner");
        var type=assembly.GetType("BatchInput",true)!;
        var input=JsonSerializer.Deserialize(data.ToJsonString(),type)!;
        var candidates=(System.Collections.IEnumerable)type.GetProperty("Candidates")!.GetValue(input)!;
        var candidate=candidates.Cast<object>().Single();
        return (CrossSection)assembly.GetType("Program")!
            .GetMethod("MakeSection",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[input,candidate])!;
    }

    [Fact]
    public void DefaultsSupplyPhysicalStrainsRatherThanL2DiagramPoints()
    {
        var section=Section();
        var g=InclinedSectionGeometry.Resolve(section,ShearPlane.Vy,180,CalcType.C);
        Assert.Equal(.002,g.Eb0,12);
        Assert.Equal(.0001,g.Ebt0,12);
        var c=section.Areas.First(a=>a.Material?.Type==MatType.Concrete).Material!.GetChars(CalcType.C)!;
        Assert.Equal(-.0015,c.Ec1Red,12);
        Assert.Equal(.00008,c.Et1Red,12);
    }

    [Fact]
    public void ExplicitPhysicalStrainsArePreservedWithoutChangingL2Points()
    {
        var section=Section(-.0021,.00011);
        var g=InclinedSectionGeometry.Resolve(section,ShearPlane.Vy,180,CalcType.C);
        Assert.Equal(.0021,g.Eb0,12);
        Assert.Equal(.00011,g.Ebt0,12);
        var c=section.Areas.First(a=>a.Material?.Type==MatType.Concrete).Material!.GetChars(CalcType.C)!;
        Assert.Equal(-.0015,c.Ec1Red,12);
        Assert.Equal(.00008,c.Et1Red,12);
    }
}
