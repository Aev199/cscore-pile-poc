namespace SectionCalc.Profile;

/// <summary>Bars of one face (wall) or of the ring (pile): rows × count of one diameter.</summary>
public sealed record BarSet(int Rows, int Count, double DiameterMm)
{
    public double AreaMm2 => Rows * Count * Math.PI * DiameterMm * DiameterMm / 4;
    public override string ToString() => (Rows > 1 ? $"{Rows}×" : "") + $"{Count}Ø{DiameterMm:0}";
}

/// <summary>A bar layout for a required area: the section with these bars (for the sketch) and the sets.</summary>
public sealed record BarChoice(SectionProject Section, BarSet Right, BarSet? Left, bool Fits)
{
    public string Text => Left is null ? $"{Right} ({Right.AreaMm2 / 100:0.0} см²)"
        : $"«+» {Right} ({Right.AreaMm2 / 100:0.0} см²), «−» {Left} ({Left.AreaMm2 / 100:0.0} см²)";
}

/// <summary>
/// Turns the required area of a station into bars of the standard diameters, for showing them in the section.
/// Of the layouts that fit with the clear spacing and keep the bar spacing within the limit, those with at most
/// 5 % more steel than the leanest one are taken, and of them the one with the fewest bars. The reinforcement
/// module of Revit makes its own choice; this is an illustration.
/// </summary>
public static class BarSuggestion
{
    public static readonly double[] Diameters = [12, 14, 16, 18, 20, 22, 25, 28, 32, 36, 40];
    /// <summary>Largest spacing of the longitudinal bars, mm.</summary>
    public const double MaxSpacingMm = 400;
    const int PileMinBars = 6;
    const double Excess = 1.05;

    /// <summary>The fewest bars among the layouts within the excess over the leanest one.</summary>
    static BarSet? Pick(List<BarSet> sets)
    {
        if (sets.Count == 0) return null;
        double lean = sets.Min(x => x.AreaMm2);
        return sets.Where(x => x.AreaMm2 <= lean * Excess + 1e-6).OrderBy(x => x.Rows * x.Count).ThenBy(x => x.AreaMm2).First();
    }

    public static BarChoice Choose(SectionProject section, double right, double left)
    {
        var s = ProjectStore.Deserialize(ProjectStore.Serialize(section));
        if (s.Kind == SectionKind.Pile)
        {
            var g = s.Pile;
            var sets = new List<BarSet>();
            foreach (double d in Diameters)
            {
                g.BarDiameterMm = d;
                double ring = Math.PI * (g.DiameterMm - 2 * g.AxisDistanceMm);
                int n = Math.Max(Math.Max(PileMinBars, (int)Math.Ceiling(ring / MaxSpacingMm)), (int)Math.Ceiling(right / (Math.PI * d * d / 4) - 1e-9));
                g.BarCount = n;
                if (RequestBuilder.Layout(s).Fits) sets.Add(new BarSet(1, n, d));
            }
            var best = Pick(sets) ?? new BarSet(1, PileMinBars, Diameters[^1]);
            g.BarCount = best.Count; g.BarDiameterMm = best.DiameterMm;
            return new BarChoice(s, best, null, RequestBuilder.Layout(s).Fits);
        }
        var w = s.Wall;
        BarSet Face(WallFace face, double area)
        {
            var sets = new List<BarSet>();
            foreach (double d in Diameters)
            {
                face.BarDiameterMm = d;
                double cage = w.WidthMm - 2 * (w.StirrupCoverMm(d) + w.StirrupDiameterMm + d / 2);
                int n = Math.Max(Math.Max(2, (int)Math.Ceiling(cage / MaxSpacingMm) + 1),
                    (int)Math.Ceiling(area / (face.Rows * Math.PI * d * d / 4) - 1e-9));
                face.BarCount = n;
                if (RequestBuilder.Layout(s).Fits) sets.Add(new BarSet(face.Rows, n, d));
            }
            var best = Pick(sets) ?? new BarSet(face.Rows, 2, Diameters[^1]);
            face.BarCount = best.Count; face.BarDiameterMm = best.DiameterMm;
            return best;
        }
        // Each face on its own, with the other at its smallest bars; then both together.
        w.NegativeFace.BarCount = 2; w.NegativeFace.BarDiameterMm = Diameters[0];
        var r = Face(w.PositiveFace, right);
        var l = Face(w.NegativeFace, left);
        return new BarChoice(s, r, l, RequestBuilder.Layout(s).Fits);
    }
}
