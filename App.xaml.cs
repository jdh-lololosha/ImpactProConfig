using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ImpactProConfig;

public partial class App : Application
{
    /// <summary>
    /// Папка для логов и пользовательских данных: %LOCALAPPDATA%\ImpactProConfig.
    ///
    /// Почему не AppContext.BaseDirectory: приложение ставится в Program Files,
    /// куда обычный пользователь писать не может. Запись туда молча падала
    /// (пустой catch), из-за чего диагностика выглядела как «процесс жив, а окна
    /// нет». LocalApplicationData доступен на запись всегда и без админа.
    /// </summary>
    internal static string DataDir
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ImpactProConfig");
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
                // Если даже это недоступно — работаем без логов, но не падаем.
            }
            return dir;
        }
    }

    internal static void Log(string msg)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(DataDir, "crash.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch
        {
            // Лог не критичен.
        }
    }

    /// <summary>Клик по телу ComboBox (шапка/шеврон) — переключает выпадающий список.</summary>
    /// <remarks>Клик гарантированно доходит до ComboBox (подтверждено телеметрией в ui.log),
    /// а убитый ToggleButton-оверлей в шаблоне получал нулевой размер и hit-test
    /// проваливался мимо него. UIA Expand при этом работал — сам механизм открытия
    /// исправен, не работала только доставка клика.</remarks>
    private void Combo_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox cb)
            return;

        // Routed-события из popup идут через ComboBox (ui.log: WIN PreviewMouseDown
        // с source=ComboBoxItem — значит, комбо есть в цепочке маршрута). Поэтому
        // сюда попадают и клики по ПУНКТАМ списка. Переключать список нельзя:
        // на туннелировании он закрывается раньше, чем ComboBoxItem обработает
        // нажатие, — выбор не применяется («не могу выбрать значение из списка»).
        bool inDropDown = false;
        for (var d = e.OriginalSource as DependencyObject; d != null; d = NextParent(d))
        {
            // PopupRoot — внутренний класс WPF, определяем его по имени типа.
            if (d is System.Windows.Controls.ComboBoxItem ||
                d.GetType().Name == "PopupRoot")
            {
                inDropDown = true;
                break;
            }
            if (ReferenceEquals(d, cb))
                break;
        }

        Log($"ComboPreview: src={e.OriginalSource?.GetType().Name} inDropDown={inDropDown} open={cb.IsDropDownOpen}");
        if (inDropDown)
            return;

        cb.IsDropDownOpen = !cb.IsDropDownOpen;
    }

    private static DependencyObject? NextParent(DependencyObject o) =>
        o is System.Windows.Media.Visual
            ? System.Windows.Media.VisualTreeHelper.GetParent(o)
            : LogicalTreeHelper.GetParent(o);

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log($"AppDomain: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
            Log($"UnobservedTask: {args.Exception}");
        base.OnStartup(e);

        // Фирменный красный акцент для контролов WPF-UI (кнопки, тумблеры).
        // Вызывается до создания окна из StartupUri.
        try
        {
            Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
                System.Windows.Media.Color.FromRgb(0xE8, 0x11, 0x23),
                Wpf.Ui.Appearance.ApplicationTheme.Dark,
                false);
        }
        catch (Exception ex)
        {
            Log($"AccentApply: {ex.Message}");
        }

        Log("Startup OK");
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log($"Dispatcher: {e.Exception}");
        e.Handled = true;
    }
}
