using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace ImpactProConfig.Services;

/// <summary>Пресет акцентного цвета интерфейса.</summary>
/// <param name="Name">Название для выпадающего списка.</param>
internal sealed record AccentPreset(string Name, byte R, byte G, byte B)
{
    public Color Color => Color.FromRgb(R, G, B);
}

/// <summary>Акцентные цвета интерфейса (слайдеры, активный DPI, подиум, кнопки).</summary>
internal static class ThemeManager
{
    /// <summary>По умолчанию — фирменный красный Ardor.</summary>
    public const int DefaultIndex = 0;

    public static readonly AccentPreset[] Presets =
    [
        new("Ardor Red", 0xE8, 0x11, 0x23),
        new("Sakura Pink", 0xF2, 0x8C, 0xB4),
        new("Cyberpunk Cyan", 0x00, 0xE0, 0xE6),
        new("Toxic Green", 0x4F, 0xD6, 0x3B),
        new("Deep Violet", 0x9B, 0x6B, 0xF5),
    ];

    public static AccentPreset At(int index) =>
        Presets[Math.Clamp(index, 0, Presets.Length - 1)];

    /// <summary>
    /// Применить акцент к темам WPF-UI и перекрасить все элементы НА ЛЕТУ.
    ///
    /// Два независимых механизма, оба обязательны:
    ///  1. ApplicationAccentColorManager — перекрашивает контролы WPF-UI
    ///     (слайдеры, кнопки, тумблеры), которые читают свой акцент из темы.
    ///  2. Application.Current.Resources — обновляет ImpactAccentBrush /
    ///     ImpactAccentColor, на которые элементы привязаны через DynamicResource.
    ///     Без этого StaticResource-подобные места (DropShadowEffect, рамки)
    ///     останутся старого цвета до перезапуска.
    /// </summary>
    public static void Apply(int index)
    {
        Color color = At(index).Color;
        ApplicationAccentColorManager.Apply(color, ApplicationTheme.Dark, false);

        // Обновляем ресурсы на лету: DynamicResource подхватит новое значение
        // без перезапуска и без пересоздания элементов.
        if (Application.Current != null)
        {
            Application.Current.Resources["ImpactAccentColor"] = color;
            Application.Current.Resources["ImpactAccentBrush"] = new SolidColorBrush(color);
        }
    }

    /// <summary>Цвет заливки неонового подиума под корпус мыши.</summary>
    public static RadialGradientBrush PodiumGlow(Color accent) => new()
    {
        Center = new Point(0.5, 0.5),
        GradientOrigin = new Point(0.5, 0.5),
        RadiusX = 0.5,
        RadiusY = 0.5,
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0x8C, accent.R, accent.G, accent.B), 0),
            new GradientStop(Color.FromArgb(0x28, accent.R, accent.G, accent.B), 0.55),
            new GradientStop(Color.FromArgb(0x00, accent.R, accent.G, accent.B), 1),
        },
    };
}