namespace SectionCalc.Profile;

/// <summary>Required reinforcement of one pile along its length (from the tables), cm² as mm², Asw/sw in cm²/m.</summary>
public sealed record PileDemand(int Pile, PileInfo Info, int Class, IReadOnlyList<Station> Stations, double[] As, double[] Asw, bool[] Exceeded)
{
    public double Length => Info.Head - Info.Tip;
    /// <summary>Largest required area along the pile, mm².</summary>
    public double AsMax => As.Max();
    public double AswMax => Asw.Where(double.IsFinite).DefaultIfEmpty(0).Max();
}

/// <summary>Zone of a reinforcement type: bars and spiral from its top to its bottom; K of the check by the engine.</summary>
public sealed record TypeZone(double Top, double Bottom, BarSet Bars, double? SpiralMm, double? K, string? Governing);

/// <summary>
/// A reinforcement type: piles that share one cage design; the envelope of their demands by the depth below the head.
/// Levels and zones are those of the longest member (the first); a shorter pile takes the cage from its head down to
/// its tip (<see cref="Shift"/> moves a level of the type onto the pile).
/// </summary>
public sealed record PileType(int Number, IReadOnlyList<PileDemand> Members, double[] Levels, double[] As, double[] Asw,
    IReadOnlyList<TypeZone> Zones, double SteelKg, double OwnSteelKg)
{
    /// <summary>Level of the pile = level of the type − shift (the type is drawn at the head of its first member).</summary>
    public double Shift(PileDemand member) => Head - member.Info.Head;
    public double MinLength => Members.Min(m => m.Length);
    public double MaxLength => Members.Max(m => m.Length);
    /// <summary>Kept by the automatic joining as it is (set by the engineer).</summary>
    public bool Locked { get; init; }
    /// <summary>The zones were checked by the engine.</summary>
    public bool Verified => Zones.All(z => z.K is not null || z.Governing is not null);
    /// <summary>Key of the member set: a type with the same piles keeps its checked zones.</summary>
    public string Key => string.Join(",", Members.Select(m => m.Pile).OrderBy(x => x));
    public double Head => Members[0].Info.Head;
    public double Tip => Members[0].Info.Tip;
    public double? K => Zones.Any(z => z.K is null) ? null : Zones.Max(z => z.K);
}

/// <summary>Demands of all piles: computed once, grouped many times (the number of types changes nothing here).</summary>
public sealed class PileDemands
{
    public required ProfileProject Project { get; init; }
    public required ForceProfile Forces { get; init; }
    public required IReadOnlyList<PileDemand> Piles { get; init; }
    /// <summary>Piles of one class have the same length: one table of required areas (the length enters η).</summary>
    public required int Classes { get; init; }
    internal RequiredReinforcement.Prepared[] Prepared { get; init; } = [];
    /// <summary>Points of the tables computed by the engine.</summary>
    public int TablePoints { get; init; }
    public bool ShearChecked { get; init; }
    internal double? SeparateSteelKg { get; set; }
}

public sealed record TypingResult(PileDemands Demands, IReadOnlyList<PileType> Types, string? Note)
{
    public double SteelKg => Types.Sum(t => t.SteelKg);
    /// <summary>Steel of the theoretical required areas of every pile (no rounding to bars).</summary>
    public double OwnSteelKg => Types.Sum(t => t.OwnSteelKg);
    /// <summary>Steel with a cage of its own for every pile, by the same rules of bars and zones.</summary>
    public double SeparateSteelKg { get; init; }
    /// <summary>Steel of the envelopes (no rounding to bars) for every number of types of the automatic joining.</summary>
    public IReadOnlyList<(int Types, double SteelKg)> Curve { get; init; } = [];
    /// <summary>Steel the typing adds over a cage of its own for every pile, share.</summary>
    public double Overspend => SeparateSteelKg > 0 ? SteelKg / SeparateSteelKg - 1 : 0;
}

/// <summary>
/// Every pile of a group designed on its own and the piles gathered into a given number of reinforcement types.
/// <list type="number">
/// <item>The required area of the ring as a function of N and M (and Asw/sw of N and Q) is tabulated by the
/// engine search over the range of the forces of the piles, per class of piles (length) and combination kind:
/// a hundred points instead of a search in every station of every pile.</item>
/// <item>Each pile along its length: the largest area of its РСУ rows from the table.</item>
/// <item>Types gather piles of any length and head: the envelope runs along the depth below the head. With a
/// cage constant along the pile (the usual design, by default) the piles sorted by their largest demand are cut
/// into the given number of groups with the least steel (exact, dynamic programming). With zones along the
/// pile the two types whose joint envelope adds the least steel are joined until the number is reached.</item>
/// <item>Bars of a type: the fewest standard bars for its demand; zones (optional) along the envelope with the
/// same bar count, short zones joined to the stronger neighbour. Each zone is checked by the engine with the
/// governing rows of its piles and strengthened if K &gt; 1.</item>
/// </list>
/// </summary>
public static class PileTyping
{
    /// <summary>Table points along N, M and Q (lowered in tests).</summary>
    public static int GridN = 7, GridM = 12, GridQ = 8;
    /// <summary>Head and tip are equal within this tolerance, m.</summary>
    public const double LevelTolerance = 0.05;
    public const double MinZoneM = 2;
    public static readonly double[] SpiralDiameters = [6, 8, 10, 12, 14, 16, 18, 20, 22, 25, 28, 32];
    const double SteelDensity = 7850;

    sealed record Table(double[] X, double[] Y, double[,] V)
    {
        public double At(double x, double y)
        {
            static (int, double) Cell(double[] a, double v)
            {
                if (a.Length == 1 || v <= a[0]) return (0, 0);
                if (v >= a[^1]) return (a.Length - 2, 1);
                int i = 0;
                while (v > a[i + 1]) i++;
                return (i, (v - a[i]) / (a[i + 1] - a[i]));
            }
            var (i, tx) = Cell(X, x);
            var (j, ty) = Cell(Y, y);
            if (X.Length == 1 && Y.Length == 1) return V[0, 0];
            if (X.Length == 1) return V[0, j] * (1 - ty) + V[0, j + 1] * ty;
            if (Y.Length == 1) return V[i, 0] * (1 - tx) + V[i + 1, 0] * tx;
            return V[i, j] * (1 - tx) * (1 - ty) + V[i + 1, j] * tx * (1 - ty) + V[i, j + 1] * (1 - tx) * ty + V[i + 1, j + 1] * tx * ty;
        }
    }

    static double[] Grid(double lo, double hi, int n)
    {
        if (hi - lo < 1e-6) return [lo];
        return Enumerable.Range(0, n).Select(i => lo + (hi - lo) * i / (n - 1)).ToArray();
    }

    static (bool, bool, bool) Kind(LoadRow r) => (r.ShortTerm, r.Seismic, r.Special);

    static bool Normal(LoadRow r) => Math.Abs(r.N) > 1e-6 || Math.Abs(r.Mx) > 1e-6;

    static LoadRow Row(LoadRow like, double n, double m, double q) => new()
    {
        Name = "таблица", N = n, Mx = m, Q = q, LoadFactor = like.LoadFactor, SustainedShare = like.SustainedShare,
        ShortTerm = like.ShortTerm, Seismic = like.Seismic, Special = like.Special,
    };

    /// <summary>The required reinforcement of every pile (the long part: the tables are computed by the engine).</summary>
    public static PileDemands Demand(ProfileProject project, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        if (project.Section.Kind != SectionKind.Pile) throw new InvalidOperationException("Типизация — для свай");
        // Model elevations: piles of one type have the same head and tip anyway.
        var settings = System.Text.Json.JsonSerializer.Deserialize<ProfileSettings>(System.Text.Json.JsonSerializer.Serialize(project.Settings))!;
        settings.AlignPileHeads = false;
        var p = new ProfileProject { Title = project.Title, Section = project.Section, Source = project.Source, Phases = project.Phases, Settings = settings };
        var forces = ForceProfile.Build(p);
        var piles = forces.Piles;
        // Classes: equal length (it enters η through l0); one table of required areas per class.
        var classOf = new int[piles.Count];
        var classes = new List<PileInfo>();
        for (int i = 0; i < piles.Count; i++)
        {
            int c = classes.FindIndex(x => Math.Abs((x.Head - x.Tip) - (piles[i].Head - piles[i].Tip)) <= LevelTolerance);
            if (c < 0) { classes.Add(piles[i]); c = classes.Count - 1; }
            classOf[i] = c;
        }
        var prepared = classes.Select(c => RequiredReinforcement.Prepare(project, c.Head - c.Tip)).ToArray();
        var stations = Enumerable.Range(0, piles.Count).Select(i => forces.Stations(p, i)).ToArray();
        bool shear = project.Section.Conditions.ShearCheck;

        // Table points per class and combination kind over the range of the rows.
        var jobs = new List<(int Class, (bool, bool, bool) Kind, bool Shear, LoadRow Like, double[] X, double[] Y)>();
        for (int c = 0; c < classes.Count; c++)
        {
            var rows = Enumerable.Range(0, piles.Count).Where(i => classOf[i] == c).SelectMany(i => stations[i]).SelectMany(s => s.Rsu).ToList();
            foreach (var kind in rows.GroupBy(Kind))
            {
                var normal = kind.Where(Normal).ToList();
                if (normal.Count > 0)
                    jobs.Add((c, kind.Key, false, normal[0], Grid(normal.Min(r => r.N), normal.Max(r => r.N), GridN), Grid(0, normal.Max(r => Math.Abs(r.Mx)), GridM)));
                var q = kind.Where(r => r.Q > 0).ToList();
                if (shear && q.Count > 0)
                    jobs.Add((c, kind.Key, true, q[0], Grid(q.Min(r => r.N), q.Max(r => r.N), GridN), Grid(0, q.Max(r => r.Q), GridQ)));
            }
        }
        // Lines of the table along M (Q) one after another: the area grows with M, so each search starts from the
        // previous answer (a few engine runs instead of a dozen).
        var lines = jobs.SelectMany((j, k) => Enumerable.Range(0, j.X.Length).Select(x => (Job: k, x))).ToList();
        int total = jobs.Sum(j => j.X.Length * j.Y.Length);
        var tables = jobs.Select(j => new Table(j.X, j.Y, new double[j.X.Length, j.Y.Length])).ToArray();
        int done = 0;
        Parallel.ForEach(lines, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Environment.ProcessorCount }, line =>
        {
            var job = jobs[line.Job];
            var prep = prepared[job.Class];
            double? previous = null;
            for (int y = 0; y < job.Y.Length; y++)
            {
                double value;
                if (!job.Shear)
                {
                    value = job.Y[y] <= 1e-9 && Math.Abs(job.X[line.x]) <= 1e-9 ? prep.MinArea
                        : RequiredReinforcement.AreaFor(prep, [Row(job.Like, job.X[line.x], job.Y[y], 0)], cancel, previous).Area;
                    previous = value;
                }
                else
                    // Q with the smallest ring: on the safe side for the concrete part.
                    value = job.Y[y] <= 1e-9 ? 0
                        : RequiredReinforcement.AswFor(prep, [Row(job.Like, job.X[line.x], 0, job.Y[y])], prep.MinArea, cancel) ?? double.NaN;
                tables[line.Job].V[line.x, y] = value;
                progress?.Report((Interlocked.Increment(ref done), total));
            }
        });

        double AreaOf(int c, LoadRow r)
        {
            int k = jobs.FindIndex(j => j.Class == c && !j.Shear && j.Kind == Kind(r));
            return k < 0 ? 0 : tables[k].At(r.N, Math.Abs(r.Mx));
        }
        double AswOf(int c, LoadRow r)
        {
            int k = jobs.FindIndex(j => j.Class == c && j.Shear && j.Kind == Kind(r));
            return k < 0 ? 0 : tables[k].At(r.N, r.Q);
        }
        var demands = new List<PileDemand>();
        for (int i = 0; i < piles.Count; i++)
        {
            int c = classOf[i];
            var st = stations[i];
            var prep = prepared[c];
            var a = st.Select(s => Math.Max(prep.MinArea, s.Rsu.Where(Normal).Select(r => AreaOf(c, r)).DefaultIfEmpty(0).Max())).ToArray();
            var w = st.Select(s => shear ? s.Rsu.Where(r => r.Q > 0).Select(r => AswOf(c, r)).DefaultIfEmpty(0).Max() : 0).ToArray();
            demands.Add(new PileDemand(i, piles[i], c, st, a, w, a.Select(x => x >= prep.MaxArea * 0.999).ToArray()));
        }
        return new PileDemands
        {
            Project = project, Forces = forces, Piles = demands, Classes = classes.Count, Prepared = prepared,
            TablePoints = total, ShearChecked = shear,
        };
    }

    static bool IsShear(string governing) => governing.Contains("наклон", StringComparison.OrdinalIgnoreCase)
        || governing.Contains("поперечн", StringComparison.OrdinalIgnoreCase);

    /// <summary>Short name of the governing check for the cages: Q, N,M, трещ., уст.</summary>
    public static string Short(string? governing) => governing is null ? ""
        : IsShear(governing) ? "Q"
        : governing.Contains("трещин", StringComparison.OrdinalIgnoreCase) ? "трещ."
        : governing.Contains("устойч", StringComparison.OrdinalIgnoreCase) ? "уст."
        : "N,M";

    /// <summary>Spiral (pile) diameter for the required Asw/sw, cm²/m: two legs at the adopted pitch.</summary>
    public static double? Spiral(double asw, double pitchMm)
    {
        if (double.IsNaN(asw)) return null;
        foreach (double d in SpiralDiameters)
            if (2 * Math.PI * d * d / 4 / pitchMm * 10 >= asw - 1e-9) return d;
        return null;
    }

    /// <summary>Steel of one pile, kg: the ring along the stations and the spiral.</summary>
    static double Steel(PileDemands d, IReadOnlyList<Station> st, double[] As, double[] asw)
    {
        double spiral = Math.PI * Math.Max(0.1, d.Project.Section.Pile.DiameterMm - 2 * d.Project.Section.Pile.SpiralCoverMm) / 1000;
        double kg = 0;
        for (int s = 0; s < st.Count; s++)
        {
            double dl = st[s].Top - st[s].Bottom;
            double a = As[s] * 1e-6 + (double.IsNaN(asw[s]) ? 0 : asw[s] * 1e-4 * spiral / 2);
            kg += a * dl * SteelDensity;
        }
        return kg;
    }

    /// <summary>
    /// The demands of the members on the stations of the first (longest) one, by the depth below the head; a shorter
    /// pile adds nothing below its tip.
    /// </summary>
    static (double[] As, double[] Asw) Envelope(IReadOnlyList<PileDemand> members)
    {
        var first = members[0];
        var st = first.Stations;
        var a = new double[st.Count];
        var w = new double[st.Count];
        foreach (var m in members)
        {
            double shift = first.Info.Head - m.Info.Head;
            for (int s = 0; s < st.Count; s++)
            {
                double level = st[s].Level - shift;
                if (level < m.Info.Tip - LevelTolerance) continue;
                int k = Nearest(m.Stations, level);
                a[s] = Math.Max(a[s], m.As[k]);
                w[s] = Math.Max(w[s], double.IsNaN(m.Asw[k]) ? double.NaN : m.Asw[k]);
            }
        }
        return (a, w);
    }

    /// <summary>Members with the longest first: the type is drawn along it.</summary>
    static List<PileDemand> Ordered(IEnumerable<PileDemand> members) =>
        members.OrderByDescending(m => m.Length).ThenByDescending(m => m.Info.Head).ThenBy(m => m.Pile).ToList();

    /// <summary>Steel of a group with one cage along every pile: the largest demand of the group over the length of each member, kg.</summary>
    static double UniformCost(PileDemands d, IReadOnlyList<PileDemand> g)
    {
        double ring = Math.PI * Math.Max(0.1, d.Project.Section.Pile.DiameterMm - 2 * d.Project.Section.Pile.SpiralCoverMm) / 1000;
        double a = g.Max(m => m.AsMax) * 1e-6, w = g.Max(m => m.AswMax) * 1e-4 * ring / 2;
        return g.Sum(m => m.Length) * (a + w) * SteelDensity;
    }

    static int Nearest(IReadOnlyList<Station> st, double level)
    {
        int best = 0;
        for (int i = 1; i < st.Count; i++)
            if (Math.Abs(st[i].Level - level) < Math.Abs(st[best].Level - level)) best = i;
        return best;
    }

    /// <summary>
    /// The piles gathered into the given number of types (fast; the zones are not yet checked). Locked groups (pile
    /// indices) stay as they are; the other piles start one per type and the two types whose joint envelope adds the
    /// least steel are joined. The joining runs to the end: the steel of every number of types is the curve.
    /// </summary>
    public static TypingResult Group(PileDemands d, int typeCount, IReadOnlyList<IReadOnlyList<int>>? locked = null)
    {
        locked ??= [];
        if (d.Project.Settings.UniformCage) return GroupUniform(d, typeCount, locked);
        var lockedPiles = locked.SelectMany(g => g).ToHashSet();
        var groups = locked.Select(g => g.Select(i => d.Piles[i]).ToList())
            .Concat(d.Piles.Where(p => !lockedPiles.Contains(p.Pile)).Select(p => new List<PileDemand> { p })).ToList();
        int fixedCount = locked.Count;
        double Cost(List<PileDemand> g)
        {
            var (a, w) = Envelope(g);
            return g.Count * Steel(d, g[0].Stations, a, w);
        }
        var cost = groups.Select(Cost).ToList();
        // Pair costs of joining: never a locked group; piles of any length (the envelope by the depth).
        double Join(int i, int j) => i < fixedCount || j < fixedCount ? double.PositiveInfinity
            : Cost(Ordered([.. groups[i], .. groups[j]])) - cost[i] - cost[j];
        int n = groups.Count;
        var delta = new double[n, n];
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++) delta[i, j] = Join(i, j);
        var alive = Enumerable.Range(0, n).ToHashSet();
        var curve = new List<(int Types, double SteelKg)> { (alive.Count, alive.Sum(i => cost[i])) };
        List<List<PileDemand>>? chosen = alive.Count <= typeCount ? alive.Select(i => groups[i].ToList()).ToList() : null;
        while (true)
        {
            (int I, int J) best = (-1, -1);
            double min = double.PositiveInfinity;
            foreach (int i in alive)
                foreach (int j in alive)
                    if (j > i && delta[i, j] < min) { min = delta[i, j]; best = (i, j); }
            if (best.I < 0) break;
            groups[best.I] = Ordered([.. groups[best.I], .. groups[best.J]]);
            cost[best.I] = Cost(groups[best.I]);
            alive.Remove(best.J);
            foreach (int k in alive)
                if (k != best.I)
                {
                    var (i, j) = k < best.I ? (k, best.I) : (best.I, k);
                    delta[i, j] = Join(i, j);
                }
            curve.Add((alive.Count, alive.Sum(i => cost[i])));
            if (chosen is null && alive.Count <= typeCount) chosen = alive.Select(i => groups[i].ToList()).ToList();
        }
        // Fewer types than possible (different heads and lengths, locked groups): as few as there can be.
        chosen ??= alive.Select(i => groups[i].ToList()).ToList();
        string? note = chosen.Count > typeCount ? $"Меньше {chosen.Count} типов нельзя: закреплённые типы" : null;
        return Finish(d, chosen, locked, note, curve);
    }

    /// <summary>Types numbered from the strongest cage (the largest demand) down; locked groups keep their flag.</summary>
    static TypingResult Finish(PileDemands d, List<List<PileDemand>> chosen, IReadOnlyList<IReadOnlyList<int>> locked, string? note,
        List<(int Types, double SteelKg)> curve)
    {
        var lockedSets = locked.Select(g => g.OrderBy(x => x).ToArray()).ToList();
        var ordered = chosen.OrderByDescending(g => g.Max(m => m.AsMax)).ThenByDescending(g => g.Count).ToList();
        var r = Build(d, ordered.Select(g => (IReadOnlyList<int>)g.Select(m => m.Pile).ToList()).ToList(),
            ordered.Select(g => lockedSets.Any(l => l.SequenceEqual(g.Select(m => m.Pile).OrderBy(x => x)))).ToList());
        return r with { Note = note, Curve = curve };
    }

    /// <summary>
    /// One cage along the pile: the steel of a type is its largest demand times the length of its piles, so the best
    /// groups are runs of the piles sorted by their largest demand. Dynamic programming finds the cheapest cut into
    /// k runs for every k (the curve) exactly; locked groups stay as they are.
    /// </summary>
    static TypingResult GroupUniform(PileDemands d, int typeCount, IReadOnlyList<IReadOnlyList<int>> locked)
    {
        var lockedPiles = locked.SelectMany(g => g).ToHashSet();
        var lockedGroups = locked.Select(g => Ordered(g.Select(i => d.Piles[i]))).ToList();
        double fixedCost = lockedGroups.Sum(g => UniformCost(d, g));
        var free = d.Piles.Where(p => !lockedPiles.Contains(p.Pile)).OrderBy(p => p.AsMax).ThenBy(p => p.AswMax).ToList();
        int n = free.Count;
        // cost[i, j]: piles i..j-1 in one type (the strongest is the last).
        var run = new double[n + 1, n + 1];
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j <= n; j++)
                run[i, j] = UniformCost(d, [.. free.Skip(i).Take(j - i)]);
        // best[k, j]: piles 0..j-1 in k types; from[k, j]: start of the last type.
        var best = new double[n + 1, n + 1];
        var from = new int[n + 1, n + 1];
        for (int k = 0; k <= n; k++) for (int j = 0; j <= n; j++) best[k, j] = double.PositiveInfinity;
        best[0, 0] = 0;
        for (int k = 1; k <= n; k++)
            for (int j = k; j <= n; j++)
                for (int i = k - 1; i < j; i++)
                    if (best[k - 1, i] + run[i, j] < best[k, j]) { best[k, j] = best[k - 1, i] + run[i, j]; from[k, j] = i; }
        var curve = Enumerable.Range(1, n).Select(k => (Types: k + lockedGroups.Count, SteelKg: best[k, n] + fixedCost)).Reverse().ToList();
        int target = Math.Clamp(typeCount - lockedGroups.Count, n == 0 ? 0 : 1, n);
        var chosen = new List<List<PileDemand>>(lockedGroups);
        for (int k = target, j = n; k > 0; k--)
        {
            int i = from[k, j];
            chosen.Add(Ordered(free.Skip(i).Take(j - i)));
            j = i;
        }
        string? note = chosen.Count > typeCount ? $"Меньше {chosen.Count} типов нельзя: закреплённые типы" : null;
        return Finish(d, chosen, locked, note, curve);
    }

    /// <summary>Types of a given partition of the piles (manual editing), numbered in its order; zones not yet checked.</summary>
    public static TypingResult Build(PileDemands d, IReadOnlyList<IReadOnlyList<int>> groups, IReadOnlyList<bool>? locked = null)
    {
        var types = groups.Where(g => g.Count > 0)
            .Select((g, k) => MakeType(d, k + 1, Ordered(g.Select(i => d.Piles[i]))) with { Locked = locked is not null && k < locked.Count && locked[k] })
            .ToList();
        // The reference: every pile a cage of its own, by the same rules (once per demands).
        d.SeparateSteelKg ??= d.Piles.Sum(p => MakeType(d, 0, [p]).SteelKg);
        return new TypingResult(d, types, null) { SeparateSteelKg = d.SeparateSteelKg.Value };
    }

    static PileType MakeType(PileDemands d, int number, List<PileDemand> members)
    {
        var (a, w) = Envelope(members);
        var st = members[0].Stations;
        double pitch = d.Project.Section.Conditions.ShearPitchMm;
        // The fewest standard bars for the largest demand of the type; zones along the pile keep this bar count.
        var top = BarSuggestion.Choose(d.Project.Section, a.Max(), 0).Right;
        int count = top.Count;
        var bars = a.Select(x => Bars(d, count, x)).ToArray();
        var spiral = w.Select(x => d.ShearChecked ? Spiral(x, pitch) : null).ToArray();
        var zones = new List<(int From, int To, BarSet Bars, double? Spiral)>();
        if (d.Project.Settings.UniformCage)
            // One cage along the whole pile: the bars and the spiral of the largest demand.
            zones.Add((0, st.Count - 1, top, spiral.Any(x => x is null) && d.ShearChecked ? null : spiral.Max()));
        else
            for (int s = 0; s < st.Count; s++)
            {
                if (zones.Count > 0 && zones[^1].Bars == bars[s] && zones[^1].Spiral == spiral[s]) zones[^1] = zones[^1] with { To = s };
                else zones.Add((s, s, bars[s], spiral[s]));
            }
        // Short zones join the stronger neighbour (its bars cover both).
        double Len((int From, int To, BarSet, double?) z) => st[z.From].Top - st[z.To].Bottom;
        static double? Max(double? x, double? y) => x is null ? y : y is null ? x : Math.Max(x.Value, y.Value);
        while (zones.Count > 1)
        {
            int i = zones.FindIndex(z => Len(z) < MinZoneM - 1e-9);
            if (i < 0) break;
            int j = i == 0 ? 1 : i == zones.Count - 1 ? i - 1
                : zones[i - 1].Bars.AreaMm2 >= zones[i + 1].Bars.AreaMm2 ? i - 1 : i + 1;
            var (lo, hi) = (Math.Min(i, j), Math.Max(i, j));
            var strong = zones[i].Bars.AreaMm2 >= zones[j].Bars.AreaMm2 ? zones[i].Bars : zones[j].Bars;
            zones[lo] = (zones[lo].From, zones[hi].To, strong, Max(zones[i].Spiral, zones[j].Spiral));
            zones.RemoveAt(hi);
            // Equal neighbours after the join become one zone.
            for (int k = zones.Count - 1; k > 0; k--)
                if (zones[k].Bars == zones[k - 1].Bars && zones[k].Spiral == zones[k - 1].Spiral)
                {
                    zones[k - 1] = zones[k - 1] with { To = zones[k].To };
                    zones.RemoveAt(k);
                }
        }
        var typeZones = zones.Select(z => new TypeZone(st[z.From].Top, st[z.To].Bottom, z.Bars, z.Spiral, null, null)).ToList();
        return new PileType(number, members, st.Select(x => x.Level).ToArray(), a, w, typeZones,
            ZoneSteel(d, members, typeZones), members.Sum(m => Steel(d, m.Stations, m.As, m.Asw)));
    }

    /// <summary>The smallest standard diameter of the given count covering the area; the largest that fits if none does.</summary>
    static BarSet Bars(PileDemands d, int count, double area)
    {
        var probe = ProjectStore.Deserialize(ProjectStore.Serialize(d.Project.Section));
        probe.Pile.BarCount = count;
        BarSet? largest = null;
        foreach (double dia in BarSuggestion.Diameters)
        {
            probe.Pile.BarDiameterMm = dia;
            if (!RequestBuilder.Layout(probe).Fits) break;
            largest = new BarSet(1, count, dia);
            if (count * Math.PI * dia * dia / 4 >= area - 1e-6) return largest;
        }
        return largest ?? new BarSet(1, count, BarSuggestion.Diameters[0]);
    }

    /// <summary>Steel of the type as built: the zone bars and spirals, kg for all its piles (each cut at its own tip).</summary>
    static double ZoneSteel(PileDemands d, IReadOnlyList<PileDemand> members, IReadOnlyList<TypeZone> zones)
    {
        double ring = Math.PI * Math.Max(0.1, d.Project.Section.Pile.DiameterMm - 2 * d.Project.Section.Pile.SpiralCoverMm) / 1000;
        double pitch = d.Project.Section.Conditions.ShearPitchMm / 1000;
        double head = members[0].Info.Head, kg = 0;
        foreach (var m in members)
        {
            double bottom = head - m.Length;
            foreach (var z in zones)
            {
                double dl = Math.Max(0, z.Top - Math.Max(z.Bottom, bottom));
                double spiral = z.SpiralMm is double sd ? Math.PI * sd * sd / 4 * 1e-6 * ring / pitch : 0;
                kg += (z.Bars.AreaMm2 * 1e-6 + spiral) * dl * SteelDensity;
            }
        }
        return kg;
    }

    /// <summary>
    /// Every zone of every type checked by the engine with its bars and spiral for the governing rows of its piles
    /// (the largest table demand); a zone with K &gt; 1 gets stronger bars (or spiral) and is checked again.
    /// </summary>
    public static TypingResult Verify(TypingResult r, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default,
        Func<PileType, bool>? only = null)
    {
        var d = r.Demands;
        var work = r.Types.Where(t => only?.Invoke(t) ?? true).SelectMany(t => t.Zones.Select((z, i) => (Type: t, Index: i))).ToList();
        var zones = r.Types.ToDictionary(t => t.Number, t => t.Zones.ToArray());
        int done = 0;
        Parallel.ForEach(work, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Environment.ProcessorCount }, item =>
        {
            var (type, index) = item;
            var zone = zones[type.Number][index];
            // The longest member: its element length enters η (on the safe side for the shorter ones).
            var prep = d.Prepared[type.Members[0].Class];
            // Rows of the zone: of every member, its stations inside the zone; the strongest by the table demand.
            var rows = new List<(LoadRow Row, double A, double W)>();
            foreach (var m in type.Members)
                for (int s = 0; s < m.Stations.Count; s++)
                {
                    var st = m.Stations[s];
                    double level = st.Level + type.Shift(m);
                    if (level > zone.Top + 1e-6 || level < zone.Bottom - 1e-6) continue;
                    foreach (var row in st.Rsu) rows.Add((row, m.As[s], m.Asw[s]));
                }
            // The table ranks the rows only roughly (it lies on the safe side by interpolation): the strongest by the
            // table, by the moment and by the shear are all checked.
            var chosen = rows.Where(x => Normal(x.Row)).OrderByDescending(x => x.A).ThenByDescending(x => Math.Abs(x.Row.Mx)).Take(8)
                .Concat(rows.Where(x => Normal(x.Row)).OrderByDescending(x => Math.Abs(x.Row.Mx)).Take(3))
                .Concat(rows.Where(x => x.Row.Q > 0).OrderByDescending(x => double.IsNaN(x.W) ? 0 : x.W).ThenByDescending(x => x.Row.Q).Take(d.ShearChecked ? 2 : 0))
                .Select(x => x.Row).Distinct().ToList();
            for (int round = 0; round < 4 && chosen.Count > 0; round++)
            {
                var section = ProjectStore.Deserialize(ProjectStore.Serialize(d.Project.Section));
                section.Conditions.ElementLengthM = prep.Section.Conditions.ElementLengthM;
                section.Pile.BarCount = zone.Bars.Count; section.Pile.BarDiameterMm = zone.Bars.DiameterMm;
                if (zone.SpiralMm is double sd) section.Pile.SpiralDiameterMm = sd;
                var (k, gov) = RequiredReinforcement.KFor(section, chosen, cancel);
                double? before = zone.K;
                zone = zone with { K = k, Governing = gov };
                if (k is double kk && kk <= 1) break;
                // The engine does not converge or nothing helps any more: the section itself does not hold it.
                string limit = gov is not null && IsShear(gov) ? "Q не обеспечивается сечением сваи (пик усилий? диаметр сваи?)"
                    : "сечение сваи не вмещает требуемую арматуру (пик усилий у оголовка? диаметр сваи?)";
                if (k is null || round == 3 || before is double b && k is double now && b - now < 0.01)
                {
                    zone = zone with { Governing = $"{gov} — {limit}" };
                    break;
                }
                // Stronger: the spiral for Q, the bars (same count) otherwise.
                if (IsShear(gov!))
                {
                    if (zone.SpiralMm is double cur && SpiralDiameters.FirstOrDefault(x => x > cur) is double next and > 0)
                        zone = zone with { SpiralMm = next };
                    else { zone = zone with { Governing = $"{gov} — {limit}" }; break; }
                }
                else
                {
                    double target = zone.Bars.AreaMm2 * Math.Pow(Math.Max(k ?? 1.2, 1.02), 1.3) * 1.02;
                    var stronger = Bars(d, zone.Bars.Count, target);
                    // The largest diameter of this count is not enough: more bars.
                    if (stronger.AreaMm2 <= zone.Bars.AreaMm2 + 1e-6) stronger = BarSuggestion.Choose(d.Project.Section, target, 0).Right;
                    if (stronger.AreaMm2 <= zone.Bars.AreaMm2 + 1e-6) { zone = zone with { Governing = $"{gov} — {limit}" }; break; }
                    zone = zone with { Bars = stronger };
                }
            }
            // A cage well below its limit (the tables lie on the safe side) gets thinner bars while the engine passes it.
            if (zone.K is double light && light < 0.85 && zone.Governing is { } g0 && !IsShear(g0) && !g0.Contains('—'))
            {
                for (int step = 0; step < 3; step++)
                {
                    int di = Array.IndexOf(BarSuggestion.Diameters, zone.Bars.DiameterMm);
                    if (di <= 0) break;
                    var thinner = new BarSet(zone.Bars.Rows, zone.Bars.Count, BarSuggestion.Diameters[di - 1]);
                    var section = ProjectStore.Deserialize(ProjectStore.Serialize(d.Project.Section));
                    section.Conditions.ElementLengthM = prep.Section.Conditions.ElementLengthM;
                    section.Pile.BarCount = thinner.Count; section.Pile.BarDiameterMm = thinner.DiameterMm;
                    if (zone.SpiralMm is double sd) section.Pile.SpiralDiameterMm = sd;
                    var (k, gov) = RequiredReinforcement.KFor(section, chosen, cancel);
                    if (k is not double kk || kk > 0.97) break;
                    zone = zone with { Bars = thinner, K = kk, Governing = gov };
                }
            }
            zones[type.Number][index] = zone;
            progress?.Report((Interlocked.Increment(ref done), work.Count));
        });
        var types = r.Types.Select(t => only is not null && !only(t) ? t
            : t with { Zones = zones[t.Number], SteelKg = ZoneSteel(d, t.Members, zones[t.Number]) }).ToList();
        return r with { Types = types };
    }
}
