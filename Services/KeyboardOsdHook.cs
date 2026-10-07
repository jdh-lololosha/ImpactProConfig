using System.Runtime.InteropServices;

namespace ImpactProConfig.Services;

/// <summary>
/// Глобальный low-level хук клавиатуры (WH_KEYBOARD_LL) — триггер OSD-оверлея.
/// В слот мыши записан shortcut «F24» (вендорская схема: KeyFunMap type=5 + контент
/// в shortCutKey[слот], HID-usage 0x73): физическое нажатие любой кнопки (включая
/// DPI-кнопку, у которой нет OS-событий мыши) заставляет мышь отправить F24.
/// Хук перехватывает VK_F24 (0x87), СЪЕДАЕТ down и up (ОС и игры клавишу не видят)
/// и вызывает колбэк — тот показывает оверлей.
///
/// Отдельный хук от диагностики MainWindow и от MouseOsdHook: все могут работать
/// одновременно.
/// </summary>
internal static class KeyboardOsdHook
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    /// <summary>VK_F24 — «свободная» клавиша, которую мышь шлёт вместо кнопки.</summary>
    public const int VkF24 = 0x87;

    private static IntPtr _hookId;
    private static KeyboardHookProc? _proc;   // живая ссылка — иначе GC соберёт делегат
    private static Func<int, bool>? _handler;
    private static bool _eatUp;               // down съеден — съедём и парный up

    private delegate IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, KeyboardHookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    public static bool Installed => _hookId != IntPtr.Zero;

    public static void Start(Func<int, bool> handler)
    {
        if (_hookId != IntPtr.Zero)
            return;
        _handler = handler;
        _proc = Callback;
        _hookId = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
    }

    public static void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        _handler = null;
        _eatUp = false;
    }

    private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && _handler != null)
            {
                int msg = (int)wParam;
                if (msg is WmKeyDown or WmSysKeyDown or WmKeyUp or WmSysKeyUp)
                {
                    var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    if (kb.vkCode == VkF24)
                    {
                        if (msg is WmKeyUp or WmSysKeyUp)
                        {
                            if (_eatUp)
                            {
                                _eatUp = false;
                                return (IntPtr)1;   // съесть отпускание после нашего down
                            }
                        }
                        else if (_handler(VkF24))
                        {
                            _eatUp = true;          // down съеден — и up съедём
                            return (IntPtr)1;
                        }
                    }
                }
            }
        }
        catch
        {
            // Хук никогда не должен падать — иначе Windows его отключит.
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }
}
