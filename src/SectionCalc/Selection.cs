namespace SectionCalc;

public sealed record SelectionCandidate(string Description, double SteelAreaMm2, SectionProject Project);

public sealed record SelectionOutcome(SelectionCandidate Candidate, Verdict Verdict, CheckRow? Governing, string? Error);

public sealed record SelectionResult(IReadOnlyList<SelectionOutcome> Outcomes, int GeometricallyRejected, int NotChecked)
{
    public SelectionOutcome? Best => Outcomes.FirstOrDefault(o => o.Verdict == Verdict.Passed);
}

/// <summary>
/// Discrete reinforcement selection: geometric candidates in ascending steel area are checked by
/// the same full CScore check as a manual run; the first fully passed one is the lightest.
/// </summary>
public static class Selection
{
    public const int AlternativesAfterBest = 2;

    public static List<SelectionCandidate> Candidates(SectionProject p, out int rejected)
    {
        var s = p.Selection;
        var list = new List<SelectionCandidate>();
        int skipped = 0;
        var diameters = s.Diameters.Where(d => d > 0).Distinct().OrderBy(d => d).ToList();
        if (p.Kind == SectionKind.Pile)
        {
            for (int n = Math.Max(6, s.PileCountMin); n <= s.PileCountMax; n += Math.Max(1, s.PileCountStep))
                foreach (var d in diameters)
                {
                    var c = Clone(p); c.Pile.BarCount = n; c.Pile.BarDiameterMm = d;
                    Add(c, $"{n} Ø{d:0}");
                }
        }
        else if (p.Kind == SectionKind.Custom)
        {
            // Bar positions stay; the contour arrangement also tries the spacings along the contour.
            var spacings = p.Custom.Arrangement == BarArrangement.Contour
                ? s.CustomSpacings.Where(x => x > 0).Distinct().OrderBy(x => x).ToList() : [0.0];
            foreach (var spacing in spacings)
                foreach (var d in diameters)
                {
                    var c = Clone(p);
                    if (c.Custom.Arrangement == BarArrangement.Contour) { c.Custom.BarDiameterMm = d; c.Custom.MaxSpacingMm = spacing; }
                    else foreach (var b in c.Custom.Bars) b.DiameterMm = d;
                    int n = c.Custom.EffectiveBars().Count;
                    Add(c, spacing > 0 ? $"{n} Ø{d:0}, шаг ≤ {spacing:0}" : $"{n} Ø{d:0}");
                }
        }
        else
        {
            var faces = (from n in Enumerable.Range(Math.Max(2, s.WallCountMin), Math.Max(0, s.WallCountMax - Math.Max(2, s.WallCountMin) + 1))
                         from d in diameters select (n, d)).ToList();
            foreach (var pos in faces)
                foreach (var neg in s.WallSymmetric ? [pos] : faces)
                {
                    var c = Clone(p);
                    c.Wall.PositiveFace = new WallFace { BarCount = pos.n, BarDiameterMm = pos.d };
                    c.Wall.NegativeFace = new WallFace { BarCount = neg.n, BarDiameterMm = neg.d };
                    Add(c, s.WallSymmetric ? $"{pos.n} Ø{pos.d:0} на каждой грани" : $"«+» {pos.n} Ø{pos.d:0}, «−» {neg.n} Ø{neg.d:0}");
                }
        }
        rejected = skipped;
        return list.OrderBy(c => c.SteelAreaMm2).ThenBy(c => c.Description).ToList();

        void Add(SectionProject c, string label)
        {
            var layout = RequestBuilder.Layout(c);
            if (!layout.Fits) { skipped++; return; }
            list.Add(new SelectionCandidate(label, layout.SteelAreaMm2, c));
        }
    }

    public static SelectionResult Run(SectionProject p, IProgress<(int Done, int Total, string Label)>? progress = null,
        CancellationToken cancel = default)
    {
        var problems = RequestBuilder.Validate(WithAdmissibleLayout(p));
        if (problems.Count > 0) throw new InvalidOperationException(string.Join("\n", problems));
        var candidates = Candidates(p, out int rejected);
        var outcomes = new List<SelectionOutcome>();
        int passed = 0;
        foreach (var c in candidates)
        {
            if (passed > AlternativesAfterBest) break;
            cancel.ThrowIfCancellationRequested();
            progress?.Report((outcomes.Count, candidates.Count, c.Description));
            try
            {
                var r = Engine.Check(c.Project, cancel);
                outcomes.Add(new SelectionOutcome(c, r.Verdict, r.Governing, null));
                if (r.Verdict == Verdict.Passed) passed++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { outcomes.Add(new SelectionOutcome(c, Verdict.Incomplete, null, ex.Message)); }
        }
        progress?.Report((outcomes.Count, candidates.Count, ""));
        return new SelectionResult(outcomes, rejected, candidates.Count - outcomes.Count);
    }

    /// <summary>The adopted bars may not fit; the selection replaces them, so validate other inputs only.</summary>
    static SectionProject WithAdmissibleLayout(SectionProject p)
    {
        var c = Clone(p);
        if (c.Kind == SectionKind.Pile) { c.Pile.BarCount = 6; c.Pile.BarDiameterMm = 12; }
        else if (c.Kind == SectionKind.Custom)
        {
            // Positions stay (table or contour), only the diameters change: check them with thin bars.
            c.Custom.BarDiameterMm = 1; c.Custom.MinClearSpacingMm = 0;
            foreach (var b in c.Custom.Bars) b.DiameterMm = 1;
        }
        else { c.Wall.PositiveFace = new WallFace { BarCount = 2, BarDiameterMm = 12 }; c.Wall.NegativeFace = new WallFace { BarCount = 2, BarDiameterMm = 12 }; }
        return c;
    }

    public static SectionProject Clone(SectionProject p) => ProjectStore.Deserialize(ProjectStore.Serialize(p));
}
