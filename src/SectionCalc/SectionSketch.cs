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
        bool custom = p.Kind == SectionKind.Custom && p.Custom.OutlineProblems().Count == 0;
        if (p.Kind == SectionKind.Custom && !custom) return;
        var props = custom ? p.Custom.Properties() : null;
        double w = props?.WidthMm ?? (p.Kind == SectionKind.Pile ? p.Pile.DiameterMm : p.Wall.WidthMm);
        double h = props?.HeightMm ?? (p.Kind == SectionKind.Pile ? p.Pile.DiameterMm : p.Wall.ThicknessMm);
        if (!(w > 0 && h > 0)) return;
        // Bounding-box centre of a custom outline at the centre of the picture.
        double ox = props is null ? 0 : (props.MinX + props.MaxX) / 2, oy = props is null ? 0 : (props.MinY + props.MaxY) / 2;
        float margin = Math.Max(58, fontPx * 3.2f);
        float scale = (float)Math.Min((width - 2 * margin) / w, (height - 2 * margin) / h);
        float cx = width / 2, cy = height / 2;
        SKPoint P(double x, double y) => new(cx + (float)(x - ox) * scale, cy - (float)(y - oy) * scale);

        using var fill = new SKPaint { Color = Concrete, IsAntialias = true };
        using var stroke = new SKPaint { Color = Outline, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        using var tie = new SKPaint { Color = Tie, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f,
            PathEffect = SKPathEffect.CreateDash([6, 4], 0) };
        using var bar = new SKPaint { Color = layout.Fits ? Bar : Error, IsAntialias = true };
        using var font = new SKFont(Typeface(), fontPx);
        using var text = new SKPaint { Color = Text, IsAntialias = true };

        if (custom)
        {
            var g = p.Custom;
            using var path = new SKPath { FillType = SKPathFillType.EvenOdd };
            foreach (var ring in new[] { g.Outline() }.Concat(g.Holes()))
            {
                path.MoveTo(P(ring[0].X, ring[0].Y));
                foreach (var v in ring.Skip(1)) path.LineTo(P(v.X, v.Y));
                path.Close();
            }
            canvas.DrawPath(path, fill);
            canvas.DrawPath(path, stroke);
            var (x0, x1, y0, y1) = (props!.MinX, props.MaxX, props.MinY, props.MaxY);
            if (g.IsRound)
                Dimension(canvas, P(x0, y0), P(x1, y0), 26, $"D = {g.DiameterMm:0}", font, text);
            else
            {
                Dimension(canvas, P(x0, y0), P(x1, y0), 26, $"{w:0}", font, text);
                Dimension(canvas, P(x1, y0), P(x1, y1), 30, $"{h:0}", font, text, vertical: true);
            }
            // Centroid: the point the forces are referred to.
            using var c = new SKPaint { Color = Plus, IsAntialias = true, StrokeWidth = 1.4f, Style = SKPaintStyle.Stroke };
            var co = P(props.CxMm, props.CyMm); float k = fontPx * 0.7f;
            canvas.DrawCircle(co, k * 0.55f, c);
            canvas.DrawLine(co.X - k, co.Y, co.X + k, co.Y, c);
            canvas.DrawLine(co.X, co.Y - k, co.X, co.Y + k, c);
        }
        else if (p.Kind == SectionKind.Pile)
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
            Dimension(canvas, P(-w / 2, -h / 2), P(w / 2, -h / 2), 26 + font.Size * 1.2f, $"b = {g.WidthMm:0}", font, text);
            Dimension(canvas, P(w / 2, -h / 2), P(w / 2, h / 2), 30, $"h = {g.ThicknessMm:0}", font, text, vertical: true);
            using var plus = new SKPaint { Color = Plus, IsAntialias = true };
            // Face names above and below the middle of the faces, clear of the dimensions and the axes.
            Label(canvas, "грань «+»", P(0, h / 2).X, P(0, h / 2).Y - font.Size * 0.5f, SKTextAlign.Center, font, plus);
            Label(canvas, "грань «−»", P(0, -h / 2).X, P(0, -h / 2).Y + font.Size * 1.1f, SKTextAlign.Center, font, text);
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
