using SkiaSharp;

namespace SectionCalc;

/// <summary>A point of the interaction diagram: axial force (N &lt; 0 — compression) and moment in one plane.</summary>
public sealed record InteractionPoint(double N, double M);

/// <summary>A design combination on the diagram.</summary>
public sealed record InteractionLoad(string Name, double N, double M, bool OtherPlane);

/// <summary>
/// Strength of the normal section in the plane N–Mx (or N–My): rays of proportional loading from the origin, the
/// engine's ultimate factor of each ray gives a point of the boundary (portions of rays run in parallel).
/// Section strength only: no accidental eccentricity, no η (they belong to the combinations, not to the section).
/// </summary>
public static class Interaction
{
    public const int Rays = 48;

    public static List<InteractionPoint> Curve(SectionProject project, bool planeY, CancellationToken cancel = default)
    {
        var p = ProjectStore.Deserialize(ProjectStore.Serialize(project));
        var c = p.Conditions;
        c.Eccentricity = EccentricityMode.InLoads; c.SecondOrder = SecondOrderMode.SectionOnly; c.ShearCheck = false; c.InclinedMoment = false;
        c.ReliabilityUls = 1; c.CrackCategory = CrackCategory.LimitedWidth; c.CoarseMesh = true;
        // Rays far outside the section: every one fails, so the crack checks are skipped (they need passed strength).
        var m = MaterialTables.Uls(p, new CombinationKind(false, false, false));
        var layout = RequestBuilder.Layout(p);
        var gross = Geometry.Gross(p);
        double n0 = 3 * (m.Rb * gross.AreaMm2 + m.Rs * layout.SteelAreaMm2) / 1000;
        double m0 = n0 * (planeY ? gross.HyMm : gross.HxMm) / 1000 / 3;
        var rays = Enumerable.Range(0, Rays).Select(i =>
        {
            double a = 2 * Math.PI * i / Rays, n = n0 * Math.Cos(a), mm = m0 * Math.Sin(a);
            return new LoadRow { Name = $"r{i}", N = n, Mx = planeY ? 0 : mm, My = planeY ? mm : 0, LoadFactor = 1, SustainedShare = 1 };
        }).ToList();
        // Portions of rays in parallel (coarse mesh, as the capacity search of the 3D view).
        var factors = new double?[Rays];
        int portion = Math.Max(1, (int)Math.Ceiling(Rays / (double)Math.Max(1, Environment.ProcessorCount)));
        Parallel.ForEach(Enumerable.Range(0, (Rays + portion - 1) / portion),
            new ParallelOptions { CancellationToken = cancel, MaxDegreeOfParallelism = Environment.ProcessorCount }, part =>
        {
            var q = ProjectStore.Deserialize(ProjectStore.Serialize(p));
            q.Conditions.CoarseMesh = true;
            q.Loads = rays.Skip(part * portion).Take(portion).ToList();
            var r = Engine.Check(q, cancel);
            foreach (var l in q.Loads)
                if (r.Cases.TryGetValue(l.Name, out var info)) factors[int.Parse(l.Name[1..])] = info.UltimateFactor;
        });
        var curve = new List<InteractionPoint>();
        for (int i = 0; i < Rays; i++)
            if (factors[i] is double k && double.IsFinite(k) && k > 0)
                curve.Add(new InteractionPoint(k * rays[i].N, k * (planeY ? rays[i].My : rays[i].Mx)));
        return curve;
    }

    /// <summary>Design combinations in the plane (with γn), the moments as given (before ea and η).</summary>
    public static List<InteractionLoad> Loads(SectionProject p, bool planeY) => p.Loads.Select(l =>
    {
        double gn = l.Special && !l.Seismic ? 1 : p.Conditions.ReliabilityUls;
        return new InteractionLoad(l.Name.Trim(), l.N * gn, (planeY ? l.My : l.Mx) * gn, Math.Abs(planeY ? l.Mx : l.My) > 1e-9);
    }).ToList();

    static readonly SKColor Grid = SKColor.Parse("#e3e8ea"), Text = SKColor.Parse("#455a64"), Curve0 = SKColor.Parse("#216897"),
        Fill = SKColor.Parse("#216897").WithAlpha(28), Point = SKColor.Parse("#263640"), Selected = SKColor.Parse("#d32f2f"), Other = SKColor.Parse("#bd6d00");

    /// <summary>The diagram: M across, compression up (−N), the boundary and the combinations; the selected one in red.</summary>
    public static byte[] Png(IReadOnlyList<InteractionPoint> curve, IReadOnlyList<InteractionLoad> loads, string? selected, bool planeY,
        int width = 900, int height = 620, float font = 15)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        if (curve.Count < 3) return Encode(surface);
        var xs = curve.Select(c => c.M).Concat(loads.Select(l => l.M)).ToList();
        var ys = curve.Select(c => -c.N).Concat(loads.Select(l => -l.N)).ToList();
        double x0 = xs.Min(), x1 = xs.Max(), y0 = ys.Min(), y1 = ys.Max();
        double padX = (x1 - x0) * 0.06 + 1e-6, padY = (y1 - y0) * 0.06 + 1e-6;
        (x0, x1, y0, y1) = (x0 - padX, x1 + padX, y0 - padY, y1 + padY);
        float left = font * 4.2f, right = width - font, top = font * 1.6f, bottom = height - font * 2;
        SKPoint P(double m, double negN) => new(left + (float)((m - x0) / (x1 - x0)) * (right - left), bottom - (float)((negN - y0) / (y1 - y0)) * (bottom - top));
        using var f = new SKFont(SectionSketch.Typeface(), font);
        using var text = new SKPaint { Color = Text, IsAntialias = true };
        using var grid = new SKPaint { Color = Grid, StrokeWidth = 1, IsAntialias = true };
        foreach (var (v, vertical) in Ticks(x0, x1).Select(v => (v, true)).Concat(Ticks(y0, y1).Select(v => (v, false))))
        {
            if (vertical)
            {
                var a = P(v, y0); canvas.DrawLine(a.X, top, a.X, bottom, grid);
                SectionSketch.Label(canvas, Fmt(v), a.X, bottom + font * 1.3f, SKTextAlign.Center, f, text);
            }
            else
            {
                var a = P(x0, v); canvas.DrawLine(left, a.Y, right, a.Y, grid);
                SectionSketch.Label(canvas, Fmt(v), font * 0.4f, a.Y + font * 0.35f, SKTextAlign.Left, f, text);
            }
        }
        using var axis = new SKPaint { Color = SKColor.Parse("#9eacb3"), StrokeWidth = 1.2f, IsAntialias = true };
        canvas.DrawLine(P(0, y0).X, top, P(0, y0).X, bottom, axis);
        canvas.DrawLine(left, P(x0, 0).Y, right, P(x0, 0).Y, axis);
        SectionSketch.Label(canvas, planeY ? "My, кН·м" : "Mx, кН·м", right - font * 4, P(x0, 0).Y - font * 0.5f, SKTextAlign.Center, f, text);
        SectionSketch.Label(canvas, "−N, кН (сжатие вверх)", left + 4, top + font, SKTextAlign.Left, f, text);

        using var path = new SKPath();
        path.MoveTo(P(curve[0].M, -curve[0].N));
        foreach (var c in curve.Skip(1)) path.LineTo(P(c.M, -c.N));
        path.Close();
        using var fill = new SKPaint { Color = Fill, IsAntialias = true };
        using var line = new SKPaint { Color = Curve0, StrokeWidth = 2.2f, Style = SKPaintStyle.Stroke, IsAntialias = true };
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, line);

        foreach (var l in loads.OrderBy(l => l.Name == selected))
        {
            bool sel = l.Name == selected;
            var at = P(l.M, -l.N);
            using var dot = new SKPaint { Color = sel ? Selected : l.OtherPlane ? Other : Point, IsAntialias = true };
            canvas.DrawCircle(at, sel ? font * 0.42f : font * 0.3f, dot);
            SectionSketch.Label(canvas, l.Name, at.X + font * 0.5f, at.Y - font * 0.4f, SKTextAlign.Left, f, sel ? dot : text);
        }
        return Encode(surface);
    }

    static byte[] Encode(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    static string Fmt(double v) => v.ToString("0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));

    static IEnumerable<double> Ticks(double lo, double hi)
    {
        double raw = (hi - lo) / 6, mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double step = new[] { 1, 2, 2.5, 5, 10 }.Select(k => k * mag).First(s => s >= raw);
        for (double v = Math.Ceiling(lo / step) * step; v <= hi; v += step) yield return Math.Abs(v) < step * 1e-9 ? 0 : v;
    }
}
