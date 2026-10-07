using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImpactProConfig.Driver;
using ImpactProConfig.Services;
using Microsoft.Win32;

namespace ImpactProConfig.ViewModels;

/// <summary>
/// Единая viewmodel главного окна.
///
/// Модель данных:
///  - _baseline — последнее состояние, прочитанное/подтверждённое с мыши;
///  - _working  — редактируемая копия: только она пишется в мышь по «Применить»;
///  - поле _loading подавляет запись изменений во время загрузки UI из map.
///
/// Все USB-вызовы — в фоне (Task.Run), UI не блокируется. Запись во флеш
/// выполняется ТОЛЬКО в ApplyAsync (см. DeviceSession.WriteFlashAsync).
/// </summary>
/// <summary>Пятно акцентного цвета в палитре на странице настроек.</summary>
/// <param name="Index">Индекс пресета — его кладём в Tag и шлём в обработчик клика.</param>
public sealed record AccentSwatch(int Index, string Name, Brush Brush);

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DeviceSession _session;

    /// <summary>История разряда в battery_stats.json.</summary>
    private readonly BatteryStatsService _batteryStats = new();

    private FlashDataMap _baseline;
    private FlashDataMap _working;
    private bool _loading;              // перезагрузка UI из map — не помечаем «изменено»
    private bool _dirty;
    private int _deviceProfileIndex = -1;
    private bool _suspendProfileHandler;

    private string _statusText = "Поиск мыши…";
    private bool _isConnected;
    private bool _isBusy;

    private int _batteryPercent = -1;
    private bool _isCharging;

    private string _mouseVersionText = "—";
    private string _dongleVersionText = "—";

    private int _selectedProfileIndex;
    private int _selectedDpiIndex;
    private int _maxDpiLevels = 5;
    private int _selectedButtonIndex = 1;
    private int _rate125, _rate250, _rate500, _rate1000, _rate2000, _rate4000;   // 1 = выбрано
    private int _sensorModeIndex;
    private int _lodIndex;
    private bool _smoothEnable;
    private bool _angleFixEnable;
    private bool _motionSyncEnable;
    private int _ledModeIndex;
    private int _ledSpeed = 3;
    private int _ledBrightnessIndex = 5;
    private int _keyDebounceMs;
    private int _sleepIndex = -1;
    private bool _autostartEnabled;

    // Локальные настройки ПК (app_settings.json) + состояние Low Battery Toast.
    private LocalSettings _local = null!;
    private bool _lowBatteryAlertEnabled = true;
    private bool _lowBatteryNotified;

    // Настройки OSD-оверлея (индексы в options-массивах) + маска слотов кнопок под OSD.
    private int _osdMonitorIndex;
    private int _osdPositionIndex;
    private int _osdDurationIndex = 1;   // по умолчанию «2 сек»
    private int _osdSlotsMask;
    private string _ledStatusText = "—";
    private Brush _batteryBrush = Brushes.Gray;
    private int _accentIndex;
    private Brush _accentBrush = Brushes.IndianRed;
    private Brush _accentGlowBrush = Brushes.Transparent;
    private int _mouseSkinIndex;
    private ImageSource _mouseImage = null!;
    private string _mouseSkinName = MouseSkin.Items[0];

    private readonly DpiSlotViewModel[] _dpiSlots = new DpiSlotViewModel[8];
    private readonly ButtonSlotViewModel[] _buttonSlots = new ButtonSlotViewModel[6];

    // Профили официалки: customComboBox_Config = «Профиль 1..4», значения 0..3.
    public static readonly string[] Profiles = { "Профиль 1", "Профиль 2", "Профиль 3", "Профиль 4" };

    // «Спящий режим»: customComboBox_PowerSaveTime (FormMain:4989) и его
    // значения из языкового файла (единица — 10 секунд).
    public static readonly string[] SleepOptions =
        { "10 сек.", "30 сек.", "1 мин", "5 мин", "10 мин", "15 мин", "20 мин", "25 мин", "30 мин", "35 мин", "40 мин" };
    private static readonly int[] SleepValues = { 1, 3, 6, 30, 60, 90, 120, 150, 180, 210, 240 };

    // Параметры сенсора.
    public static readonly string[] SensorModeOptions = { "LP", "HP" };
    public static readonly string[] LodOptions = { "0.7 мм", "1 мм", "2 мм" };
    private static readonly byte[] LodValues = { 3, 1, 2 };   // языковой файл customComboBox_LOD

    // Подсветка DPI (customComboBox_DPIEffect): 0/1/2.
    public static readonly string[] LedModeOptions = { "Выключить", "Постоянный свет", "Дыхание" };

    // Количество активных уровней DPI (Config.ini: DPIMaxGrade=5).
    public static readonly int[] LevelOptions = { 1, 2, 3, 4, 5 };

    // Экземплярные ссылки для простых биндингов в XAML.
    public int[] LevelItems => LevelOptions;
    public string[] SensorModeItems => SensorModeOptions;
    public string[] LodItems => LodOptions;
    public string[] SleepItems => SleepOptions;

    // ===== Образ корпуса мыши =====

    public string[] MouseSkinItems => MouseSkin.Items;

    /// <summary>Выбранный образ корпуса; 0 = «Авто (по MID)».</summary>
    public int SelectedMouseSkinIndex
    {
        get => _mouseSkinIndex;
        set
        {
            if (!Set(ref _mouseSkinIndex, Math.Clamp(value, 0, MouseSkin.Items.Length - 1)))
                return;
            _local.MouseSkinIndex = _mouseSkinIndex;
            SaveLocal();
            ResolveSkin();
        }
    }

    /// <summary>Картинка корпуса для вкладки «Кнопки».</summary>
    public ImageSource MouseImage
    {
        get => _mouseImage;
        private set
        {
            if (Set(ref _mouseImage, value))
                OnPropertyChanged(nameof(MouseImageSize));
        }
    }

    /// <summary>Размер полотна образа — на нём заморожены координаты маркеров.</summary>
    public string MouseImageSize =>
        _mouseImage is not null ? $"{_mouseImage.Width:0}×{_mouseImage.Height:0}" : "—";

    /// <summary>Отображаемый вариант корпуса (может отличаться от выбранного при «Авто»).</summary>
    public string MouseSkinName
    {
        get => _mouseSkinName;
        private set => Set(ref _mouseSkinName, value);
    }

    private void ResolveSkin()
    {
        int resolved = _mouseSkinIndex;
        if (_mouseSkinIndex == MouseSkin.AutoIndex)
        {
            resolved = _autoResolvedSkin >= 0
                ? _autoResolvedSkin
                : MouseSkin.IndexFromMid(_deviceMid);

            // «Авто» ещё не знает MID (мышь не подключалась в этом запуске).
            // Раньше сюда уходил 0 и Load просил assets/dev0.png — такого файла
            // нет, и MainWindow падал на XamlParseException ещё до показа окна.
            // Падаем на dev3: это единственный реально наблюдавшийся вариант
            // (CID 16 / MID 6), а не произвольный дефолт.
            if (resolved <= 0)
            {
                App.Log("Skin: MID ещё неизвестен, временно показываем dev3");
                resolved = 3;
            }
        }

        MouseImage = MouseSkin.Load(resolved);
        MouseSkinName = MouseSkin.NameOf(resolved);
        OnPropertyChanged(nameof(SelectedMouseSkinIndex));
    }

    /// <summary>MID мыши (0 — неизвестен); приходит из DeviceSession, команда 16.</summary>
    private byte _deviceMid;
    private int _autoResolvedSkin = -1;

    // ===== Акцентная тема интерфейса =====

    public string[] AccentItems => ThemeManager.Presets.Select(p => p.Name).ToArray();

    /// <summary>Палитра для быстрого выбора темы: цвет-пятно + индекс пресета.</summary>
    public AccentSwatch[] AccentSwatches => _accentSwatches;

    private readonly AccentSwatch[] _accentSwatches =
        ThemeManager.Presets
            .Select((p, i) => new AccentSwatch(i, p.Name, new SolidColorBrush(p.Color)))
            .ToArray();

    /// <summary>Выбранный акцент; перекрашивает UI на лету.</summary>
    public int SelectedAccentIndex
    {
        get => _accentIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, ThemeManager.Presets.Length - 1);
            if (!Set(ref _accentIndex, clamped))
                return;
            ApplyAccent();
            _local.AccentIndex = clamped;
            SaveLocal();
        }
    }

    /// <summary>Акцентный цвет (плашки, бейдж несохранённых изменений).</summary>
    public Brush AccentBrush
    {
        get => _accentBrush;
        private set => Set(ref _accentBrush, value);
    }

    /// <summary>Радиальное свечение подиума под мышью — перекрашивается вместе с темой.</summary>
    public Brush AccentGlowBrush
    {
        get => _accentGlowBrush;
        private set => Set(ref _accentGlowBrush, value);
    }

    private void ApplyAccent()
    {
        var preset = ThemeManager.At(_accentIndex);
        try
        {
            ThemeManager.Apply(_accentIndex);
        }
        catch (Exception ex)
        {
            App.Log($"AccentApply: {ex.Message}");
        }

        AccentBrush = new SolidColorBrush(preset.Color);
        AccentGlowBrush = ThemeManager.PodiumGlow(preset.Color);
    }

    public MainViewModel()
    {
        for (int i = 0; i < _dpiSlots.Length; i++)
        {
            int idx = i;
            _dpiSlots[i] = new DpiSlotViewModel { SlotIndex = i, Edited = _ => OnDpiEdited(idx) };
        }

        // Маркеры 1..6 по изображению dev3.png (холст 432×356; центр маркера).
        // Слот 4 устройства = «Вперёд» (param1=0x10), слот 3 = «Назад» (0x08) —
        // так метки совпадают с назначением по умолчанию (KeyParam4/5 Config.ini).
        // ПОЗИЦИИ ЗАМОРОЖЕНЫ НА УРОВНЕ КОДА: правка только в исходниках, здесь.
        // buttons_layout.json не читается и не пишется — изменить снаружи нельзя,
        // перетаскивание в окне не подключено (см. ButtonsPage).
        var markerDefs = new (int marker, int slot, string name, double cx, double cy)[]
        {
            (1, 0, "Левая кнопка", 139, 179),
            (2, 1, "Правая кнопка", 79,  169),
            (3, 2, "Колесо",        130, 145),
            (4, 4, "Вперёд",        273, 165),
            (5, 3, "Назад",         307, 159),
            (6, 5, "DPI",           268, 241),
        };
        for (int i = 0; i < markerDefs.Length; i++)
        {
            var (marker, slot, name, cx, cy) = markerDefs[i];
            _buttonSlots[i] = new ButtonSlotViewModel
            {
                MarkerNumber = marker,
                DeviceSlot = slot,
                DisplayName = name,
                MarkerCenterX = cx,
                MarkerCenterY = cy,
                ActionChanged = OnButtonActionChanged
            };
        }

        _session = new DeviceSession();
        _session.StateChanged += OnSessionStateChanged;
        _session.FlashDataUpdated += OnFlashDataUpdated;
        _session.BatteryUpdated += OnBatteryUpdated;
        _session.ReportRateChanged += OnReportRateChanged;
        _session.CurrentDpiChanged += OnCurrentDpiChanged;
        _session.DpiLedUpdated += OnDpiLedUpdated;
        _session.ProfileChanged += OnProfileChanged;
        _session.DeviceInfoUpdated += OnDeviceInfoUpdated;

        // Состояние автозагрузки читаем из реестра сразу (локальная настройка ПК).
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            _autostartEnabled = key?.GetValue("ImpactProConfig") is string s && s.Length > 0;
        }
        catch
        {
            _autostartEnabled = false;
        }

        // Локальные настройки ПК: низкий заряд, OSD, маска OSD-слотов.
        _local = LocalSettingsStore.Load();
        _lowBatteryAlertEnabled = _local.LowBatteryAlertEnabled;
        _lowBatteryNotified = _local.LowBatteryNotified;
        _osdSlotsMask = _local.OsdSlotsMask;
        _osdPositionIndex = Math.Clamp(_local.OsdPositionIndex, 0, OsdPositionOptions.Length - 1);
        _osdDurationIndex = Math.Clamp(_local.OsdDurationIndex, 0, OsdDurationOptions.Length - 1);
        OsdMonitorItems = BuildOsdMonitorItems();
        _osdMonitorIndex = Math.Clamp(_local.OsdMonitorIndex, 0, OsdMonitorItems.Length - 1);

        // Акцент применяется последним: к этому моменту _local уже прочитан.
        _accentIndex = Math.Clamp(_local.AccentIndex, 0, ThemeManager.Presets.Length - 1);
        ApplyAccent();

        // Образ корпуса: сначала значение из настроек, «Авто» уточнится по MID.
        _mouseSkinIndex = Math.Clamp(_local.MouseSkinIndex, 0, MouseSkin.Items.Length - 1);
        ResolveSkin();
    }

    /// <summary>
    /// CID/MID от мыши (команда 16). По вендорской логике MID выбирает образ
    /// корпуса: 4 → dev1, 5 → dev2, 6 → dev3. Неизвестный MID не молча
    /// подменяем дефолтом — пишем в лог и оставляем текущий выбор.
    /// </summary>
    private void OnDeviceInfoUpdated(DeviceInfo info)
    {
        _deviceMid = info.MID;

        int fromMid = MouseSkin.IndexFromMid(info.MID);
        if (fromMid < 0)
        {
            App.Log($"Skin: неизвестный MID={info.MID} (ожидаются 4/5/6) — авто-выбор не сработал");
        }
        else
        {
            _autoResolvedSkin = fromMid;
        }

        if (_mouseSkinIndex == MouseSkin.AutoIndex)
            ResolveSkin();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    #region Публичные свойства

    public DpiSlotViewModel[] DpiSlots => _dpiSlots;
    public ButtonSlotViewModel[] ButtonSlots => _buttonSlots;

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (Set(ref _isConnected, value))
                RaiseCommandStates();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
                RaiseCommandStates();
        }
    }

    public bool HasUnsavedChanges
    {
        get => _dirty;
        private set
        {
            if (Set(ref _dirty, value))
                RaiseCommandStates();
        }
    }

    /// <summary>«Применить» доступно только при подключении и наличии изменений.</summary>
    public bool CanApply => IsConnected && HasUnsavedChanges && !IsBusy;

    /// <summary>Смена профиля блокируется при несохранённых изменениях.</summary>
    public bool CanSwitchProfile => IsConnected && !HasUnsavedChanges && !IsBusy;

    /// <summary>
    /// Сопряжение с донглом: только при живой сессии через ресивер —
    /// вендор (FormPair) не пускает в паринг по кабелю (isUSB -> Dialogs[46]).
    /// </summary>
    public bool CanPair => IsConnected && !_session.IsCable && !_session.PairingActive && !IsBusy;

    public int BatteryPercent
    {
        get => _batteryPercent;
        private set => Set(ref _batteryPercent, value);
    }

    public bool IsCharging
    {
        get => _isCharging;
        private set => Set(ref _isCharging, value);
    }

    public Brush BatteryBrush
    {
        get => _batteryBrush;
        private set => Set(ref _batteryBrush, value);
    }

    public string BatteryText => _batteryPercent < 0
        ? "—"
        : $"{_batteryPercent}%" + (_isCharging ? " ⚡" : "");

    /// <summary>Тип подключения: true — приёмник 2.4G, false — кабель Type-C.</summary>
    public bool IsWireless => _session.ConnectedPid == DeviceSession.ReceiverPid;

    /// <summary>true — активен кабель (провод): зарядка и приоритетный канал.</summary>
    public bool IsWired => _session.IsCable;

    /// <summary>Человекочитаемый статус подключения для статус-бара.</summary>
    public string ConnectionText => _session.ConnectedPid switch
    {
        null => _session.State == ConnectionState.Reconnecting
            ? "Переподключение…"
            : "Отключено",
        DeviceSession.CablePid => "Подключено (провод)",
        DeviceSession.ReceiverPid => "Подключено (ресивер)",
        _ => "Подключено"
    };

    /// <summary>Максимальная частота опроса для текущего подключения.</summary>
    public REPORT_RATE MaxReportRate => _session.MaxReportRate;

    /// <summary>Доступна ли частота выше 1000 Гц (только на проводе).</summary>
    public bool HighRatesAllowed => _session.IsCable;

    // ===== Телеметрия батареи =====

    /// <summary>Скорость разряда, напр. «6.4 %/ч».</summary>
    public string BatteryDrainText => _batteryStats.DrainText;

    /// <summary>Примерное время работы до нуля, напр. «~12 ч активной игры».</summary>
    public string BatteryHoursLeftText =>
        _batteryStats.HoursLeftText(_batteryPercent);

    /// <summary>Сколько точек уже накоплено — видно, что статистика не пустая.</summary>
    public string BatteryStatsSamplesText => $"{_batteryStats.SampleCount} замеров";

    /// <summary>Хватает ли истории для честной оценки.</summary>
    public bool HasBatteryStats => _batteryStats.HasEnoughData;

    // ===== Проверка обновлений =====

    /// <summary>Показать ли плашку обновления (есть релиз новее текущей версии).</summary>
    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        private set => Set(ref _isUpdateAvailable, value);
    }

    /// <summary>Текст плашки, напр. «v1.2.0 (у вас 1.1.0)».</summary>
    public string UpdateMessage
    {
        get => _updateMessage;
        private set => Set(ref _updateMessage, value);
    }

    /// <summary>Ссылка на .zip из релиза (null — ассет приложен не был).</summary>
    public string? UpdateDownloadUrl { get; private set; }

    /// <summary>Просьба закрыть приложение (после запуска портативного обновления).</summary>
    public event Action? ExitRequested;

    private bool _isUpdateAvailable;
    private string _updateMessage = string.Empty;

    /// <summary>
    /// Фоновая проверка GitHub Releases. Вызывается один раз при старте и в
    /// фоне: сеть не должна блокировать окно. Ошибка сети молча игнорируется.
    /// </summary>
    public async Task CheckForUpdatesAsync()
    {
        var info = await new UpdateService().CheckAsync();
        if (info is not { Available: true })
            return;

        // Релиз без .zip показать можно, но кнопка «Скачать» поведёт в никуда —
        // в этом случае молча выходим, лучше без плашки.
        if (string.IsNullOrEmpty(info.Url))
        {
            App.Log($"UpdateCheck: найден {info.Tag}, но .zip в релизе нет");
            return;
        }

        UpdateDownloadUrl = info.Url;
        UpdateMessage = $"{info.Tag} (у вас {UpdateService.CurrentVersion.ToString(3)})";
        IsUpdateAvailable = true;
    }

    /// <summary>
    /// Портативное обновление: скачать .zip, запустить отдельный Updater.exe
    /// и выйти — тот дождётся выхода, заменит файлы и перезапустит exe.
    /// </summary>
    public async Task InstallUpdateAsync()
    {
        string? url = UpdateDownloadUrl;
        if (string.IsNullOrEmpty(url))
            return;

        IsBusy = true;
        StatusText = "Загрузка обновления…";
        try
        {
            await UpdateService.DownloadAndUpdateAsync(url);
            StatusText = "Обновление скачано. Перезапуск…";
            ExitRequested?.Invoke();
        }
        catch (Exception ex)
        {
            App.Log($"UpdateInstall: {ex.Message}");
            StatusText = $"Не удалось скачать обновление: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public string MouseVersionText
    {
        get => _mouseVersionText;
        private set => Set(ref _mouseVersionText, value);
    }

    public string DongleVersionText
    {
        get => _dongleVersionText;
        private set => Set(ref _dongleVersionText, value);
    }

    public int SelectedProfileIndex
    {
        get => _selectedProfileIndex;
        set
        {
            if (_suspendProfileHandler)
            {
                Set(ref _selectedProfileIndex, value);
                return;
            }
            if (value == _selectedProfileIndex)
                return;

            int requested = value;
            if (!CanSwitchProfile)
            {
                RevertProfileSelection();
                if (HasUnsavedChanges)
                    StatusText = "Сначала примените или отмените изменения — профиль не переключён.";
                return;
            }

            Set(ref _selectedProfileIndex, value);
            _ = SwitchProfileAsync(requested);
        }
    }

    public int SelectedDpiIndex
    {
        get => _selectedDpiIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, Math.Max(0, _maxDpiLevels - 1));
            if (!Set(ref _selectedDpiIndex, clamped))
                return;
            for (int i = 0; i < _dpiSlots.Length; i++)
                _dpiSlots[i].IsSelected = i == clamped;
            OnPropertyChanged(nameof(SelectedDpiValue));
        }
    }

    /// <summary>DPI выбранной ступени (слайдер/NumberBox). Шаг 50, диапазон 50..26000
    /// из Config.ini DPIRange=50,26000,50.</summary>
    public int SelectedDpiValue
    {
        get => _dpiSlots[_selectedDpiIndex].DpiValue;
        set
        {
            if (_loading)
                return;
            _dpiSlots[_selectedDpiIndex].DpiValue = value;
        }
    }

    public int MaxDpiLevels
    {
        get => _maxDpiLevels;
        set
        {
            int clamped = Math.Clamp(value, 1, LevelOptions.Length);
            if (!Set(ref _maxDpiLevels, clamped))
                return;

            if (!_loading)
            {
                _working.mouseConfig.maxDPI = (byte)clamped;
                MarkDirty();
                if (_selectedDpiIndex >= clamped)
                    SelectedDpiIndex = clamped - 1;
            }

            UpdateLevelVisibility();
            OnPropertyChanged(nameof(SelectedLevelIndex));
        }
    }

    /// <summary>Индекс для ComboBox «Уровни DPI» (0..4 ↔ 1..5).</summary>
    public int SelectedLevelIndex
    {
        get => _maxDpiLevels - 1;
        set => MaxDpiLevels = value + 1;
    }

    // ===== Частота опроса (REPORT_RATE: 1/2/4/8) =====

    public bool IsRate125
    {
        get => _rate125 != 0;
        set => SetRate(ref _rate125, value, REPORT_RATE.R_125);
    }

    public bool IsRate250
    {
        get => _rate250 != 0;
        set => SetRate(ref _rate250, value, REPORT_RATE.R_250);
    }

    public bool IsRate500
    {
        get => _rate500 != 0;
        set => SetRate(ref _rate500, value, REPORT_RATE.R_500);
    }

    public bool IsRate1000
    {
        get => _rate1000 != 0;
        set => SetRate(ref _rate1000, value, REPORT_RATE.R_1000);
    }

    public bool IsRate2000
    {
        get => _rate2000 != 0;
        set => SetRate(ref _rate2000, value, REPORT_RATE.R_2000);
    }

    public bool IsRate4000
    {
        get => _rate4000 != 0;
        set => SetRate(ref _rate4000, value, REPORT_RATE.R_4000);
    }

    // ===== Параметры сенсора =====

    public int SensorModeIndex
    {
        get => _sensorModeIndex;
        set
        {
            if (!Set(ref _sensorModeIndex, Math.Clamp(value, 0, 1)) || _loading)
                return;
            _working.mouseConfig.sensorPowerSavingModeEnable = (byte)_sensorModeIndex;
            MarkDirty();
        }
    }

    public int LodIndex
    {
        get => _lodIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, LodOptions.Length - 1);
            if (!Set(ref _lodIndex, clamped) || _loading)
                return;
            _working.mouseConfig.silenceHeight = LodValues[clamped];
            MarkDirty();
        }
    }

    public bool SmoothEnable
    {
        get => _smoothEnable;
        set
        {
            if (!Set(ref _smoothEnable, value) || _loading)
                return;
            _working.mouseConfig.rippleControlEnable = (byte)(value ? 1 : 0);
            MarkDirty();
        }
    }

    public bool AngleFixEnable
    {
        get => _angleFixEnable;
        set
        {
            if (!Set(ref _angleFixEnable, value) || _loading)
                return;
            _working.mouseConfig.linearCorrectionEnable = (byte)(value ? 1 : 0);
            MarkDirty();
        }
    }

    public bool MotionSyncEnable
    {
        get => _motionSyncEnable;
        set
        {
            if (!Set(ref _motionSyncEnable, value) || _loading)
                return;
            _working.mouseConfig.motionSyncEnable = (byte)(value ? 1 : 0);
            MarkDirty();
        }
    }

    // ===== Подсветка DPI =====

    public int LedModeIndex
    {
        get => _ledModeIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, LedModeOptions.Length - 1);
            if (!Set(ref _ledModeIndex, clamped) || _loading)
                return;

            // Вендор (_3In1:556-561): индекс 0 -> enable=0; иначе enable=1, mode=индекс.
            if (clamped == 0)
                _working.dpiLed.enable = 0;
            else
            {
                _working.dpiLed.enable = 1;
                _working.dpiLed.mode = (byte)clamped;
            }
            MarkDirty();
            UpdateLedStatusText();
            OnPropertyChanged(nameof(IsLedOff));
            OnPropertyChanged(nameof(IsLedAlways));
            OnPropertyChanged(nameof(IsLedBreath));
        }
    }

    // Три RadioButtons «Выключить / Постоянный свет / Дыхание».
    public bool IsLedOff
    {
        get => _ledModeIndex == 0;
        set { if (value) LedModeIndex = 0; }
    }

    public bool IsLedAlways
    {
        get => _ledModeIndex == 1;
        set { if (value) LedModeIndex = 1; }
    }

    public bool IsLedBreath
    {
        get => _ledModeIndex == 2;
        set { if (value) LedModeIndex = 2; }
    }

    public int LedSpeed
    {
        get => _ledSpeed;
        set
        {
            int clamped = Math.Clamp(value, 1, 5);
            if (!Set(ref _ledSpeed, clamped) || _loading)
                return;
            _working.dpiLed.breathSpeed = (byte)clamped;   // вендор пишет значение напрямую
            MarkDirty();
            UpdateLedStatusText();
        }
    }

    public int LedBrightnessIndex
    {
        get => _ledBrightnessIndex;
        set
        {
            int clamped = Math.Clamp(value, 1, 10);
            if (!Set(ref _ledBrightnessIndex, clamped) || _loading)
                return;
            _working.dpiLed.brightness = BrightnessIndexToByte(clamped);
            MarkDirty();
            UpdateLedStatusText();
        }
    }

    public string LedStatusText
    {
        get => _ledStatusText;
        private set => Set(ref _ledStatusText, value);
    }

    // ===== Кнопки =====

    public int SelectedButtonIndex
    {
        get => _selectedButtonIndex;
        set
        {
            if (!Set(ref _selectedButtonIndex, value))
                return;
            foreach (var slot in _buttonSlots)
                slot.IsSelected = slot.MarkerNumber == value;
        }
    }

    // --- Позиции маркеров: заморозка на уровне кода ---------------------------
    // Загрузка/сохранение buttons_layout.json, FileSystemWatcher и перетаскивание
    // удалены: координаты задаются только константами markerDefs в конструкторе.

    /// <summary>Время отклика кнопок, мс (языковой файл: 0..20 мс; Config.ini: 8).</summary>
    public int KeyDebounceMs
    {
        get => _keyDebounceMs;
        set
        {
            int clamped = Math.Clamp(value, 0, 20);
            if (!Set(ref _keyDebounceMs, clamped) || _loading)
                return;
            _working.mouseConfig.keyDebounceTime = (byte)clamped;
            MarkDirty();
        }
    }

    // ===== Настройки =====

    public int SleepSelectedIndex
    {
        get => _sleepIndex;
        set
        {
            int clamped = value < 0 ? -1 : Math.Clamp(value, 0, SleepOptions.Length - 1);
            if (!Set(ref _sleepIndex, clamped) || _loading || clamped < 0)
                return;
            _working.mouseConfig.allLedOffTime = (byte)SleepValues[clamped];
            MarkDirty();
        }
    }

    public bool AutostartEnabled
    {
        get => _autostartEnabled;
        set
        {
            if (!Set(ref _autostartEnabled, value))
                return;
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run");
                if (value)
                {
                    string exe = Environment.ProcessPath ?? "";
                    if (exe.Length > 0)
                        key.SetValue("ImpactProConfig", $"\"{exe}\"");
                }
                else
                {
                    key.DeleteValue("ImpactProConfig", false);
                }
                StatusText = value
                    ? "Автозагрузка включена (изменение применяется сразу)."
                    : "Автозагрузка выключена (изменение применяется сразу).";
            }
            catch (Exception ex)
            {
                StatusText = $"Автозагрузка: {ex.Message}";
            }
        }
    }

    /// <summary>Тумблер «Уведомлять о низком заряде батареи (&lt; 15%)» — локальная настройка ПК.</summary>
    public bool LowBatteryAlertEnabled
    {
        get => _lowBatteryAlertEnabled;
        set
        {
            if (!Set(ref _lowBatteryAlertEnabled, value))
                return;
            _local.LowBatteryAlertEnabled = value;
            SaveLocal();
            // Включение при уже низком заряде — проверяем сразу, не дожидаясь опроса.
            if (value)
                CheckLowBatteryAlert();
        }
    }

    // --- Настройки OSD-оверлея (вкладка «Настройки») ---

    public static readonly string[] OsdPositionOptions =
        { "Сверху справа", "Сверху по центру", "Снизу справа", "По центру" };

    public static readonly string[] OsdDurationOptions =
        { "1 сек", "2 сек", "3 сек", "5 сек" };

    private static readonly int[] OsdDurationMs = { 1000, 2000, 3000, 5000 };

    public string[] OsdMonitorItems { get; private set; } = { "Основной монитор" };
    public string[] OsdPositionItems => OsdPositionOptions;
    public string[] OsdDurationItems => OsdDurationOptions;

    public int OsdMonitorIndex
    {
        get => _osdMonitorIndex;
        set
        {
            if (Set(ref _osdMonitorIndex, value))
            {
                _local.OsdMonitorIndex = value;
                SaveLocal();
            }
        }
    }

    public int OsdPositionIndex
    {
        get => _osdPositionIndex;
        set
        {
            if (Set(ref _osdPositionIndex, value))
            {
                _local.OsdPositionIndex = value;
                SaveLocal();
            }
        }
    }

    public int OsdDurationIndex
    {
        get => _osdDurationIndex;
        set
        {
            if (Set(ref _osdDurationIndex, value))
            {
                _local.OsdDurationIndex = value;
                SaveLocal();
            }
        }
    }

    #endregion

    #region Инициализация (ТОЛЬКО чтение)

    /// <summary>Старт: только безопасное чтение, в фоне, без записи во флеш.</summary>
    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _session.ConnectReadOnlyAsync();
            if (result == null)
            {
                // Возможна гонка: сессию параллельно подключили watchdog или
                // USB-событие — тогда статус уже «Подключено», не «не найдена».
                if (_session.State == ConnectionState.ConnectedReadOnly)
                {
                    StatusText = ConnectionText;
                    _ = RefreshDongleVersionAsync();
                    return;
                }

                StatusText = "Мышь не найдена. Подключите кабель или ресивер.";
                return;
            }

            ApplyBattery(result.Battery);
            MouseVersionText = FormatVersion(result.Version);
            StatusText = ConnectionText;   // «Подключено (провод)» / «Подключено (ресивер)»

            _ = RefreshDongleVersionAsync();
        }
        catch (DllNotFoundException)
        {
            StatusText = "hidusb.dll не найден рядом с приложением.";
        }
        catch (Exception ex)
        {
            StatusText = $"Ошибка: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshDongleVersionAsync()
    {
        string? endpoint = _session.Endpoint;
        if (endpoint == null)
            return;
        try
        {
            var text = await Task.Run(() =>
            {
                if (HidUsbNative.TryGetSlaveVersion(endpoint, out int slaveVersion))
                    return FormatVersion(slaveVersion);
                return "—";
            });
            DongleVersionText = text;
        }
        catch
        {
            DongleVersionText = "—";
        }
    }

    // ===== Колбэки сессии (фон) =====

    private void OnSessionStateChanged(ConnectionState state) => RunOnUi(() =>
    {
        IsConnected = state == ConnectionState.ConnectedReadOnly;
        if (state == ConnectionState.Disconnected)
            StatusText = "Мышь отключена.";
        else if (state == ConnectionState.Connecting)
            StatusText = "Подключение…";
        else if (state == ConnectionState.Reconnecting)
            StatusText = "Переподключение…";
        else if (state == ConnectionState.Error)
            StatusText = "Таймаут чтения состояния мыши.";
        else if (state == ConnectionState.ConnectedReadOnly)
            StatusText = ConnectionText;   // «Подключено (провод)» / «Подключено (ресивер)»

        // Мгновенное обновление статус-бара и шапки при горячем переключении.
        OnPropertyChanged(nameof(ConnectionText));
        OnPropertyChanged(nameof(IsWireless));
        OnPropertyChanged(nameof(IsWired));
        OnPropertyChanged(nameof(HighRatesAllowed));
        OnPropertyChanged(nameof(MaxReportRate));

        // На ресивере зарядки нет: убираем ⚡ сразу, не дожидаясь свежего
        // battery-push (иначе индикатор залипает от прошлой сессии на проводе).
        if (state == ConnectionState.ConnectedReadOnly && !_session.IsCable && _isCharging)
        {
            IsCharging = false;
            OnPropertyChanged(nameof(BatteryText));
        }

        EnforceRateLimit();
    });

    /// <summary>
    /// Ограничение частоты опроса под текущее подключение.
    /// На ресивере выше 1000 Гц не пускаем; если в флеше записано больше —
    /// принудительно понижаем до 1000 Гц (без записи в мышь, только в UI).
    /// </summary>
    private void EnforceRateLimit()
    {
        if (_session.IsCable)
            return;

        byte current = _working.mouseConfig.reportRate;
        if (current == (byte)REPORT_RATE.R_2000 || current == (byte)REPORT_RATE.R_4000)
        {
            App.Log($"RateLimit: ресивер не поддерживает {current} -> понижаем до 1000 Гц");
            _working.mouseConfig.reportRate = (byte)REPORT_RATE.R_1000;
            Set(ref _rate1000, 1, nameof(IsRate1000));
            Set(ref _rate2000, 0, nameof(IsRate2000));
            Set(ref _rate4000, 0, nameof(IsRate4000));
        }
    }

    /// <summary>
    /// Быстрый перескан устройств (хот-плаг). Вызывается из MainWindow по
    /// WM_DEVICECHANGE. Сбрасывает memo OFFLINE и запускает честный перескан
    /// с приоритетом кабеля.
    /// </summary>
    public async Task RescanAsync()
    {
        await _session.RescanAfterUsbEventAsync();
    }

    /// <summary>
    /// Сопряжение мыши с ресивером (2.4G Re-Pairing). Всё в фоне,
    /// таймаут 30 с — внутри DeviceSession. При успехме перескан обновляет
    /// сессию/статус на «Подключено (ресивер)».
    /// </summary>
    public async Task<PairResult> PairWithReceiverAsync()
    {
        IsBusy = true;
        try
        {
            StatusText = "Сопряжение с донглом…";
            var result = await Task.Run(() => _session.PairWithReceiverAsync());

            if (result == PairResult.Success)
            {
                // Мышь уже перепривязана — перескан подтвердит сессию
                // и статус-бар обновится на «Подключено (ресивер)».
                await _session.RescanAsync();
                StatusText = ConnectionText;
            }
            else if (result != PairResult.Cancelled)
            {
                StatusText = ConnectionText;
            }

            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnFlashDataUpdated(FlashDataMap map) => RunOnUi(() =>
    {
        // Любое перечитывание флеша (старт, «Применить», смена профиля)
        // заменяет и базовую, и рабочую копию — состояние устройства авторитетно.
        _baseline = FlashDataMap.Clone(map);
        _working = FlashDataMap.Clone(map);
        ReloadUiFromWorking();
        HasUnsavedChanges = false;
    });

    private void OnBatteryUpdated(BatteryStatus bat) => RunOnUi(() => ApplyBattery(bat));

    private void OnReportRateChanged(byte rate) => RunOnUi(() =>
    {
        _baseline.mouseConfig.reportRate = rate;
        _working.mouseConfig.reportRate = rate;
        LoadRateUi(rate);
    });

    private void OnCurrentDpiChanged(byte idx) => RunOnUi(() =>
    {
        _baseline.mouseConfig.currentDPI = idx;
        _working.mouseConfig.currentDPI = idx;
        for (int i = 0; i < _dpiSlots.Length; i++)
            _dpiSlots[i].IsActiveByDevice = i == idx;
    });

    private void OnDpiLedUpdated(DPILed led) => RunOnUi(() =>
    {
        _baseline.dpiLed = led;
        _working.dpiLed = led;
        LoadLedUi();
    });

    private void OnProfileChanged(byte index) => RunOnUi(() =>
    {
        _deviceProfileIndex = index;
        _suspendProfileHandler = true;
        try
        {
            Set(ref _selectedProfileIndex, index, nameof(SelectedProfileIndex));
        }
        finally
        {
            _suspendProfileHandler = false;
        }
        OnPropertyChanged(nameof(CanSwitchProfile));
    });

    private void ApplyBattery(BatteryStatus bat)
    {
        BatteryPercent = bat.level;
        IsCharging = bat.isCharging != 0;
        BatteryBrush = bat.level > 50 ? GreenBrush
            : bat.level > 20 ? OrangeBrush
            : RedBrush;
        OnPropertyChanged(nameof(BatteryText));

        _batteryStats.Record(bat.level, bat.isCharging != 0);
        RaiseBatteryStats();

        CheckLowBatteryAlert();
    }

    /// <summary>Обновить карточку статистики батареи.</summary>
    private void RaiseBatteryStats()
    {
        OnPropertyChanged(nameof(BatteryDrainText));
        OnPropertyChanged(nameof(BatteryHoursLeftText));
        OnPropertyChanged(nameof(BatteryStatsSamplesText));
        OnPropertyChanged(nameof(HasBatteryStats));
    }

    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xC7, 0x11));
    private static readonly Brush OrangeBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23));
    private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xE3, 0x04, 0x13));

    // ===== Low Battery Toast: оповещение о низком заряде =====

    /// <summary>Сохранение локальных настроек ПК (app_settings.json — не флеш мыши).</summary>
    private void SaveLocal() => LocalSettingsStore.Save(_local);

    /// <summary>
    /// Тост при заряде &lt; 15% и незаряженной мыши. Анти-спам: строго ОДИН РАЗ
    /// за цикл разряда — флаг сбрасывается только если мышь зарядилась выше 20%
    /// или подключён провод.
    /// </summary>
    private void CheckLowBatteryAlert()
    {
        int level = _batteryPercent;   // -1 — данных ещё нет
        if (level < 0)
            return;

        if (_isCharging || level > 20)
        {
            if (_lowBatteryNotified)
            {
                _lowBatteryNotified = false;
                _local.LowBatteryNotified = false;
                SaveLocal();
            }
            return;
        }

        if (!_lowBatteryAlertEnabled || _lowBatteryNotified || level >= 15)
            return;

        _lowBatteryNotified = true;
        _local.LowBatteryNotified = true;
        SaveLocal();
        ToastHelper.Show(
            "ARDOR GAMING Impact PRO",
            $"Низкий уровень заряда ({level}%). Подключите кабель для зарядки.");
    }

    // ===== OSD-оверлей: показ статуса мыши =====

    /// <summary>Показать OSD с текущим статусом («Проверить OSD» и физическая кнопка мыши).</summary>
    public void ShowStatusOsd()
    {
        int level = _batteryPercent;
        bool charging = _isCharging;
        string connection = _session.ConnectedPid == DeviceSession.ReceiverPid
            ? "📡 Беспроводной (2.4G)"
            : _session.ConnectedPid != null
                ? "🔌 Кабель (Type-C)"
                : "Нет подключения";

        var info = new OsdInfo(
            Battery: level < 0 ? "🔋 —" : $"🔋 {level}%",
            Status: charging ? "⚡ Заряжается" : "Работает от батареи",
            Connection: connection,
            Runtime: level < 0 ? "" : $"~{EstimateRuntimeHours(level)} ч работы",
            Charging: charging);

        var monitors = Monitors.All();
        if (monitors.Count == 0)
            return;

        OsdWindow.ShowOverlay(
            info,
            monitors[Math.Clamp(_osdMonitorIndex, 0, monitors.Count - 1)],
            Math.Clamp(_osdPositionIndex, 0, OsdPositionOptions.Length - 1),
            OsdDurationMs[Math.Clamp(_osdDurationIndex, 0, OsdDurationMs.Length - 1)]);
    }

    /// <summary>
    /// Колбэк глобального хука мыши: usage нажатой кнопки → показать OSD?
    /// true — съесть событие (ОС его не увидит). ЛКМ/ПКМ не блокируем,
    /// чтобы не сломать обычное управление.
    /// </summary>
    public bool OsdHookHandler(int mouseKeyMask)
    {
        if (!ShouldShowOsdForUsage(mouseKeyMask))
            return false;
        RunOnUi(ShowStatusOsd);
        return mouseKeyMask is not ((int)MouseKey.LeftKey or (int)MouseKey.RightKey);
    }

    /// <summary>
    /// Назначен ли OSD слоту с этим usage: ожидаем «родную» кнопку слота
    /// (маска — локальное состояние, от байтов _working не зависит).
    /// </summary>
    private bool ShouldShowOsdForUsage(int mouseKeyMask)
    {
        if (_osdSlotsMask == 0)
            return false;
        for (int i = 0; i < _buttonSlots.Length; i++)
        {
            int dev = _buttonSlots[i].DeviceSlot;
            if ((_osdSlotsMask & (1 << i)) == 0 || dev == 5)
                continue;   // слот 5 (DPI): физическая кнопка OS-событий не шлёт
            if (NativeMouseKey(dev) == mouseKeyMask)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Прогноз остатка работы: база (часы при 100%) зависит от частоты опроса,
    /// дальше — пропорционально текущему проценту заряда.
    /// </summary>
    private int EstimateRuntimeHours(int level)
    {
        // FlashDataMap — структура: до первой загрузки это default (rate=0 → ветка _ => 55).
        byte rate = _working.mouseConfig.reportRate;
        int full = rate switch
        {
            (byte)REPORT_RATE.R_125 => 110,
            (byte)REPORT_RATE.R_250 => 90,
            (byte)REPORT_RATE.R_500 => 70,
            (byte)REPORT_RATE.R_1000 => 55,
            (byte)REPORT_RATE.R_2000 => 40,
            (byte)REPORT_RATE.R_4000 => 28,
            _ => 55
        };
        return Math.Max(1, (int)Math.Round(full * Math.Clamp(level, 0, 100) / 100.0));
    }

    /// <summary>
    /// Колбэк глобального хука клавиатуры (WH_KEYBOARD_LL): мышь шлёт F24 вместо
    /// назначенной кнопки — глушим клавишу и показываем оверлей.
    /// </summary>
    public bool OsdKeyHookHandler(int vk)
    {
        if (_osdSlotsMask == 0)
            return false;   // OSD никому не назначен — F24 проходит как есть
        App.Log($"KB: F24 перехвачен → OSD (mask={_osdSlotsMask})");
        RunOnUi(ShowStatusOsd);
        return true;
    }

    private static string[] BuildOsdMonitorItems()
    {
        var monitors = Monitors.All();
        App.Log(monitors.Count == 0
            ? "MONITORS: EnumDisplayMonitors не вернул экранов"
            : "MONITORS: " + string.Join(" | ", monitors.Select(m => m.Name)));
        return monitors.Count > 0
            ? monitors.Select(m => m.Name).ToArray()
            : new[] { "Монитор 1 (Основной)" };
    }

    #endregion

    #region Загрузка UI из рабочей копии

    private void ReloadUiFromWorking()
    {
        _loading = true;
        try
        {
            var map = _working;

            // DPI-ступени. Чтение — по образцу DPIControl:368-411.
            for (int i = 0; i < _dpiSlots.Length; i++)
            {
                var d = map.dpiConfig != null && i < map.dpiConfig.Length ? map.dpiConfig[i] : default;
                _dpiSlots[i].Load(d.xDPI, d.DPIex, d.color,
                    levelActive: i < map.mouseConfig.maxDPI,
                    activeByDevice: i == map.mouseConfig.currentDPI);
            }

            Set(ref _maxDpiLevels, Math.Clamp(map.mouseConfig.maxDPI, 1, LevelOptions.Length), nameof(MaxDpiLevels));
            OnPropertyChanged(nameof(SelectedLevelIndex));
            UpdateLevelVisibility();

            _selectedDpiIndex = Math.Clamp(_selectedDpiIndex, 0, _maxDpiLevels - 1);
            OnPropertyChanged(nameof(SelectedDpiIndex));
            OnPropertyChanged(nameof(SelectedDpiValue));
            for (int i = 0; i < _dpiSlots.Length; i++)
                _dpiSlots[i].IsSelected = i == _selectedDpiIndex;

            LoadRateUi(map.mouseConfig.reportRate);

            Set(ref _sensorModeIndex, map.mouseConfig.sensorPowerSavingModeEnable <= 1
                ? map.mouseConfig.sensorPowerSavingModeEnable : 0, nameof(SensorModeIndex));

            int lod = Array.IndexOf(LodValues, map.mouseConfig.silenceHeight);
            Set(ref _lodIndex, lod >= 0 ? lod : -1, nameof(LodIndex));

            Set(ref _smoothEnable, map.mouseConfig.rippleControlEnable != 0, nameof(SmoothEnable));
            Set(ref _angleFixEnable, map.mouseConfig.linearCorrectionEnable != 0, nameof(AngleFixEnable));
            Set(ref _motionSyncEnable, map.mouseConfig.motionSyncEnable != 0, nameof(MotionSyncEnable));

            LoadLedUi();

            Set(ref _keyDebounceMs, Math.Clamp((int)map.mouseConfig.keyDebounceTime, 0, 20),
                nameof(KeyDebounceMs));

            int sleep = Array.IndexOf(SleepValues, (int)map.mouseConfig.allLedOffTime);
            Set(ref _sleepIndex, sleep >= 0 ? sleep : -1, nameof(SleepSelectedIndex));

            // Кнопки.
            for (int i = 0; i < _buttonSlots.Length; i++)
            {
                var slot = _buttonSlots[i];
                var k = map.keys != null && slot.DeviceSlot < map.keys.Length
                    ? map.keys[slot.DeviceSlot]
                    : default;
                // OSD назначается локально: при маске слота показываем опцию OSD,
                // даже если байты устройства — «родная» кнопка.
                slot.Load(k, (_osdSlotsMask >> slot.DeviceSlot & 1) != 0);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadRateUi(byte rate)
    {
        Set(ref _rate125, rate == (byte)REPORT_RATE.R_125 ? 1 : 0, nameof(IsRate125));
        Set(ref _rate250, rate == (byte)REPORT_RATE.R_250 ? 1 : 0, nameof(IsRate250));
        Set(ref _rate500, rate == (byte)REPORT_RATE.R_500 ? 1 : 0, nameof(IsRate500));
        Set(ref _rate1000, rate == (byte)REPORT_RATE.R_1000 ? 1 : 0, nameof(IsRate1000));
        Set(ref _rate2000, rate == (byte)REPORT_RATE.R_2000 ? 1 : 0, nameof(IsRate2000));
        Set(ref _rate4000, rate == (byte)REPORT_RATE.R_4000 ? 1 : 0, nameof(IsRate4000));
    }

    private void LoadLedUi()
    {
        var led = _working.dpiLed;
        int modeIndex = led.enable == 0 ? 0 : led.mode == 2 ? 2 : 1;
        Set(ref _ledModeIndex, modeIndex, nameof(LedModeIndex));
        Set(ref _ledSpeed, Math.Clamp((int)led.breathSpeed, 1, 5), nameof(LedSpeed));
        Set(ref _ledBrightnessIndex, BrightnessByteToIndex(led.brightness), nameof(LedBrightnessIndex));
        UpdateLedStatusText();
    }

    private void UpdateLevelVisibility()
    {
        for (int i = 0; i < _dpiSlots.Length; i++)
            _dpiSlots[i].IsLevelActive = i < _maxDpiLevels;
    }

    private void UpdateLedStatusText() => LedStatusText = _working.dpiLed.enable == 0
        ? "Выключен"
        : $"{LedModeOptions[Math.Clamp(_ledModeIndex, 0, 2)]} · яркость {_ledBrightnessIndex}/10 · скорость {_ledSpeed}/5";

    #endregion

    #region Изменения пользователем (пишут только в _working)

    private void OnDpiEdited(int index)
    {
        if (_loading)
            return;
        var slot = _dpiSlots[index];
        var (xDpi, dpiex) = slot.Raw;
        _working.dpiConfig[index].xDPI = xDpi;
        _working.dpiConfig[index].yDPI = xDpi;
        _working.dpiConfig[index].DPIex = dpiex;
        MarkDirty();
        if (index == _selectedDpiIndex)
            OnPropertyChanged(nameof(SelectedDpiValue));
    }

    private void OnButtonActionChanged(ButtonSlotViewModel slot, int actionIndex)
    {
        if (_loading)
            return;
        var option = slot.Actions[actionIndex];
        int devSlot = slot.DeviceSlot;
        if (option.Type == ActionOption.OsdType)
        {
            // «OSD» — в мышь пишем свободную клавишу F24 обычным shortcut'ом
            // (схема вендора CustomKeyFunction.cs:824-841): KeyFunMap{type=5,0,0},
            // контент в shortCutKey[слот] — down/up, HID_CODE_TYPE.Normal=1,
            // HID-usage F24=0x73 (в таблице вендора F13–F24 нет — пишем байты
            // напрямую, firmware шлёт usage как есть). Работает для ЛЮБОЙ кнопки,
            // включая DPI (6-ю), у которой нет OS-событий мыши: нажатие → мышь
            // шлёт F24 → глобальный WH_KEYBOARD_LL глушит клавишу (игры/система
            // её не видят) и показывает оверлей.
            _working.keys[devSlot] = new KeyFunMap { type = 5, param1 = 0, param2 = 0 };
            _working.shortCutKey[devSlot] = new ShortCutKey
            {
                contextCount = 2,
                // Ровно 6 контекстов — как у вендора (ByValArray SizeConst=6).
                context = new[]
                {
                    F24Context(keyState: 0),
                    F24Context(keyState: 1),
                    EmptyContext(), EmptyContext(), EmptyContext(), EmptyContext(),
                }
            };
            _osdSlotsMask |= 1 << devSlot;
        }
        else
        {
            _working.keys[devSlot] = new KeyFunMap
            {
                type = option.Type,
                param1 = option.Param1,
                param2 = 0
            };
            _osdSlotsMask &= ~(1 << devSlot);
        }
        _local.OsdSlotsMask = _osdSlotsMask;
        SaveLocal();
        MarkDirty();
    }

    /// <summary>HID-usage F24 (0x73): «свободная» клавиша для сигнала OSD.</summary>
    private const byte F24HidCode = 0x73;

    /// <summary>Контекст shortcut'а: type=1 — HID_CODE_TYPE.Normal,
    /// keyState — 0=KeyDown / 1=KeyUp (KEY_STATE), value[0]=HID-usage.</summary>
    private static MacroContext F24Context(byte keyState) => new()
    {
        type = 1,
        keyState = keyState,
        value = new byte[] { F24HidCode, 0 },
        delay = 0
    };

    private static MacroContext EmptyContext() => new() { value = new byte[2] };

    /// <summary>Родная кнопка слота (type=1): мышь должна слать стандартный usage мыши.</summary>
    private static byte NativeMouseKey(int slot) => slot switch
    {
        0 => (byte)MouseKey.LeftKey,
        1 => (byte)MouseKey.RightKey,
        2 => (byte)MouseKey.MiddleKey,
        3 => (byte)MouseKey.BackKey,
        _ => (byte)MouseKey.ForwardKey,   // 4 — «Вперёд»; 5 — заглушка (ветка выше)
    };

    private void SetRate(ref int flag, bool value, REPORT_RATE rate)
    {
        if (!value)
        {
            Set(ref flag, 0);
            return;
        }
        if (Set(ref flag, 1))
        {
            if (_loading)
                return;
            _working.mouseConfig.reportRate = (byte)rate;
            MarkDirty();
            // Взаимная исключимость остальных сегментов.
            if (rate != REPORT_RATE.R_125) Set(ref _rate125, 0, nameof(IsRate125));
            if (rate != REPORT_RATE.R_250) Set(ref _rate250, 0, nameof(IsRate250));
            if (rate != REPORT_RATE.R_500) Set(ref _rate500, 0, nameof(IsRate500));
            if (rate != REPORT_RATE.R_1000) Set(ref _rate1000, 0, nameof(IsRate1000));
            if (rate != REPORT_RATE.R_2000) Set(ref _rate2000, 0, nameof(IsRate2000));
            if (rate != REPORT_RATE.R_4000) Set(ref _rate4000, 0, nameof(IsRate4000));
        }
    }

    private void MarkDirty()
    {
        if (_loading)
            return;
        HasUnsavedChanges = true;
    }

    #endregion

    #region Действия

    /// <summary>«Применить»: единственная запись во флеш — строго по кнопке, в фоне.</summary>
    public async Task ApplyAsync()
    {
        if (!CanApply)
            return;

        IsBusy = true;
        StatusText = "Запись настроек в мышь…";
        try
        {
            var result = await _session.WriteFlashAsync(_working);
            if (result.Success)
            {
                HasUnsavedChanges = false;
                StatusText = result.DiffBytes == 0
                    ? "✓ Настройки записаны и подтверждены."
                    : $"✓ Записано, но проверка нашла {result.DiffBytes} байт отличий (см. session.log).";
            }
            else
            {
                StatusText = $"Ошибка записи: {result.Error}";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Ошибка записи: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>«Восстановить»: отмена локальных (неприменённых) изменений.</summary>
    public void DiscardChanges()
    {
        if (IsBusy || _baseline.dpiConfig == null)
            return;
        _working = FlashDataMap.Clone(_baseline);
        ReloadUiFromWorking();
        HasUnsavedChanges = false;
        StatusText = "Изменения отменены — показано состояние мыши.";
    }

    /// <summary>«Экспорт»: файл формата официалки (ConfigFile.FlashDataMapToByte, 10428 байт).</summary>
    public bool Export(string path)
    {
        try
        {
            if (_working.dpiConfig == null)
            {
                StatusText = "Экспорт: нет данных (мышь не подключена).";
                return false;
            }
            File.WriteAllBytes(path, FlashDataMap.ToBytes(_working));
            StatusText = $"Экспортировано: {Path.GetFileName(path)}";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Экспорт: {ex.Message}";
            return false;
        }
    }

    /// <summary>«Импорт»: файл формата официалки (ConfigFile.ByteToFlashDataMap).</summary>
    public bool Import(string path)
    {
        try
        {
            var map = FlashDataMap.FromBytes(File.ReadAllBytes(path));
            _working = map;
            ReloadUiFromWorking();
            MarkDirty();
            StatusText = $"Импортировано: {Path.GetFileName(path)} — не забудьте «Применить».";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Импорт: {ex.Message}";
            return false;
        }
    }

    private async Task SwitchProfileAsync(int index)
    {
        IsBusy = true;
        StatusText = $"Переключение на {Profiles[index]}…";
        try
        {
            var result = await _session.SwitchProfileAsync(index);
            if (result.Success)
            {
                StatusText = $"Активен {Profiles[index]}.";
                _ = RefreshDongleVersionAsync();
            }
            else
            {
                StatusText = $"Смена профиля: {result.Error}";
                RevertProfileSelection();
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Смена профиля: {ex.Message}";
            RevertProfileSelection();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RevertProfileSelection()
    {
        // Всегда поднимаем PropertyChanged, чтобы ComboBox вернул прежний индекс
        // (пользователь уже передвинул его TwoWay-биндингом).
        _suspendProfileHandler = true;
        try
        {
            _selectedProfileIndex = _deviceProfileIndex < 0 ? 0 : _deviceProfileIndex;
            OnPropertyChanged(nameof(SelectedProfileIndex));
        }
        finally
        {
            _suspendProfileHandler = false;
        }
    }

    #endregion

    #region Служебное

    /// <summary>Формат официалки ValueConvert.IntToVersion: hex, вида «v1.00».</summary>
    public static string FormatVersion(int version) =>
        "v" + (version >> 8).ToString("x") + "." + (version & 0xFF).ToString("x2");

    /// <summary>ValueConvert.DPIIndexToBrightness (индекс 1..10 → байт).</summary>
    public static byte BrightnessIndexToByte(int index) => index switch
    {
        1 => 16,
        5 => 128,
        9 => 230,
        10 => 255,
        >= 2 and <= 8 => (byte)(30 * (index - 1)),
        _ => 128
    };

    /// <summary>ValueConvert.DPIBrightnessToIndex (байт → индекс 1..10).</summary>
    public static int BrightnessByteToIndex(byte value) => value switch
    {
        16 => 1,
        128 => 5,
        230 => 9,
        255 => 10,
        _ => value % 30 == 0 && value / 30 + 1 is >= 2 and <= 8 ? value / 30 + 1 : 5
    };

    private static void RunOnUi(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher is { HasShutdownStarted: false, HasShutdownFinished: false } disp && !disp.CheckAccess())
            disp.BeginInvoke(action);
        else
            action();
    }

    private void RaiseCommandStates()
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanSwitchProfile));
        OnPropertyChanged(nameof(CanPair));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _session.Dispose();
    }

    #endregion
}

/// <summary>Вариант действия для ComboBox назначения кнопки.</summary>
public sealed record ActionOption(string Name, byte Type, byte Param1)
{
    /// <summary>Локальный тип «Показать статус мыши (OSD)»: в протокол устройства
    /// не пишется как есть — в мышь уходит shortcut «F24» (см. OnButtonActionChanged).</summary>
    public const byte OsdType = 255;

    public static readonly ActionOption Osd = new("Показать статус мыши (OSD)", OsdType, 0);

    /// <summary>Индекс опции OSD в <see cref="All"/>.</summary>
    public static int OsdIndex => Array.IndexOf(All, Osd);

    public static readonly ActionOption[] All =
    {
        new("Отключена", 0, 0),
        new("ЛКМ", 1, (byte)MouseKey.LeftKey),
        new("ПКМ", 1, (byte)MouseKey.RightKey),
        new("Колесо", 1, (byte)MouseKey.MiddleKey),
        new("Вперёд", 1, (byte)MouseKey.ForwardKey),
        new("Назад", 1, (byte)MouseKey.BackKey),
        new("Смена DPI (цикл)", 2, (byte)ChangeDPIKey.Loop),
        new("DPI +", 2, (byte)ChangeDPIKey.Add),
        new("DPI −", 2, (byte)ChangeDPIKey.Dec),
        Osd
    };

    public static ActionOption[] ForKey(KeyFunMap key)
    {
        for (int i = 0; i < All.Length; i++)
            if (All[i].Type == key.type && All[i].Param1 == key.param1)
                return All;

        // Неизвестное сочетание — показываем как есть, чтобы не соврать.
        return new[]
        {
            new ActionOption($"Сейчас: тип {key.type}, параметр {key.param1}", key.type, key.param1)
        }.Concat(All).ToArray();
    }

    public static int FindIndex(ActionOption[] options, KeyFunMap key)
    {
        for (int i = 0; i < options.Length; i++)
            if (options[i].Type == key.type && options[i].Param1 == key.param1)
                return i;
        return -1;
    }
}

/// <summary>Одна DPI-ступень профиля.</summary>
public sealed class DpiSlotViewModel : INotifyPropertyChanged
{
    private byte _xdpi;
    private byte _dpiex;
    private byte[] _color = { 255, 255, 255 };
    private bool _isSelected;
    private bool _isLevelActive = true;
    private bool _isActiveByDevice;

    public int SlotIndex { get; init; }
    public Action<DpiSlotViewModel>? Edited { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Чтение — по образцу вендорского DPIControl:391-402 (сенсор 3395):
    /// x = xDPI | ((DPIex>>6)&lt;&lt;8); шаг 50/100 по битам 0x22; ×2 по битам 0x11.
    /// Запись — ValueToDPI:784-792: x = значение/50 − 1, DPIex = биты старшие.
    /// </summary>
    public int DpiValue
    {
        get
        {
            int x = _xdpi | ((_dpiex >> 6) << 8);
            int value = (_dpiex & 0x22) > 0 ? (x + 1) * 100 : (x + 1) * 50;
            if ((_dpiex & 0x11) > 0)
                value *= 2;
            return value;
        }
        set
        {
            int normalized = Math.Clamp((int)Math.Round(value / 50.0) * 50, 50, 26000);
            int x = normalized / 50 - 1;
            _xdpi = (byte)x;
            _dpiex = (byte)(((x >> 8) << 2) | ((x >> 8) << 6));
            Raise(nameof(DpiValue));
            Raise(nameof(DpiValueText));
            Edited?.Invoke(this);
        }
    }

    public string DpiValueText => $"{DpiValue} DPI";

    /// <summary>Сырые значения для записи в map (см. DpiValue).</summary>
    public (byte XDpi, byte Dpiex) Raw => (_xdpi, _dpiex);

    public Color Color => Color.FromRgb(
        _color.Length > 0 ? _color[0] : (byte)0,
        _color.Length > 1 ? _color[1] : (byte)0,
        _color.Length > 2 ? _color[2] : (byte)0);

    /// <summary>Цвет ступени как Brush (биндинг Color→Brush в WPF не работает).</summary>
    public Brush ColorBrush => new SolidColorBrush(Color);

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    /// <summary>Ступень входит в число активных (индекс &lt; maxDPI).</summary>
    public bool IsLevelActive
    {
        get => _isLevelActive;
        set => SetField(ref _isLevelActive, value);
    }

    /// <summary>Активная на мыши ступень (currentDPI).</summary>
    public bool IsActiveByDevice
    {
        get => _isActiveByDevice;
        set => SetField(ref _isActiveByDevice, value);
    }

    public void Load(byte xDpi, byte dpiex, byte[]? color, bool levelActive, bool activeByDevice)
    {
        _xdpi = xDpi;
        _dpiex = dpiex;
        if (color is { Length: >= 3 })
            _color = color;
        Raise(nameof(DpiValue));
        Raise(nameof(DpiValueText));
        Raise(nameof(Color));
        Raise(nameof(ColorBrush));
        IsLevelActive = levelActive;
        IsActiveByDevice = activeByDevice;
    }

    private void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        Raise(name);
    }
}

/// <summary>Слот назначения кнопки + его маркер на изображении мыши.</summary>
public sealed class ButtonSlotViewModel : INotifyPropertyChanged
{
    /// <summary>Размер маркера на изображении: 22px, центр ± MarkerRadius.</summary>
    public const double MarkerRadius = 11;

    /// <summary>Холст под dev3.png (Viewbox/Grid) — границы для перетаскивания.</summary>
    public const double CanvasWidth = 432;
    public const double CanvasHeight = 356;

    private ActionOption[] _actions = ActionOption.All;
    private int _selectedActionIndex;
    private bool _isSelected;
    private double _markerCenterX;
    private double _markerCenterY;

    public int MarkerNumber { get; init; }   // 1..6 (метка на мыши)
    public int DeviceSlot { get; init; }     // индекс в map.keys
    public required string DisplayName { get; init; }
    public Action<ButtonSlotViewModel, int>? ActionChanged { get; init; }

    /// <summary>Центр маркера в координатах холста 432×356 — ровно X в buttons_layout.json.</summary>
    public double MarkerCenterX
    {
        get => _markerCenterX;
        set
        {
            if (_markerCenterX.Equals(value))
                return;
            _markerCenterX = value;
            Raise(nameof(MarkerCenterX));
            Raise(nameof(MarkerLeft));
        }
    }

    public double MarkerCenterY
    {
        get => _markerCenterY;
        set
        {
            if (_markerCenterY.Equals(value))
                return;
            _markerCenterY = value;
            Raise(nameof(MarkerCenterY));
            Raise(nameof(MarkerTop));
        }
    }

    /// <summary>Левый верх маркера (для Canvas.Left/Top).</summary>
    public double MarkerLeft => _markerCenterX - MarkerRadius;
    public double MarkerTop => _markerCenterY - MarkerRadius;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ActionOption[] Actions
    {
        get => _actions;
        private set
        {
            _actions = value;
            Raise(nameof(Actions));
        }
    }

    public int SelectedActionIndex
    {
        get => _selectedActionIndex;
        set
        {
            if (_selectedActionIndex == value)
                return;
            _selectedActionIndex = value;
            Raise(nameof(SelectedActionIndex));
            if (value >= 0 && value < _actions.Length)
            {
                Raise(nameof(FunctionText));
                ActionChanged?.Invoke(this, value);
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public string FunctionText => _selectedActionIndex >= 0 && _selectedActionIndex < _actions.Length
        ? _actions[_selectedActionIndex].Name
        : "—";

    /// <summary>forceOsd: слот назначен на OSD локально — показываем опцию OSD,
    /// несмотря на байты устройства (в них shortcut F24).</summary>
    public void Load(KeyFunMap key, bool forceOsd = false)
    {
        if (forceOsd)
        {
            Actions = ActionOption.All;
            _selectedActionIndex = ActionOption.OsdIndex;
            Raise(nameof(SelectedActionIndex));
            Raise(nameof(FunctionText));
            return;
        }

        var options = ActionOption.ForKey(key);
        if (!ReferenceEquals(_actions, options))
            Actions = options;

        int idx = ActionOption.FindIndex(_actions, key);
        _selectedActionIndex = idx >= 0 ? idx : 0;
        Raise(nameof(SelectedActionIndex));
        Raise(nameof(FunctionText));
    }

    private void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        Raise(name);
    }
}
