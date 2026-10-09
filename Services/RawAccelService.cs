using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ImpactProConfig.Services;

/// <summary>Состояние драйвера Raw Accel в системе.</summary>
internal enum RawAccelState
{
    /// <summary>Драйвер не установлен: нет ни сервиса, ни устройства.</summary>
    NotInstalled = 0,

    /// <summary>Сервис зарегистрирован, но устройство не открывается (драйвер не загружен).</summary>
    ServiceOnly = 1,

    /// <summary>Драйвер работает: устройство \\.\rawaccel открывается.</summary>
    Active = 2,
}

/// <summary>
/// Обнаружение Raw Accel и путь его установочной папки.
///
/// Драйвер Raw Accel — kernel-mode. Его сервис всегда в
/// HKLM\SYSTEM\CurrentControlSet\Services\rawaccel (HKLM, не HKCU), поэтому
/// проверка возможна обычным пользователем — чтение этого раздела реестра прав
/// администратора не требует, в отличие от установки самого драйвера.
///
/// ВАЖНО: приложение ничего не пишет в HKLM и само драйвер не грузит.
/// Установку выполняет официальный installer.exe из upstream-архива,
/// запущенный с verb=runas; мы только запускаем процесс и проверяем результат.
/// </summary>
internal static class RawAccelService
{
    /// <summary>Путь к ветке сервиса драйвера в реестре.</summary>
    private const string ServiceKeyPath =
        @"SYSTEM\CurrentControlSet\Services\rawaccel";

    /// <summary>Символическая ссылка на устройство драйвера.</summary>
    private const string DevicePath = @"\\.\rawaccel";

    /// <summary>Файл конфигурации драйвера (апстрим пишет его по имени "settings.json").</summary>
    public const string SettingsFileName = "settings.json";

    /// <summary>
    /// Папка, куда мы распаковываем Raw Accel. Апстрим не пишет путь установки
    /// ни в реестр, ни куда-либо ещё, поэтому путь храним в app_settings.json.
    /// Ставим в %LOCALAPPDATA%\Programs рядом с нашей программой: у обычного
    /// пользователя нет прав писать в Program Files, а установщику драйвера
    /// права администратора нужны в любом случае.
    /// </summary>
    internal static string DefaultInstallDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "RawAccel");

    // GENERIC_READ|GENERIC_WRITE не запрашиваем: нужно только проверить, что
    // устройство существует и обслуживается драйвером, поэтому dwDesiredAccess = 0.
    private const uint NoAccess = 0;
    private const uint ShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    /// <summary>Текущее состояние драйвера. Ничего не меняет, только читает.</summary>
    public static RawAccelState QueryState()
    {
        bool service = ServiceKeyExists();
        bool device = DeviceOpenable();
        return device  ? RawAccelState.Active
             : service ? RawAccelState.ServiceOnly
             : RawAccelState.NotInstalled;
    }

    private static bool ServiceKeyExists()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ServiceKeyPath);
            return key != null;
        }
        catch (Exception ex)
        {
            App.Log($"RawAccel: registry read failed: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// Открывает \\.\rawaccel. Символический путь существует только когда драйвер
    /// загружен и обслуживает его; иначе CreateFile возвращает INVALID_HANDLE_VALUE.
    /// </summary>
    private static bool DeviceOpenable()
    {
        try
        {
            using var h = CreateFile(DevicePath, NoAccess, ShareReadWrite,
                                     IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            return !h.IsInvalid;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (Exception ex)
        {
            App.Log($"RawAccel: device probe failed: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// Версия из файловой версии установленного rawaccel.exe.
    /// Пустая строка — файл не найден или версию прочитать не удалось.
    /// </summary>
    public static string ReadInstalledVersion(string installDir)
    {
        try
        {
            string exe = Path.Combine(installDir, "rawaccel.exe");
            if (!File.Exists(exe)) return string.Empty;
            var vi = FileVersionInfo.GetVersionInfo(exe);
            return (vi.ProductVersion ?? vi.FileVersion ?? string.Empty).Trim();
        }
        catch (Exception ex)
        {
            App.Log($"RawAccel: version read failed: {ex.GetType().Name}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Путь к settings.json. Апстрим обращается к нему по относительному имени
    /// (grapher/Models/Serialized/SettingsManager.cs: File.WriteAllText(
    /// Constants.DefaultSettingsFileName, ...)), то есть в рабочем каталоге
    /// процесса — рядом с rawaccel.exe.
    /// </summary>
    public static string SettingsPath(string installDir) =>
        Path.Combine(installDir, SettingsFileName);

    /// <summary>Распакован ли официальный Raw Accel в указанную папку.</summary>
    public static bool IsUnpacked(string installDir)
    {
        try
        {
            return File.Exists(Path.Combine(installDir, "rawaccel.exe"))
                && File.Exists(Path.Combine(installDir, "installer.exe"));
        }
        catch (Exception ex)
        {
            App.Log($"RawAccel: unpack check failed: {ex.GetType().Name}");
            return false;
        }
    }
}