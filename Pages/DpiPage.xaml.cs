using System.Windows;
using System.Windows.Controls;
using ImpactProConfig.ViewModels;

namespace ImpactProConfig.Pages;

public partial class DpiPage : Page
{
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
        if (Vm != null && sender is Border { Tag: int slot })
            Vm.SelectedDpiIndex = slot;
    }

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
