using System.Text.Json.Serialization;

namespace SectionCalc;

/// <summary>Concrete outline of an arbitrary section: a parametric template or a user polygon.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShapeKind { Rectangle, Circle, Ring, Tee, IBeam, Box, Polygon }

/// <summary>How the bars of an arbitrary section are given.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BarArrangement
{
    /// <summary>Bars at the corners of the contour inset by a, intermediate ones at a spacing not above s.</summary>
    Contour,
    /// <summary>Bars from the table (x, y, Ø).</summary>
    Manual,
}

public sealed class Vertex
{
    public double X { get; set; }
    public double Y { get; set; }
    public Vertex() { }
    public Vertex(double x, double y) { X = x; Y = y; }
}

public sealed class BarSpec
{
    public double X { get; set; }
    public double Y { get; set; }
    public double DiameterMm { get; set; } = 20;
    public BarSpec() { }
    public BarSpec(double x, double y, double d) { X = x; Y = y; DiameterMm = d; }
}

/// <summary>Gross concrete properties (holes deducted), mm; centroid in the input coordinates.</summary>
public sealed record ConcreteProperties(double AreaMm2, double CxMm, double CyMm, double IxMm4, double IyMm4,
    double MinX, double MaxX, double MinY, double MaxY)
{
    /// <summary>Size along y: the height in the plane of Mx.</summary>
    public double HeightMm => MaxY - MinY;
    /// <summary>Size along x: the height in the plane of My.</summary>
    public double WidthMm => MaxX - MinX;
    /// <summary>Radius of gyration about x (Mx bends about x).</summary>
    public double RadiusXMm => Math.Sqrt(IxMm4 / AreaMm2);
    public double RadiusYMm => Math.Sqrt(IyMm4 / AreaMm2);
}

/// <summary>
/// Arbitrary section: any concrete outline (with holes) and any bars. Coordinates are in mm with
/// the origin at the centre of the template's bounding box; the engine gets them shifted to the
/// concrete centroid, so the forces act at the centroid. Mx &gt; 0 stretches the +y side.
/// </summary>
public sealed class CustomGeometry
{
    public const int CircleSides = 96;

    public ShapeKind Shape { get; set; } = ShapeKind.Rectangle;
    /// <summary>Width b (rectangle, box).</summary>
    public double WidthMm { get; set; } = 400;
    /// <summary>Height h (rectangle, box, tee, I).</summary>
    public double HeightMm { get; set; } = 600;
    /// <summary>Outer diameter (circle, ring).</summary>
    public double DiameterMm { get; set; } = 600;
    /// <summary>Inner diameter (ring).</summary>
    public double InnerDiameterMm { get; set; } = 300;
    /// <summary>Flange width bf (tee, I).</summary>
    public double FlangeWidthMm { get; set; } = 800;
    /// <summary>Flange thickness hf (tee, I).</summary>
    public double FlangeThicknessMm { get; set; } = 150;
    /// <summary>Web thickness bw (tee, I).</summary>
    public double WebMm { get; set; } = 250;
    /// <summary>Wall thickness t (box).</summary>
    public double WallMm { get; set; } = 150;
    /// <summary>Polygon vertices, mm, in order (either direction).</summary>
    public List<Vertex> Vertices { get; set; } = [new(-200, -300), new(200, -300), new(200, 300), new(-200, 300)];

    public BarArrangement Arrangement { get; set; } = BarArrangement.Contour;
    /// <summary>Contour arrangement: distance from the concrete face to the bar axes.</summary>
    public double AxisDistanceMm { get; set; } = 50;
    public double BarDiameterMm { get; set; } = 20;
    /// <summary>Contour arrangement: largest spacing of the bar axes along the contour.</summary>
    public double MaxSpacingMm { get; set; } = 200;
    /// <summary>Manual arrangement: the bar table.</summary>
    public List<BarSpec> Bars { get; set; } = [];

    public double MinClearSpacingMm { get; set; } = 25;
    /// <summary>Transverse bars for the shear check.</summary>
    public double StirrupDiameterMm { get; set; } = 10;

    public bool IsRound => Shape is ShapeKind.Circle or ShapeKind.Ring;

    // ---- outline ----

    /// <summary>Outer contour, counterclockwise.</summary>
    public List<Vertex> Outline()
    {
        double b = WidthMm / 2, h = HeightMm / 2, f = FlangeWidthMm / 2, w = WebMm / 2, t = FlangeThicknessMm;
        var ring = Shape switch
        {
            ShapeKind.Rectangle or ShapeKind.Box => Rect(b, h),
            ShapeKind.Circle or ShapeKind.Ring => Circle(DiameterMm / 2),
            ShapeKind.Tee => [new(-w, -h), new(w, -h), new(w, h - t), new(f, h - t), new(f, h), new(-f, h), new(-f, h - t), new(-w, h - t)],
            ShapeKind.IBeam => [new(-f, -h), new(f, -h), new(f, -h + t), new(w, -h + t), new(w, h - t), new(f, h - t), new(f, h),
                new(-f, h), new(-f, h - t), new(-w, h - t), new(-w, -h + t), new(-f, -h + t)],
            _ => Vertices.Select(v => new Vertex(v.X, v.Y)).ToList(),
        };
        return Oriented(ring, counterClockwise: true);
    }

    /// <summary>Holes, clockwise.</summary>
    public List<List<Vertex>> Holes() => Shape switch
    {
        ShapeKind.Ring => [Oriented(Circle(InnerDiameterMm / 2), false)],
        ShapeKind.Box => [Oriented(Rect(WidthMm / 2 - WallMm, HeightMm / 2 - WallMm), false)],
        _ => [],
    };

    static List<Vertex> Rect(double b, double h) => [new(-b, -h), new(b, -h), new(b, h), new(-b, h)];

    static List<Vertex> Circle(double r) =>
        Enumerable.Range(0, CircleSides).Select(i => new Vertex(r * Math.Cos(2 * Math.PI * i / CircleSides), r * Math.Sin(2 * Math.PI * i / CircleSides))).ToList();

    static List<Vertex> Oriented(List<Vertex> ring, bool counterClockwise)
    {
        if (SignedArea(ring) > 0 != counterClockwise) ring.Reverse();
        return ring;
    }

    internal static double SignedArea(IReadOnlyList<Vertex> r) =>
        Enumerable.Range(0, r.Count).Sum(i => r[i].X * r[(i + 1) % r.Count].Y - r[(i + 1) % r.Count].X * r[i].Y) / 2;

    /// <summary>Problems of the outline itself (sizes, self-intersection); empty when it can be calculated.</summary>
    public List<string> OutlineProblems()
    {
        var e = new List<string>();
        bool Pos(params double[] v) => v.All(x => double.IsFinite(x) && x > 0);
        switch (Shape)
        {
            case ShapeKind.Rectangle: if (!Pos(WidthMm, HeightMm)) e.Add("Задайте b и h больше 0"); break;
            case ShapeKind.Circle: if (!Pos(DiameterMm)) e.Add("Задайте D больше 0"); break;
            case ShapeKind.Ring:
                if (!Pos(DiameterMm, InnerDiameterMm) || InnerDiameterMm >= DiameterMm) e.Add("Кольцо: 0 < d < D"); break;
            case ShapeKind.Box:
                if (!Pos(WidthMm, HeightMm, WallMm) || 2 * WallMm >= Math.Min(WidthMm, HeightMm)) e.Add("Коробка: 0 < 2t < min(b, h)"); break;
            case ShapeKind.Tee:
                if (!Pos(HeightMm, FlangeWidthMm, FlangeThicknessMm, WebMm) || FlangeThicknessMm >= HeightMm || WebMm > FlangeWidthMm)
                    e.Add("Тавр: hf < h, bw ≤ bf"); break;
            case ShapeKind.IBeam:
                if (!Pos(HeightMm, FlangeWidthMm, FlangeThicknessMm, WebMm) || 2 * FlangeThicknessMm >= HeightMm || WebMm > FlangeWidthMm)
                    e.Add("Двутавр: 2hf < h, bw ≤ bf"); break;
            case ShapeKind.Polygon:
                if (Vertices.Count < 3) e.Add("В контуре должно быть не менее 3 вершин");
                else if (Vertices.Any(v => !double.IsFinite(v.X) || !double.IsFinite(v.Y))) e.Add("Координаты вершин должны быть числами");
                else if (SelfIntersects(Vertices)) e.Add("Контур пересекает сам себя");
                else if (Math.Abs(SignedArea(Vertices)) < 1) e.Add("Контур не охватывает площади");
                break;
        }
        return e;
    }

    static bool SelfIntersects(List<Vertex> r)
    {
        int n = r.Count;
        static double Cross(Vertex o, Vertex a, Vertex b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                if (j == i + 1 || (i == 0 && j == n - 1)) continue;
                Vertex a = r[i], b = r[(i + 1) % n], c = r[j], d = r[(j + 1) % n];
                if (Cross(a, b, c) * Cross(a, b, d) < 0 && Cross(c, d, a) * Cross(c, d, b) < 0) return true;
            }
        return false;
    }

    // ---- properties ----

    public ConcreteProperties Properties()
    {
        double a = 0, sx = 0, sy = 0, ix = 0, iy = 0;
        foreach (var ring in new[] { Outline() }.Concat(Holes()))
            for (int i = 0; i < ring.Count; i++)
            {
                var (p, q) = (ring[i], ring[(i + 1) % ring.Count]);
                double c = p.X * q.Y - q.X * p.Y;
                a += c / 2;
                sx += (p.X + q.X) * c / 6;
                sy += (p.Y + q.Y) * c / 6;
                ix += (p.Y * p.Y + p.Y * q.Y + q.Y * q.Y) * c / 12;
                iy += (p.X * p.X + p.X * q.X + q.X * q.X) * c / 12;
            }
        var outline = Outline();
        double cx = a != 0 ? sx / a : 0, cy = a != 0 ? sy / a : 0;
        return new ConcreteProperties(a, cx, cy, ix - a * cy * cy, iy - a * cx * cx,
            outline.Min(v => v.X), outline.Max(v => v.X), outline.Min(v => v.Y), outline.Max(v => v.Y));
    }

    // ---- bars ----

    /// <summary>Bars of the section: the table, or the contour arrangement generated from the outline.</summary>
    public List<BarSpec> EffectiveBars() => Arrangement == BarArrangement.Manual ? Bars : ContourBars();

    /// <summary>
    /// Bars along every face at the axis distance a: round outlines get equal spacing on the circle, polygons a bar at
    /// each corner of the inset contour and intermediate bars at a spacing not above s. A wall too thin for two rows
    /// (ring, box) gets one row in its middle.
    /// </summary>
    public List<BarSpec> ContourBars()
    {
        var bars = new List<BarSpec>();
        double a = AxisDistanceMm, d = BarDiameterMm, s = MaxSpacingMm;
        if (!(a > 0 && d > 0 && s > 0) || OutlineProblems().Count > 0) return bars;
        double twoRows = 2 * a + d + MinClearSpacingMm;
        switch (Shape)
        {
            case ShapeKind.Circle:
                OnCircle(DiameterMm / 2 - a);
                break;
            case ShapeKind.Ring:
                double wall = (DiameterMm - InnerDiameterMm) / 2;
                if (wall >= twoRows) { OnCircle(DiameterMm / 2 - a); OnCircle(InnerDiameterMm / 2 + a); }
                else OnCircle((DiameterMm + InnerDiameterMm) / 4);
                break;
            case ShapeKind.Box when WallMm < twoRows:
                OnRing(Inset(Outline(), WallMm / 2));
                break;
            default:
                OnRing(Inset(Outline(), a));
                foreach (var hole in Holes()) OnRing(Inset(hole, a));
                break;
        }
        return bars;

        void OnCircle(double r)
        {
            if (r <= 0) return;
            int n = Math.Max(4, (int)Math.Ceiling(2 * Math.PI * r / s - 1e-9));
            for (int i = 0; i < n; i++)
                bars.Add(new BarSpec(Round(r * Math.Cos(2 * Math.PI * i / n)), Round(r * Math.Sin(2 * Math.PI * i / n)), d));
        }

        void OnRing(List<Vertex> ring)
        {
            for (int i = 0; i < ring.Count; i++)
            {
                var (p, q) = (ring[i], ring[(i + 1) % ring.Count]);
                double len = Math.Sqrt(Math.Pow(q.X - p.X, 2) + Math.Pow(q.Y - p.Y, 2));
                int k = Math.Max(1, (int)Math.Ceiling(len / s - 1e-9));
                for (int j = 0; j < k; j++)
                    bars.Add(new BarSpec(Round(p.X + (q.X - p.X) * j / k), Round(p.Y + (q.Y - p.Y) * j / k), d));
            }
        }
    }

    static double Round(double v) => Math.Round(v, 1);

    /// <summary>Contour shifted by <paramref name="a"/> into the material (miter joints): outer rings are counterclockwise, holes clockwise.</summary>
    internal static List<Vertex> Inset(List<Vertex> ring, double a)
    {
        int n = ring.Count;
        var result = new List<Vertex>(n);
        for (int i = 0; i < n; i++)
        {
            var (prev, v, next) = (ring[(i - 1 + n) % n], ring[i], ring[(i + 1) % n]);
            var n1 = Normal(prev, v); var n2 = Normal(v, next);
            double k = 1 + n1.X * n2.X + n1.Y * n2.Y;
            if (k < 1e-6) continue;
            result.Add(new Vertex(v.X + a * (n1.X + n2.X) / k, v.Y + a * (n1.Y + n2.Y) / k));
        }
        return result;

        // Left normal: the material side of a counterclockwise outer edge or a clockwise hole edge.
        static Vertex Normal(Vertex p, Vertex q)
        {
            double dx = q.X - p.X, dy = q.Y - p.Y, l = Math.Sqrt(dx * dx + dy * dy);
            return l > 0 ? new Vertex(-dy / l, dx / l) : new Vertex(0, 0);
        }
    }

    // ---- point tests ----

    internal static bool Inside(IReadOnlyList<Vertex> ring, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            if ((ring[i].Y > y) != (ring[j].Y > y)
                && x < (ring[j].X - ring[i].X) * (y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X)
                inside = !inside;
        return inside;
    }

    internal static double EdgeDistance(IReadOnlyList<Vertex> ring, double x, double y)
    {
        double best = double.MaxValue;
        for (int i = 0; i < ring.Count; i++)
        {
            var (p, q) = (ring[i], ring[(i + 1) % ring.Count]);
            double dx = q.X - p.X, dy = q.Y - p.Y, len2 = dx * dx + dy * dy;
            double t = len2 > 0 ? Math.Clamp(((x - p.X) * dx + (y - p.Y) * dy) / len2, 0, 1) : 0;
            best = Math.Min(best, Math.Sqrt(Math.Pow(x - p.X - t * dx, 2) + Math.Pow(y - p.Y - t * dy, 2)));
        }
        return best;
    }

    /// <summary>Clear cover of a bar: distance from its surface to the nearest concrete face; negative when it sticks out.</summary>
    public double ClearCover(BarSpec b)
    {
        var outline = Outline(); var holes = Holes();
        bool inConcrete = Inside(outline, b.X, b.Y) && !holes.Any(h => Inside(h, b.X, b.Y));
        double edge = new[] { outline }.Concat(holes).Min(r => EdgeDistance(r, b.X, b.Y));
        return (inConcrete ? edge : -edge) - b.DiameterMm / 2;
    }

    /// <summary>Short description of the outline for titles and the report.</summary>
    public string ShapeText()
    {
        string F(double v) => v.ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        return Shape switch
        {
            ShapeKind.Rectangle => $"прямоугольник {F(WidthMm)}×{F(HeightMm)}",
            ShapeKind.Circle => $"круг Ø{F(DiameterMm)}",
            ShapeKind.Ring => $"кольцо Ø{F(DiameterMm)}/{F(InnerDiameterMm)}",
            ShapeKind.Tee => $"тавр h = {F(HeightMm)}, bf×hf = {F(FlangeWidthMm)}×{F(FlangeThicknessMm)}, bw = {F(WebMm)}",
            ShapeKind.IBeam => $"двутавр h = {F(HeightMm)}, bf×hf = {F(FlangeWidthMm)}×{F(FlangeThicknessMm)}, bw = {F(WebMm)}",
            ShapeKind.Box => $"коробка {F(WidthMm)}×{F(HeightMm)}, t = {F(WallMm)}",
            _ => $"многоугольник, {Vertices.Count} вершин",
        };
    }
}
