using System.Runtime.InteropServices;

namespace ImpactProConfig.Services;

/// <summary>
/// Глобальный low-level хук мыши (WH_MOUSE_LL) для триггера OSD-оверлея.
/// Вендорский протокол не присылает отчёты о нажатиях кнопок, поэтому физические
/// нажатия перехватываем на уровне ОС. Колбэк получает usage кнопки (бит Mask из
/// MouseKey: LMB=1, RMB=2, MMB=4, «Назад»=8, «Вперёд»=0x10) и возвращает true,
/// если событие нужно СЪЕСТЬ (ОС его не увидит).
///
/// Отдельный хук от диагностики MainWindow: оба могут существовать одновременно.
/// </summary>
internal static class MouseOsdHook
{
    private const int WhMouseLl = 14;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;
    private const int WmXButtonDown = 0x020B;

    /// <summary>
    /// Момент последнего движения мыши (UTC). Хук и так получает все события
    /// мыши, поэтому метка обновляется одной записью DateTime без всяких
    /// дополнительных подписок. Нужна для разделения расхода батареи на
    /// «работа» и «ожидание»: вендорский протокол не сообщает о движении.
    ///
    /// Interlocked.Exchange вместо простого присваивания: хук зовётся из
    /// потока хука, читаем мы с UI-потока, и long не атомарен на 32-битном
    /// смещении архитектуры.
    /// </summary>
    private static long _lastMoveTicks;

    public static DateTimeOffset LastMoveAt =>
        Interlocked.Read(ref _lastMoveTicks) == 0
            ? DateTimeOffset.MinValue
            : new DateTimeOffset(Interlocked.Read(ref _lastMoveTicks), TimeSpan.Zero);

    /// <summary>
    /// Мышь двигалась недавно (в пределах <paramref name="window"/>)?
    /// До первого движения честно отвечает false, а не «да»: иначе компьютер,
    /// который только включили, попал бы в статистику как «активная работа».
    /// </summary>
    public static bool WasActiveRecently(TimeSpan window)
    {
        long ticks = Interlocked.Read(ref _lastMoveTicks);
        return ticks != 0 && DateTimeOffset.UtcNow - new DateTimeOffset(ticks, TimeSpan.Zero) < window;
    }

    private static IntPtr _hookId;
    private static MouseHookProc? _proc;   // живая ссылка — иначе GC соберёт делегат
    private static Func<int, bool>? _handler;

    private delegate IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, MouseHookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    public static void Start(Func<int, bool> handler)
    {
        if (_hookId != IntPtr.Zero)
            return;
        _handler = handler;
        _proc = Callback;
        _hookId = SetWindowsHookEx(WhMouseLl, _proc, GetModuleHandle(null), 0);
    }

    public static void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        _handler = null;
    }

    private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                int message = (int)wParam;

                // Отметка активности для статистики батареи. Стоит до проверки
                // _handler: движение мыши интересует нас всегда, даже когда
                // OSD-оверлей выключен и _handler == null.
                if (message == WmMouseMove)
                {
                    Interlocked.Exchange(ref _lastMoveTicks, DateTime.UtcNow.Ticks);
                }
                else if (_handler != null)
                {
                    int usage = message switch
                    {
                        WmLButtonDown => 1,          // MouseKey.LeftKey
                        WmRButtonDown => 2,          // MouseKey.RightKey
                        WmMButtonDown => 4,          // MouseKey.MiddleKey
                        WmXButtonDown => XUsage(lParam),
                        _ => 0
                    };
                    if (usage != 0 && _handler(usage))
                        return (IntPtr)1;            // съесть — ОС нажатие не увидит
                }
            }
        }
        catch
        {
            // Хук никогда не должен падать — иначе Windows его отключит.
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>HIWORD(mouseData): XBUTTON1 → «Вперёд» (0x10), XBUTTON2 → «Назад» (0x08).</summary>
    private static int XUsage(IntPtr lParam)
    {
        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).mouseData >> 16;
        return data switch
        {
            1u => 0x10,
            2u => 0x08,
            _ => 0
        };
    }
}
