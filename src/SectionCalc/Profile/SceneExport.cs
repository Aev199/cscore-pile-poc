using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SectionCalc.Profile;

/// <summary>
/// Capacity of a zone of a type: ultimate moment and shear for the least favourable axial force N of its piles,
/// ultimate compression and tension; Qult is null when Q is not checked (no Q check or model not confirmed).
/// </summary>
public sealed record ZoneCapacity(double N, double Mult, double? Qult = null, double NultCompression = 0, double NultTension = 0);

/// <summary>
/// 3D view of the piles in a browser window (three.js inside the page, no internet needed): the piles at their
/// places and levels, diagrams of N, M, Q and of the reinforcement along each pile, and the material diagram —
/// the ultimate moment of the cage zones against the design moment.
/// </summary>
public static class SceneExport
{
    static double R(double v, int digits = 2) => Math.Round(v, digits);

    /// <summary>
    /// The largest value t of a load ray row(t) the section takes: the given checks reach K = 1 (bisection, the engine
    /// on the coarse mesh). 0 if even t = 1 fails; NaN if the checks are not evaluated.
    /// </summary>
    static double Limit(SectionProject section, Func<double, LoadRow> row, string[] checks, bool shear, CancellationToken cancel)
    {
        double K(double t)
        {
            var q = ProjectStore.Deserialize(ProjectStore.Serialize(section));
            q.Conditions.ShearCheck = shear;
            q.Conditions.CoarseMesh = true;
            var r = row(t);
            r.Name = "предел";
            q.Loads = [r];
            return Engine.Check(q, cancel).Rows.Where(x => checks.Contains(x.CheckId) && x.Utilization is not null)
                .Select(x => x.Utilization!.Value).DefaultIfEmpty(double.NaN).Max();
        }
        double first = K(1);
        if (double.IsNaN(first)) return double.NaN;
        if (first > 1) return 0;
        double lo = 1, hi = 400;
        while (K(hi) <= 1 && hi < 1e6) { lo = hi; hi *= 2; }
        for (int i = 0; i < 8; i++)
        {
            double mid = (lo + hi) / 2;
            if (K(mid) <= 1) lo = mid; else hi = mid;
        }
        return lo;
    }

    /// <summary>
    /// Ultimate moment Mult of a section with the given axial force, kN·m: the strength of the normal section (without
    /// the stability factor) reaches K = 1.
    /// </summary>
    public static double Mult(SectionProject section, double n, CancellationToken cancel = default) =>
        Limit(section, m => new LoadRow { N = n, Mx = m }, ["normal_strength"], false, cancel);

    /// <summary>Ultimate shear of the inclined section and strip with the given N, kN; null if Q is not checked.</summary>
    public static double? Qult(SectionProject section, double n, CancellationToken cancel = default)
    {
        if (!section.Conditions.ShearCheck) return null;
        double q = Limit(section, v => new LoadRow { N = n, Q = v }, ["shear_inclined", "shear_strip"], true, cancel);
        return double.IsNaN(q) ? null : q;
    }

    /// <summary>Ultimate axial compression and tension (with the accidental eccentricity of the engine), kN.</summary>
    public static (double Compression, double Tension) Nult(SectionProject section, CancellationToken cancel = default) =>
        (Limit(section, c => new LoadRow { N = -c }, ["normal_strength"], false, cancel),
         Limit(section, t => new LoadRow { N = t }, ["normal_strength"], false, cancel));

    /// <summary>Mult of every zone of every type: N is the least compression (or the largest tension) of its piles in the zone.</summary>
    public static Dictionary<(int Type, int Zone), ZoneCapacity> Capacities(TypingResult r, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        var d = r.Demands;
        var work = r.Types.SelectMany(t => t.Zones.Select((z, i) => (Type: t, Index: i, Zone: z))).ToList();
        var result = new System.Collections.Concurrent.ConcurrentDictionary<(int, int), ZoneCapacity>();
        int done = 0;
        Parallel.ForEach(work, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Environment.ProcessorCount }, w =>
        {
            var members = w.Type.Members.Select(m => m.Pile).ToHashSet();
            // Levels of each pile moved onto the type (piles of one type may have different heads).
            var shift = w.Type.Members.ToDictionary(m => m.Pile, m => w.Type.Shift(m));
            var ns = d.Forces.Samples.Where(x => members.Contains(x.Pile) && x.Level + shift[x.Pile] <= w.Zone.Top + 1e-6
                && x.Level + shift[x.Pile] >= w.Zone.Bottom - 1e-6).Select(x => x.N).ToList();
            double n = d.Project.Settings.IgnoreAxialForce || ns.Count == 0 ? 0 : ns.Max();
            var section = ProjectStore.Deserialize(ProjectStore.Serialize(d.Project.Section));
            section.Conditions.ElementLengthM = w.Type.Head - w.Type.Tip;
            section.Pile.BarCount = w.Zone.Bars.Count; section.Pile.BarDiameterMm = w.Zone.Bars.DiameterMm;
            if (w.Zone.SpiralMm is double sd) section.Pile.SpiralDiameterMm = sd;
            var (nc, nt) = Nult(section, cancel);
            result[(w.Type.Number, w.Index)] = new ZoneCapacity(n, Mult(section, n, cancel), Qult(section, n, cancel), nc, nt);
            progress?.Report((Interlocked.Increment(ref done), work.Count));
        });
        return new Dictionary<(int, int), ZoneCapacity>(result);
    }

    static string Hex(int type)
    {
        var c = TypingChart.Color(type);
        return $"#{c.Red:x2}{c.Green:x2}{c.Blue:x2}";
    }

    /// <summary>Data of the scene: piles with diagrams along them, types with zones (when typed).</summary>
    public static JsonObject Data(ProfileProject p, TypingResult? r, IReadOnlyDictionary<(int Type, int Zone), ZoneCapacity>? capacities)
    {
        ForceProfile forces;
        if (r is not null) forces = r.Demands.Forces;
        else
        {
            // Model elevations: every pile at its own level.
            var settings = JsonSerializer.Deserialize<ProfileSettings>(JsonSerializer.Serialize(p.Settings))!;
            settings.AlignPileHeads = false;
            forces = ForceProfile.Build(new ProfileProject { Section = p.Section, Source = p.Source, Phases = p.Phases, Settings = settings });
        }
        var typeOf = r?.Types.SelectMany(t => t.Members.Select(m => (m.Pile, t))).ToDictionary(x => x.Pile, x => x.t) ?? [];
        var demandOf = r?.Demands.Piles.ToDictionary(x => x.Pile) ?? [];
        double diameter = p.Section.Kind == SectionKind.Pile ? p.Section.Pile.DiameterMm / 1000 : p.Section.Wall.ThicknessMm / 1000;
        var piles = new JsonArray();
        for (int i = 0; i < forces.Piles.Count; i++)
        {
            var info = forces.Piles[i];
            var type = typeOf.GetValueOrDefault(i);
            var demand = demandOf.GetValueOrDefault(i);
            // Level of the type = level of this pile + shift.
            double shift = type is not null && demand is not null ? type.Shift(demand) : 0;
            var stations = new JsonArray();
            foreach (var e in forces.EnvelopeOfPile(i))
            {
                var o = new JsonObject
                {
                    ["z"] = R(e.Level, 3), ["nmin"] = R(e.NMin, 1), ["nmax"] = R(e.NMax, 1),
                    ["m"] = R(Math.Max(Math.Abs(e.MMin), Math.Abs(e.MMax)), 1), ["q"] = R(e.QAbsMax, 1),
                };
                if (demand is not null)
                {
                    int k = Containing(demand.Stations, e.Level);
                    o["as"] = R(demand.As[k] / 100, 2);
                    o["asw"] = double.IsFinite(demand.Asw[k]) ? R(demand.Asw[k], 2) : null;
                }
                if (type is not null)
                {
                    int zi = type.Zones.ToList().FindIndex(z => e.Level + shift <= z.Top + 1e-6 && e.Level + shift >= z.Bottom - 1e-6);
                    if (zi >= 0)
                    {
                        o["asp"] = R(type.Zones[zi].Bars.AreaMm2 / 100, 2);
                        if (capacities?.TryGetValue((type.Number, zi), out var cap) == true)
                        {
                            o["mult"] = R(cap.Mult, 1);
                            o["qult"] = cap.Qult is double qu ? R(qu, 1) : null;
                            o["nultc"] = R(cap.NultCompression, 0);
                            o["nultt"] = R(cap.NultTension, 0);
                        }
                    }
                }
                stations.Add(o);
            }
            piles.Add(new JsonObject
            {
                ["name"] = info.Name, ["x"] = R(info.X, 3), ["y"] = R(info.Y, 3), ["head"] = R(info.Head, 3), ["tip"] = R(info.Tip, 3),
                ["type"] = type?.Number, ["color"] = type is null ? "#9aa7b0" : Hex(type.Number), ["d"] = diameter, ["shift"] = R(shift, 3),
                ["stations"] = stations,
                // The intervals of the search: the required area holds over each of them.
                ["steps"] = demand is null ? null : new JsonArray(demand.Stations.Select((st, k) => (JsonNode)new JsonObject
                {
                    ["top"] = R(st.Top, 3), ["bottom"] = R(st.Bottom, 3), ["as"] = R(demand.As[k] / 100, 2),
                    ["asw"] = double.IsFinite(demand.Asw[k]) ? R(demand.Asw[k], 2) : null,
                }).ToArray()),
            });
        }
        var types = new JsonArray((r?.Types ?? []).Select(t => (JsonNode)new JsonObject
        {
            ["number"] = t.Number, ["color"] = Hex(t.Number), ["head"] = R(t.Head, 3), ["tip"] = R(t.Tip, 3),
            ["members"] = new JsonArray(t.Members.Select(m => (JsonNode)JsonValue.Create(m.Info.Name)!).ToArray()),
            ["zones"] = new JsonArray(t.Zones.Select((z, i) => (JsonNode)new JsonObject
            {
                ["top"] = R(z.Top, 3), ["bottom"] = R(z.Bottom, 3), ["bars"] = z.Bars.ToString(), ["as"] = R(z.Bars.AreaMm2 / 100, 2),
                ["spiral"] = z.SpiralMm, ["k"] = z.K is double k ? R(k, 3) : null, ["governing"] = z.Governing,
                ["mult"] = capacities?.TryGetValue((t.Number, i), out var c) == true ? R(c.Mult, 1) : null,
                ["n"] = capacities?.TryGetValue((t.Number, i), out var c2) == true ? R(c2.N, 1) : null,
                ["qult"] = capacities?.TryGetValue((t.Number, i), out var c3) == true && c3.Qult is double qz ? R(qz, 1) : null,
                ["nultc"] = capacities?.TryGetValue((t.Number, i), out var c4) == true ? R(c4.NultCompression, 0) : null,
                ["nultt"] = capacities?.TryGetValue((t.Number, i), out var c5) == true ? R(c5.NultTension, 0) : null,
            }).ToArray()),
        }).ToArray());
        return new JsonObject
        {
            ["title"] = p.Source?.Info.Title ?? p.Title,
            ["section"] = p.Section.Kind == SectionKind.Pile
                ? $"БНС Ø{p.Section.Pile.DiameterMm:0}, {p.Section.ConcreteClass}, {p.Section.RebarClass}"
                : $"СВГ {p.Section.Wall.WidthMm:0}×{p.Section.Wall.ThicknessMm:0}",
            ["piles"] = piles, ["types"] = types,
        };
    }

    /// <summary>The station whose interval holds the level (the upper one at a boundary); the nearest otherwise.</summary>
    static int Containing(IReadOnlyList<Station> st, double level)
    {
        for (int i = 0; i < st.Count; i++)
            if (level <= st[i].Top + 1e-6 && level > st[i].Bottom + 1e-6) return i;
        return Nearest(st, level);
    }

    static int Nearest(IReadOnlyList<Station> st, double level)
    {
        int best = 0;
        for (int i = 1; i < st.Count; i++)
            if (Math.Abs(st[i].Level - level) < Math.Abs(st[best].Level - level)) best = i;
        return best;
    }

    static string Resource(string name)
    {
        using var stream = typeof(SceneExport).Assembly.GetManifestResourceStream("SectionCalc.Viewer3D." + name)
            ?? throw new InvalidOperationException("Нет ресурса " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The page: three.js and the controls inside, the data as JSON.</summary>
    public static string Html(JsonObject data)
    {
        // "</" inside the scripts would close the script element.
        string json = data.ToJsonString().Replace("</", "<\\/");
        return Resource("viewer.html")
            .Replace("/*THREE*/", Resource("three.min.js"))
            .Replace("/*ORBIT*/", Resource("OrbitControls.js"))
            .Replace("/*DATA*/", json);
    }
}
