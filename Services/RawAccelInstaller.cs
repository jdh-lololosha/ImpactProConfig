using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace ImpactProConfig.Services;

/// <summary>
/// Запуск официального установщика драйвера Raw Accel с повышенными правами.
///
/// ГРАНИЦА ОТВЕТСТВЕННОСТИ: приложение только запускает официальный
/// installer.exe и ничего не делает с драйвером само — ни не регистрирует
/// сервис, ни не пишет UpperFilters, ни не кладёт .sys в system32\drivers.
/// Всё это делает installer (installer/installer.cpp: CreateServiceW с
/// SERVICE_KERNEL_DRIVER + SetupDiSetClassRegistryPropertyW на класс мыши).
/// Так требование «строго оригинальные бинарники, чтобы не повредить подпись
/// драйвера» выполняется буквально: мы вообще не трогаем его содержимое.
///
/// ПОЧЕМУ ИМЕННО runas: installer.exe помечен в манифесте как
/// requireAdministrator (installer/install.manifest) и открывает
/// SC_MANAGER_ALL_ACCESS. Без повышения он не запустится вовсе, а наше
/// приложение намеренно остаётся без прав администратора.
/// </summary>
internal static class RawAccelInstaller
{
    public const string InstallerExeName = "installer.exe";

    /// <summary>
    /// Запускает installer.exe из installDir с повышенными правами.
    /// Возвращает false и заполняет error, если процесс не запустился.
    ///
    /// Про UAC на этой машине: EnableLUA=1, ConsentPromptBehaviorAdmin=0
    /// («повышать без запроса»), но учётная запись пользователя НЕ входит в
    /// группу Administrators. У неё нет токена для повышения вообще, поэтому
    /// UAC покажет запрос учётных данных администратора. Если таких учётных
    /// данных нет — установка драйвера невозможна ни нашим кодом, ни любым
    /// другим способом. Об этом честно сообщаем в error, а не молча падаем.
    /// </summary>
    public static bool Run(string installDir, out string error)
    {
        error = string.Empty;

        string installer = Path.Combine(installDir, InstallerExeName);
        if (!File.Exists(installer))
        {
            error = $"Не найден {InstallerExeName}. Сначала скачайте Raw Accel.";
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = installer,
                // runas — единственный способ получить права администратора.
                // UseShellExecute обязан быть true, иначе verb игнорируется.
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = installDir,
            };

            Process? p = Process.Start(psi);
            if (p == null)
            {
                error = "Не удалось запустить установщик Raw Accel.";
                return false;
            }

            App.Log($"RawAccel: installer.exe launched (elevated), pid={p.Id}");
            return true;
        }
        catch (Win32Exception ex)
        {
            // 1223 = ERROR_CANCELLED: пользователь отклонил запрос UAC.
            error = ex.NativeErrorCode == 1223
                ? "Запрос на повышение прав отменён."
                : $"Не удалось запустить установщик с правами администратора: {ex.Message}";
            App.Log($"RawAccel: installer start failed: {ex.NativeErrorCode} {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            error = $"Не удалось запустить установщик: {ex.Message}";
            App.Log($"RawAccel: installer start failed: {ex.GetType().Name}");
            return false;
        }
    }
}