using System.Linq;
using System.Runtime.InteropServices;

namespace ImpactProConfig.Services;

/// <summary>
/// Физические параметры монитора — для позиционирования OSD.
/// X/Y/Width/Height — rcMonitor (границы экрана), Work* — rcWork (рабочая область
/// без панелей задач): оверлей размещается строго в ней.
/// </summary>
internal sealed record MonitorInfo(
    string Name, bool Primary,
    int X, int Y, int Width, int Height,
    int WorkX, int WorkY, int WorkWidth, int WorkHeight,
    double DpiX, double DpiY);

/// <summary>
/// Перечисление мониторов через EnumDisplayMonitors/GetMonitorInfo (без WinForms):
/// API видит ВСЕ экраны системы, включая виртуальные/драйверные. Имена честные —
/// «Монитор N (Основной) [ширинаxвысота]»; порядок: основной первый, далее слева
/// направо. DPI берём через Shcore!GetDpiForMonitor (Win8.1+); на старых — 96.
/// </summary>
internal static class Monitors
{
    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprc, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, uint dpiType, out uint dpiX, out uint dpiY);

    private const uint MonitorinfofPrimary = 1;
    private const uint MdtEffectiveDpi = 0;

    /// <summary>LOGPIXELSX для GetDeviceCaps.</summary>
    private const int LogPixelsX = 88;

    /// <summary>LOGPIXELSY для GetDeviceCaps.</summary>
    private const int LogPixelsY = 90;

    // SM_*VIRTUALSCREEN — аварийный путь, если EnumDisplayMonitors не сработал.
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    /// <summary>
    /// Есть ли Shcore.dll (Win8.1+). Проверяется один раз: DllNotFoundException на
    /// каждом мониторе ронял бы перечисление целиком.
    /// </summary>
    private static readonly bool ShcoreAvailable = ProbeShcore();

    private static bool ProbeShcore()
    {
        try
        {
            IntPtr h = NativeLibrary.TryLoad("Shcore.dll", out IntPtr mod) ? mod : IntPtr.Zero;
            if (h == IntPtr.Zero)
                return false;
            NativeLibrary.Free(h);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// DPI монитора. Основной путь — Shcore!GetDpiForMonitor (Win8.1+).
    /// Если Shcore недоступна (Win7/8.0) — GetDC + GetDeviceCaps(LOGPIXELSX/Y),
    /// а если и это не даёт значения — базовые 96. Монитор из списка при любом
    /// сбое НЕ выбрасывается: без него OSD некуда позиционировать вообще.
    /// </summary>
    private static (double X, double Y) GetDpi(IntPtr hMonitor, IntPtr hdc)
    {
        if (ShcoreAvailable)
        {
            try
            {
                if (GetDpiForMonitor(hMonitor, MdtEffectiveDpi, out uint dx, out uint dy) == 0
                    && dx > 0)
                    return (dx, dy);
            }
            catch
            {
                // Падаем на GDI-путь ниже.
            }
        }

        try
        {
            // hdc из EnumDisplayMonitors валиден для текущего монитора; если
            // он нулевой (редко) — берём DC всего экрана.
            IntPtr dc = hdc != IntPtr.Zero ? hdc : GetDC(IntPtr.Zero);
            bool ownDc = hdc == IntPtr.Zero;
            try
            {
                int x = GetDeviceCaps(dc, LogPixelsX);
                int y = GetDeviceCaps(dc, LogPixelsY);
                if (x > 0 && y > 0)
                    return (x, y);
            }
            finally
            {
                if (ownDc)
                    ReleaseDC(IntPtr.Zero, dc);
            }
        }
        catch
        {
            // GDI тоже недоступна — базовое значение ниже.
        }

        return (96, 96);
    }

    public static IReadOnlyList<MonitorInfo> All()
    {
        var result = new List<MonitorInfo>();
        try
        {
            // Тело колбэка целиком в try: исключение НЕ должно улететь за границу native-кода.
            MonitorEnumProc callback = (IntPtr hMon, IntPtr hdc, IntPtr lprc, IntPtr dwData) =>
            {
                try
                {
                    var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                    if (!GetMonitorInfoW(hMon, ref mi))
                        return true;

                    // hdc из EnumDisplayMonitors принадлежит текущему монитору —
                    // он же используется как GDI-fallback для DPI.
                    var dpi = GetDpi(hMon, hdc);

                    bool primary = (mi.dwFlags & MonitorinfofPrimary) != 0;
                    result.Add(new MonitorInfo(
                        "",
                        primary,
                        mi.rcMonitor.Left,
                        mi.rcMonitor.Top,
                        mi.rcMonitor.Right - mi.rcMonitor.Left,
                        mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                        mi.rcWork.Left,
                        mi.rcWork.Top,
                        mi.rcWork.Right - mi.rcWork.Left,
                        mi.rcWork.Bottom - mi.rcWork.Top,
                        dpi.X,
                        dpi.Y));
                }
                catch
                {
                    // Монитор пропускаем — главное не ронять перечисление.
                }
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        }
        catch
        {
            // EnumDisplayMonitors недоступен — ниже подставим виртуальный экран.
        }

        // Страховка: OSD некуда позиционировать, если перечисление не дало
        // ничего (сбой API, необычная конфигурация). Пустой список тут означал
        // бы, что оверлей не показывается вообще, поэтому отдаём виртуальный
        // экран — координаты берём из GetSystemMetrics, DPI базовые.
        if (result.Count == 0)
            result.Add(VirtualScreenFallback());

        // Честная нумерация: основной всегда «Монитор 1», далее — слева направо.
        result = result
            .OrderBy(m => m.Primary ? 0 : 1)
            .ThenBy(m => m.X)
            .ToList();

        for (int i = 0; i < result.Count; i++)
        {
            var m = result[i];
            string label = $"Монитор {i + 1}" + (m.Primary ? " (Основной)" : "");
            result[i] = m with { Name = $"{label} [{m.Width}x{m.Height}]" };
        }
        return result;
    }

    /// <summary>Единственный «монитор» = весь виртуальный экран. Аварийный вариант.</summary>
    private static MonitorInfo VirtualScreenFallback()
    {
        int x = 0, y = 0, w = 1920, h = 1080;
        try
        {
            x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        }
        catch
        {
            // Значения выше — разумный минимум, если даже это недоступно.
        }
        if (w <= 0) w = 1920;
        if (h <= 0) h = 1080;

        return new MonitorInfo("", true, x, y, w, h, x, y, w, h, 96, 96);
    }
}
