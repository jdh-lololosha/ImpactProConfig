using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ImpactProConfig.ViewModels;
using Wpf.Ui.Controls;

namespace ImpactProConfig;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel = new();

    /// <summary>Значок в трее; живёт вместе с окном, обновляется по таймеру.</summary>
    private readonly ImpactProConfig.Services.TrayIconService _tray;

    /// <summary>Хук оконных сообщений для перехвата WM_DEVICECHANGE.</summary>
    private HwndSource? _hwndSource;

    // Константы WM_DEVICECHANGE.
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    /// <summary>true — пользователь выбрал «Выход», окно можно действительно закрыть.</summary>
    private bool _exitRequested;

    /// <summary>ViewModel окна; страницы подставляют его себе, если NavigationView
    /// создаёт их без контекста (навигация по клику идёт без dataContext).</summary>
    public MainViewModel ViewModel => _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        Loaded += OnLoaded;
        Closing += OnClosing;

        // --- Hot-Plug: перехват WM_DEVICECHANGE ---
        // Перехват вешаем НА САМО ОКНО приложения: broadcast DBT-сообщения
        // уходят только top-level окнам, а отдельное невидимое HwndSource-окно
        // их не получало — отсюда залипание статуса при выдёргивании кабеля.
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);
        };

        // --- Портативное самообновление ---
        // После скачивания .zip и запуска скрипта-обновителя приложение
        // обязано ВЫЙТИ (файлы заняты), иначе копирование не перезапишет exe.
        _viewModel.ExitRequested += RequestExit;

        // --- Трей ---
        // Сворачивание прячет окно в трей, а «закрытие» крестиком тоже сворачивает,
        // чтобы приложение продолжало писать телеметрию батареи. Выход — только
        // через «Выход» в меню трея (или Alt+F4 при видимом окне — тоже в трей).
        _tray = new ImpactProConfig.Services.TrayIconService(
            RestoreFromTray,
            index => Dispatcher.Invoke(() => _viewModel.SelectedProfileIndex = index),
            RequestExit);

        var trayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        trayTimer.Tick += (_, _) => _tray.Update(
            _viewModel.BatteryPercent, _viewModel.IsCharging,
            _viewModel.IsWireless, _viewModel.IsConnected);
        trayTimer.Start();
        Closed += (_, _) => trayTimer.Stop();

        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                Hide();
                ShowInTaskbar = false;
            }
        };

        Closed += (_, _) => _tray.Dispose();

        Closed += (_, _) =>
        {
            _viewModel.Dispose();
            ImpactProConfig.Services.MouseOsdHook.Stop();
            ImpactProConfig.Services.KeyboardOsdHook.Stop();
        };

        // OSD: глобальный перехват нажатий физической мыши (триггер оверлея).
        try
        {
            ImpactProConfig.Services.MouseOsdHook.Start(_viewModel.OsdHookHandler);
        }
        catch (Exception ex)
        {
            App.Log($"OSD-hook FAILED: {ex.Message}");
        }

        // OSD: перехват F24, который мышь шлёт вместо назначенной кнопки
        // (включая DPI-кнопку): глушим клавишу — показываем оверлей.
        try
        {
            ImpactProConfig.Services.KeyboardOsdHook.Start(_viewModel.OsdKeyHookHandler);
        }
        catch (Exception ex)
        {
            App.Log($"KB-hook FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Обработчик оконных сообщений. Ловит WM_DEVICECHANGE для хот-плага.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_DEVICECHANGE)
            return IntPtr.Zero;

        int reason = wParam.ToInt32();
        if (reason != DBT_DEVICEARRIVAL && reason != DBT_DEVICEREMOVECOMPLETE)
            return IntPtr.Zero;

        // Не дёргаем UI напрямую: перескан идёт в фоне, состояние сессии
        // само обновит статус через StateChanged.
        App.Log($"WM_DEVICECHANGE reason=0x{reason:X4} -> RescanAsync");
        _ = _viewModel.RescanAsync();
        return IntPtr.Zero;
    }

    /// <summary>Развернуть окно из трея (клик по значку или «Открыть» в меню).</summary>
    private void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            ShowInTaskbar = true;
            Show();
            WindowState = WindowState.Normal;
            Activate();
        });
    }

    /// <summary>
    /// «Выход» из меню трея и после запуска обновления — единственный путь,
    /// который реально закрывает приложение (Closing в трей не прячет при
    /// выставленном _exitRequested).
    /// </summary>
    private void RequestExit()
    {
        _exitRequested = true;
        Application.Current.Shutdown();
    }

    /// <summary>Крестик и Alt+F4 прячут окно в трей; выход — только через трей.</summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exitRequested)
            return;
        e.Cancel = true;
        ShowInTaskbar = false;
        Hide();
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

        // Проверка обновлений — после чтения устройства, отдельной задачей:
        // сеть не должна задерживать показ окна, и ошибка сети не должна
        // выглядеть как поломка конфигуратора.
        _ = Task.Run(async () =>
        {
            await _viewModel.CheckForUpdatesAsync();
        });
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
        Dispatcher.BeginInvoke(new Action(AnimateCurrentPage), DispatcherPriority.Loaded);
    }

    private void AnimateCurrentPage()
    {
        var page = FindPageContent(RootNavigation);
        if (page == null)
            return;
        if (page.RenderTransform is not TranslateTransform)
            page.RenderTransform = new TranslateTransform();
        var sb = Application.Current.FindResource("MotionPageEnter") as Storyboard;
        sb?.Begin(page);
    }

    private static FrameworkElement? FindPageContent(DependencyObject root)
    {
        var frame = FindVisualChild<Frame>(root);
        if (frame?.Content is FrameworkElement fe)
            return fe;
        var cp = FindVisualChild<ContentPresenter>(root);
        if (cp?.Content is FrameworkElement fe2)
            return fe2;
        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t)
                return t;
            var result = FindVisualChild<T>(child);
            if (result != null)
                return result;
        }
        return null;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        // Запись в мышь: явное «Применить» или автоприменение по таймеру
        // (оба идут через MainViewModel.ApplyAsync).
        await _viewModel.ApplyAsync();
    }

    /// <summary>«Отменить»: сброс отсчёта до автоприменения, изменения остаются.</summary>
    private void CancelAutoApply_Click(object sender, RoutedEventArgs e)
        => _viewModel.CancelAutoApply();

    /// <summary>«Скачать и обновить» на плашке обновления (сети не блокирует UI).</summary>
    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InstallUpdateAsync();
        }
        catch (Exception ex)
        {
            App.Log($"UpdateClick FAILED: {ex.Message}");
        }
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
