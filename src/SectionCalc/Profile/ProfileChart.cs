using System.Globalization;
using SkiaSharp;

namespace SectionCalc.Profile;

/// <summary>
/// Diagrams along the element against the PLAXIS level: envelopes of N, M, Q of the included phases and, after the
/// calculation, the required reinforcement (wall: left face to the left, right face to the right; pile: the ring).
/// </summary>
public static class ProfileChart
{
    static readonly SKColor Text = new(0x26, 0x32, 0x38), Muted = new(0x61, 0x61, 0x61), Grid = new(0xE3, 0xE8, 0xEA),
        Axis = new(0x9E, 0xAC, 0xB3), Envelope = new(0x21, 0x68, 0x97), EnvelopeFill = new(0x21, 0x68, 0x97, 0x30),
        Left = new(0x2F, 0x6E, 0xD8), LeftFill = new(0x2F, 0x6E, 0xD8, 0x55), Right = new(0x8E, 0x3B, 0xA8), RightFill = new(0x8E, 0x3B, 0xA8, 0x55),
        Min = new(0xBD, 0x6D, 0x00), Bad = new(0xD3, 0x2F, 0x2F);
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static byte[] Png(ForceProfile forces, ReinforcementProfile? result, int width, int height, float fontPx = 12)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        Draw(canvas, forces, result, width, height, fontPx);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    sealed record Panel(string Title, Func<EnvelopePoint, (double Lo, double Hi)> Envelope, bool Symmetric = false);

    public static void Draw(SKCanvas canvas, ForceProfile f, ReinforcementProfile? r, float width, float height, float fontPx)
    {
        using var font = new SKFont(SectionSketch.Typeface(), fontPx);
        using var small = new SKFont(SectionSketch.Typeface(), fontPx * 0.85f);
        using var text = new SKPaint { Color = Text, IsAntialias = true };
        using var muted = new SKPaint { Color = Muted, IsAntialias = true };
        using var grid = new SKPaint { Color = Grid, StrokeWidth = 1 };
        using var axis = new SKPaint { Color = Axis, StrokeWidth = 1 };

        float left = fontPx * 4.2f, top = fontPx * 3.2f, bottom = height - fontPx * 1.2f;
        double z0 = f.TopLevel, z1 = f.BottomLevel;
        float Y(double z) => (float)(top + (z0 - z) / Math.Max(z0 - z1, 1e-6) * (bottom - top));

        var panels = new List<Panel>
        {
            new("N, кН", e => (e.NMin, e.NMax)),
            new("M, кН·м", e => (e.MMin, e.MMax)),
            new("|Q|, кН", e => (0, e.QAbsMax)),
        };
        bool wall = f.ElementKind == "Plate";
        int count = panels.Count + (r is null ? 0 : 1) + (r?.Stations.Any(s => s.AswCm2PerM is not null) == true ? 1 : 0);
        float gap = fontPx * 1.6f;
        // The reinforcement panel is twice as wide.
        float unit = (width - left - fontPx - gap * (count - 1)) / (count + (r is null ? 0 : 1));

        // Level axis with grid lines.
        double step = Nice((z0 - z1) / 10);
        for (double z = Math.Ceiling(z1 / step) * step; z <= z0 + 1e-9; z += step)
        {
            canvas.DrawLine(left, Y(z), width - fontPx, Y(z), grid);
            SectionSketch.Label(canvas, z.ToString("0.##", Ru), left - fontPx * 0.4f, Y(z) + fontPx * 0.35f, SKTextAlign.Right, small, muted);
        }
        // Group of piles laid from their heads: levels of the highest head.
        SectionSketch.Label(canvas, f.Piles.Any(p => Math.Abs(p.Shift) > 1e-6) ? "Отм.*, м" : "Отм., м", left - fontPx * 0.4f, top - fontPx * 1.6f, SKTextAlign.Right, small, muted);

        float x = left;
        foreach (var panel in panels)
        {
            DrawEnvelope(canvas, f, panel, x, unit, Y, top, bottom, font, small, text, muted, axis);
            x += unit + gap;
        }
        if (r is null) return;

        // Required reinforcement, cm², stepped over the station intervals.
        float w = unit * 2;
        var st = r.Stations;
        double max = Math.Max(1e-6, st.Max(s => Math.Max(s.AsRightMm2, s.AsLeftMm2)) / 100);
        int side = r.Project.Settings.ExcavationSide;
        string Face(int face) => side == 0 ? (face > 0 ? "правая" : "левая") : side == face ? "котлован" : "грунт";
        SectionSketch.Label(canvas, wall ? $"As, см²: {Face(-1)} | {Face(1)}" : f.IsGroup ? $"As, см² (группа {f.Piles.Count} свай)" : "As, см²", x + w / 2, top - fontPx * 1.6f, SKTextAlign.Center, font, text);
        float mid = wall ? x + w / 2 : x;
        float scale = (float)((wall ? w / 2 : w) * 0.92 / max);
        canvas.DrawLine(mid, top, mid, bottom, axis);
        using var lf = new SKPaint { Color = LeftFill, IsAntialias = true };
        using var rf = new SKPaint { Color = RightFill, IsAntialias = true };
        using var ls = new SKPaint { Color = Left, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        using var rs = new SKPaint { Color = Right, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        using var bad = new SKPaint { Color = Bad, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        foreach (var s in st)
        {
            float yt = Y(s.Station.Top), yb = Y(s.Station.Bottom);
            float right = (float)(s.AsRightMm2 / 100) * scale;
            var rr = new SKRect(mid, yt, mid + right, yb);
            canvas.DrawRect(rr, rf); canvas.DrawRect(rr, rs);
            if (wall)
            {
                float leftW = (float)(s.AsLeftMm2 / 100) * scale;
                var lr = new SKRect(mid - leftW, yt, mid, yb);
                canvas.DrawRect(lr, lf); canvas.DrawRect(lr, ls);
            }
            if (s.Status is "exceeded" or "indeterminate") canvas.DrawRect(new SKRect(mid - (wall ? w / 2 : 0), yt, mid + (wall ? w / 2 : w), yb), bad);
        }
        foreach (var s in st)
        {
            float yc = (Y(s.Station.Top) + Y(s.Station.Bottom)) / 2 + fontPx * 0.3f;
            if (s.Station.Top - s.Station.Bottom < (z0 - z1) / 40) continue;
            SectionSketch.Label(canvas, (s.AsRightMm2 / 100).ToString("0.0", Ru), mid + (float)(s.AsRightMm2 / 100) * scale + 3, yc, SKTextAlign.Left, small, text);
            if (wall)
                SectionSketch.Label(canvas, (s.AsLeftMm2 / 100).ToString("0.0", Ru), mid - (float)(s.AsLeftMm2 / 100) * scale - 3, yc, SKTextAlign.Right, small, text);
        }
        x += w + gap;

        if (st.Any(s => s.AswCm2PerM is not null))
        {
            double amax = Math.Max(1e-6, st.Max(s => s.AswCm2PerM ?? 0));
            SectionSketch.Label(canvas, "Asw/sw, см²/м", x + unit / 2, top - fontPx * 1.6f, SKTextAlign.Center, font, text);
            canvas.DrawLine(x, top, x, bottom, axis);
            using var fill = new SKPaint { Color = new SKColor(0x33, 0x7C, 0x3B, 0x55), IsAntialias = true };
            using var line = new SKPaint { Color = new SKColor(0x33, 0x7C, 0x3B), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
            foreach (var s in st.Where(s => s.AswCm2PerM is not null))
            {
                var rect = new SKRect(x, Y(s.Station.Top), x + (float)(s.AswCm2PerM!.Value / amax * unit * 0.85), Y(s.Station.Bottom));
                canvas.DrawRect(rect, fill); canvas.DrawRect(rect, line);
            }
            SectionSketch.Label(canvas, amax.ToString("0.0#", Ru), x + unit * 0.85f, bottom + fontPx, SKTextAlign.Center, small, muted);
        }
    }

    static void DrawEnvelope(SKCanvas canvas, ForceProfile f, Panel panel, float x, float w, Func<double, float> Y, float top, float bottom,
        SKFont font, SKFont small, SKPaint text, SKPaint muted, SKPaint axis)
    {
        var pts = f.Envelope.Select(e => (e.Level, panel.Envelope(e))).ToList();
        var all = f.OriginalEnvelope.Select(panel.Envelope).ToList();
        double lo = Math.Min(0, all.Min(p => p.Lo)), hi = Math.Max(0, all.Max(p => p.Hi));
        if (hi - lo < 1e-9) hi = lo + 1;
        float X(double v) => (float)(x + (v - lo) / (hi - lo) * w);
        SectionSketch.Label(canvas, panel.Title, x + w / 2, top - font.Size * 1.6f, SKTextAlign.Center, font, text);
        canvas.DrawLine(X(0), top, X(0), bottom, axis);
        using var path = new SKPath();
        path.MoveTo(X(pts[0].Item2.Hi), Y(pts[0].Level));
        foreach (var p in pts) path.LineTo(X(p.Item2.Hi), Y(p.Level));
        for (int i = pts.Count - 1; i >= 0; i--) path.LineTo(X(pts[i].Item2.Lo), Y(pts[i].Level));
        path.Close();
        using var fill = new SKPaint { Color = EnvelopeFill, IsAntialias = true };
        using var line = new SKPaint { Color = Envelope, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f };
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, line);
        // Peak cuts: the envelope before the cut, dashed.
        var orig = f.OriginalEnvelope.Select(e => (e.Level, panel.Envelope(e))).ToList();
        if (orig.Count == pts.Count && orig.Zip(pts).Any(z => Math.Abs(z.First.Item2.Lo - z.Second.Item2.Lo) + Math.Abs(z.First.Item2.Hi - z.Second.Item2.Hi) > 1e-6))
        {
            using var dash = new SKPaint { Color = Bad, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f,
                PathEffect = SKPathEffect.CreateDash([5, 4], 0) };
            using var o = new SKPath();
            // Only where the cut changed the diagram.
            foreach (var upper in new[] { true, false })
            {
                double V((double Lo, double Hi) v) => upper ? v.Hi : v.Lo;
                bool Changed(int i) => Math.Abs(V(orig[i].Item2) - V(pts[i].Item2)) > 1e-6;
                for (int i = 1; i < orig.Count; i++)
                    if (Changed(i) || Changed(i - 1))
                    {
                        o.MoveTo(X(V(orig[i - 1].Item2)), Y(orig[i - 1].Level));
                        o.LineTo(X(V(orig[i].Item2)), Y(orig[i].Level));
                    }
            }
            canvas.DrawPath(o, dash);
        }
        var ext = new[] { (pts.MinBy(p => p.Item2.Lo), true), (pts.MaxBy(p => p.Item2.Hi), false) };
        foreach (var ((level, (l, h)), isLo) in ext)
        {
            double v = isLo ? l : h;
            if (Math.Abs(v) < 1e-9) continue;
            SectionSketch.Label(canvas, v.ToString("0", Ru), X(v), Y(level) + (isLo ? -4 : small.Size + 2), isLo ? SKTextAlign.Left : SKTextAlign.Right, small, text);
        }
        // The zero end is the axis line itself; label only the non-zero ends.
        if (Math.Abs(lo) > 1e-9) SectionSketch.Label(canvas, lo.ToString("0", Ru), x, bottom + font.Size, SKTextAlign.Left, small, muted);
        if (Math.Abs(hi) > 1e-9) SectionSketch.Label(canvas, hi.ToString("0", Ru), x + w, bottom + font.Size, SKTextAlign.Right, small, muted);
    }

    static double Nice(double raw)
    {
        double p = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-9))));
        double m = raw / p;
        return (m <= 1 ? 1 : m <= 2 ? 2 : m <= 5 ? 5 : 10) * p;
    }
}
