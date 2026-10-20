using System.Text.Json.Serialization;

namespace SectionCalc;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SectionKind { Pile, Wall }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecondOrderMode { SectionOnly, InLoads, Cscore }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EccentricityMode { InLoads, Sp63 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CrackCategory { LimitedWidth, NoCracks }

/// <summary>Bored pile (БНС): circular section with a ring of equal bars inside a spiral.</summary>
public sealed class PileGeometry
{
    public double DiameterMm { get; set; } = 800;
    /// <summary>Concrete cover to the outer surface of the spiral.</summary>
    public double CoverMm { get; set; } = 50;
    public double SpiralDiameterMm { get; set; } = 10;
    public int BarCount { get; set; } = 12;
    public double BarDiameterMm { get; set; } = 25;
    public double MinClearSpacingMm { get; set; } = 25;
}

/// <summary>One face of a diaphragm-wall section.</summary>
public sealed class WallFace
{
    public int BarCount { get; set; } = 5;
    public double BarDiameterMm { get; set; } = 25;
}

/// <summary>Diaphragm wall (СВГ): rectangular section b × h with bars on two faces.</summary>
public sealed class WallGeometry
{
    /// <summary>Width of the checked section (along the wall), b.</summary>
    public double WidthMm { get; set; } = 1000;
    /// <summary>Wall thickness, h (direction of the main moment Mx).</summary>
    public double ThicknessMm { get; set; } = 600;
    /// <summary>Concrete cover to the outer surface of the stirrups.</summary>
    public double CoverMm { get; set; } = 50;
    public double StirrupDiameterMm { get; set; } = 10;
    /// <summary>Face tensioned by a positive Mx.</summary>
    public WallFace PositiveFace { get; set; } = new();
    /// <summary>Face tensioned by a negative Mx.</summary>
    public WallFace NegativeFace { get; set; } = new();
    public double MinClearSpacingMm { get; set; } = 25;
}

/// <summary>
/// One load combination in the ARBAT "simple form": design actions with the load factor γf and the
/// sustained share k. Normative actions are design / γf, sustained ones k × normative.
/// CScore sign convention: N &lt; 0 is compression.
/// </summary>
public sealed class LoadRow
{
    public string Name { get; set; } = "";
    public double N { get; set; }
    public double Mx { get; set; }
    public double My { get; set; }
    /// <summary>Shear force in the plane of Mx.</summary>
    public double Q { get; set; }
    public double LoadFactor { get; set; } = 1;
    public double SustainedShare { get; set; } = 1;
    /// <summary>Short-term combination: γb1 = 1.</summary>
    public bool ShortTerm { get; set; }
    /// <summary>Seismic combination (SP 14.13330): m_tr to the resistances.</summary>
    public bool Seismic { get; set; }
    /// <summary>Special (accidental, non-seismic) combination (SP 296.1325800).</summary>
    public bool Special { get; set; }
}

/// <summary>Concrete working-condition factors of SP 63 6.1.12 and the hardening factor to Eb.</summary>
public sealed class ConcreteFactors
{
    /// <summary>γb1 for sustained loads: 0.9 or 1.0; short-term combinations use 1.0.</summary>
    public double Gb1 { get; set; } = 0.9;
    public double Gb2 { get; set; } = 1;
    public double Gb3 { get; set; } = 1;
    public double Gb5 { get; set; } = 1;
    public double HardeningFactor { get; set; } = 1;
}

public sealed class SeismicSettings
{
    /// <summary>m_tr for normal sections of bar-reinforced concrete (SP 14 table 5.4, line 2).</summary>
    public double NormalSectionFactor { get; set; } = 1.2;
    /// <summary>m_tr for inclined sections (SP 14 table 5.4, line 3).</summary>
    public double InclinedSectionFactor { get; set; } = 1.0;
    /// <summary>Crack checks for seismic combinations (SP 14 5.18 — only when justified).</summary>
    public bool CheckCracks { get; set; }
}

public sealed class SpecialSettings
{
    /// <summary>Normative material properties for special combinations (ARBAT default).</summary>
    public bool NormativeProperties { get; set; } = true;
    /// <summary>Working-condition factors of SP 296 appendix B.</summary>
    public double ConcreteFactor { get; set; } = 1;
    public double LongitudinalFactor { get; set; } = 1;
    public double TransverseFactor { get; set; } = 1;
    /// <summary>Additional factor reducing the resistances (SP 296 5.11, appendix B).</summary>
    public double AdditionalFactor { get; set; } = 1;
}

public sealed class Conditions
{
    public double ElementLengthM { get; set; } = 6;
    /// <summary>Effective length factor in the plane of Mx (ARBAT plane XoZ).</summary>
    public double MuX { get; set; } = 1;
    /// <summary>Effective length factor in the plane of My (ARBAT plane XoY).</summary>
    public double MuY { get; set; } = 1;
    public double SlendernessLimit { get; set; } = 200;
    public bool StaticallyDeterminate { get; set; }

    public EccentricityMode Eccentricity { get; set; } = EccentricityMode.Sp63;
    /// <summary>Given accidental eccentricities, mm; 0 = by SP 63 (CScore).</summary>
    public double EaXMm { get; set; }
    public double EaYMm { get; set; }

    public SecondOrderMode SecondOrder { get; set; } = SecondOrderMode.Cscore;

    public double ReliabilityUls { get; set; } = 1;
    public double ReliabilitySls { get; set; } = 1;

    public CrackCategory CrackCategory { get; set; } = CrackCategory.LimitedWidth;
    /// <summary>Limits from the permeability requirement instead of reinforcement preservation.</summary>
    public bool PermeabilityLimits { get; set; }
    public double CrackLimitLongMm { get; set; } = 0.3;
    public double CrackLimitShortMm { get; set; } = 0.4;

    public bool ShearCheck { get; set; }
    /// <summary>Wall: stirrup pitch; pile: full pitch of the spiral turn.</summary>
    public double ShearPitchMm { get; set; } = 200;
    /// <summary>Wall only: effective stirrup legs in the section.</summary>
    public int StirrupLegs { get; set; } = 4;
    /// <summary>Pile only: distance a from the face to the reinforcement centroid for b = 0.885D, h0 = b − a.</summary>
    public double PileShearAMm { get; set; } = 85;
    /// <summary>Engineer confirms constructive requirements (wall) or the accepted equivalent model (pile).</summary>
    public bool ShearModelConfirmed { get; set; }

    public bool InclinedMoment { get; set; }
    public double AnchorageFactor { get; set; } = 1;
    public double SupportDistanceDownM { get; set; } = 3;
    public double SupportDistanceUpM { get; set; } = 3;

    public double L0xM => MuX * ElementLengthM;
    public double L0yM => MuY * ElementLengthM;
}

/// <summary>Candidate ranges for the reinforcement selection.</summary>
public sealed class SelectionSettings
{
    public int PileCountMin { get; set; } = 8;
    public int PileCountMax { get; set; } = 24;
    public int PileCountStep { get; set; } = 2;
    public int WallCountMin { get; set; } = 3;
    public int WallCountMax { get; set; } = 10;
    public bool WallSymmetric { get; set; } = true;
    public List<double> Diameters { get; set; } = [16, 20, 25, 28, 32];
}

public sealed class SectionProject
{
    public const int CurrentVersion = 2;
    public int Version { get; set; } = CurrentVersion;
    public string Title { get; set; } = "Новое сечение";
    public SectionKind Kind { get; set; } = SectionKind.Pile;
    public PileGeometry Pile { get; set; } = new();
    public WallGeometry Wall { get; set; } = new();
    public string ConcreteClass { get; set; } = "B30";
    public string RebarClass { get; set; } = "A500";
    public string TransverseClass { get; set; } = "A240";
    public ConcreteFactors Concrete { get; set; } = new();
    /// <summary>γs of the longitudinal and transverse reinforcement.</summary>
    public double LongitudinalFactor { get; set; } = 1;
    public double TransverseFactor { get; set; } = 1;
    public List<LoadRow> Loads { get; set; } = [];
    public Conditions Conditions { get; set; } = new();
    public SeismicSettings Seismic { get; set; } = new();
    public SpecialSettings Special { get; set; } = new();
    public SelectionSettings Selection { get; set; } = new();

    public static SectionProject Example(SectionKind kind)
    {
        var p = new SectionProject { Kind = kind, Title = kind == SectionKind.Pile ? "БНС Ø800" : "СВГ 600 мм" };
        p.Loads.Add(new LoadRow { Name = "1", N = -1500, Mx = 250, Q = 120, LoadFactor = 1.15, SustainedShare = 0.8 });
        p.Loads.Add(new LoadRow { Name = "2", N = -900, Mx = -180, Q = 90, LoadFactor = 1.15, SustainedShare = 0.8 });
        return p;
    }
}
