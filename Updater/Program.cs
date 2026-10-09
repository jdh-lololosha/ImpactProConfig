using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace ImpactProConfig.Updater;

/// <summary>
/// Updater.exe — процесс-обновлятор портативной сборки.
///
/// Запущенный Windows exe нельзя перезаписать, поэтому замену файлов выполняет
/// отдельный независимый процесс:
///   1. показывает сплэш-окно в стиле Discord и ждёт завершения основного процесса (pid);
///   2. распаковывает zip поверх папки приложения (5 попыток: файлы может держать антивирус);
///   3. запускает обновлённый ImpactProConfig.exe;
///   4. закрывается.
///
/// Аргументы: --zip "&lt;архив.zip&gt;" --target "&lt;папка&gt;" --pid &lt;PID&gt; [--version &lt;vX.Y.Z&gt;]
/// Коды возврата: 0 OK, 2 аргументы, 3 zip нет, 4 папки нет, 1 исключение.
///
/// Модель потоков: UI-окно создаётся и живёт на главном STA-потоке, вся работа
/// идёт на фоновом потоке и обновляет окно через PostMessage (потокобезопасно).
/// Главный поток занят только очередью сообщений, поэтому окно перерисовывается
/// всё время распаковки и не «замерзает».
/// </summary>
internal static class Program
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "ImpactProConfig-updater.log");

    private static SplashWindow? _splash;
    private static int _exitCode = 1;

    [STAThread]
    private static int Main(string[] args)
    {
        var splash = new SplashWindow();
        _splash = splash;

        string? zip = GetArg(args, "--zip");
        string? target = GetArg(args, "--target");
        string pidText = GetArg(args, "--pid") ?? string.Empty;
        splash.Version = GetArg(args, "--version");

        // Ошибки аргументов и входных файлов показываем на том же окне,
        // а не молча умираем: пользователь должен понимать, что произошло.
        // Окно создаём сразу, до любых проверок: пользователь должен видеть
        // окно даже при ошибке аргументов, иначе апдейтер снова «молча умирает».
        splash.Show();

        string? validationError = Validate(zip, target, pidText);
        if (validationError is not null)
        {
            splash.ShowError(validationError);
            PumpUntilDismissed();
            return _exitCode;
        }

        var done = new ManualResetEventSlim(false);
        var worker = new Thread(() =>
        {
            try { _exitCode = RunUpdate(splash, zip!, target!, int.Parse(pidText)); }
            catch (Exception ex)
            {
                Log($"FATAL: {ex}");
                _exitCode = 1;
                splash.ShowError($"Ошибка обновления:\n{ex.Message}");
            }
            finally { done.Set(); }
        })
        { IsBackground = true, Name = "updater-work" };
        worker.Start();

        // Крутим очередь сообщений, пока идёт работа. Дополнительно даём
        // окну пожить после done, чтобы пользователь увидел финальную надпись.
        while (!done.IsSet)
        {
            if (!PumpOne())
                break;
        }

        done.Wait();
        Thread.Sleep(700);      // показать «Обновление установлено»
        splash.Close();
        return _exitCode;
    }

    /// <summary>Проверка входных данных. null — всё в порядке, иначе текст ошибки.</summary>
    private static string? Validate(string? zip, string? target, string pidText)
    {
        if (zip is null || target is null ||
            !int.TryParse(pidText, out int pid) || pid <= 0)
        {
            Log("ERROR: неверные аргументы (нужны --zip, --target, --pid)");
            return "Неверные аргументы запуска обновления.\n" +
                   "Скачайте архив заново и запустите обновление ещё раз.";
        }

        if (!File.Exists(zip))
        {
            Log($"ERROR: архив не найден: {zip}");
            return "Файл обновления не найден.\n" +
                   "Возможно, он был удалён.\n" +
                   "Скачайте архив заново на странице релизов.";
        }

        if (!Directory.Exists(target))
        {
            Log($"ERROR: папка не найдена: {target}");
            return $"Папка программы не найдена:\n{target}";
        }

        return null;
    }

    /// <summary>Вся логика обновления. Выполняется на фоновом потоке.</summary>
    private static int RunUpdate(SplashWindow splash, string zip, string target, int pid)
    {
        // 1. Ждём завершения основного процесса (файлы перестают быть заняты).
        splash.SetPhase(SplashWindow.Phase.WaitingApp, "Ожидание закрытия программы...");
        splash.Pump();

        try
        {
            using var main = Process.GetProcessById(pid);
            bool exited = main.WaitForExit(15000);
            Log($"wait: pid={pid} exited={exited}");
        }
        catch (ArgumentException)
        {
            Log($"wait: pid={pid} уже завершился");
        }

        // 2. Распаковка zip в целевую папку с заменой.
        splash.SetPhase(SplashWindow.Phase.Extracting,
            splash.Version is { Length: > 0 }
                ? $"Распаковка обновления {splash.Version}..."
                : "Распаковка обновления...");

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                ExtractWithProgress(zip, target, splash);
                Log($"extract: OK -> {target} (попытка {attempt})");
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 5)
                {
                    Log($"ERROR: extract попытка {attempt}: {ex.Message}");
                    splash.ShowError(
                        "Не удалось заменить файлы программы.\n\n" +
                        "Возможно, они заняты антивирусом или ещё одним экземпляром.\n" +
                        "Закройте всё лишнее и запустите обновление ещё раз.\n\n" +
                        ex.Message);
                    splash.Pump();
                    return 1;
                }
                Log($"extract: попытка {attempt} занята, повтор...");
                splash.SetPhase(SplashWindow.Phase.Extracting,
                    $"Файлы заняты... попытка {attempt} из 5");
                splash.Pump();
                Thread.Sleep(500);
            }
        }

        // 3. Удаляем временный zip.
        try
        {
            File.Delete(zip);
            Log("zip: удалён");
        }
        catch (Exception ex)
        {
            Log($"zip: не удалён ({ex.Message}) — не критично");
        }

        // 4. Запуск обновлённого приложения.
        splash.SetPhase(SplashWindow.Phase.Launching, "Запуск новой версии...");
        splash.SetFraction(0.9);

        string exe = Path.Combine(target, "ImpactProConfig.exe");
        if (!File.Exists(exe))
        {
            Log($"ERROR: после обновления нет {exe}");
            splash.ShowError($"После обновления не найден файл:\n{exe}\n\n" +
                             "Переустановите программу из архива.");
            splash.Pump();
            return 1;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = target,
            UseShellExecute = true,
        });
        Log($"start: {exe}");

        splash.SetPhase(SplashWindow.Phase.Done, "Обновление установлено");
        splash.SetFraction(1.0);
        Log("done");
        return 0;
    }

    /// <summary>
    /// Распаковка с прогрессом: сначала считаем число записей, затем пишем их
    /// по одной, отдавая долю в UI. Ошибки уходят вызывающему коду — он делает
    /// повторные попытки, потому что занятые файлы это норма.
    /// </summary>
    private static void ExtractWithProgress(string zipPath, string target, SplashWindow splash)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        int total = 0;
        foreach (var e in archive.Entries)
            if (e.Name.Length > 0) total++;

        int done = 0;
        foreach (var entry in archive.Entries)
        {
            // Директории в zip — просто создаём, файлы распаковываем.
            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(Path.Combine(target, entry.FullName));
                continue;
            }

            string destination = Path.Combine(target, entry.FullName);
            string? dir = Path.GetDirectoryName(destination);
            if (dir is not null)
                Directory.CreateDirectory(dir);

            // overwrite: true обязателен — иначе повторная попытка после занятого файла
            // упадёт на «файл уже существует».
            entry.ExtractToFile(destination, overwrite: true);

            done++;
            if (total > 0)
                splash.SetFraction((double)done / total);
        }

        splash.SetFraction(1.0);
    }

    /// <summary>
    /// Одна итерация цикла сообщений. false — окончилась очередь (WM_QUIT),
    /// либо окно закрыто пользователем.
    /// </summary>
    private static bool PumpOne()
    {
        if (!GetMessage(out MSG msg, IntPtr.Zero, 0, 0))
            return false;
        TranslateMessage(ref msg);
        DispatchMessage(ref msg);
        return _splash is { hwnd: not 0 };
    }

    /// <summary>Крутит очередь, пока окно с ошибкой не закроют (кнопка «Закрыть»).</summary>
    private static void PumpUntilDismissed()
    {
        while (_splash is { hwnd: not 0 })
        {
            if (!GetMessage(out MSG msg, IntPtr.Zero, 0, 0))
                return;
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    /// <summary>Значение аргумента командной строки вида --name value.</summary>
    private static string? GetArg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch
        {
            // Лог не критичен.
        }
    }

    // ===== Минимальный P/Invoke для цикла сообщений =====

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd, message, wParam, lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr DispatchMessage(ref MSG msg);
}