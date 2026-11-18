using System.Text.Json;

namespace SectionCalc.Profile;

/// <summary>
/// Simultaneous states that cannot govern the check are dropped before the engine runs.
/// Within one kind of combination (materials, γf, sustained share, My, side of Mx: M = 0 goes with M &gt; 0) a state B is dropped
/// when there are other states A1 with N(A1) ≤ N(B) and A2 with N(A2) ≥ N(B), both with |Mx| ≥ |Mx(B)|
/// and Q ≥ Q(B). That is exact if, for the given section, the utilization grows with |M| and Q at fixed N
/// and, at fixed |M| and Q, has no interior maximum in N (the N–M domain is convex; Q and crack width are
/// monotone or unimodal in N). Then the largest K over the kept states is the largest K over all states.
/// Only symmetric sections are reduced (a round pile, a wall with equal faces): with unequal faces the
/// smallest K at a fixed N is not at M = 0, and only exact duplicates are dropped.
/// </summary>
public static class StateReduction
{
    public static bool Reducible(ProfileProject p)
    {
        if (p.Settings.ExhaustiveStates) return false;
        if (p.Section.Kind == SectionKind.Pile) return true;
        var w = p.Section.Wall;
        return JsonSerializer.Serialize(w.PositiveFace) == JsonSerializer.Serialize(w.NegativeFace);
    }

    public static List<LoadRow> Reduce(IEnumerable<LoadRow> rows, bool dominance)
    {
        var distinct = rows.DistinctBy(l => (l.N, l.Mx, l.My, l.Q, l.LoadFactor, l.SustainedShare, l.ShortTerm, l.Seismic, l.Special)).ToList();
        if (!dominance || distinct.Count < 3) return distinct;
        var kept = new List<LoadRow>();
        foreach (var group in distinct.GroupBy(l => (l.LoadFactor, l.SustainedShare, l.ShortTerm, l.Seismic, l.Special, l.My, l.Mx < 0)))
            kept.AddRange(Keep(group.ToList()));
        // The input order (by depth, then phase) is kept for the reports.
        var set = kept.ToHashSet(ReferenceEqualityComparer.Instance);
        return distinct.Where(set.Contains).ToList();
    }

    /// <summary>The states of one group not sandwiched in N between two states with larger or equal |M| and Q.</summary>
    static IEnumerable<LoadRow> Keep(List<LoadRow> g)
    {
        int n = g.Count;
        var m = g.Select(l => Math.Abs(l.Mx)).ToArray();
        var q = g.Select(l => l.Q).ToArray();
        var order = Enumerable.Range(0, n).OrderBy(i => g[i].N).ToArray();
        var left = new bool[n];
        var right = new bool[n];
        Sweep(order, left);
        Sweep(order.Reverse().ToArray(), right);
        for (int i = 0; i < n; i++)
            if (!(left[i] && right[i])) yield return g[i];

        // dominated[i]: some state at the same N or before it in the sweep has |M| ≥ and Q ≥ (and is not the same point).
        void Sweep(int[] seq, bool[] dominated)
        {
            var stairs = new Staircase();
            for (int k = 0; k < seq.Length;)
            {
                int end = k;
                while (end < seq.Length && g[seq[end]].N == g[seq[k]].N) end++;
                // Same N: a state covered by another of the same N needs no other side.
                for (int a = k; a < end; a++)
                {
                    int i = seq[a];
                    bool covered = false;
                    for (int b = k; b < end && !covered; b++)
                        covered = b != a && m[seq[b]] >= m[i] && q[seq[b]] >= q[i];
                    if (covered) { left[i] = right[i] = true; continue; }
                    dominated[i] |= stairs.Covers(m[i], q[i]);
                }
                for (int a = k; a < end; a++) stairs.Add(m[seq[a]], q[seq[a]]);
                k = end;
            }
        }
    }

    /// <summary>Maximal points in (m, q): m strictly decreasing, q strictly increasing along the list.</summary>
    sealed class Staircase
    {
        readonly List<(double M, double Q)> points = [];

        /// <summary>Some point has m ≥ mi and q ≥ qi.</summary>
        public bool Covers(double mi, double qi)
        {
            // The points with m ≥ mi are a prefix; the largest q among them is its last one.
            int lo = 0, hi = points.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (points[mid].M >= mi) lo = mid + 1; else hi = mid;
            }
            return lo > 0 && points[lo - 1].Q >= qi;
        }

        public void Add(double mi, double qi)
        {
            if (Covers(mi, qi)) return;
            points.RemoveAll(p => p.M <= mi && p.Q <= qi);
            int at = points.FindIndex(p => p.M < mi);
            points.Insert(at < 0 ? points.Count : at, (mi, qi));
        }
    }
}
