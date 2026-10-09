using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using ImpactProConfig.Services;

namespace ImpactProConfig.Controls;

/// <summary>
/// График разряда батареи: заряд во времени, area chart с подсветкой отрезков
/// зарядки.
///
/// Рендер той же схемой, что и CurveChart: один StreamGeometry на линию,
/// второй на заливку под ней, плюс отдельная геометрия для отрезков зарядки.
/// 4000 сэмплов из battery_stats.json в Canvas превратились бы в 4000
/// элементов — здесь это две геометрии и две кисти.
///
/// Шкала Y всегда 0..100%: график процента, и обрезанный диапазон вводил бы
/// в заблуждение — падение с 40% до 10% выглядело бы как обвал до нуля.
/// </summary>
public sealed class BatteryChart : FrameworkElement
{
    /// <summary>
    /// Точки истории: время, заряд, зарядка ли.
    ///
    /// DependencyProperty, а не обычное свойство: на XAML висит привязка, а
    /// привязка к CLR-свойству FrameworkElement не работает. Точки всё
    /// равно проталкиваются вручную из code-behind (список приходит из
    /// BatteryStatsService, а не из VM как объект), но свойство обязано быть
    /// настоящим DP.
    /// </summary>
    public static readonly DependencyProperty SamplesProperty =
        DependencyProperty.Register(
            nameof(Samples), typeof(IReadOnlyList<BatterySample>), typeof(BatteryChart),
            new PropertyMetadata(null, OnSamplesChanged));

    public IReadOnlyList<BatterySample> Samples
    {
        get => (IReadOnlyList<BatterySample>?)GetValue(SamplesProperty)
               ?? Array.Empty<BatterySample>();
        set => SetValue(SamplesProperty, value);
    }

    private static void OnSamplesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((BatteryChart)d).InvalidateVisual();

    /// <summary>Акцентный цвет линии (тема оформления).</summary>
    public Color LineColor { get; set; } = Color.FromRgb(0xE8, 0x11, 0x23);

    /// <summary>Цвет отрезков зарядки.</summary>
    public Color ChargingColor { get; set; } = Color.FromRgb(0x4C, 0xC7, 0x11);

    /// <summary>Подпись окна для заголовка, напр. «последние 24 ч».</summary>
    public static readonly DependencyProperty WindowLabelProperty =
        DependencyProperty.Register(
            nameof(WindowLabel), typeof(string), typeof(BatteryChart),
            new PropertyMetadata(string.Empty, OnWindowLabelChanged));

    public string WindowLabel
    {
        get => (string)GetValue(WindowLabelProperty);
        set => SetValue(WindowLabelProperty, value);
    }

    private static void OnWindowLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((BatteryChart)d).InvalidateVisual();

    private static readonly Pen GridPen = FrozenPen(0x20, 1.0);
    private static readonly Pen AxisPen = FrozenPen(0x40, 1.0);
    private static readonly Brush LabelBrush = FrozenBrush(0x99);

    private const int HorizontalLines = 5;   // 0, 25, 50, 75, 100 %

    private static Pen FrozenPen(byte alpha, double thickness)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
        b.Freeze();
        var p = new Pen(b, thickness);
        p.Freeze();
        return p;
    }

    private static Brush FrozenBrush(byte alpha)
    {
        var b = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
        b.Freeze();
        return b;
    }

    /// <summary>Область построения: слева и снизу место под подписи осей.</summary>
    private Rect PlotRect => new Rect(
        44, 14,
        Math.Max(1, ActualWidth - 58),
        Math.Max(1, ActualHeight - 40));

    /// <summary>
    /// Точки в пикселях. Публично ради тестов: проверяем, что график
    /// действительно строится, а не рисует заглушку.
    /// </summary>
    public IReadOnlyList<Point> BuildPoints(IReadOnlyList<BatterySample> samples)
    {
        var plot = PlotRect;
        var pts = new List<Point>(samples.Count);

        if (samples.Count < 2)
            return pts;

        // Линейная шкала времени: батарея разряжается линейно, и логарифм
        // здесь исказил бы длину отрезков.
        DateTimeOffset from = samples[0].At.ToUniversalTime();
        DateTimeOffset to = samples[^1].At.ToUniversalTime();
        double span = (to - from).TotalMilliseconds;
        if (span <= 0)
            return pts;

        foreach (var s in samples)
        {
            // Точка вне [from, to] дала бы t вне [0,1] и утащила бы Path за
            // границы области. Окно X вычисляется по первой и последней точке,
            // но часы устройства могут откатиться, а порядок в файле не
            // гарантирован — прижимаем по границам.
            double t = (s.At.ToUniversalTime() - from).TotalMilliseconds / span;
            t = Math.Clamp(t, 0.0, 1.0);
            double y = Math.Clamp(s.Percent, 0, 100) / 100.0;
            pts.Add(new Point(
                plot.Left + t * plot.Width,
                plot.Bottom - y * plot.Height));
        }

        return pts;
    }

    /// <summary>
    /// Отрезки, где шла зарядка, готовые к отрисовке. Их считаем отдельно от
    /// линии: зарядка — это не «заряд падает», это другая фаза, и смешивать
    /// её с разрядом в одной ломаной нельзя, иначе график будет прыгать вверх
    /// и обратно без видимой причины.
    /// </summary>
    public IReadOnlyList<Point> BuildChargingRuns(IReadOnlyList<BatterySample> samples)
    {
        var pts = new List<Point>();
        if (samples.Count < 2) return pts;

        var plot = PlotRect;
        DateTimeOffset from = samples[0].At.ToUniversalTime();
        DateTimeOffset to = samples[^1].At.ToUniversalTime();
        double span = (to - from).TotalMilliseconds;
        if (span <= 0) return pts;

        Point ToPoint(BatterySample s)
        {
            double t = (s.At.ToUniversalTime() - from).TotalMilliseconds / span;
            t = Math.Clamp(t, 0.0, 1.0);
            double y = Math.Clamp(s.Percent, 0, 100) / 100.0;
            return new Point(plot.Left + t * plot.Width, plot.Bottom - y * plot.Height);
        }

        // Ищем НЕПРЕРЫВНЫЕ отрезки зарядки, а не каждую пару точек: иначе
        // десять точек зарядки дали бы девять перекрывающихся сегментов вместо
        // одного отрезка на весь период. Возвращаем по две точки на отрезок.
        int runStart = -1;
        for (int i = 0; i < samples.Count; i++)
        {
            bool charging = !samples[i].Discharging;

            if (charging && runStart < 0)
                runStart = i;

            bool last = i == samples.Count - 1;
            if (runStart >= 0 && (!charging || last))
            {
                int runEnd = charging ? i : i - 1;
                if (runEnd > runStart)
                {
                    pts.Add(ToPoint(samples[runStart]));
                    pts.Add(ToPoint(samples[runEnd]));
                }
                runStart = -1;
            }
        }

        return pts;
    }

    /// <summary>
    /// Один перерисовывающий счётчик: телеметрия батареи меняется раз в пять
    /// минут, так что подписываться на каждое свойство VM смысла нет.
    /// </summary>
    public static readonly DependencyProperty RevisionProperty =
        DependencyProperty.Register(
            nameof(Revision), typeof(int), typeof(BatteryChart),
            new PropertyMetadata(0, OnRevisionChanged));

    public int Revision
    {
        get => (int)GetValue(RevisionProperty);
        set => SetValue(RevisionProperty, value);
    }

    private static void OnRevisionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((BatteryChart)d).InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var plot = PlotRect;
        DrawGrid(dc, plot);

        var samples = Samples;
        if (samples.Count < 2)
        {
            // Нет истории — подписываем это прямо на графике, чтобы пустая
            // область не выглядела как поломка отрисовки.
            var empty = MakeText("Накопление истории: нужно несколько замеров");
            dc.DrawText(empty, new Point(plot.Left + 8, plot.Top + 8));
            DrawAxisLabels(dc, plot, samples.Count == 0 ? null : samples);
            return;
        }

        var pts = BuildPoints(samples);

        // 1. Заливка под линией: тот же путь, замкнутый на низ области.
        if (pts.Count >= 2)
        {
            var area = new StreamGeometry();
            using (var c = area.Open())
            {
                c.BeginFigure(new Point(pts[0].X, plot.Bottom), isFilled: true, isClosed: true);
                c.LineTo(pts[0], isStroked: false, isSmoothJoin: true);
                foreach (var p in pts)
                    c.LineTo(p, isStroked: false, isSmoothJoin: true);
                c.LineTo(new Point(pts[^1].X, plot.Bottom), isStroked: false, isSmoothJoin: false);
            }
            area.Freeze();

            // Градиент от линии к низу: у основания прозрачный, иначе заливка
            // перекрыла бы сетку и подписи.
            var fill = new LinearGradientBrush(
                Color.FromArgb(0x66, (byte)LineColor.R, (byte)LineColor.G, (byte)LineColor.B),
                Color.FromArgb(0x00, (byte)LineColor.R, (byte)LineColor.G, (byte)LineColor.B),
                new Point(0, 0), new Point(0, 1));
            fill.Freeze();
            dc.DrawGeometry(fill, null, area);
        }

        // 2. Отрезки зарядки — отдельным цветом поверх заливки.
        var runs = BuildChargingRuns(samples);
        if (runs.Count >= 2)
        {
            var chargeBrush = new SolidColorBrush(ChargingColor);
            chargeBrush.Freeze();
            var chargePen = new Pen(chargeBrush, 2.5)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            chargePen.Freeze();

            var run = new StreamGeometry();
            using (var c = run.Open())
            {
                for (int i = 0; i + 1 < runs.Count; i += 2)
                {
                    c.BeginFigure(runs[i], isFilled: false, isClosed: false);
                    c.LineTo(runs[i + 1], isStroked: true, isSmoothJoin: false);
                }
            }
            run.Freeze();
            dc.DrawGeometry(null, chargePen, run);
        }

        // 3. Линия разряда.
        var line = new StreamGeometry();
        using (var c = line.Open())
        {
            c.BeginFigure(pts[0], isFilled: false, isClosed: false);
            foreach (var p in pts)
                c.LineTo(p, isStroked: true, isSmoothJoin: true);
        }
        line.Freeze();

        var glowBrush = new SolidColorBrush(Color.FromArgb(0x44,
            (byte)LineColor.R, (byte)LineColor.G, (byte)LineColor.B));
        glowBrush.Freeze();
        var glowPen = new Pen(glowBrush, 6)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        glowPen.Freeze();
        dc.DrawGeometry(null, glowPen, line);

        var lineBrush = new SolidColorBrush(LineColor);
        lineBrush.Freeze();
        var linePen = new Pen(lineBrush, 2.25)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        linePen.Freeze();
        dc.DrawGeometry(null, linePen, line);

        DrawAxisLabels(dc, plot, samples);
    }

    private static void DrawGrid(DrawingContext dc, Rect plot)
    {
        for (int i = 0; i <= HorizontalLines; i++)
        {
            double y = plot.Bottom - plot.Height * i / HorizontalLines;
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }
        for (int i = 0; i <= 6; i++)
        {
            double x = plot.Left + plot.Width * i / 6;
            dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }
        dc.DrawLine(AxisPen, new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));
        dc.DrawLine(AxisPen, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
    }

    private static readonly Typeface AxisTypeface = new("Segoe UI");

    private static FormattedText MakeText(string s) => new(
        s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
        AxisTypeface, 10, LabelBrush, 1.0);

    private void DrawAxisLabels(DrawingContext dc, Rect plot, IReadOnlyList<BatterySample>? samples)
    {
        // Y: проценты. Шкала всегда 0..100.
        for (int i = 0; i <= HorizontalLines; i++)
        {
            int percent = i * 100 / HorizontalLines;
            double y = plot.Bottom - plot.Height * i / HorizontalLines + 4;
            dc.DrawText(MakeText(percent + "%"), new Point(8, y));
        }

        // X: время. Подписи ставим по реальным замерам, иначе на пустой
        // истории показывались бы выдуманные часы.
        if (samples is { Count: >= 2 })
        {
            DateTimeOffset from = samples[0].At.ToLocalTime();
            DateTimeOffset to = samples[^1].At.ToLocalTime();

            var left = MakeText(from.ToString("dd.MM HH:mm", CultureInfo.CurrentCulture));
            dc.DrawText(left, new Point(plot.Left, plot.Bottom + 6));

            var right = MakeText(to.ToString("dd.MM HH:mm", CultureInfo.CurrentCulture));
            dc.DrawText(right, new Point(plot.Right - right.WidthIncludingTrailingWhitespace,
                                          plot.Bottom + 6));

            string mid = from.Add((to - from) / 2)
                .ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.CurrentCulture);
            var center = MakeText(mid);
            dc.DrawText(center, new Point(
                plot.Left + plot.Width / 2 - center.WidthIncludingTrailingWhitespace / 2,
                plot.Bottom + 6));
        }

        dc.DrawText(MakeText("Заряд"), new Point(8, 0));
        if (!string.IsNullOrEmpty(WindowLabel))
        {
            var w = MakeText(WindowLabel);
            dc.DrawText(w, new Point(plot.Right - w.WidthIncludingTrailingWhitespace, 0));
        }
    }
}