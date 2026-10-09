using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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

    /// <summary>
    /// Порог ротации логов: 5 МБ на файл.
    ///
    /// Раньше ограничения не было вообще, и crash.log дорос до 1.2 ГБ за
    /// несколько сессий: исключение RenderTransform.ScaleX на MouseEnter
    /// повторялось ~86 тысяч раз за час, и каждое писалось в лог. Файл
    /// такого размера сам начинает тормозить запись и съедает диск.
    /// </summary>
    private const long MaxLogBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Обрезает лог до последних MaxLogBytes, помечая срез. Вызывается перед
    /// каждой записью: дешевле, чем после, и не даёт файлу перерасти лимит
    /// на длинном цикле записи.
    ///
    /// Порог и обрезка — общие с логом USB-сессии в Driver/DeviceSession.cs.
    /// </summary>
    internal static void TrimLogIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= MaxLogBytes)
                return;

            // Читаем хвост файла целыми строками, чтобы не порвать UTF-8
            // посреди символа и не оставить в логе обрывок сообщения.
            const int tailBytes = (int)MaxLogBytes / 2;
            string tail;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite))
            {
                stream.Seek(Math.Max(0, stream.Length - tailBytes), SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                tail = reader.ReadToEnd();
            }

            int firstNewline = tail.IndexOf('\n');
            if (firstNewline >= 0)
                tail = tail[(firstNewline + 1)..];

            string header =
                $"=== log truncated at {DateTime.Now:yyyy-MM-dd HH:mm:ss} " +
                $"(was {info.Length / 1048576.0:0.#} MB) ===\n";

            // Подмена через File.Replace: она умеет менять файл на месте, тогда
            // как File.Move(overwrite) требует DELETE-доступа и падает с
            // sharing violation, если файл открыт кем-то ещё (лог пишут
            // несколько потоков: UI, USB-сессия, Dispatcher-исключения).
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, header + tail);
            try
            {
                File.Move(tmp, path, overwrite: true);
            }
            catch (IOException)
            {
                // Файл занят другим потоком — повторяем, запись короточная.
                Thread.Sleep(40);
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch
        {
            // Обрезка не критична: если не вышло — пишем дальше, лог вырастет
            // и будет обрезан при следующем вызове.
        }
    }

    /// <summary>
    /// Сервлайн логов: без него два потока открывают один файл, и подмена
    /// при обрезке падает с sharing violation.
    /// </summary>
    internal static readonly object LogLock = new();

    internal static void Log(string msg)
    {
        string path = Path.Combine(DataDir, "crash.log");
        lock (LogLock)
        {
            try
            {
                TrimLogIfNeeded(path);
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            }
            catch
            {
                // Лог не критичен.
            }
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

    /// <summary>
    /// Последняя линия обороны UI. Раньше здесь стояло безусловное
    /// e.Handled = true на любое исключение: падение молча проглатывалось,
    /// пользователь видел «ничего не произошло», а интерфейс оставался
    /// наполовину обновлённым.
    ///
    /// Теперь: сначала пишем в crash.log (исключение уже не потерять), потом
    /// подавляем только то, что действительно безопасно подавить. Наружу
    /// пропускаем всё, что похоже на дефект логики (NRE, IndexOutOfRange,
    /// ArgumentOutOfRange, InvalidCast) — для них приложение надо перезапустить,
    /// а не продолжать работу в неизвестном состоянии.
    /// </summary>
    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log($"Dispatcher: {e.Exception}");

        if (IsRecoverable(e.Exception))
        {
            e.Handled = true;
            return;
        }

        // Не подавляем: WPF сам покажет ошибку и завершит приложение.
        // Пользователь увидит причину, а лог укажет на последний шаг.
    }

    /// <summary>
    /// Ошибки данных/окружения, от которых UI может оправиться без перезапуска.
    /// Логика (NullReference и т.п.) сюда НЕ относится намеренно.
    /// </summary>
    private static bool IsRecoverable(Exception ex) => ex switch
    {
        COMException => true,                       // COM-обёртки hidusb / OLE
        IOException => true,                        // файлы настроек, лог
        UnauthorizedAccessException => true,
        _ => false,
    };
}
