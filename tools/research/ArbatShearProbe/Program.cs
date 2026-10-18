using System.Reflection;
using System.Text.Json;
using CScore;
using CScore.Sp63Shear;

var runner=Assembly.LoadFrom(Path.GetFullPath(args[0]));
var inputType=runner.GetType("BatchInput",true)!;
var make=runner.GetType("Program")!.GetMethod("MakeSection",BindingFlags.NonPublic|BindingFlags.Static)!;
var rows=new List<object>();
var root=JsonDocument.Parse(File.ReadAllText(args[1])).RootElement;
foreach(var job in root.EnumerateArray()) {
 var input=JsonSerializer.Deserialize(job.GetProperty("engine").GetRawText(),inputType)!;
 var candidates=(System.Collections.IEnumerable)inputType.GetProperty("Candidates")!.GetValue(input)!;
 foreach(var candidate in candidates) {
  var section=(CrossSection)make.Invoke(null,[input,candidate])!;
  var pair=InclinedSectionGeometryPair.Resolve(section,ShearPlane.Vy,CalcType.C);
  foreach(var variant in job.GetProperty("variants").EnumerateArray())
  foreach(var load in job.GetProperty("reference").EnumerateArray()) {
   double b=variant.GetProperty("b_m").GetDouble(),h=variant.GetProperty("h0_m").GetDouble();
   double n=load.GetProperty("n_kn").GetDouble(),m=load.GetProperty("my_knm").GetDouble(),q=load.GetProperty("qz_kn").GetDouble();
   var side=pair.For(m);
   double area=variant.GetProperty("legs").GetDouble()*Math.PI*.012*.012/4;
   var p=new ShearInclinedInput(b,h,side.Rb,side.Rbt,170000*area/.2,.2,side.Ns,ElementKind.Other,1,0,0,0,Array.Empty<double>(),false,null,FixedB:b,FixedH0:h);
   var result=ShearInclinedChecker.Check(p,new ConstantProfile(q,m,n,0),pair,0);
   var d=result.Details.SingleOrDefault(x=>x.Formula=="8.56");
   double? ratio=d is not null&&double.IsFinite(d.Ratio)&&d.Ratio>=0?d.Ratio:null;
   rows.Add(new {group=job.GetProperty("group").GetString(),id=load.GetProperty("id").GetString(),variant=variant.GetProperty("name").GetString(),b_m=b,h0_m=h,legs=variant.GetProperty("legs").GetInt32(),n_kn=n,m_knm=m,q_kn=q,arbat=load.GetProperty("shear_inclined_utilization"),native_ratio=ratio,variables=d?.Variables.Where(x=>double.IsFinite(x.Value)).ToDictionary(x=>x.Key,x=>x.Value),warnings=result.Warnings,scope="research_geometry_hypothesis_only_not_spiral_validation"});
  }
 }
}
Console.WriteLine(JsonSerializer.Serialize(rows));
