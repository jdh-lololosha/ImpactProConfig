using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    /// <summary>Наведение на маркер — подсветка строки в списке.</summary>
    private void Marker_MouseEnter(object sender, MouseEventArgs e)
    {
        if (Vm != null && sender is Button { Tag: int number })
            Vm.SelectedButtonIndex = number;
    }

    /// <summary>Наведение на строку — подсветка маркера на корпусе.</summary>
    private void Row_MouseEnter(object sender, MouseEventArgs e)
    {
        if (Vm != null && sender is Border { Tag: int number })
            Vm.SelectedButtonIndex = number;
    }

    /// <summary>
    /// Аврора, слой 2: разгорается при наведении на корпус мыши.
    ///
    /// Ловим на MouseGrid, а не на самом эллипсе: эллипс занимает полосу в
    /// нижней части сцены, и курсор над корпусом его не пересекает. Grid
    /// покрывает всю сцену, поэтому реакция срабатывает там, где её ждёт
    /// пользователь.
    ///
    /// Здесь Storyboard из ресурсов, потому что он уже содержит EaseOut и
    /// согласованный с базовым слоем масштаб 1.08. Обратный ход — свой,
    /// с чуть большей длительностью (250 мс), чтобы уход был мягче прихода.
    /// </summary>
    private void MouseGrid_MouseEnter(object sender, MouseEventArgs e)
    {
        var sb = Application.Current?.FindResource("PodiumCharge") as Storyboard;
        if (sb != null && PodiumCharge != null)
        {
            // Повторный Begin без Stop перезапускает с текущих значений;
            // Stop гарантирует, что при быстром уходе-возврате состояние
            // не останется на середине.
            sb.Begin(PodiumCharge, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void MouseGrid_MouseLeave(object sender, MouseEventArgs e)
    {
        if (PodiumCharge is null) return;

        PodiumCharge.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(
            0, PodiumCharge.Opacity, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        });

        if (PodiumCharge.RenderTransform is ScaleTransform st)
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(
                1, st.ScaleX, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd,
            });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(
                1, st.ScaleY, TimeSpan.FromMilliseconds(250))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd,
            });
        }
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
