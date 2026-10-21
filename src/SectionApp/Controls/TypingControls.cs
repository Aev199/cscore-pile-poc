using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using SectionCalc.Profile;

namespace SectionApp.Controls;

static class Paint
{
    public static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    public static readonly IBrush Text = new SolidColorBrush(Color.Parse("#263640"));
    public static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#7a8a94"));
    public static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#d32f2f"));
    public static readonly IBrush Grid = new SolidColorBrush(Color.Parse("#e6eaec"));
    public static readonly IBrush Highlight = new SolidColorBrush(Color.Parse("#eaf2fb"));

    public static Color TypeColor(int type, byte alpha = 255)
    {
        var c = TypingChart.Color(type);
        return Color.FromArgb(alpha, c.Red, c.Green, c.Blue);
    }

    public static FormattedText Format(string text, double size, IBrush brush, bool bold = false) =>
        new(text, Ru, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, bold ? FontWeight.SemiBold : FontWeight.Normal), size, brush);

    /// <summary>Required area from little to much: blue — yellow — red.</summary>
    public static Color Heat(double t)
    {
        t = Math.Clamp(t, 0, 1);
        (double r, double g, double b) = t < 0.5
            ? (0.18 + (0.98 - 0.18) * t * 2, 0.43 + (0.80 - 0.43) * t * 2, 0.85 + (0.20 - 0.85) * t * 2)
            : (0.98 + (0.83 - 0.98) * (t - 0.5) * 2, 0.80 + (0.18 - 0.80) * (t - 0.5) * 2, 0.20);
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}

/// <summary>
/// Plan of the piles: colour by type or by the required area; wheel — zoom, right button — pan, double click — fit;
/// click — a pile (Ctrl — add), drag on the empty plan — a box of piles.
/// </summary>
public sealed class PilePlan : Control
{
    public static readonly StyledProperty<TypingResult?> TypingProperty = AvaloniaProperty.Register<PilePlan, TypingResult?>(nameof(Typing));
    public static readonly StyledProperty<int> SelectedTypeProperty =
        AvaloniaProperty.Register<PilePlan, int>(nameof(SelectedType), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<IReadOnlyList<int>> SelectedPilesProperty =
        AvaloniaProperty.Register<PilePlan, IReadOnlyList<int>>(nameof(SelectedPiles), [], defaultBindingMode: BindingMode.TwoWay);
    /// <summary>0 — by type, 1 — by the largest required area of the pile.</summary>
    public static readonly StyledProperty<int> ColorModeProperty = AvaloniaProperty.Register<PilePlan, int>(nameof(ColorMode));

    public TypingResult? Typing { get => GetValue(TypingProperty); set => SetValue(TypingProperty, value); }
    public int SelectedType { get => GetValue(SelectedTypeProperty); set => SetValue(SelectedTypeProperty, value); }
    public IReadOnlyList<int> SelectedPiles { get => GetValue(SelectedPilesProperty); set => SetValue(SelectedPilesProperty, value); }
    public int ColorMode { get => GetValue(ColorModeProperty); set => SetValue(ColorModeProperty, value); }

    static PilePlan() => AffectsRender<PilePlan>(TypingProperty, SelectedTypeProperty, SelectedPilesProperty, ColorModeProperty);

    double zoom = 1;
    Vector pan;
    Point? pointer, boxStart, panStart;
    Vector panAtStart;

    public PilePlan()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // A new set of piles: fit the plan.
        if (change.Property == TypingProperty && (change.OldValue as TypingResult)?.Demands != (change.NewValue as TypingResult)?.Demands)
        { zoom = 1; pan = default; }
    }

    (Func<PileInfo, Point> Place, double Radius) Frame()
    {
        var piles = Typing!.Demands.Piles;
        double x0 = piles.Min(p => p.Info.X), x1 = piles.Max(p => p.Info.X), y0 = piles.Min(p => p.Info.Y), y1 = piles.Max(p => p.Info.Y);
        double margin = 28, w = Bounds.Width - 2 * margin, h = Bounds.Height - 2 * margin;
        double span = Math.Max(Math.Max(x1 - x0, y1 - y0), 1);
        double scale = Math.Min(x1 - x0 > 1e-6 ? w / (x1 - x0) : double.MaxValue, y1 - y0 > 1e-6 ? h / (y1 - y0) : double.MaxValue);
        if (!double.IsFinite(scale) || scale > 1e8) scale = Math.Min(w, h) / span;
        scale *= zoom;
        var c = new Point(Bounds.Width / 2 + pan.X, Bounds.Height / 2 + pan.Y);
        Point Place(PileInfo p) => new(c.X + (p.X - (x0 + x1) / 2) * scale, c.Y - (p.Y - (y0 + y1) / 2) * scale);
        // Circles do not overlap: radius from the nearest neighbour.
        double nearest = double.MaxValue;
        for (int i = 0; i < piles.Count && i < 400; i++)
            for (int j = i + 1; j < piles.Count && j < 400; j++)
                nearest = Math.Min(nearest, Math.Sqrt(Math.Pow(piles[i].Info.X - piles[j].Info.X, 2) + Math.Pow(piles[i].Info.Y - piles[j].Info.Y, 2)));
        double radius = Math.Clamp((nearest < double.MaxValue ? nearest : span) * scale * 0.42, 3, 16);
        return (Place, radius);
    }

    int? Hit(Point at)
    {
        if (Typing is null) return null;
        var (place, radius) = Frame();
        int? best = null;
        double min = radius + 3;
        foreach (var p in Typing.Demands.Piles)
        {
            double d = Point.Distance(place(p.Info), at);
            if (d < min) { min = d; best = p.Pile; }
        }
        return best;
    }

    Dictionary<int, int> TypeOf() => Typing!.Types.SelectMany(t => t.Members.Select(m => (m.Pile, t.Number))).ToDictionary(x => x.Pile, x => x.Number);

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.White, new Rect(Bounds.Size));
        if (Typing is null || Typing.Demands.Piles.Count == 0) return;
        var (place, radius) = Frame();
        var typeOf = TypeOf();
        var selected = SelectedPiles.ToHashSet();
        double maxAs = Typing.Demands.Piles.Max(p => p.As.Max());
        foreach (var p in Typing.Demands.Piles)
        {
            int t = typeOf.GetValueOrDefault(p.Pile);
            bool faded = SelectedType > 0 && t != SelectedType && !selected.Contains(p.Pile);
            var color = ColorMode == 1 ? Paint.Heat(p.As.Max() / maxAs) : Paint.TypeColor(t);
            var brush = new SolidColorBrush(color, faded ? 0.3 : 1);
            var c = place(p.Info);
            context.DrawEllipse(brush, null, c, radius, radius);
            if (selected.Contains(p.Pile)) context.DrawEllipse(null, new Pen(Paint.Text, 2.2), c, radius + 2.5, radius + 2.5);
            if (ColorMode == 0 && radius >= 8)
            {
                var label = Paint.Format(t.ToString(Paint.Ru), Math.Min(11, radius), Brushes.White, bold: true);
                context.DrawText(label, new Point(c.X - label.Width / 2, c.Y - label.Height / 2));
            }
        }
        if (boxStart is Point a && pointer is Point b)
            context.DrawRectangle(new SolidColorBrush(Color.Parse("#2f6ed8"), 0.08), new Pen(new SolidColorBrush(Color.Parse("#2f6ed8")), 1, new DashStyle([4, 3], 0)), new Rect(a, b));
        // Hover: the pile, its type and demand.
        if (boxStart is null && pointer is Point at && Hit(at) is int h)
        {
            var p = Typing.Demands.Piles[h];
            string text = $"{p.Info.Name} · Т{typeOf.GetValueOrDefault(h)}\nAs треб. до {p.As.Max() / 100:0.0} см² · отм. {p.Info.Head:0.00}…{p.Info.Tip:0.00}\nX {p.Info.X:0.00}, Y {p.Info.Y:0.00}";
            var ft = Paint.Format(text, 11.5, Paint.Text);
            var pos = new Point(Math.Min(at.X + 14, Bounds.Width - ft.Width - 12), Math.Min(at.Y + 14, Bounds.Height - ft.Height - 10));
            context.DrawRectangle(Brushes.White, new Pen(Paint.Muted, 1), new Rect(pos.X - 6, pos.Y - 4, ft.Width + 12, ft.Height + 8), 4, 4);
            context.DrawText(ft, pos);
        }
        var hint = Paint.Format("колесо — масштаб, правая кнопка — сдвиг, двойной щелчок — вписать; рамка или Ctrl+щелчок — выбор", 10.5, Paint.Muted);
        context.DrawText(hint, new Point(8, Bounds.Height - hint.Height - 4));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Typing is null) return;
        var props = e.GetCurrentPoint(this).Properties;
        var at = e.GetPosition(this);
        if (props.IsRightButtonPressed || props.IsMiddleButtonPressed) { panStart = at; panAtStart = pan; e.Pointer.Capture(this); return; }
        if (e.ClickCount == 2) { zoom = 1; pan = default; InvalidateVisual(); return; }
        bool add = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (Hit(at) is int pile)
        {
            var set = add ? SelectedPiles.ToList() : [];
            if (add && set.Contains(pile)) set.Remove(pile); else set.Add(pile);
            SetCurrentValue(SelectedPilesProperty, set);
            SetCurrentValue(SelectedTypeProperty, TypeOf().GetValueOrDefault(pile));
            return;
        }
        boxStart = at;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        pointer = e.GetPosition(this);
        if (panStart is Point s) pan = panAtStart + (pointer.Value - s);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (panStart is not null) { panStart = null; e.Pointer.Capture(null); return; }
        if (boxStart is Point a && Typing is not null)
        {
            var b = e.GetPosition(this);
            var rect = new Rect(a, b).Normalize();
            if (rect.Width > 4 || rect.Height > 4)
            {
                var (place, _) = Frame();
                var inside = Typing.Demands.Piles.Where(p => rect.Contains(place(p.Info))).Select(p => p.Pile);
                bool add = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                SetCurrentValue(SelectedPilesProperty, (add ? SelectedPiles.Concat(inside) : inside).Distinct().ToList());
            }
            else SetCurrentValue(SelectedPilesProperty, Array.Empty<int>());
            boxStart = null;
            e.Pointer.Capture(null);
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        pointer = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var at = e.GetPosition(this);
        double factor = e.Delta.Y > 0 ? 1.2 : 1 / 1.2;
        double next = Math.Clamp(zoom * factor, 0.5, 40);
        // Zoom about the cursor.
        var c = new Point(Bounds.Width / 2 + pan.X, Bounds.Height / 2 + pan.Y);
        pan += (at - c) * (1 - next / zoom);
        zoom = next;
        InvalidateVisual();
        e.Handled = true;
    }
}

/// <summary>Cages of all types side by side on one vertical scale: zones, bars, spiral, K; click — the type.</summary>
public sealed class CagesView : Control
{
    public static readonly StyledProperty<TypingResult?> TypingProperty = AvaloniaProperty.Register<CagesView, TypingResult?>(nameof(Typing));
    public static readonly StyledProperty<int> SelectedTypeProperty =
        AvaloniaProperty.Register<CagesView, int>(nameof(SelectedType), defaultBindingMode: BindingMode.TwoWay);

    public TypingResult? Typing { get => GetValue(TypingProperty); set => SetValue(TypingProperty, value); }
    public int SelectedType { get => GetValue(SelectedTypeProperty); set => SetValue(SelectedTypeProperty, value); }

    static CagesView()
    {
        AffectsRender<CagesView>(TypingProperty, SelectedTypeProperty);
        AffectsMeasure<CagesView>(TypingProperty);
    }

    const double Axis = 50, Column = 128, Header = 46, Footer = 26;

    protected override Size MeasureOverride(Size available) =>
        new(Axis + (Typing?.Types.Count ?? 0) * Column + 8, double.IsFinite(available.Height) ? available.Height : 420);

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.White, new Rect(Bounds.Size));
        if (Typing is null || Typing.Types.Count == 0) return;
        var types = Typing.Types;
        double zTop = types.Max(t => t.Head), zBottom = types.Min(t => t.Tip);
        double top = Header + 6, bottom = Bounds.Height - Footer - 6;
        double Y(double z) => top + (zTop - z) / Math.Max(zTop - zBottom, 1e-6) * (bottom - top);
        // Elevations.
        double step = Nice((zTop - zBottom) / 8);
        for (double z = Math.Ceiling(zBottom / step) * step; z <= zTop + 1e-9; z += step)
        {
            context.DrawLine(new Pen(Paint.Grid, 1), new Point(Axis - 4, Y(z)), new Point(Bounds.Width, Y(z)));
            var ft = Paint.Format(z.ToString("0.#", Paint.Ru), 10.5, Paint.Muted);
            context.DrawText(ft, new Point(Axis - 8 - ft.Width, Y(z) - ft.Height / 2));
        }
        double maxArea = types.SelectMany(t => t.Zones).Max(z => z.Bars.AreaMm2);
        for (int i = 0; i < types.Count; i++)
        {
            var t = types[i];
            double x = Axis + i * Column;
            if (t.Number == SelectedType) context.FillRectangle(Paint.Highlight, new Rect(x + 2, 0, Column - 4, Bounds.Height));
            var color = Paint.TypeColor(t.Number);
            context.DrawEllipse(new SolidColorBrush(color), null, new Point(x + 12, 14), 6, 6);
            context.DrawText(Paint.Format($"Т{t.Number}" + (t.Locked ? " · закреплён" : ""), 12.5, Paint.Text, bold: true), new Point(x + 22, 5));
            context.DrawText(Paint.Format($"{t.Members.Count} св. · L {t.Head - t.Tip:0.0} м", 11, Paint.Muted), new Point(x + 8, 24));
            // The pile body with the zones shaded by their area.
            double bx = x + 10, bw = 28;
            foreach (var z in t.Zones)
            {
                var rect = new Rect(bx, Y(z.Top), bw, Math.Max(1, Y(z.Bottom) - Y(z.Top)));
                context.FillRectangle(new SolidColorBrush(color, 0.2 + 0.7 * z.Bars.AreaMm2 / maxArea), rect);
                context.DrawLine(new Pen(Brushes.White, 1.5), rect.TopLeft, rect.TopRight);
                double mid = (rect.Top + rect.Bottom) / 2;
                var bars = Paint.Format(z.Bars.ToString(), 11.5, Paint.Text, bold: true);
                var rest = Paint.Format((z.SpiralMm is double d ? $"сп. Ø{d:0}  " : "") + (z.K is double k ? $"K {k.ToString("0.00", Paint.Ru)}" : z.Governing is null ? "K …" : "K ?"),
                    10, z.K is > 1 || (z.K is null && z.Governing is not null) ? Paint.Bad : Paint.Muted);
                if (rect.Height >= bars.Height + rest.Height)
                {
                    context.DrawText(bars, new Point(bx + bw + 6, mid - bars.Height));
                    context.DrawText(rest, new Point(bx + bw + 6, mid));
                }
                else context.DrawText(bars, new Point(bx + bw + 6, mid - bars.Height / 2));
            }
            context.DrawRectangle(null, new Pen(new SolidColorBrush(color), 1.4), new Rect(bx, Y(t.Head), bw, Y(t.Tip) - Y(t.Head)));
            context.DrawText(Paint.Format($"{t.SteelKg / t.Members.Count:0} кг/св.", 11, Paint.Muted), new Point(x + 8, Bounds.Height - Footer + 4));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Typing is null) return;
        int i = (int)((e.GetPosition(this).X - Axis) / Column);
        if (i >= 0 && i < Typing.Types.Count) SetCurrentValue(SelectedTypeProperty, Typing.Types[i].Number);
    }

    static double Nice(double v)
    {
        if (!(v > 0)) return 1;
        double e = Math.Pow(10, Math.Floor(Math.Log10(v)));
        return new[] { 1, 2, 2.5, 5, 10 }.Select(k => k * e).First(k => k >= v);
    }
}

/// <summary>Steel of the envelopes against the number of types; click — that number.</summary>
public sealed class SteelCurve : Control
{
    public static readonly StyledProperty<TypingResult?> TypingProperty = AvaloniaProperty.Register<SteelCurve, TypingResult?>(nameof(Typing));
    public static readonly StyledProperty<int> SelectedCountProperty =
        AvaloniaProperty.Register<SteelCurve, int>(nameof(SelectedCount), defaultBindingMode: BindingMode.TwoWay);

    public TypingResult? Typing { get => GetValue(TypingProperty); set => SetValue(TypingProperty, value); }
    public int SelectedCount { get => GetValue(SelectedCountProperty); set => SetValue(SelectedCountProperty, value); }

    static SteelCurve() => AffectsRender<SteelCurve>(TypingProperty, SelectedCountProperty);

    const int MaxShown = 30;

    List<(int Types, double SteelKg)> Points() =>
        Typing?.Curve.Where(p => p.Types <= MaxShown).OrderBy(p => p.Types).ToList() ?? [];

    (double Left, double Right, double Top, double Bottom) Box() => (50, Bounds.Width - 10, 22, Bounds.Height - 20);

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.White, new Rect(Bounds.Size));
        var pts = Points();
        if (pts.Count == 0) return;
        var (l, r, t, b) = Box();
        int k0 = pts[0].Types, k1 = pts[^1].Types;
        double s0 = pts.Min(p => p.SteelKg), s1 = pts.Max(p => p.SteelKg);
        double X(int k) => k1 == k0 ? (l + r) / 2 : l + (k - k0) * (r - l) / (k1 - k0);
        double Y(double s) => s1 - s0 < 1e-9 ? (t + b) / 2 : b - (s - s0) / (s1 - s0) * (b - t);
        var line = new Pen(new SolidColorBrush(Color.Parse("#2f6ed8")), 1.6);
        for (int i = 1; i < pts.Count; i++)
            context.DrawLine(line, new Point(X(pts[i - 1].Types), Y(pts[i - 1].SteelKg)), new Point(X(pts[i].Types), Y(pts[i].SteelKg)));
        foreach (var p in pts)
        {
            bool sel = p.Types == SelectedCount;
            context.DrawEllipse(sel ? Paint.Text : new SolidColorBrush(Color.Parse("#2f6ed8")), null, new Point(X(p.Types), Y(p.SteelKg)), sel ? 5 : 3, sel ? 5 : 3);
            if (sel || p.Types == k0 || p.Types == k1 || (k1 - k0 <= 15) || p.Types % 5 == 0)
            {
                var ft = Paint.Format(p.Types.ToString(Paint.Ru), 10, sel ? Paint.Text : Paint.Muted, sel);
                context.DrawText(ft, new Point(X(p.Types) - ft.Width / 2, b + 4));
            }
        }
        var hi = Paint.Format($"{s1 / 1000:0.0} т", 10, Paint.Muted);
        var lo = Paint.Format($"{s0 / 1000:0.0} т", 10, Paint.Muted);
        context.DrawText(hi, new Point(l - 6 - hi.Width, t - 2));
        context.DrawText(lo, new Point(l - 6 - lo.Width, b - lo.Height + 2));
        if (pts.FirstOrDefault(p => p.Types == SelectedCount) is { Types: > 0 } s)
        {
            var ft = Paint.Format($"типов: {s.Types} — {s.SteelKg / 1000:0.00} т по огибающим", 11, Paint.Text);
            context.DrawText(ft, new Point(r - ft.Width, t - 4));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pts = Points();
        if (pts.Count == 0) return;
        var (l, r, _, _) = Box();
        int k0 = pts[0].Types, k1 = pts[^1].Types;
        double x = e.GetPosition(this).X;
        int k = k1 == k0 ? k0 : (int)Math.Round(k0 + (x - l) / (r - l) * (k1 - k0));
        SetCurrentValue(SelectedCountProperty, Math.Clamp(k, k0, k1));
    }
}
