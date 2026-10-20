using SkiaSharp;

namespace SectionCalc;

/// <summary>Section drawing in the SoilStiffness palette; one renderer for the screen and the report.</summary>
public static class SectionSketch
{
    static readonly SKColor Concrete = SKColor.Parse("#edf1f2"), Outline = SKColor.Parse("#9eacb3"),
        Bar = SKColor.Parse("#263640"), Tie = SKColor.Parse("#216897"), Text = SKColor.Parse("#455a64"),
        Plus = SKColor.Parse("#bd6d00"), Error = SKColor.Parse("#d32f2f");

    public static byte[] Png(SectionProject p, int width = 640, int height = 520, float fontPx = 13)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        Draw(surface.Canvas, p, width, height, fontPx);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static void Draw(SKCanvas canvas, SectionProject p, float width, float height, float fontPx = 13)
    {
        canvas.Clear(SKColors.White);
        var layout = RequestBuilder.Layout(p);
        double w = p.Kind == SectionKind.Pile ? p.Pile.DiameterMm : p.Wall.WidthMm;
        double h = p.Kind == SectionKind.Pile ? p.Pile.DiameterMm : p.Wall.ThicknessMm;
        if (!(w > 0 && h > 0)) return;
        float margin = Math.Max(58, fontPx * 3.2f);
        float scale = (float)Math.Min((width - 2 * margin) / w, (height - 2 * margin) / h);
        float cx = width / 2, cy = height / 2;
        SKPoint P(double x, double y) => new(cx + (float)x * scale, cy - (float)y * scale);

        using var fill = new SKPaint { Color = Concrete, IsAntialias = true };
        using var stroke = new SKPaint { Color = Outline, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        using var tie = new SKPaint { Color = Tie, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f,
            PathEffect = SKPathEffect.CreateDash([6, 4], 0) };
        using var bar = new SKPaint { Color = layout.Fits ? Bar : Error, IsAntialias = true };
        using var font = new SKFont(Typeface(), fontPx);
        using var text = new SKPaint { Color = Text, IsAntialias = true };

        if (p.Kind == SectionKind.Pile)
        {
            var g = p.Pile;
            float r = (float)(g.DiameterMm / 2) * scale;
            canvas.DrawCircle(cx, cy, r, fill);
            canvas.DrawCircle(cx, cy, r, stroke);
            float rt = (float)(g.DiameterMm / 2 - g.SpiralCoverMm - g.SpiralDiameterMm / 2) * scale;
            canvas.DrawCircle(cx, cy, rt, tie);
            Dimension(canvas, P(-w / 2, -h / 2 - 0), P(w / 2, -h / 2), 26, $"D = {g.DiameterMm:0}", font, text);
        }
        else
        {
            var g = p.Wall;
            var rect = new SKRect(P(-w / 2, h / 2).X, P(-w / 2, h / 2).Y, P(w / 2, -h / 2).X, P(w / 2, -h / 2).Y);
            canvas.DrawRect(rect, fill);
            canvas.DrawRect(rect, stroke);
            double t = Math.Min(g.StirrupCoverMm(g.PositiveFace.BarDiameterMm), g.StirrupCoverMm(g.NegativeFace.BarDiameterMm)) + g.StirrupDiameterMm / 2;
            canvas.DrawRect(new SKRect(P(-w / 2 + t, h / 2 - t).X, P(-w / 2 + t, h / 2 - t).Y, P(w / 2 - t, -h / 2 + t).X, P(w / 2 - t, -h / 2 + t).Y), tie);
            Dimension(canvas, P(-w / 2, -h / 2), P(w / 2, -h / 2), 26, $"b = {g.WidthMm:0}", font, text);
            Dimension(canvas, P(w / 2, -h / 2), P(w / 2, h / 2), 30, $"h = {g.ThicknessMm:0}", font, text, vertical: true);
            using var plus = new SKPaint { Color = Plus, IsAntialias = true };
            Label(canvas, "грань «+»", P(-w / 2, h / 2).X, P(-w / 2, h / 2).Y - font.Size * 0.5f, SKTextAlign.Left, font, plus);
            Label(canvas, "грань «−»", P(-w / 2, -h / 2).X, P(-w / 2, -h / 2).Y + font.Size * 1.3f, SKTextAlign.Left, font, text);
        }
        foreach (var b in layout.Bars)
            canvas.DrawCircle(P(b.XMm, b.YMm), Math.Max(fontPx * 0.25f, (float)b.DiameterMm / 2 * scale), bar);

        // Axes: Mx bends about x (stress varies along y); positive Mx stretches the +y face.
        using var axis = new SKPaint { Color = Text, IsAntialias = true, StrokeWidth = 1 };
        float arm = fontPx * 2.4f;
        var o = new SKPoint(fontPx * 1.2f, height - fontPx * 1.2f);
        canvas.DrawLine(o, new SKPoint(o.X + arm, o.Y), axis);
        canvas.DrawLine(o, new SKPoint(o.X, o.Y - arm), axis);
        Label(canvas, "x", o.X + arm + 4, o.Y + fontPx * 0.35f, SKTextAlign.Left, font, text);
        Label(canvas, "y", o.X - fontPx * 0.3f, o.Y - arm - 4, SKTextAlign.Left, font, text);
    }

    /// <summary>First installed family of the design code that has Cyrillic glyphs.</summary>
    internal static SKTypeface Typeface()
    {
        foreach (var family in new[] { "Roboto", "Segoe UI", "Arial", "Liberation Sans", "DejaVu Sans" })
        {
            var face = SKTypeface.FromFamilyName(family);
            if (face is not null && face.FamilyName == family && face.ContainsGlyphs("гр")) return face;
        }
        return SKFontManager.Default.MatchCharacter('г') ?? SKTypeface.Default;
    }

    internal static void Label(SKCanvas canvas, string label, float x, float y, SKTextAlign align, SKFont font, SKPaint paint)
    {
        using var measure = new SKPaint { Typeface = font.Typeface, TextSize = font.Size };
        float shift = align == SKTextAlign.Center ? measure.MeasureText(label) / 2 : 0;
        canvas.DrawText(label, x - shift, y, font, paint);
    }

    static void Dimension(SKCanvas canvas, SKPoint a, SKPoint b, float offset, string label, SKFont font, SKPaint paint, bool vertical = false)
    {
        using var line = new SKPaint { Color = paint.Color, IsAntialias = true, StrokeWidth = 1 };
        if (vertical)
        {
            float x = a.X + offset;
            canvas.DrawLine(x, a.Y, x, b.Y, line);
            float gap = font.Size * 1.1f;
            canvas.DrawLine(a.X + 4, a.Y, x + 4, a.Y, line);
            canvas.DrawLine(b.X + 4, b.Y, x + 4, b.Y, line);
            canvas.Save();
            canvas.RotateDegrees(-90, x + gap, (a.Y + b.Y) / 2);
            Label(canvas, label, x + gap, (a.Y + b.Y) / 2, SKTextAlign.Center, font, paint);
            canvas.Restore();
        }
        else
        {
            float y = a.Y + offset;
            canvas.DrawLine(a.X, y, b.X, y, line);
            canvas.DrawLine(a.X, a.Y + 4, a.X, y + 4, line);
            canvas.DrawLine(b.X, b.Y + 4, b.X, y + 4, line);
            Label(canvas, label, (a.X + b.X) / 2, y + font.Size * 1.25f, SKTextAlign.Center, font, paint);
        }
    }
}
