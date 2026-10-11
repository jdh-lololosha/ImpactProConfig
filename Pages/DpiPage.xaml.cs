using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ImpactProConfig.ViewModels;

namespace ImpactProConfig.Pages;

public partial class DpiPage : Page
{
    /// <summary>Слот, цвет которого сейчас редактируется в палитре.</summary>
    private int _colorSlot = -1;

    /// <summary>Защита от рекурсии при программном обновлении полей палитры.</summary>
    private bool _updatingColorFields;

    public DpiPage()
    {
        InitializeComponent();

        // NavigationView при переходах по клику не передаёт dataContext —
        // после попадания страницы в дерево подставляем VM окна, если его нет.
        Loaded += (_, _) =>
        {
            if (DataContext == null && Application.Current.MainWindow is MainWindow mw)
                DataContext = mw.ViewModel;
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>Клик по карточке DPI-ступени — выбор уровня.</summary>
    private void DpiCard_Click(object sender, RoutedEventArgs e)
    {
        // Карточка — Button (см. DpiCardButton), Tag = SlotIndex.
        if (Vm != null && sender is FrameworkElement { Tag: int slot })
            Vm.SelectedDpiIndex = slot;
    }

    // ===== Палитра цвета ступени DPI =====

    /// <summary>Клик по цветному кружку ступени — открыть палитру.</summary>
    private void DpiColor_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null || sender is not FrameworkElement { Tag: int slot })
            return;

        // Клик по кружку не должен выбирать карточку — глушим всплытие.
        e.Handled = true;
        if ((uint)slot >= (uint)Vm.DpiSlots.Length)
            return;

        _colorSlot = slot;
        ColorPopup.PlacementTarget = (UIElement)sender;
        SyncColorFieldsFromSlot(slot);
        ColorPopup.IsOpen = true;
        ColorHexBox.Focus();
    }

    /// <summary>Клик по пресету — применить цвет мгновенно.</summary>
    private void PresetColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex } || !TryParseHex(hex, out var c))
            return;
        ApplyColor(c.R, c.G, c.B);
    }

    /// <summary>HEX по Enter.</summary>
    private void ColorHexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        if (TryParseHex(ColorHexBox.Text, out var c))
            ApplyColor(c.R, c.G, c.B);
    }

    /// <summary>HEX при потере фокуса.</summary>
    private void ColorHexBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (TryParseHex(ColorHexBox.Text, out var c))
            ApplyColor(c.R, c.G, c.B);
    }

    /// <summary>Изменение R/G/B — применить и обновить HEX.</summary>
    private void ColorRgbBox_ValueChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingColorFields)
            return;
        byte r = ClampByte(ColorRBox.Value ?? 0);
        byte g = ClampByte(ColorGBox.Value ?? 0);
        byte b = ClampByte(ColorBBox.Value ?? 0);
        ApplyColor(r, g, b, updateRgb: false);
    }

    /// <summary>Кнопка «Готово» — закрыть палитру.</summary>
    private void CloseColorPopup_Click(object sender, RoutedEventArgs e)
    {
        ColorPopup.IsOpen = false;
    }

    /// <summary>Закрытие палитры — сброс редактируемого слота.</summary>
    private void ColorPopup_Closed(object? sender, EventArgs e)
    {
        _colorSlot = -1;
    }

    /// <summary>Заполнить поля палитры текущим цветом слота.</summary>
    private void SyncColorFieldsFromSlot(int slot)
    {
        var c = Vm!.DpiSlots[slot].Color;
        _updatingColorFields = true;
        try
        {
            ColorHexBox.Text = "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
            ColorRBox.Value = c.R;
            ColorGBox.Value = c.G;
            ColorBBox.Value = c.B;
        }
        finally
        {
            _updatingColorFields = false;
        }
    }

    /// <summary>Применить цвет: записать в _working и обновить поля.</summary>
    private void ApplyColor(byte r, byte g, byte b, bool updateRgb = true)
    {
        if (Vm == null || _colorSlot < 0)
            return;
        Vm.SetDpiColor(_colorSlot, r, g, b);

        _updatingColorFields = true;
        try
        {
            ColorHexBox.Text = "#" + r.ToString("X2") + g.ToString("X2") + b.ToString("X2");
            if (updateRgb)
            {
                ColorRBox.Value = r;
                ColorGBox.Value = g;
                ColorBBox.Value = b;
            }
        }
        finally
        {
            _updatingColorFields = false;
        }
    }

    /// <summary>Парсинг HEX: #RRGGBB, RRGGBB, #RGB, RGB. Пустое/неверное — без исключений.</summary>
    private static bool TryParseHex(string? text, out System.Windows.Media.Color c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string s = text.Trim().TrimStart('#');
        try
        {
            if (s.Length == 3)
            {
                int r = int.Parse(s[0].ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture) * 17;
                int g = int.Parse(s[1].ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture) * 17;
                int b = int.Parse(s[2].ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture) * 17;
                c = System.Windows.Media.Color.FromRgb((byte)r, (byte)g, (byte)b);
                return true;
            }
            if (s.Length == 6)
            {
                uint value = uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                c = System.Windows.Media.Color.FromRgb(
                    (byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));
                return true;
            }
        }
        catch (FormatException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
        return false;
    }

    private static byte ClampByte(double value) =>
        value < 0 ? (byte)0 : value > 255 ? (byte)255 : (byte)Math.Round(value);

    private void DpiMinus_Click(object sender, RoutedEventArgs e)
    {
        if (Vm != null)
            Vm.SelectedDpiValue = Math.Max(50, Vm.SelectedDpiValue - 50);
    }

    private void DpiPlus_Click(object sender, RoutedEventArgs e)
    {
        if (Vm != null)
            Vm.SelectedDpiValue = Math.Min(26000, Vm.SelectedDpiValue + 50);
    }
}
