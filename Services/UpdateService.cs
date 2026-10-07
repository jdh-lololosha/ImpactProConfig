using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace ImpactProConfig.Services;

/// <summary>Результат фоновой проверки обновлений.</summary>
/// <param name="Available">true — нашёлся релиз новее текущей версии.</param>
/// <param name="Tag">Тег релиза, напр. «v1.2.0».</param>
/// <param name="Url">Прямая ссылка на .msi — если в релизе есть вложение нужного типа.</param>
/// <param name="Error">Текст ошибки, если проверка не удалась (показываем только в логе).</param>
public sealed record UpdateInfo(bool Available, string? Tag = null, string? Url = null, string? Error = null);

/// <summary>
/// Проверка обновлений через публичный GitHub Releases API (без токена).
///
/// Почему так: у приложения нет своего сервера, а официальный репозиторий один.
/// API отдаёт <c>releases/latest</c> — самый свежий релиз, помеченный не-превью.
/// Запрос идёт в фоне и НИКОГДА не блокирует UI и не пишет во флеш мыши.
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

            string? msi = FindMsiAsset(root);

            _last = new UpdateInfo(true, tag, msi);
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
    /// Скачать .msi и запустить установку. Вызывать из фона: и загрузка, и
    /// запуск — на UI-потоке тормозят окно.
    /// </summary>
    public static async Task DownloadAndInstallAsync(string msiUrl)
    {
        string target = Path.Combine(Path.GetTempPath(),
            $"ImpactProConfig-{Guid.NewGuid():N}.msi");

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ImpactProConfig");

        await using var source = await client.GetStreamAsync(msiUrl);
        await using var dest = File.Create(target);
        await source.CopyToAsync(dest);

        // Запуск установщика: пользователь увидит UAC и подтвердит.
        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true,
        });
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

    /// <summary>Найти .msi среди вложений релиза.</summary>
    private static string? FindMsiAsset(JsonElement release)
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
            if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                return urlElement.GetString();
        }

        return null;
    }
}