using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ImpactProConfig.ViewModels;
using Wpf.Ui.Controls;

namespace ImpactProConfig;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel = new();

    /// <summary>ViewModel окна; страницы подставляют его себе, если NavigationView
    /// создаёт их без контекста (навигация по клику идёт без dataContext).</summary>
    public MainViewModel ViewModel => _viewModel;

    // --- ВРЕМЕННАЯ ДИАГНОСТИКА клика по ComboBox: пишет ui.log ---
    private static readonly object UiLogLock = new();
    private static MouseHookProc? _hookProc;
    private static IntPtr _hookId;

    private delegate IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, MouseHookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point pt);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private static void LogUi(string msg)
    {
        try
        {
            lock (UiLogLock)
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "ui.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            }
        }
        catch
        {
            // Лог не критичен.
        }
    }

    private static string Describe(DependencyObject? o)
    {
        var parts = new List<string>();
        while (o is not null && parts.Count < 6)
        {
            var s = o.GetType().Name;
            if (o is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name))
                s += "#" + fe.Name;
            parts.Add(s);
            o = o switch
            {
                Visual => VisualTreeHelper.GetParent(o),
                System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(o),
                _ => LogicalTreeHelper.GetParent(o),
            };
        }
        return string.Join(" <- ", parts);
    }

    private static string FgDesc()
    {
        try
        {
            var h = GetForegroundWindow();
            GetWindowThreadProcessId(h, out var pid);
            using var p = Process.GetProcessById((int)pid);
            return $"0x{h.ToInt64():X} {p.ProcessName}";
        }
        catch
        {
            return "?";
        }
    }

    private static string PointDesc()
    {
        try
        {
            if (!GetCursorPos(out var pt))
                return "?";
            var h = WindowFromPoint(new Point { X = pt.X, Y = pt.Y });
            GetWindowThreadProcessId(h, out var pid);
            using var p = Process.GetProcessById((int)pid);
            return $"({pt.X},{pt.Y}) hit=0x{h.ToInt64():X} {p.ProcessName}";
        }
        catch
        {
            return "?";
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _viewModel.Dispose();
            ImpactProConfig.Services.MouseOsdHook.Stop();
            ImpactProConfig.Services.KeyboardOsdHook.Stop();
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        };

        // Окно-уровневые перехваты: виден ли вообще клик нашему окну.
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(UiMouse), true);
        AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(UiMouse), true);
        // Колесо: источник, handled и метрики ScrollViewer'ов по цепочке —
        // листает кто-то или событие гасится (handledEventsToo=true — видим и погашенные).
        AddHandler(Mouse.MouseWheelEvent, new MouseWheelEventHandler(UiWheel), true);
        Activated += (_, _) => LogUi("WIN Activated");
        Deactivated += (_, _) => LogUi("WIN Deactivated");

        // Глобальный low-level хук: видит клик ДО того, как Windows решит,
        // какому окну его отдать (ловит и случаи «клик ушёл чужому окну»).
        try
        {
            _hookProc = HookCallback;
            using var curProcess = Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            _hookId = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _hookProc,
                GetModuleHandle(curModule.ModuleName), 0);
            LogUi($"LL-hook installed={_hookId != IntPtr.Zero}");
        }
        catch (Exception ex)
        {
            LogUi($"LL-hook FAILED: {ex.Message}");
        }

        // OSD: глобальный перехват нажатий физической мыши (триггер оверлея).
        try
        {
            ImpactProConfig.Services.MouseOsdHook.Start(_viewModel.OsdHookHandler);
        }
        catch (Exception ex)
        {
            LogUi($"OSD-hook FAILED: {ex.Message}");
        }

        // OSD: перехват F24, который мышь шлёт вместо назначенной кнопки
        // (включая DPI-кнопку): глушим клавишу — показываем оверлей.
        try
        {
            ImpactProConfig.Services.KeyboardOsdHook.Start(_viewModel.OsdKeyHookHandler);
            LogUi($"KB-hook installed={ImpactProConfig.Services.KeyboardOsdHook.Installed}");
        }
        catch (Exception ex)
        {
            LogUi($"KB-hook FAILED: {ex.Message}");
        }
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == 0x0201 /* WM_LBUTTONDOWN */ || wParam == 0x0202 /* WM_LBUTTONUP */))
        {
            var kind = wParam == 0x0201 ? "GLOBAL DOWN" : "GLOBAL UP";
            LogUi($"{kind} {PointDesc()} fg={FgDesc()}");
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void UiMouse(object sender, MouseButtonEventArgs e)
    {
        LogUi($"WIN {e.RoutedEvent.Name} src={Describe(e.OriginalSource as DependencyObject)} " +
              $"handled={e.Handled} fg={FgDesc()} {PointDesc()}");
        DiagnoseCombo(e);
        var stamp = e.RoutedEvent.Name;
        // Состояние всех комбо сразу после раскладки событий...
        Dispatcher.BeginInvoke(new Action(() => LogUi($"  {stamp}+layout: {ComboStates()}")),
            DispatcherPriority.Input);
        // ...и через 250 мс (поймать «открылось и мгновенно закрылось»).
        _ = Task.Delay(250).ContinueWith(_ =>
            Dispatcher.BeginInvoke(new Action(() => LogUi($"  {stamp}+250ms: {ComboStates()}"))));
    }

    /// <summary>Вглубь: границы комбо, состояние ToggleButton-оверлея и что
    /// WPF-хит-тест возвращает в точке клика (перехватывает ли оверлей).</summary>
    private void DiagnoseCombo(MouseButtonEventArgs e)
    {
        try
        {
            var combo = FindAncestor<ComboBox>(e.OriginalSource as DependencyObject);
            if (combo is null)
            {
                LogUi("  combo: не найден в цепочке OriginalSource");
                return;
            }

            var tl = combo.PointToScreen(new Point(0, 0));
            var tb = combo.Template?.FindName("ToggleButton", combo) as FrameworkElement;
            string tbInfo;
            if (tb is null)
            {
                tbInfo = "tb=удалён (оверлей выключен, клик обрабатывает EventSetter)";
            }
            else
            {
                var tbPos = e.GetPosition(tb);
                tbInfo = $"tb={tb.ActualWidth:F0}x{tb.ActualHeight:F0} vis={tb.Visibility} " +
                         $"hit={tb.IsHitTestVisible} posInTb=({tbPos.X:F1},{tbPos.Y:F1})";
            }

            LogUi($"  combo screen=({tl.X:F0},{tl.Y:F0}) size={combo.ActualWidth:F0}x{combo.ActualHeight:F0} {tbInfo}");

            var hit = VisualTreeHelper.HitTest(combo, e.GetPosition(combo));
            LogUi($"  WPF-hit в точке клика: {Describe(hit?.VisualHit)}");
        }
        catch (Exception ex)
        {
            LogUi($"  combo-diag: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static T? FindAncestor<T>(DependencyObject? o) where T : class
    {
        while (o is not null)
        {
            if (o is T t)
                return t;
            o = o is Visual ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o);
        }
        return null;
    }

    private string ComboStates()
    {
        var combos = new List<ComboBox>();
        CollectCombos(this, combos);
        return combos.Count == 0
            ? "no combos"
            : string.Join(" | ", combos.Select((c, i) => $"[{i}]open={c.IsDropDownOpen}"));
    }

    private static void CollectCombos(DependencyObject root, List<ComboBox> outList)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ComboBox cb)
                outList.Add(cb);
            CollectCombos(child, outList);
        }
    }
    // --- КОНЕЦ ВРЕМЕННОЙ ДИАГНОСТИКИ ---

    /// <summary>
    /// Телеметрия колеса: цепочка источника + метрики каждого ScrollViewer
    /// (offset уже ПОСЛЕ классного обработчика — видно, листанул ли он).
    /// </summary>
    private void UiWheel(object sender, MouseWheelEventArgs e)
    {
        var svs = new List<string>();
        DependencyObject? cur = e.OriginalSource as DependencyObject;
        int guard = 0;
        while (cur is not null && guard++ < 60)
        {
            if (cur is System.Windows.Controls.ScrollViewer sv)
                svs.Add($"(off={sv.VerticalOffset:F0} ext={sv.ExtentHeight:F0} view={sv.ViewportHeight:F0} scroll={sv.ScrollableHeight:F0})");
            cur = cur switch
            {
                Visual => VisualTreeHelper.GetParent(cur),
                System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(cur),
                _ => LogicalTreeHelper.GetParent(cur),
            };
        }
        LogUi($"WHEEL delta={e.Delta} handled={e.Handled} src={Describe(e.OriginalSource as DependencyObject)} " +
              $"SVs=[{string.Join(" ", svs)}]");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Navigate только после Loaded: шаблон NavigationView применяется
        // при OnApplyTemplate, в конструкторе он ещё не готов (NRE в UpdateContent).
        // Первая вкладка — «Кнопки» (Home в эталоне официалки).
        // Отладочный ключ --show=dpi|settings открывает нужную вкладку сразу
        // (используется автотестами вместо синтетических кликов по навигации).
        var startPage = typeof(Pages.ButtonsPage);
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (!arg.StartsWith("--show=", StringComparison.OrdinalIgnoreCase))
                continue;
            startPage = arg[7..].ToLowerInvariant() switch
            {
                "dpi" => typeof(Pages.DpiPage),
                "settings" => typeof(Pages.SettingsPage),
                _ => typeof(Pages.ButtonsPage),
            };
            break;
        }
        RootNavigation.Navigate(startPage, _viewModel);

        // Безопасное чтение: только в фоне, никаких записей во флеш.
        await _viewModel.InitializeAsync();
    }

    /// <summary>
    /// Колесо ДО классовой обработки. Закрытый ComboBox съедал колесо и крутил
    /// свой список, а «мёртвый» корневой ScrollViewer страницы (view == extent)
    /// гасил колесо впустую — приложение не листалось. Гасим оба случая
    /// и отдаём прокрутку ближайшему живому ScrollViewer выше по цепочке.
    /// </summary>
    private void UiWheelPreview(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled)
            return;

        ScrollViewer? firstSv = null;
        ScrollViewer? liveSv = null;
        System.Windows.Controls.ComboBox? combo = null;

        for (var d = e.OriginalSource as DependencyObject; d != null; d = NextParent(d))
        {
            if (combo == null && d is System.Windows.Controls.ComboBox cb)
                combo = cb;
            if (d is ScrollViewer sv)
            {
                firstSv ??= sv;
                if (sv.ScrollableHeight > 0)
                {
                    liveSv = sv;
                    break;
                }
            }
        }

        // Закрытый ComboBox: колесо не должно менять его значение — листаем приложение.
        bool closedCombo = combo != null && !combo.IsDropDownOpen;
        // Первый ScrollViewer мёртв (не прокручивается), а выше есть живой —
        // событие до живого не дойдёт, перенаправляем сами.
        bool deadRoot = firstSv != null && liveSv != null && !ReferenceEquals(firstSv, liveSv);

        if (!closedCombo && !deadRoot)
            return;

        e.Handled = true;
        if (liveSv != null)
            ScrollBy(liveSv, e.Delta);
    }

    private static DependencyObject? NextParent(DependencyObject o) =>
        o is Visual ? VisualTreeHelper.GetParent(o) : LogicalTreeHelper.GetParent(o);

    /// <summary>Прокрутка «в стиле колеса»: нечики × WheelScrollLines строк за шаг.</summary>
    private static void ScrollBy(ScrollViewer sv, int delta)
    {
        int steps = delta / 120;
        if (steps == 0)
            steps = delta > 0 ? 1 : -1;
        int per = SystemParameters.WheelScrollLines > 0 ? (int)SystemParameters.WheelScrollLines : 3;
        int lines = Math.Abs(steps) * per;
        for (int i = 0; i < lines; i++)
        {
            if (steps > 0)
                sv.LineUp();
            else
                sv.LineDown();
        }
    }

    private void RootNavigation_SelectionChanged(object sender, RoutedEventArgs e)
    {
        // Навигация по TargetPageType выполняется самим NavigationView.
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        // Единственный путь записи в мышь — явное нажатие кнопки.
        await _viewModel.ApplyAsync();
    }

    private void Guide_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(
            this,
            "ARDOR GAMING Impact PRO — конфигуратор мыши.\n\n" +
            "1. Кнопки — маркеры на изображении мыши, назначение действий, " +
            "время отклика, экспорт/импорт профиля.\n" +
            "2. Сенсор и DPI — уровни DPI, частота опроса, параметры сенсора, подсветка.\n" +
            "3. Настройки — версии прошивок, спящий режим, автозагрузка.\n\n" +
            "Изменения применяются в мышь только по кнопке «Применить» " +
            "в правом нижнем углу. До нажатия мышь не изменяется.",
            "Справка",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }
}
