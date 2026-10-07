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

                    double dpiX = 96, dpiY = 96;
                    if (GetDpiForMonitor(hMon, MdtEffectiveDpi, out uint dx, out uint dy) == 0 && dx > 0)
                    {
                        dpiX = dx;
                        dpiY = dy;
                    }

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
                        dpiX,
                        dpiY));
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
            // EnumDisplayMonitors недоступен — вернём пустой список.
        }

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
}
