using System.Diagnostics;
using System.IO.Compression;

namespace ImpactProConfig.Updater;

/// <summary>
/// Updater.exe — процесс-обновитель портативной сборки.
///
/// Запущенный Windows exe нельзя перезаписать, поэтому замену файлов
/// выполняет отдельный независимый процесс:
///   1. ждёт завершения основного приложения (pid);
///   2. распаковывает zip поверх папки приложения (overwrite);
///   3. удаляет временный zip;
///   4. запускает обновлённый ImpactProConfig.exe;
///   5. завершается.
///
/// Аргументы: --zip "&lt;архив.zip&gt;" --target "&lt;папка&gt;" --pid &lt;PID&gt;
/// Коды возврата: 0 OK, 2 аргументы, 3 zip нет, 4 папки нет, 1 исключение.
/// </summary>
internal static class Program
{
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "ImpactProConfig-updater.log");

    private static int Main(string[] args)
    {
        try
        {
            string? zip = GetArg(args, "--zip");
            string? target = GetArg(args, "--target");
            string pidText = GetArg(args, "--pid") ?? string.Empty;

            if (zip is null || target is null ||
                !int.TryParse(pidText, out int pid) || pid <= 0)
            {
                Log("ERROR: неверные аргументы (нужны --zip, --target, --pid)");
                return 2;
            }

            if (!File.Exists(zip))
            {
                Log($"ERROR: архив не найден: {zip}");
                return 3;
            }

            if (!Directory.Exists(target))
            {
                Log($"ERROR: папка не найдена: {target}");
                return 4;
            }

            // 1. Ждём завершения основного процесса (файлы перестают быть заняты).
            try
            {
                using var main = Process.GetProcessById(pid);
                bool exited = main.WaitForExit(5000);
                Log($"wait: pid={pid} exited={exited}");
            }
            catch (ArgumentException)
            {
                Log($"wait: pid={pid} уже завершился");
            }

            // 2. Распаковка zip в целевую папку с заменой. Несколько попыток:
            //    если процесс не успел выйти за 5 с, файлы ещё могут быть заняты.
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    ZipFile.ExtractToDirectory(zip, target, overwriteFiles: true);
                    Log($"extract: OK -> {target} (попытка {attempt})");
                    break;
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= 5)
                    {
                        Log($"ERROR: extract попытка {attempt}: {ex.Message}");
                        throw;
                    }
                    Log($"extract: попытка {attempt} занята, повтор...");
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
            string exe = Path.Combine(target, "ImpactProConfig.exe");
            if (!File.Exists(exe))
            {
                Log($"ERROR: после обновления нет {exe}");
                return 1;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = target,
                UseShellExecute = true,
            });
            Log($"start: {exe}");

            // 5. Готово.
            return 0;
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex}");
            return 1;
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
}
