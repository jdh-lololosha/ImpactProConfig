using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace ImpactProConfig.Services;

/// <summary>
/// Значок в системном трее с живым уровнем заряда.
/// Иконка рисуется на лету (GDI), потому что NotifyIcon не умеет менять значок
/// без пересоздания, а пересоздавать его на каждый процент — лишние хендлы.
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly Forms.NotifyIcon _notify;
    private readonly Action _open;
    private readonly Action<int> _selectProfile;
    private readonly Action _exit;

    private Icon? _currentIcon;
    private int _lastPercent = int.MinValue;
    private bool _lastCharging;
    private bool _lastWireless;
    private bool _lastConnected;
    private bool _disposed;

    /// <param name="open">Показать/восстановить главное окно.</param>
    /// <param name="selectProfile">Выбрать профиль по индексу 0..3.</param>
    /// <param name="exit">Завершить приложение.</param>
    public TrayIconService(Action open, Action<int> selectProfile, Action exit)
    {
        _open = open;
        _selectProfile = selectProfile;
        _exit = exit;

        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add("Открыть", null, (_, _) => _open());
        menu.Items.Add(new Forms.ToolStripSeparator());
        for (int i = 0; i < 4; i++)
        {
            int index = i;
            menu.Items.Add($"Профиль {i + 1}", null, (_, _) => _selectProfile(index));
        }
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => _exit());

        _notify = new Forms.NotifyIcon
        {
            Text = "Impact PRO",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notify.DoubleClick += (_, _) => _open();

        Update(-1, false, false, false);
    }

    /// <summary>Перерисовать значок и подсказку под текущее состояние мыши.</summary>
    public void Update(int percent, bool charging, bool wireless, bool connected)
    {
        if (_disposed)
            return;

        if (percent == _lastPercent && charging == _lastCharging &&
            wireless == _lastWireless && connected == _lastConnected)
        {
            return;
        }
        _lastPercent = percent;
        _lastCharging = charging;
        _lastWireless = wireless;
        _lastConnected = connected;

        SetIcon(DrawBatteryIcon(percent, charging, connected));

        string link = !connected
            ? "Нет подключения"
            : wireless ? "Беспроводной" : "Провод";
        string level = percent < 0 ? "—" : $"{percent}%";
        _notify.Text = $"Impact PRO: {level} • {link}";
    }

    /// <summary>Всплывающее уведомление из трея (используется для низкого заряда).</summary>
    public void Notify(string title, string message)
    {
        if (_disposed)
            return;
        _notify.BalloonTipTitle = title;
        _notify.BalloonTipText = message;
        _notify.ShowBalloonTip(4000);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _notify.Visible = false;
        _notify.Dispose();
        _currentIcon?.Dispose();
        _currentIcon = null;
    }

    private void SetIcon(Icon icon)
    {
        _notify.Icon = icon;
        _currentIcon?.Dispose();
        _currentIcon = icon;
    }

    private static Color BarColor(int percent, bool connected)
    {
        if (!connected || percent < 0)
            return Color.FromArgb(0x70, 0x74, 0x7D); // серый — нет данных
        if (percent >= 50)
            return Color.FromArgb(0x3C, 0xC8, 0x4A); // зелёный
        if (percent >= 20)
            return Color.FromArgb(0xE8, 0xB9, 0x23); // жёлтый
        return Color.FromArgb(0xE8, 0x11, 0x23); // красный (фирменный акцент)
    }

    /// <summary>Значок 32×32: корпус батареи с цветной полосой заряда и знаком ⚡ при зарядке.</summary>
    private static Icon DrawBatteryIcon(int percent, bool charging, bool connected)
    {
        using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var body = new RectangleF(3.5f, 9.5f, 22f, 13f);
            var cap = new RectangleF(26f, 13f, 3f, 6f);

            // Пустой корпус аккумулятора + плюс.
            var outlineColor = Color.FromArgb(0xD0, 0xD4, 0xDC);
            using (var outline = new Pen(outlineColor, 1.6f))
            using (var fill = new SolidBrush(Color.FromArgb(0x30, 0x34, 0x3C)))
            using (var capBrush = new SolidBrush(outlineColor))
            {
                g.FillRectangle(fill, body);
                g.DrawRectangle(outline, body.X, body.Y, body.Width, body.Height);
                g.FillRectangle(capBrush, cap);
            }

            // Полоска заряда.
            if (connected && percent >= 0)
            {
                float inner = 4f;
                float w = (body.Width - inner * 2) * Math.Clamp(percent / 100f, 0f, 1f);
                if (w > 0.5f)
                {
                    var bar = new RectangleF(
                        body.X + inner, body.Y + inner,
                        w, body.Height - inner * 2);
                    using var brush = new SolidBrush(BarColor(percent, connected));
                    using var path = RoundedRect(bar, 1.5f);
                    g.FillPath(brush, path);
                }
            }

            if (charging)
            {
                using var bolt = new SolidBrush(Color.FromArgb(0xFF, 0xD7, 0x2E));
                g.FillPolygon(bolt,
                [
                    new PointF(18f, 4f), new PointF(11.5f, 15f), new PointF(15.5f, 15f),
                    new PointF(13f, 28f), new PointF(20.5f, 16f), new PointF(16.5f, 16f),
                ]);
            }
        }

        IntPtr handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            // GetHicon выдал GDI-хендл, который сам не освободится — снимаем вручную,
            // иначе иконка трея течёт по нескольку хендлов в минуту.
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}