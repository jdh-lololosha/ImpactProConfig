using System;
using System.Windows;
using System.Windows.Threading;
using ImpactProConfig.Driver;
using ImpactProConfig.ViewModels;
using Wpf.Ui.Controls;

namespace ImpactProConfig;

/// <summary>
/// Диалог сопряжения мыши с USB-ресивером (2.4G Re-Pairing).
/// Два шага инструкции -> прогресс (30 с) -> итог. Вся работа идёт в
/// <see cref="MainViewModel.PairWithReceiverAsync"/> (фон, без блокировки UI).
/// </summary>
public partial class PairingDialog : FluentWindow
{
    private const int TimeoutSeconds = 30;

    private readonly DispatcherTimer _timer;
    private DateTime _startedAt;
    private bool _running;

    public PairingDialog()
    {
        InitializeComponent();

        // Детерминированный прогресс-бар: 0..100 за 30 секунд таймаута.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) =>
        {
            if (!_running)
                return;
            double elapsed = (DateTime.UtcNow - _startedAt).TotalSeconds;
            ProgressBar.Value = Math.Min(100, elapsed / TimeoutSeconds * 100.0);
        };

        // Закрытие крестиком на середине сопряжения: без этого таймер тикал бы
        // вечно, а лямбда продолжала бы держать закрытое окно и ProgressBar.
        Closed += (_, _) =>
        {
            _running = false;
            _timer.Stop();
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;

        if (!vm.CanPair)
        {
            ShowResult("Сопряжение недоступно: нужен ресивер и подключённая мышь ❌");
            return;
        }

        StepsPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        CloseButton.IsEnabled = false;
        ProgressText.Text = $"Ожидание подключения мыши (таймаут {TimeoutSeconds} сек)...";

        _running = true;
        _startedAt = DateTime.UtcNow;
        ProgressBar.Value = 0;
        _timer.Start();

        PairResult result;
        try
        {
            // Всё в фоне: DeviceSession сам опрашивает GetPairState раз в секунду.
            result = await vm.PairWithReceiverAsync();
        }
        finally
        {
            _running = false;
            _timer.Stop();
            CloseButton.IsEnabled = true;
        }

        ProgressPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;

        switch (result)
        {
            case PairResult.Success:
                // Статус в приложении уже «Подключено (ресивер)» — Rescan внутри VM.
                ResultText.Text = "Мышь успешно привязана! ✅";
                CloseButton.Content = "Готово";
                break;

            case PairResult.WrongMode:
                ResultText.Text = "Отключите кабель: сопряжение работает только через ресивер ❌";
                CloseButton.Content = "Повторить";
                break;

            case PairResult.NotConnected:
                ResultText.Text = "Мышь не подключена ❌";
                CloseButton.Content = "Повторить";
                break;

            case PairResult.Timeout:
                ResultText.Text = "Время вышло, повторите попытку ❌";
                CloseButton.Content = "Повторить";
                break;

            case PairResult.DeviceLost:
                ResultText.Text = "Ресивер перестал отвечать, повторите попытку ❌";
                CloseButton.Content = "Повторить";
                break;

            default:
                ResultText.Text = "Сопряжение не удалось, повторите попытку ❌";
                CloseButton.Content = "Повторить";
                break;
        }
    }

    private void ShowResult(string text)
    {
        StepsPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;
        ResultText.Text = text;
        CloseButton.IsEnabled = true;
        CloseButton.Content = "Повторить";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // «Повторить» — возврат к инструкции (шаг 1/2), иначе закрытие.
        if (CloseButton.Content as string == "Повторить")
        {
            ResultPanel.Visibility = Visibility.Collapsed;
            StepsPanel.Visibility = Visibility.Visible;
            CloseButton.Content = "Закрыть";
            CloseButton.IsEnabled = true;
            return;
        }

        Close();
    }
}
