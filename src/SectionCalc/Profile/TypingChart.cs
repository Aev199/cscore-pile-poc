using System.Globalization;
using SkiaSharp;

namespace SectionCalc.Profile;

/// <summary>Pile types: the plan of the piles coloured by type and the reinforcement of the selected type along its length.</summary>
public static class TypingChart
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    public static readonly SKColor[] Palette =
    [
        new(0x2F, 0x6E, 0xD8), new(0xE0, 0x7A, 0x1F), new(0x2E, 0x9E, 0x5B), new(0xC2, 0x3B, 0x6B), new(0x7B, 0x4F, 0xC9),
        new(0x1F, 0xA3, 0xB8), new(0xB8, 0x8A, 0x1F), new(0x8E, 0x5A, 0x3B), new(0x5B, 0x6B, 0x78), new(0xD9, 0x4A, 0x3A),
    ];
    static readonly SKColor Text = new(0x26, 0x36, 0x40), Muted = new(0x7A, 0x8A, 0x94), Grid = new(0xE6, 0xEA, 0xEC);

    public static SKColor Color(int type) => Palette[(type - 1) % Palette.Length];

    public static byte[] Png(TypingResult r, int selected, int width, int height, float fontPx)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        using var font = new SKFont(SectionSketch.Typeface(), fontPx);
        using var small = new SKFont(SectionSketch.Typeface(), fontPx * 0.8f);
        float split = width * 0.45f;
        Plan(canvas, r, selected, new SKRect(0, 0, split, height), font, small);
        if (r.Types.FirstOrDefault(t => t.Number == selected) is { } type)
            Profile(canvas, r, type, new SKRect(split + fontPx, 0, width, height), font, small);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    static void Plan(SKCanvas canvas, TypingResult r, int selected, SKRect box, SKFont font, SKFont small)
    {
        using var text = new SKPaint { Color = Text, IsAntialias = true };
        using var muted = new SKPaint { Color = Muted, IsAntialias = true };
        Label(canvas, "План свай по типам", box.MidX, box.Top + font.Size * 1.4f, SKTextAlign.Center, font, text);
        var piles = r.Demands.Piles;
        double x0 = piles.Min(p => p.Info.X), x1 = piles.Max(p => p.Info.X), y0 = piles.Min(p => p.Info.Y), y1 = piles.Max(p => p.Info.Y);
        float pad = font.Size * 2.2f, top = box.Top + font.Size * 3f, bottom = box.Bottom - font.Size * 2.5f;
        double span = Math.Max(Math.Max(x1 - x0, y1 - y0), 1);
        float scale = (float)Math.Min((box.Width - 2 * pad) / Math.Max(x1 - x0, 1e-6), (bottom - top) / Math.Max(y1 - y0, 1e-6));
        if (!float.IsFinite(scale) || scale <= 0) scale = (float)((box.Width - 2 * pad) / span);
        scale = Math.Min(scale, (float)((box.Width - 2 * pad) / span) * 4);
        float cx = box.MidX, cy = (top + bottom) / 2;
        SKPoint P(PileInfo p) => new(cx + (float)(p.X - (x0 + x1) / 2) * scale, cy - (float)(p.Y - (y0 + y1) / 2) * scale);
        // Circles do not overlap: radius from the nearest neighbour.
        float radius = font.Size * 0.9f;
        for (int i = 0; i < piles.Count; i++)
            for (int j = i + 1; j < piles.Count; j++)
            {
                var a = P(piles[i].Info); var b = P(piles[j].Info);
                radius = Math.Min(radius, Math.Max(3, SKPoint.Distance(a, b) * 0.45f));
            }
        var typeOf = r.Types.SelectMany(t => t.Members.Select(m => (m.Pile, t.Number))).ToDictionary(x => x.Pile, x => x.Number);
        foreach (var p in piles)
        {
            int t = typeOf[p.Pile];
            var c = P(p.Info);
            using var fill = new SKPaint { Color = Color(t).WithAlpha(t == selected ? (byte)255 : (byte)150), IsAntialias = true };
            canvas.DrawCircle(c, radius, fill);
            if (t == selected)
            {
                using var ring = new SKPaint { Color = Text, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
                canvas.DrawCircle(c, radius + 2, ring);
            }
            if (radius >= small.Size * 0.6f)
            {
                using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
                Label(canvas, t.ToString(Ru), c.X, c.Y + small.Size * 0.35f, SKTextAlign.Center, small, white);
            }
        }
        // Legend.
        float lx = box.Left + pad * 0.5f, ly = bottom + font.Size * 1.6f;
        foreach (var t in r.Types)
        {
            using var fill = new SKPaint { Color = Color(t.Number), IsAntialias = true };
            canvas.DrawCircle(lx + small.Size * 0.4f, ly - small.Size * 0.35f, small.Size * 0.45f, fill);
            string label = $"Т{t.Number} ({t.Members.Count})";
            Label(canvas, label, lx + small.Size * 1.1f, ly, SKTextAlign.Left, small, t.Number == selected ? text : muted);
            lx += Width(small, label) + small.Size * 2.2f;
            if (lx > box.Right - small.Size * 6) { lx = box.Left + pad * 0.5f; ly += small.Size * 1.5f; }
        }
    }

    static void Profile(SKCanvas canvas, TypingResult r, PileType type, SKRect box, SKFont font, SKFont small)
    {
        using var text = new SKPaint { Color = Text, IsAntialias = true };
        using var muted = new SKPaint { Color = Muted, IsAntialias = true };
        using var grid = new SKPaint { Color = Grid, StrokeWidth = 1 };
        bool shear = r.Demands.ShearChecked;
        float left = box.Left + font.Size * 3.2f, top = box.Top + font.Size * 3f, bottom = box.Bottom - font.Size * 1.6f;
        float gap = font.Size * 1.4f;
        float wAs = shear ? (box.Right - left - gap) * 0.68f : box.Right - left - font.Size;
        double z0 = type.Head, z1 = type.Tip;
        float Y(double z) => (float)(top + (z0 - z) / Math.Max(z0 - z1, 1e-6) * (bottom - top));
        double step = Nice((z0 - z1) / 8);
        for (double z = Math.Ceiling(z1 / step) * step; z <= z0 + 1e-9; z += step)
        {
            canvas.DrawLine(left, Y(z), box.Right, Y(z), grid);
            Label(canvas, z.ToString("0.#", Ru), left - small.Size * 0.4f, Y(z) + small.Size * 0.35f, SKTextAlign.Right, small, muted);
        }
        Label(canvas, $"Тип {type.Number}: As, см²", left + wAs / 2, box.Top + font.Size * 1.4f, SKTextAlign.Center, font, text);
        var color = Color(type.Number);

        // Required areas: every member (thin), the type envelope (filled), the zones as built (steps with labels).
        double max = Math.Max(type.Zones.Max(z => z.Bars.AreaMm2), type.As.Max()) / 100 * 1.08;
        float X(double cm2) => left + (float)(cm2 / max) * wAs;
        using (var fill = new SKPaint { Color = color.WithAlpha(50), IsAntialias = true })
        using (var path = new SKPath())
        {
            path.MoveTo(X(0), Y(type.Levels[0]));
            for (int i = 0; i < type.Levels.Length; i++) path.LineTo(X(type.As[i] / 100), Y(type.Levels[i]));
            path.LineTo(X(0), Y(type.Levels[^1]));
            path.Close();
            canvas.DrawPath(path, fill);
        }
        using (var thin = new SKPaint { Color = Muted.WithAlpha(150), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 })
            foreach (var m in type.Members)
            {
                using var path = new SKPath();
                for (int i = 0; i < m.Stations.Count; i++)
                {
                    var pt = new SKPoint(X(m.As[i] / 100), Y(m.Stations[i].Level));
                    if (i == 0) path.MoveTo(pt); else path.LineTo(pt);
                }
                canvas.DrawPath(path, thin);
            }
        using (var zone = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.2f })
        using (var bad = new SKPaint { Color = new SKColor(0xD3, 0x2F, 0x2F), IsAntialias = true })
            foreach (var z in type.Zones)
            {
                float x = X(z.Bars.AreaMm2 / 100);
                canvas.DrawRect(new SKRect(left, Y(z.Top), x, Y(z.Bottom)), zone);
                string label = z.Bars + (z.K is double k ? $"  K {k.ToString("0.00", Ru)}" : "");
                Label(canvas, label, Math.Min(x + 4, left + wAs - Width(small, label)), (Y(z.Top) + Y(z.Bottom)) / 2 + small.Size * 0.35f,
                    SKTextAlign.Left, small, z.K > 1 ? bad : text);
            }
        Label(canvas, (max / 1.08).ToString("0", Ru), left + wAs, bottom + small.Size * 1.3f, SKTextAlign.Right, small, muted);
        if (!shear) return;

        // Asw/sw: envelope and the spirals of the zones.
        float l2 = left + wAs + gap, w2 = box.Right - l2;
        Label(canvas, "Asw/sw, см²/м", l2 + w2 / 2, box.Top + font.Size * 1.4f, SKTextAlign.Center, font, text);
        double pitch = r.Demands.Project.Section.Conditions.ShearPitchMm;
        double SpiralAsw(double d) => 2 * Math.PI * d * d / 4 / pitch * 10;
        double max2 = Math.Max(1e-6, Math.Max(type.Asw.Where(double.IsFinite).DefaultIfEmpty(0).Max(),
            type.Zones.Select(z => z.SpiralMm is double d ? SpiralAsw(d) : 0).DefaultIfEmpty(0).Max())) * 1.1;
        float X2(double v) => l2 + (float)(v / max2) * w2;
        using (var fill = new SKPaint { Color = color.WithAlpha(50), IsAntialias = true })
            for (int i = 0; i < type.Levels.Length; i++)
            {
                if (!double.IsFinite(type.Asw[i])) continue;
                double half = i + 1 < type.Levels.Length ? (type.Levels[i] - type.Levels[i + 1]) / 2 : (type.Levels[Math.Max(0, i - 1)] - type.Levels[i]) / 2;
                canvas.DrawRect(new SKRect(l2, Y(type.Levels[i] + half), X2(type.Asw[i]), Y(type.Levels[i] - half)), fill);
            }
        using var spiral = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.2f };
        foreach (var z in type.Zones)
            if (z.SpiralMm is double d)
            {
                canvas.DrawRect(new SKRect(l2, Y(z.Top), X2(SpiralAsw(d)), Y(z.Bottom)), spiral);
                Label(canvas, $"Ø{d:0}", X2(SpiralAsw(d)) - 3, (Y(z.Top) + Y(z.Bottom)) / 2 + small.Size * 0.35f, SKTextAlign.Right, small, text);
            }
    }

    static float Width(SKFont font, string text)
    {
        using var measure = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        return measure.MeasureText(text);
    }

    static void Label(SKCanvas canvas, string text, float x, float y, SKTextAlign align, SKFont font, SKPaint paint)
    {
        float w = Width(font, text);
        canvas.DrawText(text, align == SKTextAlign.Center ? x - w / 2 : align == SKTextAlign.Right ? x - w : x, y, font, paint);
    }

    static double Nice(double v)
    {
        if (!(v > 0)) return 1;
        double e = Math.Pow(10, Math.Floor(Math.Log10(v)));
        return new[] { 1, 2, 2.5, 5, 10 }.Select(k => k * e).First(k => k >= v);
    }
}
