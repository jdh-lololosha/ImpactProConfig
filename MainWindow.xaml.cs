using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

        // Пружина нажатия цепляется на окно, а не через Style.
        //
        // Почему не Style: неявный стиль в Application.Resources перекрывает
        // неявный стиль WPF-UI (у него низший приоритет из-за
        // MergedDictionaries), а BasedOn на стиль без BasedOn отбрасывает
        // Background/Foreground/шаблон - кнопка рисуется системной серой.
        // Тот же приём не годится и для BasedOn="{StaticResource
        // {x:Type ui:Button}}": разрешение неявного стиля привело бы к самому
        // себе. Обработчики на окне меняют только RenderTransform и не
        // трогают Style вообще.
        PreviewMouseLeftButtonDown += OnPressDown;
        PreviewMouseLeftButtonUp += OnPressUp;
        PreviewMouseMove += OnPressCancel;
        LostMouseCapture += OnPressCancel;
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
                "battery" => typeof(Pages.BatteryPage),
                "accel" => typeof(Pages.AccelerationPage),
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

        AnimateCardsCascade(page);
    }

    /// <summary>Пружина нажатия: Scale 0.95 за 80 мс, отскок 1.02 и посадка в 1.0.</summary>
    private void OnPressDown(object sender, MouseButtonEventArgs e)
    {
        var button = FindPressTarget(e.OriginalSource as DependencyObject);
        if (button is null)
            return;

        var scale = EnsureScale(button);
        if (scale is null)
            return;

        // Стартуем от текущего значения, а не от 1.0: если кнопку уже
        // отпустили и тут же нажали снова, прыжка не будет.
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, SpringAnimation(
            scale.ScaleX, PressScale, TimeSpan.FromMilliseconds(80), null));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, SpringAnimation(
            scale.ScaleY, PressScale, TimeSpan.FromMilliseconds(80), null));
    }

    private void OnPressUp(object sender, MouseButtonEventArgs e)
    {
        ReleaseSpring(e.OriginalSource as DependencyObject);
    }

    /// <summary>
    /// Уход курсора с кнопки во время нажатия: если мышь отжали мимо кнопки
    /// или увели за её пределы, нажатие «залипает» на Scale 0.95. Этот
    /// обработчик возвращает элемент в 1.0.
    /// </summary>
    private void OnPressCancel(object sender, MouseEventArgs e)
    {
        if (e is MouseButtonEventArgs mbe)
            ReleaseSpring(mbe.OriginalSource as DependencyObject);
        else if (e.OriginalSource is DependencyObject src)
            ReleaseSpring(src);
    }

    private void ReleaseSpring(DependencyObject? source)
    {
        var button = FindPressTarget(source);
        var scale = EnsureScale(button);
        if (scale is null)
            return;

        // Перелёт: 0.95 -> 1.02 за 150 мс, затем посадка в 1.0.
        var bounce = SpringAnimation(
            PressScale, ReleaseOvershoot, TimeSpan.FromMilliseconds(150),
            new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 });
        bounce.Completed += (_, _) =>
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, SpringAnimation(
                ReleaseOvershoot, 1.0, TimeSpan.FromMilliseconds(100),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, SpringAnimation(
                ReleaseOvershoot, 1.0, TimeSpan.FromMilliseconds(100),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
    }

    private const double PressScale = 0.95;
    private const double ReleaseOvershoot = 1.02;

    private static DoubleAnimation SpringAnimation(
        double from, double to, TimeSpan duration, IEasingFunction? easing)
    {
        var anim = new DoubleAnimation(from, to, duration)
        {
            FillBehavior = FillBehavior.HoldEnd,
        };
        if (easing is not null)
            anim.EasingFunction = easing;
        else
            anim.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        return anim;
    }

    /// <summary>
    /// Ближайший ButtonBase вверх по визуальному дереву от точки нажатия.
    /// Ищем от источника события, а не от фокуса: фокус может быть на поле
    /// ввода внутри карточки, а пружинить нужно саму карточку-кнопку.
    /// </summary>
    private static ButtonBase? FindPressTarget(DependencyObject? source)
    {
        // Пять уровней вверх — достаточно для шаблонов WPF-UI; глубже
        // заходить смысла нет: вложенных кнопок в кнопке не бывает.
        for (int i = 0; source is not null && i < 5; i++)
        {
            if (source is ButtonBase b)
                return b;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    /// <summary>
    /// Возвращает ScaleTransform элемента, создавая его при необходимости.
    ///
    /// Важно не затереть чужую трансформацию: если у элемента уже есть
    /// RenderTransform, который не ScaleTransform (например, анимация
    /// появления), оборачиваем оба в TransformGroup. Иначе нажатие
    /// уничтожило бы анимацию появления карточки.
    /// </summary>
    private static ScaleTransform? EnsureScale(DependencyObject? target)
    {
        if (target is not FrameworkElement fe)
            return null;

        if (fe.RenderTransform is ScaleTransform existing)
            return existing;

        var scale = new ScaleTransform(1, 1);
        if (fe.RenderTransform is null || fe.RenderTransform.Value.IsIdentity)
        {
            fe.RenderTransform = scale;
        }
        else
        {
            // Чужая трансформация есть - сохраняем её в группе и добавляем
            // наш масштаб вторым, чтобы оба применялись вместе.
            var group = new TransformGroup();
            group.Children.Add(fe.RenderTransform);
            group.Children.Add(scale);
            fe.RenderTransform = group;
        }

        // Без явного Origin масштаб берётся от (0,0), и кнопка «уезжает» из
        // своей позиции вместо сжатия по центру.
        fe.RenderTransformOrigin = new Point(0.5, 0.5);
        return scale;
    }

    /// <summary>
    /// Каскадное появление карточек страницы: снизу вверх, со сдвигом
    /// задержки 50 мс на карточку. Без этого все карточки всплывают разом и
    /// интерфейс выглядит статично.
    ///
    /// Только RenderTransform (Translate) и Opacity - оба параметра WPF
    /// композитит на GPU без растеризации элемента. КАДРОВЫЙ РЕНДЕР НЕ
    /// ИСПОЛЬЗУЕТСЯ: карточки получают готовую трансформацию сразу, а
    /// анимация идёт через диспетчер композиции, то есть она не занимает
    /// UI-поток даже при десятке карточек.
    ///
    /// Трансформацию ставим в коде, а не в XAML: страницы разные, и общий
    /// стиль навязал бы анимацию элементам, которые её не должны иметь
    /// (например, самому ScrollViewer).
    /// </summary>
    private static void AnimateCardsCascade(FrameworkElement page)
    {
        // Верхний контейнер содержимого страницы: у наших страниц это Grid
        // внутри ScrollViewer. Берём именно его, чтобы не анимировать сам
        // ScrollViewer (он и есть "страница" для MotionPageEnter).
        var container = page is ScrollViewer sv
            ? sv.Content as FrameworkElement
            : (FrameworkElement?)page;

        if (container is null)
            return;

        var cards = CollectTopLevelCards(container);
        if (cards.Count == 0)
            return;

        const int StaggerMs = 50;
        const double RiseY = 25;

        for (int i = 0; i < cards.Count; i++)
        {
            FrameworkElement card = cards[i];

            // Заголовок страницы не "вылетает": он задаёт контекст, и его
            // мигание вместе с карточками выглядит как ошибка вёрстки.
            if (card is StackPanel
                or System.Windows.Controls.TextBlock
                or Wpf.Ui.Controls.TextBlock)
                continue;

            if (card.RenderTransform is not TranslateTransform)
                card.RenderTransform = new TranslateTransform();

            card.Opacity = 0;
            ((TranslateTransform)card.RenderTransform).Y = RiseY;

            int delay = i * StaggerMs;

            var rise = new DoubleAnimation(0, RiseY, TimeSpan.FromMilliseconds(280))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd,
            };
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
            {
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd,
            };

            card.BeginAnimation(TranslateTransform.YProperty, rise);
            card.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    /// <summary>
    /// Прямые дочерние элементы контейнера страницы — это и есть карточки.
    /// Глубже не идём: у ItemsControl внутри карточек десятки элементов, и
    /// каскад по ним дал бы «мельтешение» вместо аккуратного раскрытия.
    /// </summary>
    private static List<FrameworkElement> CollectTopLevelCards(DependencyObject root)
    {
        var result = new List<FrameworkElement>();

        // VisualTreeHelper, а не LogicalTreeHelper: у него нет методов
        // GetChildrenCount/GetChild (они в WPF-UI-обёртке), и при этом он
        // отдаёт именно отрендеренные элементы, а не логические узлы.
        // При Loaded дерево визуализации уже построено - страницу мы
        // анимируем по DispatcherPriority.Loaded, после отрисовки.
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(root, i) is FrameworkElement fe
                && fe.Visibility == Visibility.Visible)
            {
                result.Add(fe);
            }
        }
        return result;
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
