using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace ImpactProConfig.Services;

/// <summary>Результат фоновой проверки обновлений.</summary>
/// <param name="Available">true — нашёлся релиз новее текущей версии.</param>
/// <param name="Tag">Тег релиза, напр. «v1.2.0».</param>
/// <param name="Url">Прямая ссылка на .zip — если в релизе есть вложение нужного типа.</param>
/// <param name="Error">Текст ошибки, если проверка не удалась (показываем только в логе).</param>
public sealed record UpdateInfo(bool Available, string? Tag = null, string? Url = null, string? Error = null);

/// <summary>
/// Проверка обновлений через публичный GitHub Releases API (без токена).
///
/// Почему так: у приложения нет своего сервера, а официальный репозиторий один.
/// API отдаёт <c>releases/latest</c> — самый свежий релиз, помеченный не-превью.
/// Запрос идёт в фоне и НИКОГДА не блокирует UI и не пишет во флеш мыши.
///
/// Релиз портативный: ассет — .zip (MSI отменён, не нужен ни UAC, ни Program Files).
/// </summary>
internal sealed class UpdateService
{
    private const string ApiUrl =
        "https://api.github.com/repos/jdh-lololosha/ImpactProConfig/releases/latest";

    /// <summary>Кэш последнего ответа, чтобы InfoBar не мигал при повторных проверках.</summary>
    private UpdateInfo? _last;

    /// <summary>Текущая версия приложения из сборки.</summary>
    public static Version CurrentVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
        ?? new Version(0, 0, 0, 0);

    /// <summary>
    /// Проверить релиз. Вызывать из фона. Возвращает null, если обновлений нет.
    /// Ошибка сети не считается «есть обновление» — плашку не показываем.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ImpactProConfig");

            using var response = await client.GetAsync(ApiUrl, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagElement))
                return null;

            string tag = tagElement.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(tag))
                return null;

            Version? latest = ParseVersion(tag);
            if (latest is null || latest <= StripBuild(CurrentVersion))
            {
                _last = new UpdateInfo(false, tag);
                return null;
            }

            string? zip = FindZipAsset(root);

            _last = new UpdateInfo(true, tag, zip);
            return _last;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            // Нет сети — не ошибка для пользователя: молча ничего не показываем,
            // текст оставляем в crash.log.
            App.Log($"UpdateCheck: {ex.Message}");
            _last = new UpdateInfo(false, Error: ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Портативное самообновление (классическая схема с отдельным процессом):
    ///  1. качаем .zip новой версии во временную папку (%TEMP%);
    ///  2. запускаем Updater.exe с аргументами --zip / --target / --pid;
    ///  3. выходим из приложения — апдейтер дождётся выхода, распакует архив
    ///     поверх папки приложения, удалит zip и перезапустит exe.
    ///
    /// Запущенный Windows exe нельзя перезаписать — в этом весь смысл
    /// отдельного процесса. Копия апдейтера кладётся в ту же временную папку:
    /// Updater.exe в папке приложения сам является обновляемым файлом.
    /// Прав админа не нужно — программа живёт в своей папке.
    /// </summary>
    public static async Task DownloadAndUpdateAsync(string zipUrl)
    {
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"ImpactProConfig-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string zipPath = Path.Combine(tempDir, "update.zip");

        CleanupOldUpdateDirs(tempDir);

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ImpactProConfig");

        await using (var source = await client.GetStreamAsync(zipUrl))
        await using (var dest = File.Create(zipPath))
        {
            await source.CopyToAsync(dest);
        }

        string appDir = AppContext.BaseDirectory.TrimEnd('\\');
        string updaterSrc = Path.Combine(appDir, "Updater.exe");
        if (!File.Exists(updaterSrc))
            throw new FileNotFoundException(
                "Updater.exe отсутствует рядом с приложением — автообновление невозможно.");

        // Копия апдейтера во временную папку: в папке приложения свой же
        // Updater.exe запущен и перезаписать его нельзя (те же блокировки exe).
        string updaterCopy = Path.Combine(tempDir, "Updater.exe");
        File.Copy(updaterSrc, updaterCopy, overwrite: true);

        int pid = Environment.ProcessId;
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = updaterCopy,
            Arguments = $"--zip \"{zipPath}\" --target \"{appDir}\" --pid {pid}",
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        App.Log($"Update: zip скачан, Updater.exe запущен (pid={pid}), выход приложения");
    }

    /// <summary>
    /// Подметаём папки прошлых обновлений в %TEMP% (включая старую
    /// powershell-схему). Залоченные недавним обновлением — молча пропускаем.
    /// </summary>
    private static void CleanupOldUpdateDirs(string keepDir)
    {
        try
        {
            string temp = Path.GetTempPath();
            foreach (var dir in Directory.GetDirectories(temp, "ImpactProConfig-update-*"))
            {
                if (string.Equals(dir.TrimEnd('\\'), keepDir.TrimEnd('\\'),
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                try { Directory.Delete(dir, true); } catch { /* залочено — не критично */ }
            }
        }
        catch
        {
            // Чистка не критична.
        }
    }

    /// <summary>Версия из тега вида «v1.2.0» или «1.2.0-beta».</summary>
    private static Version? ParseVersion(string tag)
    {
        string cleaned = tag.TrimStart('v', 'V');
        int dash = cleaned.IndexOf('-');
        if (dash > 0)
            cleaned = cleaned[..dash];
        return Version.TryParse(cleaned, out var v) ? v : null;
    }

    /// <summary>Собрать версию 1.2.3 из 1.2.3.0 — иначе 1.2.3 < 1.2.3.0 ложно.</summary>
    private static Version StripBuild(Version v) =>
        new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    /// <summary>Найти .zip среди вложений релиза (портативная сборка).</summary>
    private static string? FindZipAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameElement) ||
                !asset.TryGetProperty("browser_download_url", out var urlElement))
            {
                continue;
            }

            string name = nameElement.GetString() ?? string.Empty;
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return urlElement.GetString();
        }

        return null;
    }
}
