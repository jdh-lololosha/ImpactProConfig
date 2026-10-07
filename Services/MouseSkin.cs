using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImpactProConfig.Services;

/// <summary>
/// Выбор образа корпуса мыши.
///
/// Механизм взят из официалки, а не придуман: Config.ini содержит
/// <c>DeviceTotal=3</c> и секции <c>[Device1] MID=4</c>, <c>[Device2] MID=5</c>,
/// <c>[Device3] MID=6</c>. FormHomePage при подключении читает MID мыши
/// (команда 16, <c>CS_UsbServer_ReadCidMid</c>), ищет секцию с таким MID и
/// берёт картинку <c>dev(j+1)</c>, где j — индекс секции с нуля.
/// То есть dev1/dev2/dev3 — это НЕ цвета, а слоты MID 4/5/6 одной модели
/// Impact PRO 16. Совпадение с чёрным/белым/розовым корпусом — побочный факт
/// конкретной прошивки, а не правило протокола.
///
/// Источник: docs/decompiled/.../FormHomePage.cs:306-312 и :858-878.
/// </summary>
internal static class MouseSkin
{
    public const int AutoIndex = 0;

    /// <summary>CID продукта из Config.ini (<c>Impact PRO16</c> → 16).</summary>
    public const byte ExpectedCid = 16;

    public static readonly string[] Items =
    [
        "Авто (по MID)",
        "Чёрный",
        "Белый",
        "Розовый",
    ];

    /// <summary>Номер файла-образа для каждого пункта списка. У «Авто» (0) своего файла
    /// нет — он вычисляется по MID, поэтому 0 здесь не используется.</summary>
    private static readonly int[] ImageByIndex = { 3, 1, 2, 3 };

    /// <summary>
    /// Индекс образа по MID: MID 4 → dev1, 5 → dev2, 6 → dev3.
    /// Возвращает -1, если MID вне известного диапазона (тогда App.Log пишет
    /// «MID неизвестен» и UI остаётся на текущем выборе).
    /// </summary>
    public static int IndexFromMid(byte mid) => mid switch
    {
        4 => 1,
        5 => 2,
        6 => 3,
        _ => -1,
    };

    private static readonly ConcurrentDictionary<int, ImageSource> Cache = new();

    /// <summary>Короткое имя выбранного варианта для UI.</summary>
    public static string NameOf(int index) =>
        index >= 0 && index < Items.Length ? Items[index] : Items[AutoIndex];

    /// <summary>
    /// Загрузить bitmap корпуса (dev1/dev2/dev3) как ImageSource для XAML.
    /// Кэшируем по номеру файла — иначе каждая смена темы читает файл заново.
    /// </summary>
    public static ImageSource Load(int index)
    {
        int slot = ImageByIndex[Math.Clamp(index, 0, ImageByIndex.Length - 1)];
        return Cache.GetOrAdd(slot, static s =>
        {
            var uri = new Uri($"pack://application:,,,/Assets/dev{s}.png", UriKind.Absolute);
            var bitmap = new BitmapImage(uri);
            bitmap.Freeze();
            return bitmap;
        });
    }
}