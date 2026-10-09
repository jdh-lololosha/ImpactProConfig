using System.Runtime.InteropServices;

namespace ImpactProConfig.Updater;

// Ручные алиасы GDI: без System.Drawing эти имена не нужны, но читаются
// как в документации MSDN.
using HPEN = System.IntPtr;
using HBRUSH = System.IntPtr;
using HGDIOBJ = System.IntPtr;

/// <summary>
/// Окно обновления: 380x260, по центру экрана, тёмная карточка #161B22
/// со скруглением 12 и тонкой рамкой.
///
/// Сделано на голом Win32 (P/Invoke), а не на WPF/WinForms, потому что Updater
/// собирается с PublishAot: AOT несовместим с WPF, а self-contained WPF-апдейтер
/// добавил бы в портативный архив ~70 МБ второго рантайма. Рисуем всё сами
/// через WM_PAINT — это держит Apphost на 2 МБ и сохраняет независимость
/// от установленного .NET.
/// </summary>
internal sealed class SplashWindow
{
    // ===== Состояние, общее для рабочего потока и UI =====
    private readonly object _gate = new();
    private Phase _phase = Phase.WaitingApp;
    private string _status = "Ожидание закрытия программы...";
    private string? _error;
    private double _fraction;          // 0..1 для детерминированного прогресса
    private DateTime _spinAnchor;      // для анимации неопределённого прогресса

    internal enum Phase
    {
        WaitingApp,       // ждём выход основного процесса
        Extracting,       // распаковка zip
        Launching,        // запуск новой версии
        Done,             // всё хорошо, закрываемся
        Failed,           // показать ошибку и кнопку «Закрыть»
    }

    /// <summary>
    /// HWND окна. Ноль, пока окно не создано или уже закрыто — по нему главный
    /// поток понимает, продолжать ли крутить очередь сообщений.
    /// </summary>
    internal IntPtr hwnd;

    /// <summary>Версия обновления для подзаголовка (задаётся до Show).</summary>
    public string? Version { get; set; }

    private GCHandle _handle;
    private bool _classRegistered;
    private static IntPtr _font;

    public SplashWindow()
    {
        _spinAnchor = DateTime.Now;
    }

    // ===== Публичный API для рабочего потока =====

    /// <summary>Сменить фазу и статус. Безопасно из любого потока.</summary>
    public void SetPhase(Phase phase, string status)
    {
        lock (_gate)
        {
            _phase = phase;
            _status = status;
        }
        Post(MsgPaint);
    }

    /// <summary>
    /// Дать главному потоку обработать очередь сообщений, чтобы окно
    /// перерисовалось. Нужна после смены фазы: иначе пользователь увидит
    /// предыдущий статус, пока идёт распаковка.
    /// </summary>
    public void Pump() => Post(MsgPaint);

    /// <summary>Закрыть окно из рабочего потока.</summary>
    public void Close() => Post(WM_CLOSE);

    /// <summary>Детерминированный прогресс распаковки (0..1).</summary>
    public void SetFraction(double fraction)
    {
        lock (_gate)
        {
            _fraction = fraction < 0 ? 0 : fraction > 1 ? 1 : fraction;
        }
        Post(MsgPaint);
    }

    /// <summary>Показать ошибку: окно остаётся с кнопкой «Закрыть».</summary>
    public void ShowError(string message)
    {
        lock (_gate)
        {
            _error = message;
            _phase = Phase.Failed;
            _status = "Не удалось обновить";
        }
        Post(MsgPaint);
    }

    // ===== Создание и показ окна =====

    public void Show()
    {
        EnsureClassRegistered();

        int screenW = GetSystemMetrics(SM_CXSCREEN);
        int screenH = GetSystemMetrics(SM_CYSCREEN);
        int x = (screenW - Width) / 2;
        int y = (screenH - Height) / 2;

        _handle = GCHandle.Alloc(this);

        hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            _classNameStr, WindowTitle,
            // Только WS_POPUP: WS_CAPTION рисует системный заголовок
            // (серую полосу) поверх нашей рамки.
            WS_POPUP,
            x, y, Width, Height,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            GCHandle.ToIntPtr(_handle));

        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException(
                $"Не удалось создать окно обновления (код {Marshal.GetLastWin32Error()}).");

        // Скруглённые углы: окно = регион с радиусом 12.
        IntPtr region = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, 24, 24);
        SetWindowRgn(hwnd, region, true);   // система владеет регионом после вызова

        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
        UpdateWindow(hwnd);
        InvalidateRect(hwnd, IntPtr.Zero, false);
    }

    // ===== Оконная процедура =====

    private static IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_NCCREATE)
        {
            // lpParam -> CREATESTRUCT -> lpCreateParams: так объект доступен
            // с самого первого сообщения, до WM_PAINT.
            var cs = Marshal.PtrToStructure<CREATESTRUCT>(lParam);
            if (cs.lpCreateParams != IntPtr.Zero)
                SetWindowLongPtr(hWnd, GwlpUserData, cs.lpCreateParams);
        }

        var self = Context(hWnd);
        if (self is null)
            return DefWindowProc(hWnd, msg, wParam, lParam);

        switch (msg)
        {
            case WM_PAINT:
                self.OnPaint();
                return IntPtr.Zero;

            case WM_ERASEBKGND:
                // Фон рисуем сами: возврат 1 отменяет заливку системным цветом.
                return new IntPtr(1);

            case MsgPaint:
                // Запрос на перерисовку с рабочего потока.
                InvalidateRect(hWnd, IntPtr.Zero, false);
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                // В фазе ошибки клик по кнопке «Закрыть» завершает процесс.
                if (self.IsInCloseButton(lParam))
                {
                    lock (self._gate)
                    {
                        if (self._phase == Phase.Failed)
                            self._phase = Phase.Done;
                    }
                    DestroyWindow(hWnd);
                }
                return IntPtr.Zero;

            case WM_CLOSE:
                // Закрытие допускаем только в фазе ошибки: во время
                // распаковки закрывать окно нельзя, апдейт должен дойти до конца.
                lock (self._gate)
                {
                    if (self._phase != Phase.Failed)
                        return IntPtr.Zero;
                }
                DestroyWindow(hWnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                self.hwnd = IntPtr.Zero;
                // Освобождаем GCHandle только после выхода из WndProc,
                // иначе объект может быть собран прямо во время обработки.
                if (self._handle.IsAllocated)
                {
                    var handle = self._handle;
                    self._handle = default;
                    handle.Free();
                }
                PostQuitThread(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private bool IsInCloseButton(IntPtr lParam)
    {
        int x = (short)(lParam.ToInt64() & 0xFFFF);
        int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        return x >= CloseButtonX && x <= CloseButtonX + CloseButtonW
            && y >= CloseButtonY && y <= CloseButtonY + CloseButtonH;
    }

    // ===== Рисование =====

    private void OnPaint()
    {
        Phase phase;
        string status;
        string? error;
        double fraction;
        lock (_gate)
        {
            phase = _phase;
            status = _status;
            error = _error;
            fraction = _fraction;
        }

        IntPtr dc = BeginPaint(hwnd, out PAINTSTRUCT ps);
        try
        {
            // Фон карточки #161B22
            RECT full = new() { Left = 0, Top = 0, Right = Width, Bottom = Height };
            HBRUSH bg = CreateSolidBrush(Rgb(0x16, 0x1B, 0x22));
            FillRect(dc, ref full, bg);
            DeleteObject(bg);

            // Рамка: эквивалент #30FFFFFF в GDI (alpha не поддерживается).
            HPEN border = CreatePen(PS_SOLID, 1, Rgb(0x33, 0x38, 0x40));
            HGDIOBJ oldPen = SelectObject(dc, border);
            HGDIOBJ oldBrush = SelectObject(dc, GetStockObject(NULL_BRUSH));
            Rectangle(dc, 0, 0, Width - 1, Height - 1);
            SelectObject(dc, oldBrush);
            SelectObject(dc, oldPen);
            DeleteObject(border);

            SetBkMode(dc, TRANSPARENT);
            SetTextColor(dc, Rgb(0xFF, 0xFF, 0xFF));

            // Шрифт обязан быть выбран в DC явно: у окна, созданного
            // CreateWindowEx, в DC нет шрифта, и DrawText рисует вместо
            // букв вертикальные полосы.
            SelectFont(dc, 17, FW_NORMAL);

            // --- Иконка приложения слева сверху ---
            DrawAppIcon(dc, 24, 22, 44);

            // --- Заголовок ---
            RECT titleRect = new() { Left = 82, Top = 26, Right = Width - 20, Bottom = 52 };
            DrawUtf16(dc, "ImpactProConfig Updater", ref titleRect,
                DT_LEFT | DT_SINGLELINE | DT_VCENTER);

            // --- Подзаголовок с версией, если известна ---
            if (Version is { Length: > 0 })
            {
                SelectFont(dc, 14, FW_NORMAL);
                SetTextColor(dc, Rgb(0x8B, 0x94, 0xA0));
                RECT verRect = new() { Left = 82, Top = 50, Right = Width - 20, Bottom = 72 };
                DrawUtf16(dc, Version, ref verRect, DT_LEFT | DT_SINGLELINE | DT_VCENTER);
                SelectFont(dc, 17, FW_NORMAL);
            }

            // --- Прогресс-бар ---
            const int barX = 24;
            const int barY = 92;
            const int barW = Width - 48;
            const int barH = 6;
            DrawProgressBar(dc, barX, barY, barW, barH, phase, fraction, error is not null);

            // --- Статус-текст по центру ---
            SelectFont(dc, 15, FW_NORMAL);
            SetTextColor(dc, error is not null ? Rgb(0xFF, 0x85, 0x85) : Rgb(0xC9, 0xD1, 0xD9));
            RECT statusRect = new()
            {
                Left = 24, Top = barY + 16,
                Right = Width - 24, Bottom = barY + 48,
            };
            DrawLinesUtf16(dc, status, ref statusRect, DT_CENTER, 20);

            // --- При ошибке: понятный текст и кнопка «Закрыть» ---
            if (error is not null)
            {
                SelectFont(dc, 13, FW_NORMAL);
                SetTextColor(dc, Rgb(0x8B, 0x94, 0xA0));
                RECT errorRect = new()
                {
                    Left = 24, Top = barY + 50,
                    Right = Width - 24, Bottom = CloseButtonY - 14,
                };
                DrawLinesUtf16(dc, Shorten(error, 400), ref errorRect, DT_CENTER, 18);

                DrawCloseButton(dc);
            }
        }
        finally
        {
            EndPaint(hwnd, ref ps);
        }
    }

    /// <summary>Иконка приложения из ресурсов exe, при неудаче — фирменный ромб.</summary>
    private void DrawAppIcon(IntPtr dc, int x, int y, int size)
    {
        IntPtr icon = LoadImage(IntPtr.Zero, ApplicationIconPath(), IMAGE_ICON,
            size, size, LR_LOADFROMFILE);

        if (icon != IntPtr.Zero)
        {
            DrawIconEx(dc, x, y, icon, size, size, 0, IntPtr.Zero, DI_NORMAL);
            DestroyIcon(icon);
            return;
        }

        // Фирменный акцент-ромб, если иконки нет.
        HPEN pen = CreatePen(PS_SOLID, 2, Rgb(0xE8, 0x11, 0x23));
        HGDIOBJ op = SelectObject(dc, pen);
        HGDIOBJ ob = SelectObject(dc, GetStockObject(NULL_BRUSH));
        POINT[] pts =
        {
            new() { X = x + size / 2, Y = y },
            new() { X = x + size,     Y = y + size / 2 },
            new() { X = x + size / 2, Y = y + size },
            new() { X = x,             Y = y + size / 2 },
        };
        Polygon(dc, pts, 4);
        SelectObject(dc, ob);
        SelectObject(dc, op);
        DeleteObject(pen);
    }

    private void DrawProgressBar(IntPtr dc, int x, int y, int w, int h,
                                 Phase phase, double fraction, bool failed)
    {
        RECT trackRect = new() { Left = x, Top = y, Right = x + w, Bottom = y + h };
        HBRUSH track = CreateSolidBrush(failed ? Rgb(0x2A, 0x1F, 0x22) : Rgb(0x2B, 0x31, 0x3A));
        FillRect(dc, ref trackRect, track);
        DeleteObject(track);

        if (failed)
            return;

        int originX = x;
        if (phase == Phase.WaitingApp)
        {
            // Неопределённый прогресс: бегущая полоса.
            double t = (DateTime.Now - _spinAnchor).TotalSeconds;
            const double period = 1.1;
            double phase01 = (t % period) / period;
            int seg = w / 3;
            int head = (int)(phase01 * (w + seg));
            int left = Math.Max(head - seg, 0);
            int right = Math.Min(head, w);
            x += left;
            w = Math.Max(right - left, 0);
        }
        else if (phase is Phase.Extracting or Phase.Launching)
        {
            w = (int)(w * fraction);
        }
        // Phase.Done: полоса остаётся полностью заполненной.

        if (w <= 0)
            return;

        // Акцентный красный #E81123 (и светлее #FF2E2E на «готовом»).
        RECT fillRect = new()
        {
            Left = originX + (x - originX), Top = y,
            Right = originX + (x - originX) + w, Bottom = y + h,
        };
        HBRUSH fill = CreateSolidBrush(
            phase == Phase.Done ? Rgb(0xFF, 0x2E, 0x2E) : Rgb(0xE8, 0x11, 0x23));
        FillRect(dc, ref fillRect, fill);
        DeleteObject(fill);
    }

    private void DrawCloseButton(IntPtr dc)
    {
        RECT r = new()
        {
            Left = CloseButtonX, Top = CloseButtonY,
            Right = CloseButtonX + CloseButtonW, Bottom = CloseButtonY + CloseButtonH,
        };

        HBRUSH button = CreateSolidBrush(Rgb(0xE8, 0x11, 0x23));
        FillRect(dc, ref r, button);
        DeleteObject(button);

        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, Rgb(0xFF, 0xFF, 0xFF));
        SelectFont(dc, 14, FW_NORMAL);
        RECT text = new()
        {
            Left = r.Left, Top = r.Top + 1,
            Right = r.Right, Bottom = r.Bottom - 1,
        };
        DrawUtf16(dc, "Закрыть", ref text, DT_CENTER | DT_SINGLELINE | DT_VCENTER);

        // Обводка кнопки, чтобы не сливалась с тёмным фоном.
        HPEN pen = CreatePen(PS_SOLID, 1, Rgb(0xFF, 0x5C, 0x69));
        HGDIOBJ op = SelectObject(dc, pen);
        HGDIOBJ ob = SelectObject(dc, GetStockObject(NULL_BRUSH));
        Rectangle(dc, r.Left, r.Top, r.Right, r.Bottom);
        SelectObject(dc, ob);
        SelectObject(dc, op);
        DeleteObject(pen);
    }

    // ===== Текст =====

    /// <summary>
    /// Однострочная отрисовка с реальной длиной строки: DrawText с count = -1
    /// умеет только ANSI и обрезает кириллицу до первого символа.
    /// </summary>
    private static void DrawUtf16(IntPtr dc, string text, ref RECT rc, uint flags) =>
        DrawText(dc, text, text.Length, ref rc, flags);

    /// <summary>
    /// Многострочный текст по центру. Каждая строка рисуется отдельным
    /// DT_SINGLELINE-вызовом: DT_WORDBREAK вместе с '\n' внутри строки
    /// ломает расчёт прямоугольника в GDI (текст рисуется мусором).
    /// </summary>
    private static void DrawLinesUtf16(IntPtr dc, string text, ref RECT area,
                                       uint align, int lineHeight)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        lines = WrapLines(dc, lines, area.Right - area.Left);
        int totalHeight = lineHeight * lines.Length;

        int y = area.Top + Math.Max(0, (area.Bottom - area.Top - totalHeight) / 2);
        foreach (string line in lines)
        {
            if (line.Length > 0)
            {
                RECT lineRect = new()
                {
                    Left = area.Left, Top = y,
                    Right = area.Right, Bottom = y + lineHeight,
                };
                DrawUtf16(dc, line, ref lineRect, align | DT_SINGLELINE | DT_VCENTER);
            }
            y += lineHeight;
        }
    }

    /// <summary>
    /// Выбирает шрифт в DC. Кэшируем на весь процесс: CreateFont дорог.
    /// Без явного выбора шрифта DC у DrawText нет глифов и он рисует
    /// вертикальные полосы вместо букв.
    /// </summary>
    private static void SelectFont(IntPtr dc, int heightPx, int weight)
    {
        if (_font == IntPtr.Zero)
        {
            _font = CreateFontW(
                -heightPx, 0, 0, 0, weight,
                0, 0, 0,
                DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS,
                CLEARTYPE_QUALITY, 0, "Segoe UI");
        }
        SelectObject(dc, _font);
    }

    /// <summary>
/// Перенос по словам под ширину области. DrawText с DT_WORDBREAK этого
/// не делает: он ломает расчёт прямоугольника и рисует мусор, а без него
/// длинная строка просто обрезается по краю окна.
/// </summary>
private static string[] WrapLines(IntPtr dc, string[] lines, int maxWidth)
    {
        var result = new List<string>();
        foreach (string line in lines)
        {
            if (line.Length == 0)
            {
                result.Add(line);
                continue;
            }

            var current = new System.Text.StringBuilder();
            foreach (string word in line.Split(' '))
            {
                // Ширину меряем в пикселях через GetTextExtentPoint32W:
                // сравнение по числу символов неверно для кириллицы,
                // у неё глифы шире латиницы.
                if (current.Length > 0 &&
                    MeasurePx(dc, current + " " + word) > maxWidth)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    current.Append(word);
                }
                else if (current.Length > 0)
                {
                    current.Append(' ').Append(word);
                }
                else
                {
                    current.Append(word);
                }
            }
            if (current.Length > 0)
                result.Add(current.ToString());
        }
        return result.ToArray();
    }

    /// <summary>Ширина строки в пикселях для текущего шрифта DC.</summary>
    private static int MeasurePx(IntPtr dc, string text)
    {
        if (!GetTextExtentPoint32W(dc, text, text.Length, out SIZE size))
            return text.Length * 8;
        return size.cx;
    }

    private static string Shorten(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "...";

    private static string ApplicationIconPath()
    {
        string dir = AppContext.BaseDirectory;
        string exe = Path.Combine(dir, "ImpactProConfig.exe");
        return File.Exists(exe) ? exe : dir;
    }

    // ===== Геометрия =====

    private const int Width = 380;
    // 290, а не 220: при ошибке нужно место под 4-5 строк текста и кнопку.
    // При 260 последняя строка уходила под кнопку «Закрыть».
    private const int Height = 290;
    private const int CloseButtonW = 116;
    private const int CloseButtonH = 34;
    private static int CloseButtonX => (Width - CloseButtonW) / 2;
    private static int CloseButtonY => Height - 54;

    private const string WindowTitle = "ImpactProConfig Updater";

    // Регистрация класса окна. Делегат WndProc обязан жить всё время работы
    // программы: если он соберётся GC, вызов из user32 упадёт. Поэтому он
    // static, а не локальная переменная.
    private static readonly WndProcDelegate WndProcThunk = WndProc;

    private void EnsureClassRegistered()
    {
        if (_classRegistered)
            return;
        _classRegistered = true;

        // Статические буферы: их адреса передаём в WNDCLASS как LPCWSTR.
        // static — чтобы GC не собрал строку между регистрацией и Show.
        _classNamePtr = Marshal.StringToHGlobalUni(_classNameStr);

        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcThunk),
            hInstance = GetModuleHandleW(null),
            lpszMenuName = IntPtr.Zero,
            lpszClassName = _classNamePtr,
        };
        ushort atom = RegisterClassW(ref wc);
        if (atom == 0)
            throw new InvalidOperationException(
                $"Не удалось зарегистрировать класс окна (код {Marshal.GetLastWin32Error()}).");
    }

    private static readonly string _classNameStr = "ImpactProConfigUpdaterSplash";
    private static IntPtr _classNamePtr;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private static SplashWindow? Context(IntPtr hWnd)
    {
        IntPtr raw = GetWindowLongPtr(hWnd, GwlpUserData);
        return raw == IntPtr.Zero ? null : GCHandle.FromIntPtr(raw).Target as SplashWindow;
    }

    private void Post(int msg)
    {
        IntPtr h = hwnd;
        if (h != IntPtr.Zero)
            PostMessageW(h, msg, IntPtr.Zero, IntPtr.Zero);
    }

    // ===== Структуры =====

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore;
        public bool fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREATESTRUCT
    {
        public IntPtr lpCreateParams, hInstance, hMenu, hwndParent,
                    cy, cx, y, x, style, dwExStyle, lpszClass, lpszName, lpszType;
    }

    // ВАЖНО: у WNDCLASS НЕТ поля cbSize — это поле WNDCLASSEX. Лишнее поле
    // сдвигает все последующие на 4 байта, hInstance читается не оттуда, и
    // RegisterClassW возвращает ERROR_INVALID_PARAMETER (87).
    // Правильный размер структуры на x64 — 72 байта.
    // Строковые поля — указатели LPCWSTR: ByValTStr/SizeConst=256 дали бы
    // размер 1088 байт вместо 80, и регистрация класса откажется.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public int style;
        // Уже готовый указатель на делегат: MarshalAs(FunctionPtr) здесь
        // недопустим, это ломает маршалинг всей структуры.
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    // ===== Константы =====

    private const int WM_DESTROY = 0x0002;
    private const int WM_PAINT = 0x000F;
    private const int WM_CLOSE = 0x0010;
    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_NCCREATE = 0x0081;
    private const int WM_LBUTTONUP = 0x0202;
    private const int MsgPaint = 0x0400 + 1;   // WM_APP+1
    private const int GwlpUserData = -21;      // GWLP_USERDATA

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int SW_SHOWNOACTIVATE = 4;

    private const int PS_SOLID = 0;
    private const int NULL_BRUSH = 5;
    private const int TRANSPARENT = 1;
    private const int DI_NORMAL = 0x0003;

    private const int DEFAULT_CHARSET = 1;
    private const int OUT_DEFAULT_PRECIS = 0;
    private const int CLIP_DEFAULT_PRECIS = 0;
    private const int CLEARTYPE_QUALITY = 5;
    private const int FW_NORMAL = 400;

    private const uint DT_LEFT = 0x00000000;
    private const uint DT_CENTER = 0x00000001;
    private const uint DT_VCENTER = 0x00000004;
    private const uint DT_SINGLELINE = 0x00000020;

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private const int IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;

    // ===== P/Invoke =====
    // ВАЖНО: FillRect и DrawText экспортирует user32.dll, а не gdi32.
    // Объявление в gdi32 даёт EntryPointNotFoundException в рантайме.
    // Явный суффикс W + ExactSpelling обязателен: без ExactSpelling
    // маршаллер дописывает второй W и ищет "DrawTextWW".

    [DllImport("user32.dll", EntryPoint = "FillRect", ExactSpelling = true)]
    private static extern bool FillRect(IntPtr hdc, ref RECT rc, IntPtr brush);

    [DllImport("user32.dll", EntryPoint = "DrawTextW",
        CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int DrawText(IntPtr hdc, string text, int count, ref RECT rc, uint flags);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr hdc, uint color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern bool Rectangle(IntPtr hdc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern int Polygon(IntPtr hdc, [In] POINT[] pts, int count);
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePen(int style, int width, uint color);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
    [DllImport("gdi32.dll", EntryPoint = "GetTextExtentPoint32W",
        CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool GetTextExtentPoint32W(IntPtr hdc, string text, int count, out SIZE size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

    [DllImport("gdi32.dll", EntryPoint = "CreateFontW",
        CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateFontW(
        int height, int width, int escapement, int orientation, int weight,
        int italic, int underline, int strikeOut, int charSet,
        int outputPrecision, int clipPrecision, int quality, int pitchAndFamily,
        string faceName);

    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT ps);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);
    [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
    private static extern bool PostMessageW(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern void PostQuitThread(int exitCode);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW",
        CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr CreateWindowEx(int ex, string cls, string title,
        int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASS wc);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowRgn(IntPtr h, IntPtr region, bool redraw);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", ExactSpelling = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", ExactSpelling = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "LoadImageW",
        CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr LoadImage(IntPtr hinst, string name, int type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon,
        int cx, int cy, int step, IntPtr brush, int flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW",
        CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string? name);

    /// <summary>GDI-цвет: 0x00BBGGRR.</summary>
    private static uint Rgb(byte r, byte g, byte b) =>
        (uint)(r | (g << 8) | (b << 16));
}