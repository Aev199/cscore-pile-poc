namespace SectionCalc;

/// <summary>SP 63.13330.2018 tables 6.7, 6.8, 6.11 (heavy concrete), MPa.</summary>
public sealed record ConcreteClass(string Name, double Rb, double Rbt, double RbSer, double RbtSer, double Eb);

/// <summary>
/// SP 63.13330.2018 with amendment 1, tables 6.13, 6.14, 6.15, MPa. Rsc is the compression resistance,
/// RscShort the value in brackets of table 6.14 (short-term action only). CScore builds the bar diagram
/// symmetric by Rs (MaterialChars.D2L: "Rsc only for the formula checks"), so Rsc stays reference data
/// for the cross-check (tools/crosscheck) until the engine takes it.
/// </summary>
public sealed record RebarClass(string Name, double Rs, double Rsc, double RscShort, double RsSer, double Rsw, double Es);

/// <summary>Resistances passed to CScore for one kind of combination, MPa.</summary>
public sealed record MaterialSet(string Key, string Label, double Rb, double Rbt, double Eb, double Rs, double Es, double Rsw)
{
    internal Dictionary<string, object> Engine() => MaterialTables.Engine(Rb, Rbt, Eb, Rs, Es);
}

/// <summary>What a combination does to the material resistances.</summary>
public sealed record CombinationKind(bool ShortTerm, bool Seismic, bool Special)
{
    public static CombinationKind Of(LoadRow r) => new(r.ShortTerm, r.Seismic, r.Special && !r.Seismic);
}

public static class MaterialTables
{
    /// <summary>ARBAT takes the normative transverse resistance as Rsw multiplied back by γs = 1.15.</summary>
    public const double TransverseNormativeFactor = 1.15;

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
        new("A240", 210, 210, 210, 240, 170, 200000),
        new("A400", 340, 340, 340, 390, 280, 200000),
        new("A500", 435, 435, 400, 500, 300, 200000),
    ];

    public static ConcreteClass Concrete(string name) =>
        Concretes.FirstOrDefault(c => c.Name == name) ?? throw new ArgumentException($"Неизвестный класс бетона {name}");

    public static RebarClass Rebar(string name) =>
        Rebars.FirstOrDefault(r => r.Name == name) ?? throw new ArgumentException($"Неизвестный класс арматуры {name}");

    /// <summary>
    /// Design resistances for one combination kind. γb1 → Rb, Rbt; γb2, γb3, γb5 → Rb (SP 63 6.1.12);
    /// hardening factor → Eb; γs → Rs, Rsw. Seismic: m_tr (SP 14 5.15) to all resistances,
    /// <paramref name="inclined"/> selects the factor for inclined sections. Special: normative
    /// properties and the SP 296 factors; no γ of SP 63.
    /// </summary>
    public static MaterialSet Uls(SectionProject p, CombinationKind kind, bool inclined = false)
    {
        var c = Concrete(p.ConcreteClass); var s = Rebar(p.RebarClass); var t = Rebar(p.TransverseClass); var f = p.Concrete;
        double eb = c.Eb * f.HardeningFactor;
        if (kind.Special)
        {
            var sp = p.Special;
            bool n = sp.NormativeProperties;
            double gb1 = n ? 1 : kind.ShortTerm ? 1 : f.Gb1, gb = n ? 1 : f.Gb2 * f.Gb3 * f.Gb5;
            double rb = (n ? c.RbSer : c.Rb * gb1 * gb) * sp.ConcreteFactor * sp.AdditionalFactor;
            double rbt = (n ? c.RbtSer : c.Rbt * gb1) * sp.ConcreteFactor * sp.AdditionalFactor;
            double rs = (n ? s.RsSer : s.Rs * p.LongitudinalFactor) * sp.LongitudinalFactor * sp.AdditionalFactor;
            double rsw = (n ? t.Rsw * TransverseNormativeFactor : t.Rsw * p.TransverseFactor) * sp.TransverseFactor * sp.AdditionalFactor;
            return new("special", n ? "особое: нормативные характеристики" : "особое: расчётные характеристики", rb, rbt, eb, rs, s.Es, rsw);
        }
        double g1 = kind.ShortTerm ? 1 : f.Gb1;
        double m = kind.Seismic ? (inclined ? p.Seismic.InclinedSectionFactor : p.Seismic.NormalSectionFactor) : 1;
        string key = (kind.ShortTerm ? "short" : "normal") + (kind.Seismic ? inclined ? "_seismic_q" : "_seismic" : "");
        var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        string label = (kind.ShortTerm ? "кратковременное (γb1 = 1)" : "γb1 = " + g1.ToString("0.##", ru))
            + (kind.Seismic ? ", сейсмика m_tr = " + m.ToString("0.##", ru) : "");
        return new(key, label, c.Rb * g1 * f.Gb2 * f.Gb3 * f.Gb5 * m, c.Rbt * g1 * m, eb, s.Rs * p.LongitudinalFactor * m, s.Es,
            t.Rsw * p.TransverseFactor * m);
    }

    /// <summary>Normative (SLS) characteristics: Rb,ser, Rbt,ser, Rs,ser; γ factors of the first group do not apply.</summary>
    public static MaterialSet Sls(SectionProject p)
    {
        var c = Concrete(p.ConcreteClass); var s = Rebar(p.RebarClass);
        return new("sls", "нормативные", c.RbSer, c.RbtSer, c.Eb * p.Concrete.HardeningFactor, s.RsSer, s.Es, 0);
    }

    internal static Dictionary<string, object> Engine(double rb, double rbt, double eb, double rs, double es) => new()
    {
        ["concrete_rb_kpa"] = rb * 1000, ["concrete_rbt_kpa"] = rbt * 1000, ["concrete_e_kpa"] = eb * 1000,
        ["concrete_ec1"] = -0.0015, ["concrete_ec2"] = -0.0035, ["concrete_et1"] = 0.00008, ["concrete_et2"] = 0.00015,
        ["rebar_rs_kpa"] = rs * 1000, ["rebar_e_kpa"] = es * 1000, ["rebar_ec2"] = -0.025, ["rebar_et2"] = 0.025,
    };
}
