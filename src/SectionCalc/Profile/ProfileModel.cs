using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace SectionCalc.Profile;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum PeakCutMethod
{
    /// <summary>Inside the interval the diagram is replaced by the straight line between its values at the ends.</summary>
    Chord,
    /// <summary>Values above the limit are cut down to it.</summary>
    Limit,
}

/// <summary>Peak of M and/or Q cut by the engineer (on his responsibility), between two levels.</summary>
public sealed class PeakCut
{
    public double TopLevel { get; set; }
    public double BottomLevel { get; set; }
    public bool Moment { get; set; } = true;
    public bool Shear { get; set; }
    public PeakCutMethod Method { get; set; } = PeakCutMethod.Chord;
    /// <summary>Limit method: |M|, kN·m per element (section width / pile).</summary>
    public double MomentLimit { get; set; }
    /// <summary>Limit method: |Q|, kN per element.</summary>
    public double ShearLimit { get; set; }
}

/// <summary>What is designed and how the PLAXIS forces relate to one element.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum ProfileScenario
{
    /// <summary>Diaphragm wall, PLAXIS 2D Plate: forces per metre × the panel width.</summary>
    Wall,
    /// <summary>Retaining piles (bored secant / contiguous), PLAXIS 2D Plate or Embedded beam row: per metre × the pile spacing.</summary>
    RetainingPiles,
    /// <summary>Piles of a PLAXIS 3D model (Embedded beam / Beam): forces per pile.</summary>
    Piles3D,
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum ProfileMode
{
    /// <summary>Required reinforcement along the element.</summary>
    Required,
    /// <summary>The section as given (bars of the section module) checked along the element.</summary>
    Check,
}

/// <summary>Combination kind and inclusion of one PLAXIS phase.</summary>
public sealed class PhaseSetting
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public bool Include { get; set; } = true;
    public bool ShortTerm { get; set; }
    public bool Seismic { get; set; }
    public bool Special { get; set; }
}

public sealed class ProfileSettings
{
    /// <summary>PLAXIS project units; taken from Output and editable.</summary>
    public string ForceUnit { get; set; } = "kN";
    public string LengthUnit { get; set; } = "m";
    public ProfileScenario Scenario { get; set; } = ProfileScenario.Wall;
    public ProfileMode Mode { get; set; } = ProfileMode.Required;
    /// <summary>Retaining piles from PLAXIS 2D: forces per metre out of plane are multiplied by the pile spacing.</summary>
    public double PileSpacingM { get; set; } = 1;
    /// <summary>Diaphragm wall: panel width, m — the forces per metre are multiplied by it and it is the section width b.</summary>
    public double PanelWidthM { get; set; }
    /// <summary>+1: positive PLAXIS M stretches the right face (larger x); −1: the left face.</summary>
    public int MomentSign { get; set; } = 1;
    /// <summary>Wall: side of the excavation, +1 — the right face (larger x), −1 — the left face, 0 — not set.</summary>
    public int ExcavationSide { get; set; }
    /// <summary>
    /// Group of piles (PLAXIS 3D): the diagrams of all piles are laid from their heads, so that piles with different
    /// head levels get one reinforcement along the pile; otherwise by the elevations of the model.
    /// </summary>
    public bool AlignPileHeads { get; set; } = true;
    /// <summary>The engineer has checked the units, scale and moment sign against Output.</summary>
    public bool Confirmed { get; set; }
    public double StationStepM { get; set; } = 0.5;
    /// <summary>Minimum reinforcement: wall — per face, % of b·h0; pile — total, % of the section area.</summary>
    public double MinPercent { get; set; } = 0.1;
    /// <summary>The PLAXIS forces are design values; normative (cracks) = design / this factor.</summary>
    public double ServiceDivisor { get; set; } = 1;
    public double SustainedShare { get; set; } = 1;
    /// <summary>Axial force not taken into account (N = 0): on the safe side for bending when N is uncertain.</summary>
    public bool IgnoreAxialForce { get; set; }
    public List<PeakCut> PeakCuts { get; set; } = [];
    /// <summary>Upper bound of the search: bar diameter of the adopted layout, mm.</summary>
    public double MaxBarDiameterMm { get; set; } = 40;
}

/// <summary>Whole pile or wall from PLAXIS forces: section definition, raw export, phase kinds, settings.</summary>
public sealed class ProfileProject
{
    public const int CurrentVersion = 1;
    public int Version { get; set; } = CurrentVersion;
    public string Title { get; set; } = "Эпюра армирования";
    /// <summary>Cross-section, materials and design conditions; the bar diameters are varied by the search.</summary>
    public SectionProject Section { get; set; } = SectionProject.Example(SectionKind.Wall);
    public PlaxisExport? Source { get; set; }
    public List<PhaseSetting> Phases { get; set; } = [];
    public ProfileSettings Settings { get; set; } = new();

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    public static ProfileProject Deserialize(string json)
    {
        var p = JsonSerializer.Deserialize<ProfileProject>(json, Options) ?? throw new InvalidDataException("Пустой файл");
        if (p.Version != CurrentVersion) throw new InvalidDataException("Неподдерживаемая версия файла эпюры");
        if (p.Section.Version != SectionProject.CurrentVersion) throw new InvalidDataException("Неподдерживаемая версия сечения в файле эпюры");
        return p;
    }

    /// <summary>Adopts a new export: phase settings are kept by phase name.</summary>
    public void UseSource(PlaxisExport export)
    {
        var old = Phases.ToDictionary(p => p.Name);
        Source = export;
        Phases = export.Phases.Select(ph => old.TryGetValue(ph.Name, out var s)
            ? new PhaseSetting { Id = ph.Id, Name = ph.Name, Include = s.Include, ShortTerm = s.ShortTerm, Seismic = s.Seismic, Special = s.Special }
            : new PhaseSetting { Id = ph.Id, Name = ph.Name }).ToList();
        // The scenario follows the model: 3D — piles of the model; Embedded beam row — retaining piles; Plate — kept.
        if (export.Is3D) Settings.Scenario = ProfileScenario.Piles3D;
        else if (export.Objects.Any(o => o.Kind != "Plate")) Settings.Scenario = ProfileScenario.RetainingPiles;
        else if (Settings.Scenario == ProfileScenario.Piles3D) Settings.Scenario = ProfileScenario.Wall;
        if (Unit.Force(export.Info.UnitForce) is not null) Settings.ForceUnit = export.Info.UnitForce!;
        if (Unit.Length(export.Info.UnitLength) is not null) Settings.LengthUnit = export.Info.UnitLength!;
        // The deflected shape tells which face positive M stretches.
        if (export.MomentSign is { Sign: 1 or -1, Agreement: >= 0.5 } hint) Settings.MomentSign = hint.Sign;
        // A retaining wall moves towards the excavation.
        if (export.MomentSign?.Excavation is "right" or "left") Settings.ExcavationSide = export.MomentSign.Excavation == "right" ? 1 : -1;
        Settings.Confirmed = false;
    }
}

public static class Unit
{
    public static double? Force(string? unit) => unit?.Trim() switch
    {
        "kN" => 1, "N" => 0.001, "MN" => 1000, "tf" or "tonf" => 9.80665, "kip" => 4.448222, _ => null,
    };

    public static double? Length(string? unit) => unit?.Trim() switch
    {
        "m" => 1, "mm" => 0.001, "cm" => 0.01, "ft" => 0.3048, "in" => 0.0254, _ => null,
    };
}
