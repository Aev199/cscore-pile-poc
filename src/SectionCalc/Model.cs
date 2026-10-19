using System.Text.Json.Serialization;

namespace SectionCalc;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SectionKind { Pile, Wall }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoadBasis { Uls, Sls }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecondOrderMode { SectionOnly, InLoads, Cscore }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EccentricityMode { InLoads, Sp63 }

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

/// <summary>One force combination. CScore sign convention: N &lt; 0 is compression.</summary>
public sealed class LoadRow
{
    public string Name { get; set; } = "";
    public LoadBasis Basis { get; set; } = LoadBasis.Uls;
    public double N { get; set; }
    public double Mx { get; set; }
    public double My { get; set; }
    public double Q { get; set; }
    /// <summary>SLS only: sustained parts; null means equal to the total value.</summary>
    public double? NLong { get; set; }
    public double? MxLong { get; set; }
    public double? MyLong { get; set; }
}

public sealed class Conditions
{
    public EccentricityMode Eccentricity { get; set; } = EccentricityMode.Sp63;
    /// <summary>Actual member length for the accidental eccentricity.</summary>
    public double MemberLengthM { get; set; } = 6;
    public bool StaticallyDeterminate { get; set; }

    public SecondOrderMode SecondOrder { get; set; } = SecondOrderMode.Cscore;
    public double L0xM { get; set; } = 6;
    public double L0yM { get; set; } = 6;
    /// <summary>Share of the sustained moment for eta (0…1).</summary>
    public double SustainedShare { get; set; } = 1;

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
    public int Version { get; set; } = 1;
    public string Title { get; set; } = "Новое сечение";
    public SectionKind Kind { get; set; } = SectionKind.Pile;
    public PileGeometry Pile { get; set; } = new();
    public WallGeometry Wall { get; set; } = new();
    public string ConcreteClass { get; set; } = "B30";
    public string RebarClass { get; set; } = "A500";
    public string TransverseClass { get; set; } = "A240";
    /// <summary>γb1 = 0,9 for sustained loads applied to Rb and Rbt.</summary>
    public bool SustainedLoadFactor { get; set; } = true;
    public List<LoadRow> Loads { get; set; } = [];
    public Conditions Conditions { get; set; } = new();
    public SelectionSettings Selection { get; set; } = new();

    public static SectionProject Example(SectionKind kind)
    {
        var p = new SectionProject { Kind = kind, Title = kind == SectionKind.Pile ? "БНС Ø800" : "СВГ 600 мм" };
        p.Loads.Add(new LoadRow { Name = "Расчётное 1", Basis = LoadBasis.Uls, N = -1500, Mx = 250, Q = 120 });
        p.Loads.Add(new LoadRow { Name = "Расчётное 2", Basis = LoadBasis.Uls, N = -900, Mx = -180, Q = 90 });
        p.Loads.Add(new LoadRow { Name = "Нормативное", Basis = LoadBasis.Sls, N = -1200, Mx = 190, MxLong = 150 });
        return p;
    }
}
