using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace ImpactProConfig.Services;

/// <summary>
/// Отправка стандартных toast-уведомлений Windows для unpackaged-приложения:
///  1) один раз регистрируем свой AppUserModelID в HKCU (без права админа) —
///     без него ToastNotificationManager для приложения без упаковки не создаёт notifier;
///  2) строим ToastGeneric-XML (заголовок + текст) и показываем.
/// Все ошибки глотаются — уведомление не критично для работы приложения.
/// </summary>
internal static class ToastHelper
{
    private const string Aumid = "ArdorGaming.ImpactProConfig";
    private const string AppDisplayName = "ARDOR GAMING Impact PRO";

    private static bool _aumidReady;

    public static void Show(string title, string message)
    {
        try
        {
            EnsureAumid();

            string xml =
                "<toast>" +
                "<visual><binding template=\"ToastGeneric\">" +
                $"<text>{Escape(title)}</text>" +
                $"<text>{Escape(message)}</text>" +
                "</binding></visual>" +
                "</toast>";

            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var toast = new ToastNotification(doc)
            {
                // Одинаковый Tag — повторный показ заменяет старый тост (антистек).
                Tag = "impactpro",
                Group = "impactpro",
                ExpirationTime = DateTimeOffset.Now.AddMinutes(10),
            };
            ToastNotificationManager.CreateToastNotifier(Aumid).Show(toast);
        }
        catch
        {
            // Тост не показался — работа приложения не должна страдать.
        }
    }

    /// <summary>Регистрация AppUserModelID: HKCU\Software\Classes\AppUserModelId\{Aumid}.</summary>
    private static void EnsureAumid()
    {
        if (_aumidReady)
            return;
        using var key = Registry.CurrentUser.CreateSubKey(
            $@"Software\Classes\AppUserModelId\{Aumid}");
        key.SetValue("DisplayName", AppDisplayName);
        _aumidReady = true;
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
