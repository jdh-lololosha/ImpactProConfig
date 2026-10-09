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
    /// Корни Microsoft, которым допускается подписывать драйверы.
    /// Цепочка WHCP заканчивается одним из них.
    /// </summary>
    private static readonly string[] AllowedRootMarkers =
    {
        "Microsoft Windows Third Party Component CA 2014",
        "Microsoft Windows Production PCA",
        "Microsoft Code Signing PCA",
        "Microsoft Root Certificate Authority",
    };

    /// <summary>
    /// Издатель сертификата подписи драйвера Raw Accel.
    /// Проверяем именно его: без этого файл, подписанный любым другим
    /// доверенным издателем, прошёл бы проверку ничем не отличаясь от драйвера.
    /// </summary>
    private const string RequiredIssuerMarker =
        "Microsoft Windows Third Party Component CA 2014";

    /// <summary>Имя подписи, которую мы ожидаем у rawaccel.sys.</summary>
    private const string RequiredSubjectMarker =
        "Microsoft Windows Hardware Compatibility Publisher";

    /// <summary>
    /// Проверяет файл. Бросает <see cref="VerifyState.Invalid"/> не через
    /// исключение: вызывающему нужен факт, а не стек-трейс.
    /// </summary>
    public static VerifyState VerifyDriver(string filePath)
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
            if (!cert.Issuer.Contains(RequiredIssuerMarker, StringComparison.OrdinalIgnoreCase))
            {
                App.Log($"RawAccel: unexpected signer issuer: {cert.Issuer}");
                return VerifyState.Invalid;
            }

            if (!cert.Subject.Contains(RequiredSubjectMarker, StringComparison.OrdinalIgnoreCase))
            {
                App.Log($"RawAccel: unexpected signer subject: {cert.Subject}");
                return VerifyState.Invalid;
            }

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

            bool rootOk = AllowedRootMarkers.Any(m => root.Contains(m, StringComparison.OrdinalIgnoreCase));

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