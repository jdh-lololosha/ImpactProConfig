using System.Diagnostics;
using System.IO;

namespace ImpactProConfig.Services;

/// <summary>
/// Применение настроек к драйверу.
///
/// ГЛАВНОЕ: запись settings.json НЕ применяет настройки. Драйвер держит конфиг
/// только в памяти и при загрузке восстанавливает его из реестра/образа, а не из
/// файла. Документированный путь апстрима (doc/FAQ.md): «The driver itself does
/// not store your settings. To enable them on PC start, run the GUI, or run
/// writer.exe settings.json».
///
/// ПОЧЕМУ writer.exe, А НЕ DeviceIoControl: rawaccel открывает устройство как
/// CreateFileW(L"\\\\.\\rawaccel") и шлёт IOCTL с native-структурами
/// (common/rawaccel-io.hpp). Эти раскладки документированы только в заголовках,
/// версионно-зависимы, и min_driver_version = {1,7,0} с проверкой «драйвер не
/// новее клиента». Собирать это вручную из C# — источник тихих поломок при
/// смене версии драйвера. writer.exe делает то же самое штатно.
///
/// Известные особенности writer.exe, которые мы НЕ прячем:
///   - это WinExe, поэтому он всегда показывает MessageBox с результатом.
///     Узнать успех по коду возврата нельзя — признаём это в UI.
///   - он принимает путь к файлу аргументом, поэтому мы передаём АБСОЛЮТНЫЙ путь:
///     сам апстрим читает относительный "settings.json" от текущего каталога
///     процесса, и при запуске из проводника или из автообновления тот каталог
///     был бы другим.
/// </summary>
internal static class RawAccelApplier
{
    public const string WriterExeName = "writer.exe";

    /// <summary>
    /// Запускает writer.exe с абсолютным путём к settings.json.
    /// Возвращает false, если writer.exe не найден или не запустился.
    /// </summary>
    public static bool Apply(string installDir, string settingsPath, out string error)
    {
        error = string.Empty;

        string writer = Path.Combine(installDir, WriterExeName);
        if (!File.Exists(writer))
        {
            error = $"Не найден {WriterExeName}. Запустите установку Raw Accel заново.";
            return false;
        }

        if (!File.Exists(settingsPath))
        {
            error = "Файл settings.json не найден — нечего применять.";
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = writer,
                // Абсолютный путь: writer.exe читает аргумент как путь к файлу
                // (writer/Program.cs: DriverConfig.Convert(File.ReadAllText(args[0]))).
                Arguments = "\"" + settingsPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = installDir,
            };

            var p = Process.Start(psi);
            if (p == null)
            {
                error = "Не удалось запустить writer.exe.";
                return false;
            }

            App.Log($"RawAccel: writer.exe started for {settingsPath}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"Не удалось запустить {WriterExeName}: {ex.Message}";
            App.Log($"RawAccel: writer.exe start failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}