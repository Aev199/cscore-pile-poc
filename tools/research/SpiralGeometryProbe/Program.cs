using System.Reflection;
using System.Text.Json;
using CScore;

// Read native fibre forces after a native N/M solve. No shear resistance law.
if (args.Length!=2) throw new ArgumentException("runner.dll engine-input.json");
var runner=Assembly.LoadFrom(Path.GetFullPath(args[0]));
var inputType=runner.GetType("BatchInput",throwOnError:true)!;
var input=JsonSerializer.Deserialize(File.ReadAllText(args[1]),inputType)!;
var json=JsonDocument.Parse(File.ReadAllText(args[1])).RootElement;
var candidates=(System.Collections.IEnumerable)inputType.GetProperty("Candidates")!.GetValue(input)!;
var make=runner.GetType("Program")!.GetMethod("MakeSection",BindingFlags.NonPublic|BindingFlags.Static)!;
var output=new List<object>();
foreach (var candidate in candidates)
foreach (var load in json.GetProperty("loads").EnumerateArray())
{
    string id=load.GetProperty("id").GetString()!;
    double n=load.GetProperty("n_kn").GetDouble(), mx=load.GetProperty("mx_knm").GetDouble();
    double my=load.GetProperty("my_knm").GetDouble();
    var section=(CrossSection)make.Invoke(null,[input,candidate])!;
    var solver=new StrainSolver(section,CalcType.C,ten:false,tol:.01,maxIter:100);
    var plane=solver.Solve(n,mx,my);
    var actual=section.Integral(plane,CalcType.C,ten:false);
    double residual=Math.Sqrt(Math.Pow(actual.N-n,2)+Math.Pow(actual.Mx-mx,2)+Math.Pow(actual.My-my,2));
    var fibres=section.Areas.SelectMany(a=>a.Fibers).ToArray();
    var positive=fibres.Where(f=>f.N>0).ToArray();
    var negative=fibres.Where(f=>f.N<0).ToArray();
    double tension=positive.Sum(f=>f.N),compression=-negative.Sum(f=>f.N);
    double? yt=tension>1e-8 ? positive.Sum(f=>f.N*f.Y)/tension : null;
    double? yc=compression>1e-8 ? -negative.Sum(f=>f.N*f.Y)/compression : null;
    bool forceClosure=Math.Abs(tension-compression-actual.N)<1e-6;
    bool momentClosure=Math.Abs(fibres.Sum(f=>f.N*f.Y)-actual.Mx)<1e-6;
    string status=!solver.Converged||!double.IsFinite(residual)||residual>.02 ? "native_no_convergence"
        : !forceClosure||!momentClosure ? "fibre_readout_not_closed"
        : Math.Abs(my)>1e-9 ? "biaxial_out_of_scope"
        : yt is null||yc is null||Math.Abs(yt.Value-yc.Value)<1e-9 ? "no_tension_compression_pair"
        : "native_NM_chords_only";
    output.Add(new { id,n_kn=n,mx_knm=mx,my_knm=my,status,
        native_converged=solver.Converged,native_residual=residual,
        tension_kn=tension,compression_kn=compression,tension_y_m=yt,compression_y_m=yc,
        z_m=yt is double t && yc is double c ? (double?)Math.Abs(t-c) : null,
        native_force_n=actual.N,native_force_mx=actual.Mx,
        mesh_fibres=fibres.Length,force_closure=forceClosure,moment_closure=momentClosure,
        limitation="N_M_state_readout_only_not_full_shear_truss_equilibrium_not_SP63_validation" });
}
Console.WriteLine(JsonSerializer.Serialize(output,new JsonSerializerOptions { WriteIndented=true }));
