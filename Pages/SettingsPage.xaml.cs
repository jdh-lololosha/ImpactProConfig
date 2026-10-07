using System.Windows;
using System.Windows.Controls;

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
}
