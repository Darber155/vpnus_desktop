using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace VpnUs.App.Controls;

/// <summary>
/// График задержки (TCP-пинг): одна серия, сетка, автомасштабирование по максимуму.
/// Пропуски (double.NaN) разрывают линию, чтобы потеря пакетов была видна.
/// </summary>
public sealed class PingChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable<double>), typeof(PingChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(PingChart),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x6B, 0x8C, 0xFF))));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(PingChart),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(PingChart),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x8C, 0x93, 0xA6))));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(PingChart),
        new FrameworkPropertyMetadata(45, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(PingChart),
        new FrameworkPropertyMetadata("Нет данных"));

    public PingChart()
    {
        Loaded += (_, _) =>
        {
            AttachSeries();
            InvalidateVisual();
        };
        Unloaded += (_, _) => DetachSeries();
    }

    public IEnumerable<double>? Values
    {
        get => (IEnumerable<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush LineBrush
    {
        get => (Brush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public int Capacity
    {
        get => (int)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    private INotifyCollectionChanged? _attached;

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (PingChart)d;
        chart.AttachSeries();
        chart.InvalidateVisual();
    }

    private void AttachSeries()
    {
        DetachSeries();
        if (Values is INotifyCollectionChanged values)
        {
            values.CollectionChanged += OnCollectionChanged;
            _attached = values;
        }
    }

    private void DetachSeries()
    {
        if (_attached is not null)
        {
            _attached.CollectionChanged -= OnCollectionChanged;
            _attached = null;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 24 || height < 24)
        {
            return;
        }

        var capacity = Math.Max(2, Capacity);
        var values = Normalize(Values, capacity);

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));

        if (values.Length == 0 || !values.Any(double.IsFinite))
        {
            DrawEmpty(dc, width, height);
            dc.Pop();
            return;
        }

        var padTop = 16.0;
        var padBottom = 2.0;
        var chartHeight = height - padTop - padBottom;
        var max = NiceMaximum(values.Where(double.IsFinite).Max());

        var gridPen = new Pen(GridBrush, 1)
        {
            DashStyle = new DashStyle([2, 4], 0),
        };
        gridPen.Freeze();

        for (var i = 0; i <= 3; i++)
        {
            var y = Math.Round(padTop + chartHeight * i / 3) + 0.5;
            dc.DrawLine(gridPen, new Point(0, y), new Point(width, y));
        }

        DrawScaleLabel(dc, width, max);

        var step = width / (capacity - 1);
        var offset = Math.Max(0, capacity - values.Length);
        var segments = BuildSegments(values, offset, step, padTop, chartHeight, max);
        var drawLastPoint = values.Length > 0 && double.IsFinite(values[^1]);

        for (var i = 0; i < segments.Count; i++)
        {
            DrawSegment(dc, segments[i], height, drawLastPoint && i == segments.Count - 1);
        }

        dc.Pop();
    }

    private void DrawEmpty(DrawingContext dc, double width, double height)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(
            EmptyText,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable Text, Segoe UI"),
            12.5,
            TextBrush,
            dpi);

        dc.DrawText(text, new Point((width - text.Width) / 2, (height - text.Height) / 2));
    }

    private void DrawScaleLabel(DrawingContext dc, double width, double max)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(
            $"{max:0} мс",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable Text, Segoe UI"),
            10.5,
            TextBrush,
            dpi);

        dc.DrawText(text, new Point(Math.Max(0, width - text.Width), 0));
    }

    private void DrawSegment(DrawingContext dc, Point[] points, double height, bool drawLastPoint)
    {
        if (points.Length == 0)
        {
            return;
        }

        if (points.Length == 1)
        {
            var color = ExtractColor(LineBrush);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B)), null, points[0], 2.5, 2.5);
            return;
        }

        var lineColor = ExtractColor(LineBrush);
        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(points[0].X, height), true, true);
            foreach (var point in points)
            {
                ctx.LineTo(point, true, false);
            }

            ctx.LineTo(new Point(points[^1].X, height), true, false);
        }

        area.Freeze();
        dc.DrawGeometry(new LinearGradientBrush(
            Color.FromArgb(0x38, lineColor.R, lineColor.G, lineColor.B),
            Color.FromArgb(0, lineColor.R, lineColor.G, lineColor.B),
            new Point(0, 0),
            new Point(0, 1)), null, area);

        var glow = new Pen(new SolidColorBrush(Color.FromArgb(0x24, lineColor.R, lineColor.G, lineColor.B)), 6)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        glow.Freeze();

        var line = new Pen(LineBrush, 2.2)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        line.Freeze();

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            foreach (var point in points)
            {
                ctx.LineTo(point, true, false);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, glow, geometry);
        dc.DrawGeometry(null, line, geometry);

        if (drawLastPoint)
        {
            var last = points[^1];
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x35, lineColor.R, lineColor.G, lineColor.B)), null, last, 5, 5);
            dc.DrawEllipse(LineBrush, null, last, 2.6, 2.6);
        }
    }

    private static List<Point[]> BuildSegments(double[] values, int offset, double step, double padTop, double chartHeight, double max)
    {
        var segments = new List<Point[]>();
        var current = new List<Point>();

        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (!double.IsFinite(value))
            {
                if (current.Count > 0)
                {
                    segments.Add(current.ToArray());
                    current.Clear();
                }

                continue;
            }

            var x = (offset + i) * step;
            var y = padTop + chartHeight - Math.Min(1, Math.Max(0, value) / max) * chartHeight;
            current.Add(new Point(x, y));
        }

        if (current.Count > 0)
        {
            segments.Add(current.ToArray());
        }

        return segments;
    }

    private static double[] Normalize(IEnumerable<double>? source, int capacity)
    {
        if (source is null)
        {
            return [];
        }

        var values = source as IList<double> ?? source.ToList();
        if (values.Count == 0)
        {
            return [];
        }

        var start = Math.Max(0, values.Count - capacity);
        var result = new double[values.Count - start];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = values[start + i];
        }

        return result;
    }

    private static double NiceMaximum(double value)
    {
        if (value <= 10)
        {
            return 10;
        }

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalized = value / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    private static Color ExtractColor(Brush brush) => brush switch
    {
        SolidColorBrush solid => solid.Color,
        GradientBrush gradient when gradient.GradientStops.Count > 0 => gradient.GradientStops[0].Color,
        _ => Colors.White,
    };
}
