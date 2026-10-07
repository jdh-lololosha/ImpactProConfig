using System.IO;
using System.Text.Json;

namespace ImpactProConfig.Services;

/// <summary>Локальные настройки приложения (только настройки ПК, ничего из флеша мыши).</summary>
internal sealed class LocalSettings
{
    /// <summary>Уведомлять о низком заряде батареи (&lt; 15%). По умолчанию включено.</summary>
    public bool LowBatteryAlertEnabled { get; set; } = true;

    /// <summary>Тост о низком заряде уже отправлен в текущем цикле разряда (анти-спам).</summary>
    public bool LowBatteryNotified { get; set; }

    /// <summary>Индекс монитора для OSD (порядок <see cref="Monitors.All"/>).</summary>
    public int OsdMonitorIndex { get; set; }

    /// <summary>Позиция OSD: 0 — сверху справа, 1 — сверху по центру, 2 — снизу справа, 3 — по центру.</summary>
    public int OsdPositionIndex { get; set; }

    /// <summary>Время показа OSD: индекс в {1, 2, 3, 5} сек (по умолчанию 2 сек).</summary>
    public int OsdDurationIndex { get; set; } = 1;

    /// <summary>Битовая маска слотов кнопок (бит = DeviceSlot 0..5), назначенных на «Показать статус мыши (OSD)».</summary>
    public int OsdSlotsMask { get; set; }

    /// <summary>Индекс акцентной темы в ThemeManager.Presets (по умолчанию Ardor Red).</summary>
    public int AccentIndex { get; set; }

    /// <summary>Индекс выбранного корпуса мыши в MouseSkin.Items (0 = Авто по MID).</summary>
    public int MouseSkinIndex { get; set; }
}

/// <summary>
/// Загрузка/сохранение <see cref="LocalSettings"/> в app_settings.json рядом с exe.
/// Файл локальный, пишется только при изменении настройки/флага — во флеш мыши не пишем.
/// </summary>
internal static class LocalSettingsStore
{
    // %LOCALAPPDATA%\ImpactProConfig, а не рядом с exe: приложение стоит в
    // Program Files, куда обычный пользователь писать не может.
    private static readonly string FilePath =
        Path.Combine(App.DataDir, "app_settings.json");

    private static readonly object Gate = new();

    public static LocalSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(FilePath))
                       ?? new LocalSettings();
        }
        catch
        {
            // Битый или недоступный файл — работаем с настройками по умолчанию.
        }
        return new LocalSettings();
    }

    public static void Save(LocalSettings settings)
    {
        try
        {
            lock (Gate)
            {
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(settings,
                        new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch
        {
            // Локальное сохранение не критично (папка может быть временно недоступна).
        }
    }
}
