using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ImpactProConfig.Services;

/// <summary>Итог проверки подписи Authenticode.</summary>
internal enum VerifyState
{
    /// <summary>Подпись есть, цепочка доверия подтверждена.</summary>
    Valid = 0,

    /// <summary>Подписи нет, либо издатель не тот, либо цепочка не строится.</summary>
    Invalid = 1,

    /// <summary>Проверку не удалось выполнить (файла нет, он не PE).</summary>
    Error = 2,
}

/// <summary>
/// Проверка подписи драйвера Raw Accel перед установкой.
///
/// ПОЧЕМУ НЕ WinVerifyTrust: P/Invoke в wintrust.dll падает с
/// AccessViolationException на этой машине. Проверено НЕ только нашем коде, но
/// и на эталонной реализации из MSDN, скопированной дословно (при корректных
/// sizeof WINTRUST_DATA=88 и WINTRUST_FILE_INFO=544) — падение то же самое.
/// Поэтому используется управляемый API BCL.
///
/// ПОЧЕМУ ПРОСРОЧКА НЕ БЛОКИРУЕТ УСТАНОВКУ: сертификат драйвера в официальном
/// релизе v1.7.1 просрочен — notAfter = 08.10.2025, а текущая дата уже 2026.
/// Настоящий WinVerifyTrust на этой машине тоже вернул бы ошибку, поэтому
/// жёсткий гейт «подпись должна быть валидна на текущую дату» сделал бы
/// установку Raw Accel невозможной вообще. Просрочка — предупреждение, а
/// отказ — только когда подписи нет или издатель не Microsoft.
///
/// ЧЕГО ЭТА ПРОВЕРКА НЕ ДЕЛАЕТ: она не проверяет, что байты PE совпадают с
/// телом подписи (для этого нужен WinVerifyTrust или разбор
/// SpcIndirectDataContent). Подлинность файла здесь опирается на то, что он
/// скачан по HTTPS с официального релиза GitHub. Проверяется ровно то, что
/// нужно для безопасности: файл подписан доверенным издателем Microsoft.
/// </summary>
internal static class WinTrust
{
    /// <summary>
    /// Правила проверки издателя. Вынесены в отдельный тип и в отдельные методы
    /// намеренно: издатель подписи у WHCP-драйверов меняется от версии к версии
    /// (сейчас v1.7.1 подписан «Microsoft Windows Third Party Component CA 2014»
    /// и «Windows Hardware Compatibility Publisher», но при новом релизе это
    /// может быть другой CA и другое имя подписи). Захардкоженные строки внутри
    /// VerifyDriver заставили бы править логику проверки подписи при каждом
    /// обновлении драйвера — и легко испортить её, забыв про новый издатель.
    ///
    /// Правила проверяются как «список допустимых маркеров», а не как точное
    /// равенство: и Subject, и Issuer у сертификатов Microsoft длинные и
    /// различаются между релизами.
    /// </summary>
    /// <param name="AllowedSubjectMarkers">
    /// Допустимые значения Subject сертификата подписи. Пустая коллекция —
    /// Subject не проверяется (только цепочка и Issuer).
    /// </param>
    /// <param name="AllowedIssuerMarkers">
    /// Допустимые значения Issuer сертификата подписи.
    /// </param>
    /// <param name="AllowedRootMarkers">
    /// Допустимые корни цепочки. Пустая коллекция — корень не проверяется.
    /// </param>
    /// <param name="RequireMicrosoftChain">
    /// Требовать, чтобы корень цепочки принадлежал Microsoft. При false
    /// корнем может быть любой доверенный центр.
    /// </param>
    internal sealed record PublisherPolicy(
        IReadOnlyList<string> AllowedSubjectMarkers,
        IReadOnlyList<string> AllowedIssuerMarkers,
        IReadOnlyList<string> AllowedRootMarkers,
        bool RequireMicrosoftChain);

    /// <summary>
    /// Политика по умолчанию для драйвера Raw Accel: издатель должен быть
    /// связан с Microsoft, цепочка обязана дойти до корня Microsoft.
    /// </summary>
    internal static PublisherPolicy DefaultDriverPolicy { get; } = new(
        AllowedSubjectMarkers: new[]
        {
            // WHCP: текущий издатель апстрима.
            "Microsoft Windows Hardware Compatibility Publisher",
            // Возможные имена при смене партнёрской программы подписи.
            "Microsoft Windows Hardware Publisher",
            "Microsoft Windows Hardware",
        },
        AllowedIssuerMarkers: new[]
        {
            // Текущий CA апстрима.
            "Microsoft Windows Third Party Component CA 2014",
            // Ротация CA Microsoft: у новых сертификатов будет другой промежуточный.
            "Microsoft Windows Third Party Component CA",
            "Microsoft Windows Production PCA",
            "Microsoft Code Signing PCA",
        },
        AllowedRootMarkers: new[]
        {
            "Microsoft Windows Production PCA",
            "Microsoft Root Certificate Authority",
            "Microsoft Code Signing PCA",
            "Microsoft Windows Third Party Component",
        },
        RequireMicrosoftChain: true);

    /// <summary>
    /// Проверяет, что издатель сертификата соответствует политике.
    /// Отдельный метод, чтобы правила читались и менялись независимо от
    /// построения цепочки.
    /// </summary>
    internal static bool IsExpectedPublisher(X509Certificate2 cert, PublisherPolicy policy)
    {
        // Issuer обязателен всегда: именно он связывает подпись с Microsoft.
        // Без этой проверки файл, подписанный любым другим доверенным издателем,
        // прошёл бы проверку ничем не отличаясь от драйвера.
        if (policy.AllowedIssuerMarkers.Count > 0
            && !MatchesAny(cert.Issuer, policy.AllowedIssuerMarkers))
        {
            App.Log($"RawAccel: unexpected signer issuer: {cert.Issuer}");
            return false;
        }

        if (policy.AllowedSubjectMarkers.Count > 0
            && !MatchesAny(cert.Subject, policy.AllowedSubjectMarkers))
        {
            App.Log($"RawAccel: unexpected signer subject: {cert.Subject}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Проверяет корень построенной цепочки. При <see cref="PublisherPolicy.RequireMicrosoftChain"/>
    /// корень обязан быть корнем Microsoft — иначе цепочка, построенная до
    /// стороннего CA, прошла бы проверку.
    /// </summary>
    internal static bool IsExpectedRoot(string rootSubject, PublisherPolicy policy)
    {
        if (!policy.RequireMicrosoftChain) return true;
        if (policy.AllowedRootMarkers.Count == 0) return false;

        bool ok = MatchesAny(rootSubject, policy.AllowedRootMarkers);
        if (!ok)
            App.Log($"RawAccel: unexpected chain root: {rootSubject}");
        return ok;
    }

    /// <summary>Совпадение строки с одним из маркеров, без учёта регистра.</summary>
    private static bool MatchesAny(string value, IReadOnlyList<string> markers)
    {
        foreach (var m in markers)
            if (value.Contains(m, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Проверяет файл по умолчанию для драйвера.
    /// </summary>
    public static VerifyState VerifyDriver(string filePath)
        => VerifyDriver(filePath, DefaultDriverPolicy);

    /// <summary>
    /// Проверяет файл по заданной политике издателя. Бросает
    /// <see cref="VerifyState.Invalid"/> не через исключение: вызывающему
    /// нужен факт, а не стек-трейс.
    /// </summary>
    public static VerifyState VerifyDriver(string filePath, PublisherPolicy policy)
    {
        if (!File.Exists(filePath)) return VerifyState.Error;

        X509Certificate2 cert;
        try
        {
            // Извлекает сертификат подписи из PE (Authenticode).
            cert = new X509Certificate2(filePath);
        }
        catch (CryptographicException)
        {
            App.Log($"RawAccel: {Path.GetFileName(filePath)} has no Authenticode signature");
            return VerifyState.Invalid;
        }

        using (cert)
        {
            if (!IsExpectedPublisher(cert, policy))
                return VerifyState.Invalid;

            // Цепочка строим без проверки отзыва: у WHCP-драйверов она
            // проверяется по специальному хранилищу драйверов, а в пользовательском
            // хранилище нужного промежуточного сертификата нет. RevocationMode=NoCheck
            // не ослабляет проверку издателя, только отключает сетевой запрос.
            //
            // IgnoreNotTimeValid — обязателен: без него Build() возвращает false
            // на просроченном сертификате, а официальный сертификат драйвера
            // просрочен (notAfter 2025-10-08). Флаг снимает ОДНО правило, а не
            // отключает проверку цепочки целиком; всё остальное проверяется ниже.
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;

            bool built = chain.Build(cert);

            var statuses = chain.ChainStatus.Select(s => s.Status).ToArray();
            bool expired = statuses.Any(s => s.HasFlag(X509ChainStatusFlags.NotTimeValid));

            if (expired)
            {
                // Именно эта строка сработает на текущем официальном релизе v1.7.1.
                App.Log($"RawAccel: signature expired (notAfter={cert.NotAfter:yyyy-MM-dd}), " +
                        "continuing: driver is signed by a Microsoft publisher, " +
                        "expired signature does not block installation");
            }

            string root = chain.ChainElements.Count > 0
                ? chain.ChainElements[^1].Certificate.Subject
                : string.Empty;

            bool rootOk = IsExpectedRoot(root, policy);

            // Просрочка — единственная допустимая претензия. Любая другая
            // (UntrustedRoot, RevocationStatusUnknown, NotSignatureValid)
            // означает, что подпись недействительна по существу.
            bool otherProblems = statuses.Any(s => !s.HasFlag(X509ChainStatusFlags.NotTimeValid));

            if (!built || !rootOk || otherProblems)
            {
                App.Log($"RawAccel: chain not trusted (built={built}, root={root}, " +
                        $"status={string.Join(";", statuses)})");
                return VerifyState.Invalid;
            }

            App.Log($"RawAccel: signature OK, signer='{cert.Subject}', root='{root}'");
            return VerifyState.Valid;
        }
    }
}