using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ImpactProConfig.Services;

namespace ImpactProConfig.Controls;

/// <summary>
/// График кривой акселерации Raw Accel.
///
/// Рендер: StreamGeometry + один Polyline, без Canvas-элементов на точку.
/// 400 точек в StreamGeometry — это один объект геометрии, который GPU
/// рисует за один проход; Canvas с 400 Path заметно дороже, а движок мыши
/// на этой вкладке перерисовывает кривую на каждый шаг ползунка.
///
/// Формулы — в Services/RawAccelCurveEngine.cs, перенесены из апстрима
/// (common/accel-*.hpp). Здесь только геометрия и масштаб.
///
/// Оси: X — нормализованная скорость ввода (counts/ms -> in/s при DPI 1000,
/// см. NORMALIZED_DPI в rawaccel-base.hpp), Y — множитель чувствительности.
/// Единица на единице = 1.0 = акселерации нет.
/// </summary>
public sealed class CurveChart : FrameworkElement
{
    // ---- параметры кривой (приходят из VM) ----

    /// <summary>Тип кривой. null — драйвер не активен, рисуем заглушку.</summary>
    public RawAccelCurveMode? CurveMode { get; set; }

    /// <summary>Параметры формулы.</summary>
    public RawAccelCurveEngine.Args? CurveArgs { get; set; }

    /// <summary>Акцентный цвет темы. Меняется при смене темы оформления.</summary>
    public Color CurveColor { get; set; } = Color.FromRgb(0xE8, 0x11, 0x23);

    // ---- геометрия области ----

    private const double GridThickness = 1.0;
    private const int GridLinesX = 8;
    private const int GridLinesY = 6;

    /// <summary>Число выборок по оси скорости. 400 — точность выше
    /// разрешения экрана, но Path остаётся лёгким.</summary>
    private const int SampleCount = 400;

    /// <summary>Верхняя граница оси скорости.</summary>
    public double MaxSpeed { get; set; } = 200;

    /// <summary>Верхняя граница множителя. Держим чуть выше 1, иначе при
    /// cap=1.5 кривая упирается в верх карточки.</summary>
    public double MaxMultiplier { get; set; } = 2.0;

    private static readonly Pen GridPen = FrozenPen(0x20, 1.0);
    private static readonly Pen AxisPen = FrozenPen(0x40, 1.0);
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    private static readonly Pen DisabledPen = FrozenPen(0x33, 1.5);

    private Pen? _curvePen;

    /// <summary>
    /// Замороженное перо заданной прозрачности и толщины. Кисти и перья
    /// в статических полях обязательно замораживать: незамороженный Freezable
    /// удерживает весь визуальный граф и не даёт сборщику освободить элемент.
    /// </summary>
    private static Pen FrozenPen(byte alpha, double thickness)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
        brush.Freeze();
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    /// <summary>
    /// Счётчик перерисовки. На него вешается привязка к VM, а изменение
    /// значения вызывает InvalidateVisual. Так вместо проброса
    /// INotifyPropertyChanged на десять параметров кривой через границу
    /// управления проходит одно целое на каждое изменение ползунка.
    /// </summary>
    public static readonly DependencyProperty CurveRevisionProperty =
        DependencyProperty.Register(
            nameof(CurveRevision), typeof(int), typeof(CurveChart),
            new PropertyMetadata(0, OnCurveRevisionChanged));

    public int CurveRevision
    {
        get => (int)GetValue(CurveRevisionProperty);
        set => SetValue(CurveRevisionProperty, value);
    }

    private static void OnCurveRevisionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CurveChart)d).InvalidateVisual();

    /// <summary>Область построения с отступом под подписи осей.</summary>
    private Rect PlotRect => new Rect(
        42, 12,
        Math.Max(1, ActualWidth - 56),
        Math.Max(1, ActualHeight - 44));

    /// <summary>
    /// Точки кривой в пикселях. Публично для тестов: проверяем, что Path
    /// действительно строится, не подменяясь заглушкой.
    /// </summary>
    public IReadOnlyList<Point> BuildPoints(RawAccelCurveMode mode, RawAccelCurveEngine.Args args)
    {
        Rect plot = PlotRect;
        var pts = new List<Point>(SampleCount);

        for (int i = 0; i < SampleCount; i++)
        {
            // Логарифмическая шкала по X: скорость мыши распределена
            // неравномерно, и на линейной шкате вся interesting часть
            // кривой (первые единицы in/s) схлопывается в пиксель у нуля.
            // Так же делает GUI Raw Accel.
            double t = i / (double)(SampleCount - 1);
            double speed = MaxSpeed * Math.Pow(t, 2.0);

            double y = RawAccelCurveEngine.Evaluate(mode, speed, args);

            if (double.IsNaN(y) || double.IsInfinity(y)) continue;
            if (y < 0) y = 0;

            double px = plot.Left + t * plot.Width;
            double py = plot.Bottom - Math.Min(y / MaxMultiplier, 1.0) * plot.Height;
            pts.Add(new Point(px, py));
        }

        return pts;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        Rect plot = PlotRect;
        DrawGrid(dc, plot);
        DrawLabels(dc, plot);

        var args = CurveArgs;
        var mode = CurveMode;

        if (mode is null || args is null)
        {
            // Драйвер не активен: линия-подсказка «множитель 1.0», а не пустота,
            // иначе карточка выглядит сломанной.
            double y1 = plot.Bottom - plot.Height;
            dc.DrawLine(DisabledPen,
                new Point(plot.Left, y1), new Point(plot.Right, y1));
            return;
        }

        var pts = BuildPoints(mode.Value, args);
        if (pts.Count < 2) return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(pts[0], isFilled: false, isClosed: false);
            // LineTo, а не Bezier: между соседними выборками шаг ~0.5 px,
            // сглаживание не даёт ничего, но стоит CPU.
            for (int i = 1; i < pts.Count; i++)
                ctx.LineTo(pts[i], isStroked: true, isSmoothJoin: false);
        }
        geometry.Freeze();

        _curvePen ??= CreateCurvePen();

        // Неон: та же геометрия рисуется вторым проходом с полупрозрачным
        // толстым пером. Два прохода дешевле, чем Effect на Path (он
        // заставляет WPF растрировать элемент целиком).
        var glowBrush = new SolidColorBrush(Color.FromArgb(0x55,
            (byte)CurveColor.R, (byte)CurveColor.G, (byte)CurveColor.B));
        glowBrush.Freeze();

        var glowPen = new Pen(glowBrush, 6)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        glowPen.Freeze();
        dc.DrawGeometry(null, glowPen, geometry);

        dc.DrawGeometry(null, _curvePen, geometry);
    }

    private Pen CreateCurvePen()
    {
        var brush = new SolidColorBrush(CurveColor);
        brush.Freeze();
        var pen = new Pen(brush, 2.25)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        return pen;
    }

    private static void DrawGrid(DrawingContext dc, Rect plot)
    {
        for (int i = 0; i <= GridLinesX; i++)
        {
            double x = plot.Left + plot.Width * i / GridLinesX;
            dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }
        for (int i = 0; i <= GridLinesY; i++)
        {
            double y = plot.Bottom - plot.Height * i / GridLinesY;
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        // Оси: чуть заметнее сетки.
        dc.DrawLine(AxisPen, new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));
        dc.DrawLine(AxisPen, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
    }

    private static readonly Typeface AxisTypeface = new("Segoe UI");

    private void DrawLabels(DrawingContext dc, Rect plot)
    {
        // Подписи оси Y: множитель чувствительности.
        for (int i = 0; i <= GridLinesY; i += 2)
        {
            double frac = i / (double)GridLinesY;
            double value = frac * MaxMultiplier;
            double y = plot.Bottom - plot.Height * frac + 4;
            var text = MakeText(value.ToString("0.##", CultureInfo.InvariantCulture) + "x");
            dc.DrawText(text, new Point(6, y));
        }

        // Подписи оси X: скорость ввода.
        for (int i = 0; i <= GridLinesX; i += 2)
        {
            double frac = i / (double)GridLinesX;
            double speed = MaxSpeed * Math.Pow(frac, 2.0);
            double x = plot.Left + plot.Width * frac - 12;
            var text = MakeText(speed.ToString("0.#", CultureInfo.InvariantCulture));
            dc.DrawText(text, new Point(x, plot.Bottom + 6));
        }

        // Заголовки осей.
        dc.DrawText(MakeText("Множитель чувствительности"),
            new Point(6, 0));

        var xLabel = MakeText("Скорость движения (in/s)");
        double width = MeasureTextWidth(xLabel);
        dc.DrawText(xLabel, new Point(plot.Right - width, plot.Bottom + 22));
    }

    private static FormattedText MakeText(string s) => new(
        s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        AxisTypeface, 10, LabelBrush, 1.0);

    private static double MeasureTextWidth(FormattedText t) =>
        t.WidthIncludingTrailingWhitespace;
}