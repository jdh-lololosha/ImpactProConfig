using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ImpactProConfig.ViewModels;
using Microsoft.Win32;

namespace ImpactProConfig.Pages;

public partial class ButtonsPage : Page
{
    public ButtonsPage()
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

    // ---- Маркеры на изображении мыши ---------------------------------------
    // Позиции заморожены на уровне кода (MainViewModel, markerDefs):
    // перетаскивание удалено, buttons_layout.json не читается и не пишется.
    // Клик по маркеру — только выбор кнопки.

    /// <summary>Клик по номеру-маркеру на изображении мыши — выбор кнопки.</summary>
    private void Marker_Click(object sender, RoutedEventArgs e)
    {
        if (Vm != null && sender is Button { Tag: int number })
            Vm.SelectedButtonIndex = number;
    }

    /// <summary>Клик по строке списка — подсветка маркера.</summary>
    private void Row_Click(object sender, RoutedEventArgs e)
    {
        if (Vm != null && sender is Border { Tag: int number })
            Vm.SelectedButtonIndex = number;
    }

    private void Restore_Click(object sender, RoutedEventArgs e) => Vm?.DiscardChanges();

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null)
            return;
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт профиля",
            Filter = "Профиль Impact PRO (*.bin)|*.bin|Все файлы (*.*)|*.*",
            FileName = "ImpactPRO_profile.bin"
        };
        if (dialog.ShowDialog() == true)
            Vm.Export(dialog.FileName);
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null)
            return;
        var dialog = new OpenFileDialog
        {
            Title = "Импорт профиля",
            Filter = "Профиль Impact PRO (*.bin)|*.bin|Все файлы (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
            Vm.Import(dialog.FileName);
    }
}
