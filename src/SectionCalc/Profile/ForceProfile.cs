namespace SectionCalc.Profile;

/// <summary>
/// Design forces of one phase at one node, per element (wall: per section width b; pile: per pile).
/// Envelope route: one sample per corner of the envelope (Corner 1…4), Q is the largest |Q|.
/// </summary>
public sealed record ForceSample(int Phase, double Level, double N, double M, double Q, int Corner = 0);

/// <summary>Envelope of all included phases at one elevation.</summary>
public sealed record EnvelopePoint(double Level, double NMin, double NMax, double MMin, double MMax, double QAbsMax);

/// <summary>One station of the reinforcement diagram: the interval it covers and the РСУ found in it.</summary>
public sealed record Station(double Level, double Top, double Bottom, IReadOnlyList<LoadRow> Rsu);

/// <summary>Converted PLAXIS forces of the selected element: samples, envelope, stations with РСУ.</summary>
public sealed class ForceProfile
{
    public required IReadOnlyList<ForceSample> Samples { get; init; }
    public required IReadOnlyList<EnvelopePoint> Envelope { get; init; }
    public required double TopLevel { get; init; }
    public required double BottomLevel { get; init; }
    public required double X { get; init; }
    public required string ElementKind { get; init; }
    /// <summary>Forces are Output envelopes (no simultaneous N, M, Q): РСУ are the envelope corners.</summary>
    public bool IsEnvelope { get; init; }
    /// <summary>Factor from PLAXIS forces per metre to forces per element.</summary>
    public required double ForceScale { get; init; }
    public double Length => TopLevel - BottomLevel;

    public static ForceProfile Build(ProfileProject p)
    {
        var src = p.Source ?? throw new InvalidOperationException("Нет данных PLAXIS: подключитесь к Output или откройте выгрузку");
        var s = p.Settings;
        double f = Unit.Force(s.ForceUnit) ?? throw new InvalidOperationException($"Неизвестная единица силы «{s.ForceUnit}»");
        double l = Unit.Length(s.LengthUnit) ?? throw new InvalidOperationException($"Неизвестная единица длины «{s.LengthUnit}»");
        var kinds = src.Objects.Select(o => o.Kind).Distinct().ToList();
        if (kinds.Count != 1) throw new InvalidOperationException("Выберите элементы одного типа: Plate или Embedded Beam Row");
        string kind = kinds[0];
        if (kind == "Plate" && p.Section.Kind != SectionKind.Wall) throw new InvalidOperationException("Plate — стена в грунте: выберите тип сечения СВГ");
        if (kind != "Plate" && p.Section.Kind != SectionKind.Pile) throw new InvalidOperationException("Embedded Beam Row — свая: выберите тип сечения БНС");
        // Plate and Embedded Beam Row of PLAXIS 2D V22 give forces per metre out of plane.
        double width = kind == "Plate" ? p.Section.Wall.WidthMm / 1000 : s.PileSpacingM;
        if (!(width > 0)) throw new InvalidOperationException("Задайте шаг свай в ряду");
        var included = p.Phases.Where(ph => ph.Include).Select(ph => ph.Id).ToHashSet();
        if (included.Count == 0) throw new InvalidOperationException("Не выбрано ни одной фазы");

        var samples = new List<ForceSample>();
        var xs = new List<double>();
        foreach (var series in src.Data.Where(d => included.Contains(d.Phase)))
            foreach (var n in series.Nodes)
            {
                xs.Add(n[0] * l);
                // N, Q in F/L per metre; M in F·L/L per metre.
                double level = n[1] * l, nf = f / l * width, mf = f * width;
                if (!src.IsEnvelope)
                {
                    samples.Add(new ForceSample(series.Phase, level, n[2] * nf, n[3] * mf, n[4] * nf));
                    continue;
                }
                // n_min, n_max, m_min, m_max, q_min, q_max: Mmax/Mmin each with Nmin and Nmax.
                double q = Math.Max(Math.Abs(n[6]), Math.Abs(n[7])) * nf;
                samples.Add(new ForceSample(series.Phase, level, n[2] * nf, n[5] * mf, q, 1));
                samples.Add(new ForceSample(series.Phase, level, n[3] * nf, n[5] * mf, q, 2));
                samples.Add(new ForceSample(series.Phase, level, n[2] * nf, n[4] * mf, q, 3));
                samples.Add(new ForceSample(series.Phase, level, n[3] * nf, n[4] * mf, q, 4));
            }
        if (samples.Count == 0) throw new InvalidOperationException("В выгрузке нет результатов выбранных фаз");
        if (xs.Max() - xs.Min() > 0.01) throw new InvalidOperationException("Выбранные элементы не лежат на одной вертикали (разброс X более 10 мм)");

        var envelope = samples.GroupBy(x => Math.Round(x.Level, 4)).OrderByDescending(g => g.Key)
            .Select(g => new EnvelopePoint(g.Key, g.Min(x => x.N), g.Max(x => x.N), g.Min(x => x.M), g.Max(x => x.M), g.Max(x => Math.Abs(x.Q))))
            .ToList();
        return new ForceProfile
        {
            Samples = samples, Envelope = envelope, TopLevel = samples.Max(x => x.Level), BottomLevel = samples.Min(x => x.Level),
            X = xs.Average(), ElementKind = kind, ForceScale = f * width, IsEnvelope = src.IsEnvelope,
        };
    }

    /// <summary>
    /// Stations every step from the top. Each covers the interval around it, so peaks between stations are not lost:
    /// the РСУ of a station come from all nodes of the interval (and the values interpolated at its ends).
    /// РСУ per combination kind: Mmax, Mmin, Nmax, Nmin, |Q|max with the accompanying forces.
    /// </summary>
    public List<Station> Stations(ProfileProject p)
    {
        double step = p.Settings.StationStepM;
        if (!(step > 0)) throw new InvalidOperationException("Шаг сечений должен быть больше 0");
        int count = Math.Max(1, (int)Math.Ceiling(Length / step - 1e-9));
        var phases = p.Phases.ToDictionary(ph => ph.Id);
        // Envelope corners are separate curves: interpolate each one on its own.
        var byPhase = Samples.GroupBy(x => (x.Phase, x.Corner)).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Level).ToList());
        var stations = new List<Station>();
        for (int i = 0; i <= count; i++)
        {
            double level = Math.Max(BottomLevel, TopLevel - i * step);
            double top = Math.Min(TopLevel, level + step / 2), bottom = Math.Max(BottomLevel, level - step / 2);
            var rows = new List<LoadRow>();
            foreach (var group in byPhase.Where(kv => phases.ContainsKey(kv.Key.Phase))
                         .GroupBy(kv => CombinationKind.Of(Row(phases[kv.Key.Phase]))))
            {
                var candidates = group.SelectMany(kv => Window(kv.Value, top, bottom)).ToList();
                if (candidates.Count == 0) continue;
                if (IsEnvelope)
                {
                    rows.AddRange(EnvelopeRows(p, candidates, phases));
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
                    row.Name = $"{ph.Name}: {string.Join(", ", same.Select(x => x.Label))}";
                    row.N = sample.N; row.Q = Math.Abs(sample.Q);
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
    static IEnumerable<LoadRow> EnvelopeRows(ProfileProject p, List<ForceSample> c, Dictionary<int, PhaseSetting> phases)
    {
        double mMax = c.Max(x => x.M), mMin = c.Min(x => x.M), nMax = c.Max(x => x.N), nMin = c.Min(x => x.N), q = c.Max(x => Math.Abs(x.Q));
        var ph = phases[c[0].Phase];
        string source = string.Join(", ", c.Select(x => phases[x.Phase].Name).Distinct());
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
                yield return new ForceSample(a.Phase, level, a.N + t * (b.N - a.N), a.M + t * (b.M - a.M), a.Q + t * (b.Q - a.Q), a.Corner);
            }
    }
}
