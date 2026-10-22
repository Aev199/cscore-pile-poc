using System.Reflection;
using System.Text.Json;
using CScore;
using CScore.Sp63Shear;

if(args.Length==1 && args[0]=="--phi-boundaries") {
 var geometry=new InclinedSectionGeometry(1,1,0,0,1000,100,1,0,
   30000000,.002,.0001,ShearPlane.Vy,true,[]);
 // Independent tabulated normative references, not a replacement resistance law.
 // SP63.13330.2018 amendment 2, 8.1.34; As=0 makes sigma=abs(N)/Ab.
 var cases=new (string Kind,double N,double Expected)[]{
   ("compression",0,1),("compression",-100,1.1),("compression",-250,1.25),
   ("compression",-500,1.25),("compression",-750,.625),("compression",-900,.25),
   ("compression",-1000,0),("compression",-1100,0),("compression",-2000,0),
   ("tension",50,.75),("tension",100,.5),("tension",200,0),("tension",300,0)};
 Console.WriteLine(JsonSerializer.Serialize(cases.Select(c=>{
   var actual=PhiNCalculator.Compute(ElementKind.Other,c.N,geometry);
   return new {kind=c.Kind,n_kn=c.N,expected_sp63_amendment2=c.Expected,native=actual.Value,
     agrees=Math.Abs(actual.Value-c.Expected)<1e-12,applies_to_strip=actual.AppliesToStrip,
     scope="coefficient_boundary_diagnostic_not_capacity_or_spiral_validation"};
 })));
 return;
}

var runner=Assembly.LoadFrom(Path.GetFullPath(args[0]));
var inputType=runner.GetType("BatchInput",true)!;
var make=runner.GetType("Program")!.GetMethod("MakeSection",BindingFlags.NonPublic|BindingFlags.Static)!;
var rows=new List<object>();
var root=JsonDocument.Parse(File.ReadAllText(args[1])).RootElement;
foreach(var job in root.EnumerateArray()) {
 var input=JsonSerializer.Deserialize(job.GetProperty("engine").GetRawText(),inputType)!;
 var candidates=(System.Collections.IEnumerable)inputType.GetProperty("Candidates")!.GetValue(input)!;
 foreach(var candidate in candidates)
 foreach(var metadata in job.TryGetProperty("strain_audit",out var audit)&&audit.GetBoolean()
   ? new[]{"adapter_current","sp63_6_1_14_metadata"} : new[]{"adapter_current"}) {
  var section=(CrossSection)make.Invoke(null,[input,candidate])!;
  if(metadata=="sp63_6_1_14_metadata")
   foreach(var material in section.Areas.Select(a=>a.Material).Where(m=>m?.Type==MatType.Concrete).Distinct()) {
    // Only physical reference strains used by native phiN; L2 diagram points stay unchanged.
    var chars=material!.GetChars(CalcType.C)!;
    chars.Ec0=-.002;
    chars.Et0=.0001;
   }
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
   var strip=result.Details.SingleOrDefault(x=>x.Formula=="8.55");
   double? stripRatio=strip is not null&&double.IsFinite(strip.Ratio)&&strip.Ratio>=0?strip.Ratio:null;
   double? ratio=d is not null&&double.IsFinite(d.Ratio)&&d.Ratio>=0?d.Ratio:null;
   var phi=PhiNCalculator.Compute(ElementKind.Other,n,side);
   rows.Add(new {group=job.GetProperty("group").GetString(),id=load.GetProperty("id").GetString(),variant=variant.GetProperty("name").GetString(),metadata_variant=metadata,native_eb0=side.Eb0,native_ebt0=side.Ebt0,native_ab=side.Ab,native_as=side.AsTotal,native_es=side.Es,native_rb=side.Rb,native_rbt=side.Rbt,native_phi=phi.Value,b_m=b,h0_m=h,legs=variant.GetProperty("legs").GetInt32(),n_kn=n,m_knm=m,q_kn=q,arbat=load.GetProperty("shear_inclined_utilization"),native_ratio=ratio,native_strip_ratio=stripRatio,variables=d?.Variables.Where(x=>double.IsFinite(x.Value)).ToDictionary(x=>x.Key,x=>x.Value),warnings=result.Warnings,scope="research_geometry_hypothesis_only_not_spiral_validation"});
  }
 }
}
Console.WriteLine(JsonSerializer.Serialize(rows));
