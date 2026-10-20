using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace VpnUs.App.Controls;

/// <summary>
/// Минималистичный график скорости: две серии (загрузка/отдача), заливка, сетка и
/// масштабирование по пиковому значению. Рисуется вручную, без сторонних библиотек.
/// </summary>
public sealed class TrafficChart : FrameworkElement
{
    public static readonly DependencyProperty DownValuesProperty = DependencyProperty.Register(
        nameof(DownValues), typeof(IEnumerable<double>), typeof(TrafficChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public static readonly DependencyProperty UpValuesProperty = DependencyProperty.Register(
        nameof(UpValues), typeof(IEnumerable<double>), typeof(TrafficChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public static readonly DependencyProperty DownBrushProperty = DependencyProperty.Register(
        nameof(DownBrush), typeof(Brush), typeof(TrafficChart),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x46, 0xE0, 0xA1))));

    public static readonly DependencyProperty UpBrushProperty = DependencyProperty.Register(
        nameof(UpBrush), typeof(Brush), typeof(TrafficChart),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x6B, 0x8C, 0xFF))));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(TrafficChart),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF))));

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(TrafficChart),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x8C, 0x93, 0xA6))));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(TrafficChart),
        new FrameworkPropertyMetadata(90, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(TrafficChart),
        new FrameworkPropertyMetadata("Нет данных"));

    public IEnumerable<double>? DownValues
    {
        get => (IEnumerable<double>?)GetValue(DownValuesProperty);
        set => SetValue(DownValuesProperty, value);
    }

    public IEnumerable<double>? UpValues
    {
        get => (IEnumerable<double>?)GetValue(UpValuesProperty);
        set => SetValue(UpValuesProperty, value);
    }

    public Brush DownBrush
    {
        get => (Brush)GetValue(DownBrushProperty);
        set => SetValue(DownBrushProperty, value);
    }

    public Brush UpBrush
    {
        get => (Brush)GetValue(UpBrushProperty);
        set => SetValue(UpBrushProperty, value);
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

    public TrafficChart()
    {
        Loaded += (_, _) =>
        {
            AttachSeries();
            InvalidateVisual();
        };
        Unloaded += (_, _) => DetachSeries();
    }

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (TrafficChart)d;
        chart.AttachSeries();
        chart.InvalidateVisual();
    }

    private void AttachSeries()
    {
        DetachSeries();
        if (DownValues is INotifyCollectionChanged down)
        {
            down.CollectionChanged += OnCollectionChanged;
            _attachedDown = down;
        }

        if (UpValues is INotifyCollectionChanged up)
        {
            up.CollectionChanged += OnCollectionChanged;
            _attachedUp = up;
        }
    }

    private void DetachSeries()
    {
        if (_attachedDown is not null)
        {
            _attachedDown.CollectionChanged -= OnCollectionChanged;
            _attachedDown = null;
        }

        if (_attachedUp is not null)
        {
            _attachedUp.CollectionChanged -= OnCollectionChanged;
            _attachedUp = null;
        }
    }

    private INotifyCollectionChanged? _attachedDown;
    private INotifyCollectionChanged? _attachedUp;

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
        var down = Normalize(DownValues, capacity);
        var up = Normalize(UpValues, capacity);

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));

        if (down.Length == 0 && up.Length == 0)
        {
            DrawEmpty(dc, width, height);
            dc.Pop();
            return;
        }

        var padTop = 10.0;
        var padBottom = 2.0;
        var chartHeight = height - padTop - padBottom;
        var max = NiceMaximum(Math.Max(Max(down), Max(up)));

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

        // Нулевые линии в начале истории заполняют график слева, поэтому данные «растут» справа.
        var step = width / (capacity - 1);
        var offset = Math.Max(0, capacity - Math.Max(down.Length, up.Length));
        var downPoints = BuildPoints(down, offset, step, padTop, chartHeight, max);
        var upPoints = BuildPoints(up, offset, step, padTop, chartHeight, max);

        DrawSeries(dc, upPoints, height, UpBrush, 0x30, 2.0, 6, drawLastPoint: up.Length > 0);
        DrawSeries(dc, downPoints, height, DownBrush, 0x40, 2.2, 7, drawLastPoint: down.Length > 0);

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

    private static void DrawSeries(
        DrawingContext dc,
        Point[] points,
        double height,
        Brush brush,
        byte fillAlpha,
        double thickness,
        double glowThickness,
        bool drawLastPoint)
    {
        if (points.Length == 0)
        {
            return;
        }

        if (points.Length == 1)
        {
            var single = points[0];
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)), null, single, 2.5, 2.5);
            return;
        }

        var color = ExtractColor(brush);
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
            Color.FromArgb(fillAlpha, color.R, color.G, color.B),
            Color.FromArgb(0, color.R, color.G, color.B),
            new Point(0, 0),
            new Point(0, 1)), null, area);

        var glow = new Pen(new SolidColorBrush(Color.FromArgb(0x24, color.R, color.G, color.B)), glowThickness)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        glow.Freeze();

        var line = new Pen(brush, thickness)
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
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x35, color.R, color.G, color.B)), null, last, 5, 5);
            dc.DrawEllipse(brush, null, last, 2.6, 2.6);
        }
    }

    private static Point[] BuildPoints(double[] values, int offset, double step, double padTop, double chartHeight, double max)
    {
        var result = new Point[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var value = double.IsFinite(values[i]) ? Math.Max(0, values[i]) : 0;
            var x = (offset + i) * step;
            var y = padTop + chartHeight - Math.Min(1, value / max) * chartHeight;
            result[i] = new Point(x, y);
        }

        return result;
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

    private static double Max(double[] values)
    {
        var max = 0.0;
        foreach (var value in values)
        {
            if (double.IsFinite(value) && value > max)
            {
                max = value;
            }
        }

        return max;
    }

    private static double NiceMaximum(double value)
    {
        if (value <= 1)
        {
            return 1;
        }

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalized = value / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return nice * magnitude * 1.12;
    }

    private static Color ExtractColor(Brush brush) => brush switch
    {
        SolidColorBrush solid => solid.Color,
        GradientBrush gradient when gradient.GradientStops.Count > 0 => gradient.GradientStops[0].Color,
        _ => Colors.White,
    };
}
