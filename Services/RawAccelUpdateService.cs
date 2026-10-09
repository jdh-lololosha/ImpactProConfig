using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace ImpactProConfig.Services;

/// <summary>Информация о последнем релизе Raw Accel на GitHub.</summary>
internal sealed class RawAccelReleaseInfo
{
    public string TagName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;   // TagName без ведущей 'v'
    public string AssetName { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public long AssetSize { get; init; }
}

/// <summary>
/// Тег релиза, который мы фактически установили и записали на диск.
///
/// Хранится рядом с распакованным Raw Accel, а не в реестре: апстрим своей
/// метки не ведёт. Пока файла нет, установленную версию определить нельзя,
/// и обновление не предлагается — иначе плашка «доступно обновление 1.7.1»
/// висела бы вечно у пользователя с самой свежей сборкой.
/// </summary>
internal static class RawAccelVersionStamp
{
    private const string FileName = ".rawaccel-version";

    public static string PathFor(string installDir) =>
        System.IO.Path.Combine(installDir, FileName);

    /// <summary>Записывает установленный тег. Вызывается только после успешной установки.</summary>
    public static void Write(string installDir, string tag)
    {
        try
        {
            System.IO.File.WriteAllText(PathFor(installDir), tag ?? string.Empty);
        }
        catch (Exception ex)
        {
            App.Log($"RawAccel: version stamp write failed: {ex.GetType().Name}");
        }
    }

    /// <summary>Читает установленный тег. Пусто — неизвестно.</summary>
    public static string Read(string installDir)
    {
        try
        {
            string p = PathFor(installDir);
            return System.IO.File.Exists(p)
                ? System.IO.File.ReadAllText(p).Trim()
                : string.Empty;
        }
        catch (Exception ex)
        {
            App.Log($"RawAccel: version stamp read failed: {ex.GetType().Name}");
            return string.Empty;
        }
    }

    /// <summary>
    /// Есть ли смысл предлагать обновление.
    ///
    /// Нет, если метка неизвестна: мы не знаем, что у пользователя стоит, а
    /// FileVersion для сравнения с тегом непригоден (см. комментарий в
    /// RawAccelService.ReadInstalledVersion). Лучше не показать плашку вовсе,
    /// чем показать её всегда.
    /// </summary>
    public static bool ShouldOfferUpdate(string latestTag, string installedTag)
        => RawAccelUpdateService.TryParseVersion(latestTag, out var l)
        && !string.IsNullOrWhiteSpace(installedTag)
        && RawAccelUpdateService.TryParseVersion(installedTag, out var i)
        && l > i;
}

/// <summary>
/// Проверка обновлений Raw Accel по официальному GitHub API.
///
/// ПОЧЕМУ ИМЕННО ТАК: апстрим требует, чтобы в драйвер грузились только
/// оригинальные бинарники — драйвер rawaccel.sys подписан Microsoft
/// (WHCP/attestation), и любая модификация .sys или EXE ломает подпись, а
/// подписанный уязвимый драйвер — это то, из-за чего Windows помечает его
/// небезопасным. Поэтому здесь нет ни одной операции, которая пишет в файлы
/// релиза: только скачивание официального архива как есть и запуск
/// официального installer.exe.
///
/// Репозиторий a1xd/rawaccel перенаправляет на организацию
/// RawAccelOfficial/rawaccel; используем конечный адрес, чтобы не зависеть от
/// редиректа.
/// </summary>
internal static class RawAccelUpdateService
{
    /// <summary>Официальный репозиторий (a1xd/rawaccel редиректит сюда).</summary>
    public const string Repo = "RawAccelOfficial/rawaccel";

    private const string LatestReleaseApi =
        "https://api.github.com/repos/" + Repo + "/releases/latest";

    /// <summary>Имя аксета с бинарниками: RawAccel_vX.Y.Z.zip.</summary>
    private const string AssetPattern = "RawAccel_v";

    /// <summary>
    /// Разбирает "v1.7.1" в (1, 7, 1). Возвращает false на мусоре и на
    /// пре-релизах вида "v1.8.0-beta": их не предлагаем, там может быть
    /// неподписанная сборка.
    /// </summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        // Пре-релиз / сборка с суффиксом — не предлагаем.
        if (!System.Text.RegularExpressions.Regex.IsMatch(s, @"^\d+(\.\d+){1,3}$"))
            return false;
        return Version.TryParse(s, out version!);
    }

    /// <summary>
    /// Есть ли апдейт относительно установленной версии.
    ///
    /// ОСТОРОЖНО, метод ненадёжен для сравнения тега GitHub с FileVersion
    /// бинарников: апстрим патчит тег, не меняя версию в файлах (релиз v1.7.1
    /// несёт 1.7.0). Для продакшн-проверки используй
    /// <see cref="RawAccelVersionStamp.ShouldOfferUpdate"/>.
    /// </summary>
    public static bool IsNewer(string? latest, string? installed)
    {
        if (!TryParseVersion(latest, out var l)) return false;
        if (!TryParseVersion(installed, out var i)) return true; // не установлен — апдейт нужен
        return l > i;
    }

    /// <summary>
    /// Забирает последний релиз. Бросает исключение с текстом — вызывающий
    /// показывает его пользователю, молча проглатывать сетевые ошибки нельзя.
    /// </summary>
    public static async Task<RawAccelReleaseInfo> FetchLatestAsync(
        CancellationToken ct = default)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ImpactProConfig-rawaccel-check");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using var resp = await client
            .GetAsync(LatestReleaseApi, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        using var doc = await JsonDocument
            .ParseAsync(await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                        cancellationToken: ct)
            .ConfigureAwait(false);

        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? string.Empty;

        // Релиз без бинарников нам не нужен: ставить нечего.
        if (!root.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                "В релизе Raw Accel нет вложений с бинарниками.");

        foreach (var a in assets.EnumerateArray())
        {
            string name = a.GetProperty("name").GetString() ?? string.Empty;
            if (!name.StartsWith(AssetPattern, StringComparison.OrdinalIgnoreCase)
                || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                continue;

            return new RawAccelReleaseInfo
            {
                TagName = tag,
                Version = tag.TrimStart('v', 'V'),
                AssetName = name,
                DownloadUrl = a.GetProperty("browser_download_url").GetString() ?? string.Empty,
                AssetSize = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
            };
        }

        throw new InvalidOperationException(
            $"В релизе {tag} нет архива {AssetPattern}*.zip.");
    }

    /// <summary>
    /// Скачивает официальный архив во временный файл и распаковывает его в
    /// installDir. Байты файлов не изменяются: ZipFile.ExtractToDirectory
    /// копирует как есть, поэтому подпись rawaccel.sys остаётся валидной.
    ///
    /// Архив сам по себе содержит единственную папку "RawAccel/", поэтому
    /// извлекаем во временную папку и переносим её содержимое в installDir —
    /// иначе получится installDir\RawAccel\rawaccel.exe и пути в настройках
    /// разъедутся.
    /// </summary>
    public static async Task<string> DownloadAndExtractAsync(
        RawAccelReleaseInfo release,
        string installDir,
        IProgress<(long Done, long Total)>? progress = null,
        CancellationToken ct = default)
    {
        string tmpDir = Path.Combine(Path.GetTempPath(),
            "ImpactProConfig.RawAccel." + Guid.NewGuid().ToString("N"));
        string zipPath = Path.Combine(tmpDir, release.AssetName);
        string extractDir = Path.Combine(tmpDir, "extract");

        Directory.CreateDirectory(tmpDir);
        try
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ImpactProConfig-rawaccel-install");
                using var resp = await client
                    .GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                long total = release.AssetSize > 0
                    ? release.AssetSize
                    : resp.Content.Headers.ContentLength ?? 0;

                await using var src = await resp.Content
                    .ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(
                    zipPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    81920, useAsync: true);

                await CopyWithProgressAsync(src, dst, total, progress, ct).ConfigureAwait(false);
            }

            ZipFile.ExtractToDirectory(zipPath, extractDir);

            // В архиве единственная папка RawAccel/ — поднимаем её уровнем выше.
            string source = Directory.Exists(Path.Combine(extractDir, "RawAccel"))
                ? Path.Combine(extractDir, "RawAccel")
                : extractDir;

            // Проверяем подпись ДО переноса в installDir. Подписанный
            // уязвимый драйвер Windows помечает небезопасным, а неподписанный
            // не загрузится вообще; в обоих случаях пользователь получил бы
            // невнятную ошибку от установщика. Отказываемся сразу и понятно.
            VerifyDriverSignature(source);

            if (Directory.Exists(installDir))
                Directory.Delete(installDir, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(installDir)!);
            Directory.Move(source, installDir);

            App.Log($"RawAccel: unpacked {release.AssetName} -> {installDir}");
            return installDir;
        }
        finally
        {
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true); }
            catch (Exception ex) { App.Log($"RawAccel: temp cleanup failed: {ex.GetType().Name}"); }
        }
    }

    /// <summary>
    /// Проверяет Authenticode-подпись драйвера в распакованном дереве.
    /// Требование жёсткое: файлы не модифицируются, поэтому подпись должна
    /// быть валидной. Невалидная — отказ, а не предупреждение.
    /// </summary>
    private static void VerifyDriverSignature(string root)
    {
        string sys = Path.Combine(root, "driver", "rawaccel.sys");
        if (!File.Exists(sys))
            throw new InvalidOperationException(
                "В архиве нет driver\\rawaccel.sys — это не официальный релиз Raw Accel.");

        var status = WinTrust.VerifyDriver(sys);

        if (status != VerifyState.Valid)
        {
            App.Log($"RawAccel: driver signature rejected: {status}");
            throw new InvalidOperationException(
                $"Подпись rawaccel.sys недействительна ({status}). " +
                "Установка отменена: драйвер не будет загружен.");
        }

        App.Log("RawAccel: rawaccel.sys signature VALID (Microsoft WHCP)");
    }

    private static async Task CopyWithProgressAsync(
        Stream source, Stream dest, long total,
        IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dest.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            progress?.Report((done, total));
        }
        await dest.FlushAsync(ct).ConfigureAwait(false);
    }
}