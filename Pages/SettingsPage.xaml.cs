using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ImpactProConfig.Pages;

public partial class SettingsPage : Page
{
    public SettingsPage()
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

    /// <summary>«Проверить OSD»: предпросмотр оверлея с текущими настройками.</summary>
    private void CheckOsd_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is MainWindow mw)
            mw.ViewModel.ShowStatusOsd();
    }

    /// <summary>Клик по пятну палитры: переключает акцентную тему.</summary>
    private void AccentSwatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int index } &&
            Application.Current.MainWindow is MainWindow mw)
        {
            mw.ViewModel.SelectedAccentIndex = index;
        }
    }

    /// <summary>«Сопряжение с донглом»: модальный диалог 2.4G Re-Pairing.</summary>
    private void Pairing_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current.MainWindow is not MainWindow mw)
            return;

        var dialog = new PairingDialog
        {
            Owner = mw,
            DataContext = mw.ViewModel
        };
        dialog.ShowDialog();
    }
}
