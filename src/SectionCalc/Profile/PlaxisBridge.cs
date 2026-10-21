using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SectionCalc.Profile;

public sealed class PlaxisInfo
{
    public string? UnitForce { get; set; }
    public string? UnitLength { get; set; }
    public string? Version { get; set; }
    public string? Title { get; set; }
}

public sealed class PlaxisObject
{
    public int Id { get; set; }
    /// <summary>Plate or EmbeddedBeamRow.</summary>
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class PlaxisPhase
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    /// <summary>Calculation type from Input (Plastic, Consolidation, Safety…), when read through Input.</summary>
    [JsonPropertyName("calc_type")] public string? CalcType { get; set; }
    /// <summary>Envelope route: the phases of the branch this envelope covers.</summary>
    public List<string> Covers { get; set; } = [];
    public string Label => string.IsNullOrWhiteSpace(Title) || Title == Name ? Name : $"{Name} — {Title}";
}

public sealed class PlaxisCatalog
{
    public PlaxisInfo Info { get; set; } = new();
    public List<PlaxisObject> Objects { get; set; } = [];
    public List<PlaxisPhase> Phases { get; set; } = [];
}

/// <summary>Node results of one object in one phase: rows of x, y, N, M, Q in PLAXIS project units.</summary>
public sealed class PlaxisSeries
{
    public int Object { get; set; }
    public int Phase { get; set; }
    public List<double[]> Nodes { get; set; } = [];
}

/// <summary>+1: positive PLAXIS M stretches the right face (larger x); share of nodes agreeing; phase used.</summary>
public sealed class MomentSignHint
{
    public int Sign { get; set; }
    public double Agreement { get; set; }
    public string? Phase { get; set; }
}

/// <summary>Raw export from PLAXIS Output. Kept in the project so that settings can change without PLAXIS.</summary>
public sealed class PlaxisExport
{
    public PlaxisInfo Info { get; set; } = new();
    public List<PlaxisObject> Objects { get; set; } = [];
    public List<PlaxisPhase> Phases { get; set; } = [];
    public List<string> Columns { get; set; } = [];
    /// <summary>"envelope": rows are envelopes computed by Output per branch; otherwise one row set per phase.</summary>
    public string? Mode { get; set; }
    public bool IsEnvelope => Mode == "envelope";
    public static readonly string[] PhaseColumns = ["x", "y", "n", "m", "q"];
    public static readonly string[] EnvelopeColumns = ["x", "y", "n_min", "n_max", "m_min", "m_max", "q_min", "q_max"];
    public List<PlaxisSeries> Data { get; set; } = [];
    /// <summary>Names selected in Input (when read through Input).</summary>
    public List<string> Selection { get; set; } = [];
    /// <summary>Safety phases left out.</summary>
    [JsonPropertyName("excluded_phases")] public List<PlaxisPhase> ExcludedPhases { get; set; } = [];
    /// <summary>Plate: which face positive M stretches, found from the deflected shape.</summary>
    [JsonPropertyName("moment_sign")] public MomentSignHint? MomentSign { get; set; }
    /// <summary>Phases in which the selected structure is not active.</summary>
    [JsonPropertyName("inactive_phases")] public List<string> InactivePhases { get; set; } = [];
}

/// <summary>
/// Runs the bundled plaxis_export.py with the Python of the PLAXIS installation (it contains plxscripting).
/// The Output remote scripting password is passed through the environment, not the command line.
/// </summary>
public static class PlaxisBridge
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    /// <summary>python.exe of PLAXIS installations, newest first.</summary>
    public static List<string> FindPython()
    {
        var found = new List<string>();
        foreach (var root in new[] { Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            string dir = Path.Combine(Environment.GetFolderPath(root), "Bentley", "Geotechnical");
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var sub in Directory.GetDirectories(dir))
                    foreach (var candidate in new[] { Path.Combine(sub, "python", "python.exe"), Path.Combine(sub, "Python", "python.exe") })
                        if (File.Exists(candidate)) found.Add(candidate);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return found.Distinct().OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Structures selected in PLAXIS Input; Input opens Output and the results of every phase except Safety are read.
    /// </summary>
    public static async Task<PlaxisExport> FromInput(string python, int inputPort, string password, int? outputPort = null, CancellationToken cancel = default)
    {
        var args = new List<string> { "input", "--port", inputPort.ToString() };
        if (outputPort is int o) { args.Add("--output-port"); args.Add(o.ToString()); }
        return Parse(await Run(python, args.ToArray(), password, cancel));
    }

    public static async Task<PlaxisCatalog> Catalog(string python, int port, string password, CancellationToken cancel = default) =>
        JsonSerializer.Deserialize<PlaxisCatalog>(await Run(python, ["catalog", "--port", port.ToString()], password, cancel), Json)!;

    public static async Task<PlaxisExport> Extract(string python, int port, string password, IReadOnlyList<int> objects,
        IReadOnlyList<int> phases, CancellationToken cancel = default)
    {
        string request = Path.Combine(Path.GetTempPath(), $"plaxis-request-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new { objects, phases }), cancel);
        try { return Parse(await Run(python, ["extract", "--port", port.ToString(), "--request", request], password, cancel)); }
        finally { File.Delete(request); }
    }

    /// <summary>Export JSON as written by plaxis_export.py (also a saved file).</summary>
    public static PlaxisExport Parse(string json)
    {
        var export = JsonSerializer.Deserialize<PlaxisExport>(json, Json) ?? throw new InvalidDataException("Пустая выгрузка PLAXIS");
        var columns = export.IsEnvelope ? PlaxisExport.EnvelopeColumns : PlaxisExport.PhaseColumns;
        if (!export.Columns.SequenceEqual(columns))
            throw new InvalidDataException("Выгрузка PLAXIS: ожидаются столбцы " + string.Join(", ", columns));
        if (export.Data.Any(s => s.Nodes.Any(n => n.Length != columns.Length || n.Any(v => !double.IsFinite(v)))))
            throw new InvalidDataException("Выгрузка PLAXIS содержит неполные или нечисловые строки");
        return export;
    }

    static async Task<string> Run(string python, string[] args, string password, CancellationToken cancel)
    {
        if (!File.Exists(python)) throw new FileNotFoundException("Не найден python.exe PLAXIS: " + python);
        string script = Path.Combine(Path.GetTempPath(), "section-app-plaxis-export.py");
        await using (var resource = typeof(PlaxisBridge).Assembly.GetManifestResourceStream("SectionCalc.Plaxis.plaxis_export.py")!)
        await using (var file = File.Create(script))
            await resource.CopyToAsync(file, cancel);
        var info = new ProcessStartInfo(python) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(script);
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["PLAXIS_PASSWORD"] = password;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить Python PLAXIS");
        var stdout = process.StandardOutput.ReadToEndAsync(cancel);
        var stderr = process.StandardError.ReadToEndAsync(cancel);
        try { await process.WaitForExitAsync(cancel); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
        string output = await stdout;
        if (output.TrimStart().StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
            return output;
        }
        string err = (await stderr).Trim();
        throw new InvalidOperationException("Python PLAXIS завершился с ошибкой" + (err.Length > 0 ? ": " + err.Split('\n')[^1] : ""));
    }
}
