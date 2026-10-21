namespace SectionCalc.Profile;

/// <summary>
/// Required reinforcement of one station. Wall: right face (stretched by Mx &gt; 0) and left face; pile: the whole ring
/// in <see cref="AsRightMm2"/>. Status: ok; minimum — the constructive minimum governs; exceeded — not enough even with
/// the largest bars; indeterminate — the engine did not converge; none — no forces in the interval.
/// </summary>
public sealed record StationResult(Station Station, double AsRightMm2, double AsLeftMm2, double? AswCm2PerM, string Status,
    string? Governing, string? Combination, double? K, string? Note, int Evaluations)
{
    public double AsTotalMm2 => AsRightMm2 + AsLeftMm2;
}

/// <summary>Result along the element. IsCheck: the given section was checked (As — the given areas, K — its utilization).</summary>
public sealed record ReinforcementProfile(ProfileProject Project, ForceProfile Forces, IReadOnlyList<StationResult> Stations, bool IsCheck = false);

/// <summary>
/// Searches the smallest reinforcement satisfying every CScore check (strength with η, stability, cracks) for the
/// РСУ of each station. The bar layout of the section is kept and the bar diameter changes continuously, so the result
/// is a required area. With the Q check the required Asw/sw follows for the adopted stirrup pitch and legs.
/// </summary>
public static class RequiredReinforcement
{
    const double Tolerance = 0.03;
    const int MaxEvaluations = 16;
    static readonly HashSet<string> Demand = ["normal_strength", "normal_member_strength", "member_stability",
        "crack_width_long", "crack_width_short", "no_cracks"];

    public static ReinforcementProfile Run(ProfileProject project, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        var forces = ForceProfile.Build(project);
        var stations = forces.Stations(project);
        var section = Clone(project.Section);
        section.Conditions.ElementLengthM = Math.Max(0.1, Math.Round(forces.Length, 3));
        // A wall from Plate is a strip of a plane-strain model: it bends only in its own plane.
        section.Conditions.OnePlane = section.Kind == SectionKind.Wall;
        FixLayout(section, project.Settings.MaxBarDiameterMm);
        var problems = RequestBuilder.Validate(WithLoads(section, [new LoadRow { Name = "проверка", N = -1 }]));
        if (problems.Count > 0) throw new InvalidOperationException("Сечение: " + string.Join("; ", problems));
        var faces = Faces(section, project.Settings);
        var results = new StationResult[stations.Count];
        int done = 0;
        Parallel.For(0, stations.Count, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            results[i] = Station(section, stations[i], faces, cancel);
            progress?.Report((Interlocked.Increment(ref done), stations.Count));
        });
        return new ReinforcementProfile(project, forces, results);
    }

    /// <summary>
    /// The section as given in the section module (bars, rows, stirrups) checked with the РСУ of every station:
    /// the largest K of all checks (strength with η, stability, cracks, Q when enabled).
    /// </summary>
    public static ReinforcementProfile Check(ProfileProject project, IProgress<(int Done, int Total)>? progress = null, CancellationToken cancel = default)
    {
        var forces = ForceProfile.Build(project);
        var stations = forces.Stations(project);
        var section = Clone(project.Section);
        section.Conditions.ElementLengthM = Math.Max(0.1, Math.Round(forces.Length, 3));
        section.Conditions.OnePlane = section.Kind == SectionKind.Wall;
        var problems = RequestBuilder.Validate(WithLoads(section, [new LoadRow { Name = "проверка", N = -1 }]));
        if (problems.Count > 0) throw new InvalidOperationException("Сечение: " + string.Join("; ", problems));
        double right, left;
        if (section.Kind == SectionKind.Pile) (right, left) = (Area(section.Pile.BarCount, section.Pile.BarDiameterMm), 0);
        else
        {
            var w = section.Wall;
            (right, left) = (Area(w.PositiveFace.BarCount * w.PositiveFace.Rows, w.PositiveFace.BarDiameterMm),
                             Area(w.NegativeFace.BarCount * w.NegativeFace.Rows, w.NegativeFace.BarDiameterMm));
        }
        double? asw = null;
        if (section.Conditions.ShearCheck)
        {
            bool pile = section.Kind == SectionKind.Pile;
            double d = pile ? section.Pile.SpiralDiameterMm : section.Wall.StirrupDiameterMm;
            asw = (pile ? 2 : section.Conditions.StirrupLegs) * Math.PI * d * d / 4 / section.Conditions.ShearPitchMm * 10;
        }
        var results = new StationResult[stations.Count];
        int done = 0;
        Parallel.For(0, stations.Count, new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            var station = stations[i];
            if (station.Rsu.Count == 0)
                results[i] = new StationResult(station, right, left, asw, "none", null, null, null, "Нет усилий в интервале", 0);
            else
            {
                // As in the search: rows with Q only do not load the normal section, they go to the Q check alone.
                var normal = station.Rsu.Where(l => Math.Abs(l.N) > 1e-6 || Math.Abs(l.Mx) > 1e-6).ToList();
                var shearOnly = station.Rsu.Except(normal).Where(l => l.Q > 0).ToList();
                var rows = new List<CheckRow>();
                if (normal.Count > 0)
                    rows.AddRange(Engine.Check(WithLoads(section, normal), cancel).Rows.Where(x => Demand.Contains(x.CheckId) || Shears.Contains(x.CheckId)));
                if (section.Conditions.ShearCheck && shearOnly.Count > 0)
                    rows.AddRange(Engine.Check(WithLoads(section, shearOnly), cancel).Rows.Where(x => Shears.Contains(x.CheckId)));
                bool indet = rows.Any(x => x.Status == "indeterminate" && Demand.Contains(x.CheckId));
                bool shearOpen = rows.Any(x => x.Status == "indeterminate" && Shears.Contains(x.CheckId));
                var gov = rows.Where(x => x.Utilization is not null && (x.CheckId != "member_stability" || x.IsFailure))
                    .MaxBy(x => x.Utilization);
                double? k = gov?.Utilization;
                string status = indet ? "indeterminate" : k > 1 ? "exceeded" : "ok";
                results[i] = new StationResult(station, right, left, asw, status, gov?.Title, gov?.Combination, k,
                    indet ? "Расчёт ядра не сошёлся" : shearOpen ? "Q не проверен: подтвердите модель поперечного армирования" : null, 1);
            }
            progress?.Report((Interlocked.Increment(ref done), stations.Count));
        });
        return new ReinforcementProfile(project, forces, results, IsCheck: true);
    }

    static readonly HashSet<string> Shears = ["shear_strip", "shear_inclined", "inclined_moment"];

    sealed record Face(int Index, int Bars, double Min, double Max);

    /// <summary>Bars of one row of the reference layout of a wall face: about one per 100 mm of the section width.</summary>
    internal static int ReferenceBars(WallGeometry w) => Math.Max(2, (int)Math.Floor(w.WidthMm / 100));

    /// <summary>
    /// The required area does not depend on the bars given in the section: the bar axes (cover, reference diameter,
    /// rows) are fixed, and a reference grid of bars carries the area being searched.
    /// </summary>
    static void FixLayout(SectionProject s, double maxDiameter)
    {
        if (s.Kind == SectionKind.Pile)
        {
            s.Pile.FixedAxisMm = s.Pile.AxisDistanceMm;
            // The upper bound of the search is what the ring holds: as many of the largest bars as fit, not the
            // bar count given in the section.
            var probe = Clone(s);
            probe.Pile.BarDiameterMm = maxDiameter;
            int n = Math.Max(s.Pile.BarCount, 4);
            probe.Pile.BarCount = n;
            if (RequestBuilder.Layout(probe).Fits)
            {
                while (n < 200) { probe.Pile.BarCount = n + 1; if (!RequestBuilder.Layout(probe).Fits) break; n++; }
                s.Pile.BarCount = n;
            }
            return;
        }
        var w = s.Wall;
        int bars = ReferenceBars(w);
        foreach (var face in new[] { w.PositiveFace, w.NegativeFace })
        {
            face.FixedAxisMm = w.AxisDistanceMm(face);
            face.FixedRowPitchMm = face.RowPitchMm;
            face.BarCount = bars;
        }
    }

    /// <summary>Wall: per face, minimum % of b·h0; pile: ring, % of the area. Maximum: the largest bars that fit.</summary>
    static Face[] Faces(SectionProject s, ProfileSettings settings)
    {
        double pct = settings.MinPercent / 100;
        if (s.Kind == SectionKind.Pile)
        {
            var g = s.Pile;
            return [new Face(0, g.BarCount, pct * Math.PI * g.DiameterMm * g.DiameterMm / 4, Area(g.BarCount, MaxDiameter(s, 0, settings.MaxBarDiameterMm)))];
        }
        var w = s.Wall;
        Face For(WallFace face, int index)
        {
            double h0 = w.ThicknessMm - w.CentroidMm(face);
            int bars = face.BarCount * face.Rows;
            return new Face(index, bars, pct * w.WidthMm * h0, Area(bars, MaxDiameter(s, index, settings.MaxBarDiameterMm)));
        }
        return [For(w.PositiveFace, 0), For(w.NegativeFace, 1)];
    }

    static double Area(int bars, double d) => bars * Math.PI * d * d / 4;
    static double Diameter(int bars, double area) => Math.Sqrt(4 * area / (bars * Math.PI));

    /// <summary>Face 0: wall right face (positive) or pile ring; face 1: wall left face.</summary>
    static void SetDiameter(SectionProject s, int face, double d)
    {
        if (s.Kind == SectionKind.Pile) s.Pile.BarDiameterMm = d;
        else if (face == 0) s.Wall.PositiveFace.BarDiameterMm = d;
        else s.Wall.NegativeFace.BarDiameterMm = d;
    }

    /// <summary>Largest diameter up to the limit with which the bars still fit with the clear spacing.</summary>
    static double MaxDiameter(SectionProject s, int face, double limit)
    {
        var probe = Clone(s);
        bool Fits(double d) { SetDiameter(probe, face, d); return RequestBuilder.Layout(probe).Fits; }
        if (Fits(limit)) return limit;
        double lo = 4, hi = limit;
        for (int i = 0; i < 30; i++) { double mid = (lo + hi) / 2; if (Fits(mid)) lo = mid; else hi = mid; }
        return lo;
    }

    sealed class Search(Face face)
    {
        public Face Face { get; } = face;
        public double A = face.Min;
        public double? Lo, KLo, Hi, KHi;

        public bool Settled => Hi is double hi && (hi <= Face.Min * 1.0001 || Lo is double lo && (hi - lo) / hi < Tolerance);
        public bool AtMax => A >= Face.Max * 0.9999;

        public void Record(double k, bool pass)
        {
            if (pass) { if (Hi is null || A < Hi) { Hi = A; KHi = k; } }
            else if (Lo is null || A > Lo) { Lo = A; KLo = k; }
            // Another face changed: a once passing area may fail now.
            if (Lo is double lo && Hi is double hi && lo >= hi) { Hi = null; KHi = null; }
        }

        public double Next(double k)
        {
            if (Settled) return Hi!.Value;
            if (Hi is null) return Math.Min(Face.Max, A * Math.Max(1.15, Math.Pow(k, 1.3)));
            if (Lo is null) return Math.Max(Face.Min, A * Math.Clamp(k, 0.3, 0.97));
            double lo = Lo.Value, hi = Hi.Value, klo = KLo!.Value, khi = Math.Max(KHi!.Value, 1e-3);
            // K falls roughly as a power of the area: interpolate log K over log A to K = 0.98.
            double t = Math.Log(klo / 0.98) / Math.Log(klo / khi);
            double next = Math.Exp(Math.Log(lo) + (double.IsFinite(t) ? t : 0.5) * (Math.Log(hi) - Math.Log(lo)));
            return Math.Clamp(next, lo + 0.15 * (hi - lo), hi - 0.15 * (hi - lo));
        }
    }

    static StationResult Station(SectionProject section, Station station, Face[] faces, CancellationToken cancel)
    {
        if (station.Rsu.Count == 0)
            return new StationResult(station, 0, 0, null, "none", null, null, null, "Нет усилий в интервале", 0);
        // Rows with Q only do not load the normal section.
        var normal = station.Rsu.Where(l => Math.Abs(l.N) > 1e-6 || Math.Abs(l.Mx) > 1e-6).ToList();
        if (normal.Count == 0)
        {
            string? only = null;
            double? q = section.Conditions.ShearCheck ? Shear(section, station, faces, faces.Select(f => f.Min).ToArray(), cancel, ref only) : null;
            return new StationResult(station, faces[0].Min, faces.Length > 1 ? faces[1].Min : 0, q, "minimum", null, null, null, only, 0);
        }
        var basis = WithLoads(section, normal);
        basis.Conditions.ShearCheck = false;
        var search = faces.Select(f => new Search(f)).ToArray();
        (double[] A, CheckRow? Gov)? best = null;
        CheckRow? lastGov = null;
        bool exceeded = false, indeterminate = false;
        int evaluations = 0;
        while (evaluations < MaxEvaluations)
        {
            cancel.ThrowIfCancellationRequested();
            var trial = Clone(basis);
            trial.Conditions.CoarseMesh = true;
            foreach (var s in search) SetDiameter(trial, s.Face.Index, Diameter(s.Face.Bars, s.A));
            var result = Engine.Check(trial, cancel);
            evaluations++;
            var (k, indet, gov) = Demands(result, faces.Length);
            lastGov = gov;
            bool pass = !indet && k.All(x => x <= 1);
            if (pass && (best is null || search.Sum(s => s.A) < best.Value.A.Sum())) best = (search.Select(s => s.A).ToArray(), gov);
            for (int i = 0; i < search.Length; i++) search[i].Record(k[i], !indet && k[i] <= 1);

            // A face failing at its largest area cannot be helped.
            exceeded = search.Zip(k).Any(x => x.First.AtMax && (x.Second > 1 || indet));
            indeterminate = indet && search.All(s => s.AtMax);
            if (exceeded || indeterminate) break;
            if (pass && search.All(s => s.Settled && Math.Abs(s.A - s.Hi!.Value) < 1e-9)) break;
            for (int i = 0; i < search.Length; i++)
                search[i].A = search[i].Next(indet ? 1.5 : k[i]);
        }
        double[] areas = best?.A ?? search.Select(s => s.A).ToArray();
        // The search runs on a coarser mesh: verify with the normal one and increase where needed.
        for (int round = 0; best is not null && round < 3; round++)
        {
            var trial = Clone(basis);
            for (int i = 0; i < faces.Length; i++) SetDiameter(trial, i, Diameter(faces[i].Bars, areas[i]));
            var (k, indet, gov) = Demands(Engine.Check(trial, cancel), faces.Length);
            evaluations++;
            if (!indet && k.All(x => x <= 1)) { best = (areas, gov); break; }
            if (indet || round == 2) { best = null; indeterminate = indet; break; }
            areas = areas.Select((a, i) => k[i] > 1 ? Math.Min(faces[i].Max, a * Math.Pow(k[i], 1.3) * 1.01) : a).ToArray();
        }
        string status = best is null ? (indeterminate ? "indeterminate" : "exceeded")
            : areas.Select((a, i) => a <= faces[i].Min * 1.0001).All(x => x) ? "minimum" : "ok";
        var g = best?.Gov ?? lastGov;
        string? note = best is null
            ? (indeterminate ? "Расчёт ядра не сошёлся" : "Не обеспечивается при наибольшем диаметре стержней")
            : evaluations >= MaxEvaluations && !search.All(s => s.Settled) ? "Подбор остановлен по числу итераций, площадь в запас" : null;
        // Asw also where the longitudinal bars are not enough: with the largest area.
        double? asw = section.Conditions.ShearCheck && (best is not null || !indeterminate)
            ? Shear(section, station, faces, areas, cancel, ref note) : null;
        return new StationResult(station, areas[0], areas.Length > 1 ? areas[1] : 0, asw, status, g?.Title, g?.Combination, g?.Utilization, note, evaluations);
    }

    /// <summary>Largest K per face. Wall rows are assigned by the sign of Mx: positive — right face; zero — both.</summary>
    static (double[] K, bool Indeterminate, CheckRow? Governing) Demands(CheckResult r, int faces)
    {
        var k = new double[faces];
        bool indet = false;
        CheckRow? gov = null;
        foreach (var row in r.Rows.Where(x => Demand.Contains(x.CheckId)))
        {
            if (row.Status == "indeterminate") { indet = true; continue; }
            if (row.Utilization is not double u || row.CheckId == "member_stability" && !row.IsFailure) continue;
            double mx = r.Cases.TryGetValue(row.CaseId, out var c) ? c.Mx : r.Service.FirstOrDefault(s => s.Id == row.CaseId)?.Mx ?? 0;
            for (int i = 0; i < faces; i++)
            {
                bool mine = faces == 1 || row.CheckId == "member_stability" || Math.Abs(mx) < 1e-9 || (i == 0) == (mx > 0);
                if (mine && u > k[i]) k[i] = u;
            }
            if (gov is null || u > (gov.Utilization ?? 0)) gov = row;
        }
        return (k, indet, gov);
    }

    /// <summary>Required Asw/sw, cm²/m: the stirrup (spiral) diameter is varied with the adopted pitch and legs.</summary>
    static double? Shear(SectionProject section, Station station, Face[] faces, double[] areas, CancellationToken cancel, ref string? note)
    {
        var basis = WithLoads(section, station.Rsu.Where(l => l.Q > 0));
        if (basis.Loads.Count == 0) return 0;
        for (int i = 0; i < faces.Length; i++) SetDiameter(basis, i, Diameter(faces[i].Bars, areas[i]));
        bool pile = section.Kind == SectionKind.Pile;
        int legs = pile ? 2 : section.Conditions.StirrupLegs;
        double pitch = section.Conditions.ShearPitchMm;
        double Asw(double d) => legs * Math.PI * d * d / 4 / pitch * 10;
        (double K, bool Strip) Check(double d)
        {
            var trial = Clone(basis);
            if (pile) trial.Pile.SpiralDiameterMm = d; else trial.Wall.StirrupDiameterMm = d;
            var r = Engine.Check(trial, cancel);
            double k = r.Rows.Where(x => x.CheckId is "shear_inclined").Max(x => x.Utilization) ?? double.NaN;
            bool strip = r.Rows.Any(x => x.CheckId == "shear_strip" && x.Utilization > 1);
            return (k, strip);
        }
        double lo = 4, hi = pile ? 25 : 32;
        var (kMin, strip0) = Check(lo);
        if (double.IsNaN(kMin))
        {
            note = (note is null ? "" : note + ". ") + "Q не проверен: подтвердите модель и конструктивные требования поперечного армирования";
            return null;
        }
        if (strip0) note = (note is null ? "" : note + ". ") + "Не выполняется условие прочности бетонной полосы (8.55)";
        if (kMin <= 1)
        {
            // Practically without stirrups (Ø0,5 mm) the condition still holds: not required by calculation.
            if (Check(0.5).K <= 1) return 0;
            double fail = 0.5, pass = lo;
            for (int i = 0; i < 5; i++) { double mid = (fail + pass) / 2; if (Check(mid).K <= 1) pass = mid; else fail = mid; }
            return Asw(pass);
        }
        var (kMax, _) = Check(hi);
        if (kMax > 1) { note = (note is null ? "" : note + ". ") + "Q не обеспечивается поперечной арматурой"; return null; }
        for (int i = 0; i < 7; i++)
        {
            double mid = (lo + hi) / 2;
            if (Check(mid).K <= 1) hi = mid; else lo = mid;
        }
        return Asw(hi);
    }

    static SectionProject Clone(SectionProject p) => ProjectStore.Deserialize(ProjectStore.Serialize(p));

    static SectionProject WithLoads(SectionProject p, IEnumerable<LoadRow> loads)
    {
        var q = Clone(p);
        q.Loads = loads.Select(l => new LoadRow { Name = l.Name, N = l.N, Mx = l.Mx, My = l.My, Q = l.Q, LoadFactor = l.LoadFactor,
            SustainedShare = l.SustainedShare, ShortTerm = l.ShortTerm, Seismic = l.Seismic, Special = l.Special }).ToList();
        return q;
    }
}
