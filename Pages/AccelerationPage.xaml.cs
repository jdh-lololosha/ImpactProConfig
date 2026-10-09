using System.Windows;
using System.Windows.Controls;
using ImpactProConfig.ViewModels;

namespace ImpactProConfig.Pages;

/// <summary>
/// Вкладка «Акселерация». Управление драйвером Raw Accel и кривыми отклика.
///
/// Здесь используется ОТДЕЛЬНАЯ ViewModel (AccelerationViewModel), а не общий
/// MainViewModel: её состояние — статус драйвера в реестре, версия с GitHub и
/// черновик настроек кривой. В MainViewModel (уже 2200+ строк) этому не место,
/// и общий VM не должен знать про реестр HKLM и установщики драйверов.
/// </summary>
public partial class AccelerationPage : Page
{
    private AccelerationViewModel? _vm;

    public AccelerationPage()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private AccelerationViewModel? Vm => _vm;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Создаём VM здесь, а не в конструкторе: страница может быть
        // создана навигацией до готовности UI, и проверка реестра на
        // конструкторе задерживала бы отрисовку окна.
        _vm ??= new AccelerationViewModel();
        DataContext = _vm;

        _vm.RefreshStatus();

        // Проверку обновлений делаем после отрисовки, иначе окно открывается
        // с задержкой на сетевой запрос к api.github.com.
        await _vm.CheckDriverUpdatesAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // DataContext оставляем, но отцепляем от UI: иначе VM живёт до
        // пересоздания навигации и держит страницу живой (утечка).
        DataContext = null;
    }

    private async void InstallDriver_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        await Vm.InstallDriverAsync();
        // Установщик мог завершить установку — перечитываем статус.
        Vm.RefreshStatus();
    }

    private async void UpdateDriver_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;
        await Vm.InstallDriverAsync();
        Vm.RefreshStatus();
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Vm?.ApplySettings();
}