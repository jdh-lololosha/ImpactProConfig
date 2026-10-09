using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using ImpactProConfig.Services;

namespace ImpactProConfig.ViewModels;

/// <summary>
/// Вкладка «Батарея»: график разряда и разделение расхода по нагрузке.
///
/// Отдельная ViewModel, а не дополнение MainViewModel: здесь своё окно
/// времени (24 ч / 7 дней), свои окна оценок и перерисовка по своему
/// счётчику. Данные берутся из общего BatteryStatsService через
/// MainViewModel, чтобы обе страницы писали в один файл.
///
/// ЧЕСТНОСТЬ ОЦЕНОК: каждая карточка показывает «нужно больше данных»,
/// пока для её режима не набралось хотя бы двух разрядных точек с нужной
/// частотой опроса. Догадки вроде «в игре батарея живёт 20 часов» на
/// новой установке были бы выдумкой, поэтому их нет.
/// </summary>
internal sealed class BatteryViewModel : INotifyPropertyChanged
{
    /// <summary>Окно по умолчанию: последние сутки.</summary>
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);

    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private readonly MainViewModel _main;

    private TimeSpan _window = Day;
    private int _revision;
    private bool _hasData;

    private string _currentPercent = "—";
    private string _currentState = string.Empty;
    private string _lastChargeText = "не было в истории";
    private string _cycleText = "нет цикла разряда";
    private string _samplesText = "0 замеров";
    private string _windowLabel = "последние 24 ч";

    private string _gamingRate = "нужно больше данных";
    private string _gamingLeft = string.Empty;
    private string _gamingHint = string.Empty;

    private string _normalRate = "нужно больше данных";
    private string _normalLeft = string.Empty;
    private string _normalHint = string.Empty;

    private string _idleLeft = string.Empty;
    private string _idleHint = string.Empty;

    public BatteryViewModel(MainViewModel main)
    {
        _main = main;
        Refresh();
    }

    /// <summary>
    /// Окно графика. null в XAML не берём: значение всегда задано здесь,
    /// а обработчик в code-behind меняет окно целиком.
    /// </summary>
    public TimeSpan Window
    {
        get => _window;
        set
        {
            TimeSpan w = value <= TimeSpan.Zero ? Day : value;
            if (_window == w) return;
            _window = w;
            OnPropertyChanged();
            OnPropertyChanged(nameof(WindowLabel));
            Refresh();
        }
    }

    public string WindowLabel => _windowLabel;

    /// <summary>Счётчик перерисовки графика.</summary>
    public int Revision
    {
        get => _revision;
        private set => Set(ref _revision, value);
    }

    public bool HasData
    {
        get => _hasData;
        private set => Set(ref _hasData, value);
    }

    public Color LineColor => _main.AccentColorForCharts;

    public string CurrentPercent
    {
        get => _currentPercent;
        private set => Set(ref _currentPercent, value);
    }

    public string CurrentState
    {
        get => _currentState;
        private set => Set(ref _currentState, value);
    }

    public string LastChargeText
    {
        get => _lastChargeText;
        private set => Set(ref _lastChargeText, value);
    }

    public string CycleText
    {
        get => _cycleText;
        private set => Set(ref _cycleText, value);
    }

    public string SamplesText
    {
        get => _samplesText;
        private set => Set(ref _samplesText, value);
    }

    // ---- карточки по режимам ----

    public string GamingRate
    {
        get => _gamingRate;
        private set => Set(ref _gamingRate, value);
    }

    public string GamingLeft
    {
        get => _gamingLeft;
        private set => Set(ref _gamingLeft, value);
    }

    public string GamingHint
    {
        get => _gamingHint;
        private set => Set(ref _gamingHint, value);
    }

    public string NormalRate
    {
        get => _normalRate;
        private set => Set(ref _normalRate, value);
    }

    public string NormalLeft
    {
        get => _normalLeft;
        private set => Set(ref _normalLeft, value);
    }

    public string NormalHint
    {
        get => _normalHint;
        private set => Set(ref _normalHint, value);
    }

    public string IdleLeft
    {
        get => _idleLeft;
        private set => Set(ref _idleLeft, value);
    }

    public string IdleHint
    {
        get => _idleHint;
        private set => Set(ref _idleHint, value);
    }

    /// <summary>Точки для графика. Отдаём снимок, а не ссылку на внутренний список.</summary>
    public IReadOnlyList<BatterySample> Samples { get; private set; } = Array.Empty<BatterySample>();

    /// <summary>Пересчёт всего: график, карточки, инфо о зарядке.</summary>
    public void Refresh()
    {
        var snap = _main.BatterySnapshotFor(_window);

        Samples = snap.Samples;
        HasData = snap.Samples.Count >= 2;

        _windowLabel = _window >= Week ? "последние 7 дней" : "последние 24 ч";
        OnPropertyChanged(nameof(WindowLabel));

        CurrentPercent = _main.BatteryPercent >= 0 ? $"{_main.BatteryPercent}%" : "—";
        CurrentState = _main.BatteryPercent < 0
            ? "мышь не подключена"
            : _main.IsCharging ? "зарядка" : "на батарее";

        SamplesText = $"{_main.BatteryStatsSamplesText} всего, {snap.Samples.Count} в окне";

        LastChargeText = FormatMoment(snap.LastChargeAt);
        CycleText = FormatCycle(snap.CurrentCycleStart);

        UpdateModeCard(LoadMode.Gaming, out var gRate, out var gLeft, out var gHint);
        GamingRate = gRate; GamingLeft = gLeft; GamingHint = gHint;

        UpdateModeCard(LoadMode.Normal, out var nRate, out var nLeft, out var nHint);
        NormalRate = nRate; NormalLeft = nLeft; NormalHint = nHint;

        UpdateIdleCard(snap);

        Revision++;
        OnPropertyChanged(nameof(Samples));
        OnPropertyChanged(nameof(HasData));
        OnPropertyChanged(nameof(LineColor));
    }

    /// <summary>
    /// Скорость и остаток для режима. Оценка остатка считается от текущего
    /// заряда, поэтому при полном заряде честно показывает больше времени.
    /// </summary>
    private void UpdateModeCard(LoadMode mode, out string rate, out string left, out string hint)
    {
        var est = _main.BatteryEstimateFor(mode);

        rate = est.PercentPerHour is double r
            ? $"~{r:0.0} %/ч"
            : "нужно больше данных";

        if (est.PercentPerHour is not double rateValue)
        {
            left = string.Empty;
            hint = est.SampleCount == 0
                ? "Замеров в этом режиме ещё не было."
                : $"Замеров: {est.SampleCount}. Нужно минимум два с падением заряда.";
            return;
        }

        int percent = _main.BatteryPercent;
        if (percent <= 0)
        {
            left = string.Empty;
            hint = "Подключите мышь, чтобы узнать остаток.";
            return;
        }

        double hours = percent / rateValue;
        left = hours >= 48
            ? $"Осталось: ~{hours / 24:0.0} дн"
            : $"Осталось: ~{hours:0} ч";
        hint = $"Замеров: {est.SampleCount} за {est.ObservedSpan.TotalHours:0.#} ч";
    }

    /// <summary>
    /// Режим ожидания считаем в днях: при простоях расход на порядок ниже,
    /// и «осталось ~400 ч игры» было бы бессмысленным числом.
    /// </summary>
    private void UpdateIdleCard(BatterySnapshot snap)
    {
        var est = _main.BatteryEstimateFor(LoadMode.Idle);

        if (est.PercentPerHour is double r && _main.BatteryPercent > 0)
        {
            double days = _main.BatteryPercent / r / 24;
            IdleLeft = $"Осталось: ~{days:0} дн";
            IdleHint = $"Простой ~{r:0.00} %/ч, замеров: {est.SampleCount}";
        }
        else
        {
            IdleLeft = string.Empty;
            IdleHint = est.SampleCount == 0
                ? "Нужны замеры в простое: оставьте мышь без движения."
                : $"Замеров: {est.SampleCount}. Нужно минимум два с падением заряда.";
        }
    }

    private static string FormatMoment(DateTimeOffset? at)
    {
        if (at is not DateTimeOffset t) return "не было в истории";

        DateTimeOffset local = t.ToLocalTime();
        TimeSpan ago = DateTimeOffset.Now - local;
        string when = ago.TotalHours < 24
            ? local.ToString("HH:mm")
            : local.ToString("dd.MM HH:mm");

        string rel = ago.TotalHours < 1
            ? $"{Math.Max(1, (int)ago.TotalMinutes)} мин назад"
            : ago.TotalHours < 48
                ? $"{(int)ago.TotalHours} ч назад"
                : $"{(int)ago.TotalDays} дн назад";

        return $"{when} ({rel})";
    }

    private static string FormatCycle(DateTimeOffset? start)
    {
        if (start is not DateTimeOffset t) return "нет цикла разряда";

        TimeSpan span = DateTimeOffset.Now - t.ToLocalTime();
        if (span < TimeSpan.Zero) return "нет цикла разряда";

        if (span.TotalMinutes < 60)
            return $"{(int)Math.Max(1, span.TotalMinutes)} мин с отключения кабеля";

        if (span.TotalHours < 48)
            return $"{(int)span.TotalHours} ч {span.Minutes} мин с отключения кабеля";

        return $"{(int)span.TotalDays} дн {(int)span.TotalHours % 24} ч с отключения кабеля";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}