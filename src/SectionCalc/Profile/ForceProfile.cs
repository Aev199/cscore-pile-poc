namespace SectionCalc.Profile;

/// <summary>
/// Design forces of one phase at one node, per element (wall: per section width b; pile: per pile).
/// Envelope route: one sample per corner of the envelope (Corner 1…4), Q is the largest |Q|.
/// Group of piles: Pile is the index in <see cref="ForceProfile.Piles"/>; the level is shifted to the common head.
/// PLAXIS 3D: M and Q are the resultants of the two local axes.
/// </summary>
public sealed record ForceSample(int Phase, double Level, double N, double M, double Q, int Corner = 0, int Pile = 0);

/// <summary>One pile of a group: model elevations of its head and tip, and the shift of its diagrams to the common head.</summary>
public sealed record PileInfo(string Name, double Head, double Tip, double Shift, double X, double Y);

/// <summary>Envelope of all included phases at one elevation.</summary>
public sealed record EnvelopePoint(double Level, double NMin, double NMax, double MMin, double MMax, double QAbsMax);

/// <summary>One station of the reinforcement diagram: the interval it covers and the РСУ found in it.</summary>
public sealed record Station(double Level, double Top, double Bottom, IReadOnlyList<LoadRow> Rsu);

/// <summary>Converted PLAXIS forces of the selected element: samples, envelope, stations with РСУ.</summary>
public sealed class ForceProfile
{
    public required IReadOnlyList<ForceSample> Samples { get; init; }
    public required IReadOnlyList<EnvelopePoint> Envelope { get; init; }
    /// <summary>Envelope before the peak cuts (equal to <see cref="Envelope"/> without cuts).</summary>
    public required IReadOnlyList<EnvelopePoint> OriginalEnvelope { get; init; }
    public required double TopLevel { get; init; }
    public required double BottomLevel { get; init; }
    public required double X { get; init; }
    public required string ElementKind { get; init; }
    /// <summary>Forces are Output envelopes (no simultaneous N, M, Q): РСУ are the envelope corners.</summary>
    public bool IsEnvelope { get; init; }
    /// <summary>Factor from PLAXIS forces per metre to forces per element.</summary>
    public required double ForceScale { get; init; }
    /// <summary>PLAXIS 3D piles (one or a group); 2D: a single entry for the selected element.</summary>
    public IReadOnlyList<PileInfo> Piles { get; init; } = [];
    public bool Is3D { get; init; }
    public bool IsGroup => Piles.Count > 1;
    /// <summary>Element (and branch) whose envelope Nmax = 0 along all its length was taken as a phase before it was made.</summary>
    public IReadOnlyList<string> InactiveZeroN { get; init; } = [];
    /// <summary>A wall: M has a sign (faces); a pile: the bending moment is taken by its magnitude.</summary>
    public bool IsWall { get; init; }
    public double Length => TopLevel - BottomLevel;

    public static ForceProfile Build(ProfileProject p)
    {
        var src = p.Source ?? throw new InvalidOperationException("Нет данных PLAXIS: подключитесь к Output или откройте выгрузку");
        var s = p.Settings;
        double f = Unit.Force(s.ForceUnit) ?? throw new InvalidOperationException($"Неизвестная единица силы «{s.ForceUnit}»");
        double l = Unit.Length(s.LengthUnit) ?? throw new InvalidOperationException($"Неизвестная единица длины «{s.LengthUnit}»");
        var kinds = src.Objects.Select(o => o.Kind).Distinct().ToList();
        if (kinds.Count != 1) throw new InvalidOperationException("Выберите элементы одного типа: Plate, Embedded Beam (Row) или Beam");
        string kind = kinds[0];
        bool is3D = src.Is3D;
        var scenario = s.Scenario;
        if (is3D != (scenario == ProfileScenario.Piles3D))
            throw new InvalidOperationException(is3D ? "Выгрузка PLAXIS 3D: выберите сценарий «Сваи из 3D»"
                : "Выгрузка PLAXIS 2D: выберите сценарий «Сваи ограждения» или «СВГ»");
        if (scenario == ProfileScenario.Wall && kind != "Plate") throw new InvalidOperationException("СВГ рассчитывается по Plate: выберите сценарий «Сваи ограждения»");
        if (kind == "Plate" && is3D) throw new InvalidOperationException("PLAXIS 3D: стены пока не поддерживаются");
        var need = scenario == ProfileScenario.Wall ? SectionKind.Wall : SectionKind.Pile;
        if (p.Section.Kind != need)
            throw new InvalidOperationException($"Сценарий требует сечение {(need == SectionKind.Wall ? "СВГ" : "БНС")}: задайте его в режиме «БНС · СВГ»");
        // PLAXIS 2D: forces per metre out of plane × panel width or pile spacing; PLAXIS 3D: per pile.
        double width = scenario switch
        {
            ProfileScenario.Piles3D => 1, ProfileScenario.Wall => p.Section.Wall.WidthMm / 1000, _ => s.PileSpacingM,
        };
        if (!(width > 0)) throw new InvalidOperationException(scenario == ProfileScenario.Wall ? "Задайте ширину захватки" : "Задайте шаг свай");
        var included = p.Phases.Where(ph => ph.Include).Select(ph => ph.Id).ToHashSet();
        if (included.Count == 0) throw new InvalidOperationException("Не выбрано ни одной фазы");

        // Piles of a group: Output segments are joined by the Input name they belong to.
        var pileNames = src.Objects.Select(o => o.Pile ?? "").Distinct().ToList();
        var pileOf = src.Objects.ToDictionary(o => o.Id, o => pileNames.IndexOf(o.Pile ?? ""));
        var used = src.Data.Where(d => included.Contains(d.Phase)).ToList();
        int zi = is3D ? 2 : 1;
        var piles = new List<PileInfo>();
        for (int i = 0; i < pileNames.Count; i++)
        {
            var nodes = used.Where(d => pileOf.GetValueOrDefault(d.Object) == i).SelectMany(d => d.Nodes).ToList();
            if (nodes.Count == 0) continue;
            string name = pileNames[i].Length > 0 ? pileNames[i] : string.Join(", ", src.Objects.Select(o => o.Name));
            double head = nodes.Max(n => n[zi]) * l, tip = nodes.Min(n => n[zi]) * l;
            double spread = is3D
                ? nodes.Max(n => Math.Sqrt(Math.Pow(n[0] - nodes[0][0], 2) + Math.Pow(n[1] - nodes[0][1], 2))) * l
                : (nodes.Max(n => n[0]) - nodes.Min(n => n[0])) * l;
            if (spread > (is3D ? 0.05 : 0.01))
                throw new InvalidOperationException(is3D ? $"Свая {name} не вертикальна (отклонение оси более 50 мм): наклонные сваи пока не поддерживаются"
                    : "Выбранные элементы не лежат на одной вертикали (разброс X более 10 мм)");
            piles.Add(new PileInfo(name, head, tip, 0, nodes.Average(n => n[0]) * l, is3D ? nodes.Average(n => n[1]) * l : 0));
        }
        if (piles.Count == 0) throw new InvalidOperationException("В выгрузке нет результатов выбранных фаз");
        double commonHead = piles.Max(x => x.Head);
        if (s.AlignPileHeads) piles = piles.Select(x => x with { Shift = commonHead - x.Head }).ToList();
        var pileIndex = pileNames.Select(n => piles.FindIndex(x => x.Name == (n.Length > 0 ? n : x.Name))).ToList();

        var samples = new List<ForceSample>();
        double nf = f / l * width, mf = f * width;
        if (is3D) (nf, mf) = (f, f * l); // per pile: F and F·L
        static double Abs2(double a, double b) => Math.Max(Math.Abs(a), Math.Abs(b));
        // Envelopes with Nmax = 0 along the whole element: a phase before it was made; use Nmin for both corners.
        int nMinAt = is3D ? 3 : 2, nMaxAt = is3D ? 4 : 3;
        var zeroN = new List<string>();
        foreach (var series in used)
        {
            int pile = pileIndex[pileOf[series.Object]];
            bool inactiveZero = src.IsEnvelope && s.SkipInactiveZeroN && series.Nodes.Count > 0
                && series.Nodes.All(n => Math.Abs(n[nMaxAt]) < 1e-9) && series.Nodes.Any(n => n[nMinAt] < -1e-9);
            if (inactiveZero)
            {
                string name = $"{piles[pile].Name} ({p.Phases.FirstOrDefault(x => x.Id == series.Phase)?.Name})";
                if (!zeroN.Contains(name)) zeroN.Add(name);
            }
            foreach (var raw in series.Nodes)
            {
                var n = raw;
                if (inactiveZero) { n = (double[])raw.Clone(); n[nMaxAt] = n[nMinAt]; }
                double level = n[zi] * l + piles[pile].Shift;
                if (is3D && !src.IsEnvelope)
                {
                    // x, y, z, N, M2, M3, Q12, Q13: resultant bending moment and shear of the round pile.
                    samples.Add(new ForceSample(series.Phase, level, n[3] * nf, Math.Sqrt(n[4] * n[4] + n[5] * n[5]) * mf,
                        Math.Sqrt(n[6] * n[6] + n[7] * n[7]) * nf, 0, pile));
                    continue;
                }
                if (is3D)
                {
                    // Envelopes of M2 and M3 are not simultaneous: their extremes combined, on the safe side.
                    double m = Math.Sqrt(Math.Pow(Abs2(n[5], n[6]), 2) + Math.Pow(Abs2(n[7], n[8]), 2)) * mf;
                    double q3 = Math.Sqrt(Math.Pow(Abs2(n[9], n[10]), 2) + Math.Pow(Abs2(n[11], n[12]), 2)) * nf;
                    samples.Add(new ForceSample(series.Phase, level, n[3] * nf, m, q3, 1, pile));
                    samples.Add(new ForceSample(series.Phase, level, n[4] * nf, m, q3, 2, pile));
                    continue;
                }
                // N, Q in F/L per metre; M in F·L/L per metre.
                if (!src.IsEnvelope)
                {
                    samples.Add(new ForceSample(series.Phase, level, n[2] * nf, n[3] * mf, n[4] * nf, 0, pile));
                    continue;
                }
                // n_min, n_max, m_min, m_max, q_min, q_max: Mmax/Mmin each with Nmin and Nmax.
                double q = Math.Max(Math.Abs(n[6]), Math.Abs(n[7])) * nf;
                samples.Add(new ForceSample(series.Phase, level, n[2] * nf, n[5] * mf, q, 1, pile));
                samples.Add(new ForceSample(series.Phase, level, n[3] * nf, n[5] * mf, q, 2, pile));
                samples.Add(new ForceSample(series.Phase, level, n[2] * nf, n[4] * mf, q, 3, pile));
                samples.Add(new ForceSample(series.Phase, level, n[3] * nf, n[4] * mf, q, 4, pile));
            }
        }
        if (samples.Count == 0) throw new InvalidOperationException("В выгрузке нет результатов выбранных фаз");

        var original = EnvelopeOf(samples);
        foreach (var cut in s.PeakCuts) samples = Cut(samples, cut);
        return new ForceProfile
        {
            Samples = samples, Envelope = EnvelopeOf(samples), OriginalEnvelope = original,
            TopLevel = samples.Max(x => x.Level), BottomLevel = samples.Min(x => x.Level),
            X = piles.Average(x => x.X), ElementKind = kind, ForceScale = f * width, IsEnvelope = src.IsEnvelope,
            Piles = piles, Is3D = is3D, IsWall = scenario == ProfileScenario.Wall, InactiveZeroN = zeroN,
        };
    }

    /// <summary>
    /// Extremes of every phase (envelope branch) taken alone, with the current units and settings: shows which
    /// phase governs, e.g. a branch of another scenario left in the list.
    /// </summary>
    public static Dictionary<int, (double NMin, double NMax, double MAbs, double QAbs)> PhaseExtremes(ProfileProject p)
    {
        var result = new Dictionary<int, (double, double, double, double)>();
        foreach (var phase in p.Phases)
        {
            var one = new ProfileProject
            {
                Section = p.Section, Source = p.Source, Settings = p.Settings,
                Phases = p.Phases.Select(x => new PhaseSetting { Id = x.Id, Name = x.Name, Include = x.Id == phase.Id }).ToList(),
            };
            try
            {
                var e = Build(one).OriginalEnvelope;
                result[phase.Id] = (e.Min(x => x.NMin), e.Max(x => x.NMax), e.Max(x => Math.Max(Math.Abs(x.MMin), Math.Abs(x.MMax))), e.Max(x => x.QAbsMax));
            }
            catch (InvalidOperationException) { }
        }
        return result;
    }

    /// <summary>
    /// Envelope over every curve (phase, corner, pile) at the union of their node levels. Piles of a group have their
    /// nodes at different levels: each curve is interpolated, otherwise the envelope would jump between the piles.
    /// </summary>
    /// <summary>Envelope of one pile of a group (its own samples).</summary>
    public List<EnvelopePoint> EnvelopeOfPile(int pile) => EnvelopeOf(Samples.Where(x => x.Pile == pile).ToList());

    static List<EnvelopePoint> EnvelopeOf(List<ForceSample> samples)
    {
        var curves = samples.GroupBy(x => (x.Phase, x.Corner, x.Pile)).Select(g => g.OrderByDescending(x => x.Level).ToList()).ToList();
        var levels = samples.Select(x => Math.Round(x.Level, 4)).Distinct().OrderByDescending(z => z).ToList();
        var result = new List<EnvelopePoint>(levels.Count);
        foreach (double z in levels)
        {
            var at = curves.SelectMany(c => At(c, z)).ToList();
            if (at.Count == 0) continue;
            result.Add(new EnvelopePoint(z, at.Min(x => x.N), at.Max(x => x.N), at.Min(x => x.M), at.Max(x => x.M), at.Max(x => Math.Abs(x.Q))));
        }
        return result;
    }

    /// <summary>Values of a curve (nodes from the top down) at a level: its nodes there, or the interpolated value.</summary>
    static IEnumerable<ForceSample> At(List<ForceSample> nodes, double level)
    {
        var exact = nodes.Where(a => Math.Abs(a.Level - level) <= 1e-4).ToList();
        if (exact.Count > 0) return exact;
        for (int i = 0; i + 1 < nodes.Count; i++)
        {
            var a = nodes[i]; var b = nodes[i + 1];
            if (a.Level <= level || b.Level >= level) continue;
            double t = (a.Level - level) / (a.Level - b.Level);
            return [a with { Level = level, N = a.N + t * (b.N - a.N), M = a.M + t * (b.M - a.M), Q = a.Q + t * (b.Q - a.Q) }];
        }
        return [];
    }

    /// <summary>Peak cut on every curve (phase, envelope corner): chord between the interval ends, or a limit.</summary>
    internal static List<ForceSample> Cut(List<ForceSample> samples, PeakCut cut)
    {
        double top = Math.Max(cut.TopLevel, cut.BottomLevel), bottom = Math.Min(cut.TopLevel, cut.BottomLevel);
        var result = new List<ForceSample>(samples.Count);
        foreach (var curve in samples.GroupBy(x => (x.Phase, x.Corner, x.Pile)))
        {
            var nodes = curve.OrderByDescending(x => x.Level).ToList();
            (double M, double Q)? At(double level)
            {
                for (int i = 0; i + 1 < nodes.Count; i++)
                {
                    var a = nodes[i]; var b = nodes[i + 1];
                    if (level > a.Level + 1e-9 || level < b.Level - 1e-9) continue;
                    double t = a.Level - b.Level < 1e-9 ? 0 : (a.Level - level) / (a.Level - b.Level);
                    return (a.M + t * (b.M - a.M), a.Q + t * (b.Q - a.Q));
                }
                return null;
            }
            var (upper, lower) = (At(top), At(bottom));
            foreach (var n in nodes)
            {
                if (n.Level >= top - 1e-9 || n.Level <= bottom + 1e-9) { result.Add(n); continue; }
                double m = n.M, q = n.Q;
                if (cut.Method == PeakCutMethod.Limit)
                {
                    if (cut.Moment) m = Math.Clamp(m, -cut.MomentLimit, cut.MomentLimit);
                    if (cut.Shear) q = Math.Clamp(q, -cut.ShearLimit, cut.ShearLimit);
                }
                else if (upper is { } u && lower is { } l)
                {
                    double t = (top - n.Level) / (top - bottom);
                    if (cut.Moment) m = u.M + t * (l.M - u.M);
                    if (cut.Shear) q = u.Q + t * (l.Q - u.Q);
                }
                result.Add(n with { M = m, Q = q });
            }
        }
        return result;
    }

    /// <summary>
    /// Stations every step from the top. Each covers the interval around it, so peaks between stations are not lost:
    /// the РСУ of a station come from all nodes of the interval (and the values interpolated at its ends).
    /// РСУ per combination kind: Mmax, Mmin, Nmax, Nmin, |Q|max with the accompanying forces.
    /// </summary>
    /// <param name="pile">Only this pile of a group, along its own length (the piles one by one).</param>
    public List<Station> Stations(ProfileProject p, int? pile = null)
    {
        double step = p.Settings.StationStepM;
        if (!(step > 0)) throw new InvalidOperationException("Шаг сечений должен быть больше 0");
        IReadOnlyList<ForceSample> samples = pile is int only ? Samples.Where(x => x.Pile == only).ToList() : Samples;
        if (samples.Count == 0) return [];
        double topLevel = pile is null ? TopLevel : samples.Max(x => x.Level), bottomLevel = pile is null ? BottomLevel : samples.Min(x => x.Level);
        int count = Math.Max(1, (int)Math.Ceiling((topLevel - bottomLevel) / step - 1e-9));
        var phases = p.Phases.ToDictionary(ph => ph.Id);
        // Envelope corners are separate curves: interpolate each one on its own.
        var byPhase = samples.GroupBy(x => (x.Phase, x.Corner, x.Pile)).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Level).ToList());
        var stations = new List<Station>();
        for (int i = 0; i <= count; i++)
        {
            double level = Math.Max(bottomLevel, topLevel - i * step);
            double top = Math.Min(topLevel, level + step / 2), bottom = Math.Max(bottomLevel, level - step / 2);
            var rows = new List<LoadRow>();
            foreach (var group in byPhase.Where(kv => phases.ContainsKey(kv.Key.Phase))
                         .GroupBy(kv => CombinationKind.Of(Row(phases[kv.Key.Phase]))))
            {
                var candidates = group.SelectMany(kv => Window(kv.Value, top, bottom)).ToList();
                if (candidates.Count == 0) continue;
                if (IsEnvelope)
                {
                    rows.AddRange(EnvelopeRows(p, candidates, phases, this));
                    continue;
                }
                var picked = new List<(string Label, ForceSample S)>
                {
                    ("Mmax", candidates.MaxBy(x => x.M)!), ("Mmin", candidates.MinBy(x => x.M)!),
                    ("Nmax", candidates.MaxBy(x => x.N)!), ("Nmin", candidates.MinBy(x => x.N)!),
                    ("Qmax", candidates.MaxBy(x => Math.Abs(x.Q))!),
                };
                foreach (var same in picked.GroupBy(x => x.S))
                {
                    var sample = same.Key;
                    var ph = phases[sample.Phase];
                    var row = Row(ph);
                    row.Name = $"{PileLabel(sample.Pile)}{ph.Name}: {string.Join(", ", same.Select(x => x.Label))}";
                    row.N = p.Settings.IgnoreAxialForce ? 0 : sample.N; row.Q = Math.Abs(sample.Q);
                    // Wall: Mx > 0 stretches the positive (right) face.
                    row.Mx = p.Section.Kind == SectionKind.Wall ? p.Settings.MomentSign * sample.M : Math.Abs(sample.M);
                    row.LoadFactor = p.Settings.ServiceDivisor; row.SustainedShare = p.Settings.SustainedShare;
                    rows.Add(row);
                }
            }
            stations.Add(new Station(level, top, bottom, rows));
        }
        return stations;
    }

    /// <summary>
    /// РСУ from envelopes: N and M are not simultaneous, so the extreme moments are taken with both extreme
    /// axial forces of the interval (on the safe side); Q is the largest |Q|.
    /// </summary>
    /// <summary>"EmbeddedBeam_2, " in a group of piles: the governing pile is seen in the combination name.</summary>
    string PileLabel(int pile) => IsGroup ? Piles[pile].Name + ", " : "";

    static IEnumerable<LoadRow> EnvelopeRows(ProfileProject p, List<ForceSample> c, Dictionary<int, PhaseSetting> phases, ForceProfile f)
    {
        double mMax = c.Max(x => x.M), mMin = c.Min(x => x.M), nMax = c.Max(x => x.N), nMin = c.Min(x => x.N), q = c.Max(x => Math.Abs(x.Q));
        if (p.Settings.IgnoreAxialForce) nMax = nMin = 0;
        var ph = phases[c[0].Phase];
        string source = string.Join(", ", c.Select(x => phases[x.Phase].Name).Distinct());
        if (f.IsGroup)
        {
            // The governing pile: the one with the largest |M| in the interval.
            var top = c.MaxBy(x => Math.Abs(x.M))!;
            source = $"{f.Piles[top.Pile].Name} (из {c.Select(x => x.Pile).Distinct().Count()} свай), {source}";
        }
        var seen = new HashSet<(double, double)>();
        foreach (var (label, n, m) in new[] { ("Mmax, Nmin", nMin, mMax), ("Mmax, Nmax", nMax, mMax), ("Mmin, Nmin", nMin, mMin), ("Mmin, Nmax", nMax, mMin) })
        {
            if (!seen.Add((n, m))) continue;
            var row = Row(ph);
            row.Name = $"огибающая {source}: {label}";
            row.N = n; row.Q = q;
            row.Mx = p.Section.Kind == SectionKind.Wall ? p.Settings.MomentSign * m : Math.Abs(m);
            row.LoadFactor = p.Settings.ServiceDivisor; row.SustainedShare = p.Settings.SustainedShare;
            yield return row;
        }
    }

    static LoadRow Row(PhaseSetting ph) => new() { ShortTerm = ph.ShortTerm, Seismic = ph.Seismic, Special = ph.Special && !ph.Seismic };

    /// <summary>Samples of one phase inside [bottom, top] plus the values interpolated at both ends.</summary>
    static IEnumerable<ForceSample> Window(List<ForceSample> nodes, double top, double bottom)
    {
        foreach (var n in nodes)
            if (n.Level <= top + 1e-9 && n.Level >= bottom - 1e-9) yield return n;
        foreach (double level in new[] { top, bottom })
            for (int i = 0; i + 1 < nodes.Count; i++)
            {
                var a = nodes[i]; var b = nodes[i + 1];
                if (a.Level - b.Level < 1e-9 || level > a.Level || level < b.Level) continue;
                double t = (a.Level - level) / (a.Level - b.Level);
                yield return a with { Level = level, N = a.N + t * (b.N - a.N), M = a.M + t * (b.M - a.M), Q = a.Q + t * (b.Q - a.Q) };
            }
    }
}
