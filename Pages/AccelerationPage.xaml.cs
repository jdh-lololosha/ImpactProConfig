using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ImpactProConfig.Controls;
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

    /// <summary>
    /// Раз в загрузку страницы отдаём графику формулу и тип кривой.
    ///
    /// Оба свойства — обычные CLR-свойства на FrameworkElement, а не
    /// DependencyProperty, поэтому обычной привязки XAML мало: WPF их не
    /// слушает. Ставим значения вручную, а перерисовку получаем через
    /// CurveRevision (он уже является DependencyProperty).
    ///
    /// Почему так, а не 10-ю DependencyProperty на каждый параметр: параметров
    /// кривой десять, но перерисовка нужна по факту изменения любого из них.
    /// Один счётчик проще и дешевле, а формула всё равно читается из VM целиком
    /// в момент перерисовки — рассинхронизации тут быть не может, потому что
    /// единственный источник значений один.
    /// </summary>
    private void SyncChart()
    {
        if (Vm is null) return;

        foreach (var chart in FindCharts(this))
        {
            chart.CurveMode = Vm.GraphMode;
            chart.CurveArgs = Vm.GraphArgs;
            chart.CurveColor = Vm.CurveColor;
            chart.MaxSpeed = 200;
            chart.MaxMultiplier = 2;
            chart.InvalidateVisual();
        }
    }

    private static IEnumerable<CurveChart> FindCharts(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is CurveChart chart)
            {
                yield return chart;
                yield break;   // на странице график ровно один
            }

            foreach (var nested in FindCharts(child))
            {
                yield return nested;
                yield break;
            }
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Создаём VM здесь, а не в конструкторе: страница может быть
        // создана навигацией до готовности UI, и проверка реестра на
        // конструкторе задерживала бы отрисовку окна.
        _vm ??= new AccelerationViewModel();
        DataContext = _vm;

        _vm.RefreshStatus();
        SyncChart();

        // Перерисовываем по сигналу VM. Подписка на PropertyChanged здесь, а не
        // в конструкторе: CurveRevision меняется при каждом движении ползунка,
        // и нам нужно только это событие — остальные 40 уведомлений VM
        // графику не нужны, но сработают и проверка if за O(1).
        _vm.PropertyChanged += OnVmPropertyChanged;

        // Проверку обновлений делаем после отрисовки, иначе окно открывается
        // с задержкой на сетевой запрос к api.github.com.
        await _vm.CheckDriverUpdatesAsync();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Счётчик приходит из VM; подробности формулы подтягиваем здесь же,
        // иначе на перерисовку уедут устаревшие значения.
        if (e.PropertyName == nameof(AccelerationViewModel.CurveRevision))
            SyncChart();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Отцепляемся от VM обязательно: подписка на PropertyChanged без
        // отписки — классическая утечка. Пока страница в кэше навигации,
        // VM держит ссылку на её обработчик и vice versa.
        if (_vm != null)
            _vm.PropertyChanged -= OnVmPropertyChanged;

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