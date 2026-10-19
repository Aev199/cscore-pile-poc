namespace SectionCalc;

/// <summary>SP 63.13330.2018 tables 6.7, 6.8, 6.11 (heavy concrete), MPa.</summary>
public sealed record ConcreteClass(string Name, double Rb, double Rbt, double RbSer, double RbtSer, double Eb);

/// <summary>SP 63.13330.2018 tables 6.13, 6.14, 6.15, MPa.</summary>
public sealed record RebarClass(string Name, double Rs, double RsSer, double Rsw, double Es);

public static class MaterialTables
{
    public static readonly IReadOnlyList<ConcreteClass> Concretes =
    [
        new("B15", 8.5, 0.75, 11.0, 1.10, 24000),
        new("B20", 11.5, 0.90, 15.0, 1.35, 27500),
        new("B25", 14.5, 1.05, 18.5, 1.55, 30000),
        new("B30", 17.0, 1.15, 22.0, 1.75, 32500),
        new("B35", 19.5, 1.30, 25.5, 1.95, 34500),
        new("B40", 22.0, 1.40, 29.0, 2.10, 36000),
        new("B45", 25.0, 1.50, 32.0, 2.25, 37000),
        new("B50", 27.5, 1.60, 36.0, 2.45, 38000),
        new("B55", 30.0, 1.70, 39.5, 2.60, 39000),
        new("B60", 33.0, 1.80, 43.0, 2.75, 39500),
    ];

    public static readonly IReadOnlyList<RebarClass> Rebars =
    [
        new("A240", 210, 240, 170, 200000),
        new("A400", 350, 400, 280, 200000),
        new("A500", 435, 500, 300, 200000),
    ];

    public static ConcreteClass Concrete(string name) =>
        Concretes.FirstOrDefault(c => c.Name == name) ?? throw new ArgumentException($"Неизвестный класс бетона {name}");

    public static RebarClass Rebar(string name) =>
        Rebars.FirstOrDefault(r => r.Name == name) ?? throw new ArgumentException($"Неизвестный класс арматуры {name}");

    /// <summary>Design (ULS) characteristics in the engine's kPa contract; two-linear diagrams of SP 63 6.1.20–6.1.22.</summary>
    internal static Dictionary<string, object> UlsMaterials(SectionProject p)
    {
        var c = Concrete(p.ConcreteClass); var s = Rebar(p.RebarClass);
        double gb1 = p.SustainedLoadFactor ? 0.9 : 1.0;
        return Engine(c.Rb * gb1, c.Rbt * gb1, c.Eb, s.Rs, s.Es);
    }

    /// <summary>Normative (SLS) characteristics: Rb,ser, Rbt,ser, Rs,ser.</summary>
    internal static Dictionary<string, object> SlsMaterials(SectionProject p)
    {
        var c = Concrete(p.ConcreteClass); var s = Rebar(p.RebarClass);
        return Engine(c.RbSer, c.RbtSer, c.Eb, s.RsSer, s.Es);
    }

    static Dictionary<string, object> Engine(double rb, double rbt, double eb, double rs, double es) => new()
    {
        ["concrete_rb_kpa"] = rb * 1000, ["concrete_rbt_kpa"] = rbt * 1000, ["concrete_e_kpa"] = eb * 1000,
        ["concrete_ec1"] = -0.0015, ["concrete_ec2"] = -0.0035, ["concrete_et1"] = 0.00008, ["concrete_et2"] = 0.00015,
        ["rebar_rs_kpa"] = rs * 1000, ["rebar_e_kpa"] = es * 1000, ["rebar_ec2"] = -0.025, ["rebar_et2"] = 0.025,
    };
}
