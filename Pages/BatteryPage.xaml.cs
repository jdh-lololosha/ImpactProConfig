using System;
using System.Windows;
using System.Windows.Controls;
using ImpactProConfig.ViewModels;

namespace ImpactProConfig.Pages;

/// <summary>
/// Вкладка «Батарея»: график разряда и оценка времени работы по режимам.
///
/// Точки на график передаём вручную, а не привязкой: BatteryChart.Samples —
/// обычное CLR-свойство на FrameworkElement, WPF его не слушает. Перерисовку
/// отдаём через Revision (DependencyProperty), а список отдаём в момент
/// обновления, чтобы на отрисовку уехали актуальные точки.
/// </summary>
public partial class BatteryPage : Page
{
    private BatteryViewModel? _vm;
    private MainViewModel? _main;

    public BatteryPage()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private BatteryViewModel? Vm => _vm;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is not MainWindow mw)
            return;

        _main ??= mw.ViewModel;

        _vm ??= new BatteryViewModel(_main);
        DataContext = _vm;

        // По умолчанию выбран пункт «Последние 24 ч».
        if (WindowCombo.SelectedItem is null && WindowCombo.Items.Count > 0)
            WindowCombo.SelectedIndex = 0;

        PushSamples();

        // Пересчёт по событию батареи: она приходит раз в несколько минут,
        // график обязан подхватить новую точку.
        _main.PropertyChanged += OnMainPropertyChanged;

        // На всякий случай подтягиваем состояние при открытии вкладки.
        await Task.Yield();
        RefreshAll();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_main != null)
            _main.PropertyChanged -= OnMainPropertyChanged;

        DataContext = null;
    }

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Нас интересуют только заряд и состояние зарядки — они меняют и
        // текст, и оценки остатка. Остальные 40+ событий VM игнорируем.
        if (e.PropertyName == nameof(MainViewModel.BatteryPercent)
            || e.PropertyName == nameof(MainViewModel.IsCharging)
            || e.PropertyName == nameof(MainViewModel.BatteryStatsSamplesText))
        {
            RefreshAll();
        }
    }

    private void RefreshAll()
    {
        _vm?.Refresh();
        PushSamples();
    }

    private void PushSamples()
    {
        if (_vm is null) return;

        var snap = _vm.Samples;
        Chart.Samples = snap;
        Chart.LineColor = _vm.LineColor;
        Chart.WindowLabel = _vm.WindowLabel;

        // Цвет берём из темы: при смене акцента график должен совпасть.
        Chart.LineColor = _main?.AccentColorForCharts ?? Chart.LineColor;

        Chart.InvalidateVisual();
    }

    private void Window_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is null) return;
        if (WindowCombo.SelectedItem is not ComboBoxItem item) return;

        Vm.Window = (item.Tag as string) == "Week"
            ? TimeSpan.FromDays(7)
            : TimeSpan.FromHours(24);

        PushSamples();
    }
}