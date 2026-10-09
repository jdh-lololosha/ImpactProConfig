using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace ImpactProConfig.Services;

/// <summary>Данные, выводимые в OSD-оверлее.</summary>
internal sealed record OsdInfo(string Battery, string Status, string Connection, string Runtime, bool Charging);

/// <summary>
/// Безрамочный Topmost-оверлей: тёмное полупрозрачное стекло, скруглённые углы,
/// тень и плавный fade-in → пауза → fade-out.
/// Позиция считается в ФИЗИЧЕСКИХ пикселях выбранного монитора и задаётся через
/// SetWindowPos до первого показа (корректно при смешанных DPI, без воровства фокуса).
/// </summary>
internal sealed class OsdWindow : Window
{
    private const int MarginPx = 24;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    private static OsdWindow? _current;

    private readonly MonitorInfo _monitor;
    private readonly int _positionIndex;
    private readonly int _durationMs;
    private DispatcherTimer? _holdTimer;
    private DispatcherTimer? _closeTimer;
    private bool _closing;

    /// <summary>
    /// Гасит оба таймера окна и зануляет ссылки на них. Оба лямбда-хендлера
    /// захватывают this, поэтому незакрытый таймер удерживает уже мёртвое окно
    /// и продолжает в него стучаться. Вызывается из FadeOut, ForceClose и Closed.
    /// </summary>
    private void StopTimers()
    {
        if (_holdTimer is not null)
        {
            _holdTimer.Stop();
            _holdTimer = null;
        }
        if (_closeTimer is not null)
        {
            _closeTimer.Stop();
            _closeTimer = null;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        StopTimers();
        base.OnClosed(e);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    /// <summary>Показать/перезапустить оверлей (старое окно закрывается мгновенно).</summary>
    public static void ShowOverlay(OsdInfo info, MonitorInfo monitor, int positionIndex, int durationMs)
    {
        _current?.ForceClose();
        var w = new OsdWindow(info, monitor, positionIndex, durationMs);
        _current = w;
        w.Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, w))
                _current = null;
        };
        w.Show();
    }

    private OsdWindow(OsdInfo info, MonitorInfo monitor, int positionIndex, int durationMs)
    {
        _monitor = monitor;
        _positionIndex = positionIndex;
        _durationMs = durationMs;

        WindowStyle = WindowStyle.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        ShowInTaskbar = false;
        ShowActivated = false;              // не воровать фокус у активного окна
        Topmost = true;
        Left = monitor.WorkX;              // близко к цели — туда же уедет SetWindowPos
        Top = monitor.WorkY;

        Content = BuildContent(info);

        Loaded += (_, _) =>
        {
            var enterStoryboard = Application.Current.FindResource("MotionOsdEnter") as Storyboard;
            enterStoryboard?.Begin((FrameworkElement)Content);
            _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250 + _durationMs) };
            _holdTimer.Tick += (_, _) =>
            {
                if (!_closing)
                    FadeOut();
            };
            _holdTimer.Start();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            // Домеряем контент ДО показа: размер нужен для углового якоря.
            Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            int pw = (int)Math.Ceiling(DesiredSize.Width * _monitor.DpiX / 96.0);
            int ph = (int)Math.Ceiling(DesiredSize.Height * _monitor.DpiY / 96.0);

            // Позиционируемся строго в границах рабочей области (rcWork) монитора:
            // она учитывает панель задач и сворачиваемые окна.
            int wx = _monitor.WorkX, wy = _monitor.WorkY;
            int ww = _monitor.WorkWidth, wh = _monitor.WorkHeight;

            int right = wx + ww - MarginPx - pw;
            int bottom = wy + wh - MarginPx - ph;
            int centerX = wx + (ww - pw) / 2;

            (int x, int y) = _positionIndex switch
            {
                1 => (centerX, wy + MarginPx),                      // сверху по центру
                2 => (right, bottom),                               // снизу справа
                3 => (centerX, wy + (wh - ph) / 2),                 // по центру
                _ => (right, wy + MarginPx),                        // сверху справа
            };

            // Страховка: даже при ошибочных размерах окно не выйдет за rcWork.
            x = Math.Max(wx + MarginPx, Math.Min(x, wx + Math.Max(0, ww - pw - MarginPx)));
            y = Math.Max(wy + MarginPx, Math.Min(y, wy + Math.Max(0, wh - ph - MarginPx)));

            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowPos(hwnd, new IntPtr(-1) /* HWND_TOPMOST */, x, y, 0, 0, SwpNoSize | SwpNoActivate);
        }
        catch
        {
            // Останемся на позиции по умолчанию — оверлей всё равно покажется.
        }
    }

    private void FadeOut()
    {
        if (_closing)
            return;
        _closing = true;
        StopTimers();
        try
        {
            var exitStoryboard = Application.Current.FindResource("MotionOsdExit") as Storyboard;
            exitStoryboard?.Begin((FrameworkElement)Content);
        }
        catch
        {
            // Ресурс недоступен — просто закрываемся позже по таймеру.
        }

        // Закрытие по таймеру, а не по Completed: ресурсный Storyboard
        // не гарантирует Completed (часы могут не завершиться) — окно зависало.
        // Таймер в поле, а не в локальной переменной: иначе ForceClose() не мог
        // его остановить, и он продолжал звать Close() на уже закрытое окно.
        _closeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),  // Exit-анимация 150 мс + запас
        };
        _closeTimer.Tick += (_, _) =>
        {
            StopTimers();
            try
            {
                Close();
            }
            catch
            {
                // Уже закрыт.
            }
        };
        _closeTimer.Start();
    }

    private void ForceClose()
    {
        _closing = true;
        StopTimers();
        try
        {
            Close();
        }
        catch
        {
            // Уже закрыт.
        }
    }

    private static UIElement BuildContent(OsdInfo info)
    {
        var chipBg = info.Charging
            ? Color.FromArgb(0x33, 0xF5, 0xA6, 0x23)   // «Заряжается» — тёплый акцент
            : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF);
        var chipFg = info.Charging
            ? Color.FromRgb(0xFF, 0xD9, 0xA0)
            : Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF);

        // Строка 1: заряд крупно + статус зарядки пилюлей справа.
        var battery = new TextBlock
        {
            Text = info.Battery,
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Пульсация иконки батареи, но только пока она заряжается: при
        // разряде мигание означало бы «тревога» там, где её быть не должно.
        // Только Opacity - это GPU-путь, в отличие от Effect или Blur.
        if (info.Charging)
        {
            battery.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(800))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                });
        }
        var chip = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(chipBg),
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = new TextBlock
            {
                Text = info.Status,
                FontSize = 12,
                Foreground = new SolidColorBrush(chipFg),
                TextWrapping = TextWrapping.Wrap,
            },
        };
        var row1 = new Grid();
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(battery, 0);
        Grid.SetColumn(chip, 1);
        row1.Children.Add(battery);
        row1.Children.Add(chip);

        var panel = new StackPanel();
        panel.Children.Add(row1);
        panel.Children.Add(new TextBlock
        {
            Text = info.Connection,
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = new SolidColorBrush(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF)),
            TextWrapping = TextWrapping.Wrap,
        });
        if (info.Runtime.Length > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "⏱ " + info.Runtime,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF)),
            });
        }

        var border = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0xE8, 0x1C, 0x1C, 0x1F)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xE8, 0x11, 0x23)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 14, 18, 14),
            MinWidth = 240,
            Effect = new DropShadowEffect
            {
                BlurRadius = 28,
                ShadowDepth = 6,
                Opacity = 0.5,
                Color = Color.FromArgb(0xFF, 0xE8, 0x11, 0x23),
            },
            Child = panel,
        };
        // TransformGroup, а не голый TranslateTransform: прилёт OSD теперь
        // анимирует и Y, и Scale (MotionOsdEnter). Обе трансформации должны
        // жить в одной группе - иначе путь до ScaleX не разрешится.
        // Порядок важен: [0] = Translate (Y), [1] = Scale. Storyboard
        // в Motion.xaml обращается к Children[1] именно как к ScaleTransform.
        border.RenderTransform = new TransformGroup();
        ((TransformGroup)border.RenderTransform).Children.Add(new TranslateTransform());
        ((TransformGroup)border.RenderTransform).Children.Add(new ScaleTransform(1, 1));
        return border;
    }
}
