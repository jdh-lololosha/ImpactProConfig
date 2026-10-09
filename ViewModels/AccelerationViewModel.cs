using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using ImpactProConfig.Services;

namespace ImpactProConfig.ViewModels;

/// <summary>
/// Вкладка «Акселерация»: менеджер драйвера Raw Accel и настройка кривых.
///
/// ГРАНИЦЫ ОТВЕТСТВЕННОСТИ:
///   - приложение НЕ устанавливает и НЕ грузит драйвер само; установку делает
///     официальный installer.exe, запущенный с verb=runas;
///   - приложение НЕ модифицирует файлы релиза Raw Accel — подпись драйвера
///     не должна ломаться, поэтому байты не трогаем вовсе;
///   - настройки пишутся в официальный settings.json, применяются через
///     официальный writer.exe.
/// </summary>
internal sealed class AccelerationViewModel : INotifyPropertyChanged
{
    private RawAccelState _driverState = RawAccelState.NotInstalled;
    private string _statusText = "Проверка драйвера…";
    private string _versionText = string.Empty;
    private string _latestVersionText = string.Empty;
    private bool _updateAvailable;
    private bool _busy;
    private string _busyText = string.Empty;

    private RawAccelSettings? _settings;
    private string _loadError = string.Empty;

    private RawAccelCurve _curve = RawAccelCurve.Classic;
    private double _horizontalSens = 1.0;
    private double _verticalSens = 1.0;
    private double _acceleration = 0.005;
    private double _inputOffset;
    private double _outputOffset;
    private double _powerExponent = 0.05;
    private double _capSpeed = 15.0;
    private double _capGain = 1.5;
    private double _snapAngle;

    private LocalSettings _local;

    public AccelerationViewModel()
    {
        _local = LocalSettingsStore.Load();
        RefreshStatus();
    }

    // ---------------- статус драйвера ----------------

    public bool IsDriverActive => _driverState == RawAccelState.Active;

    public string StatusText
    {
        get => _statusText;
        private set { if (Set(ref _statusText, value)) { OnPropertyChanged(nameof(IsDriverActive)); OnPropertyChanged(nameof(ShowInstallCard)); } }
    }

    public bool ShowInstallCard => _driverState == RawAccelState.NotInstalled;

    public string VersionText
    {
        get => _versionText;
        private set => Set(ref _versionText, value);
    }

    // ---------------- обновление апстрима ----------------

    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set
        {
            if (Set(ref _updateAvailable, value))
            {
                OnPropertyChanged(nameof(UpdateBannerText));
                OnPropertyChanged(nameof(CanUpdate));
            }
        }
    }

    public string LatestVersionText
    {
        get => _latestVersionText;
        private set { if (Set(ref _latestVersionText, value)) OnPropertyChanged(nameof(UpdateBannerText)); }
    }

    public string UpdateBannerText => UpdateAvailable
        ? $"Доступно обновление драйвера Raw Accel {LatestVersionText}"
        : string.Empty;

    // ---------------- занятость ----------------

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
            {
                OnPropertyChanged(nameof(BusyText));
                OnPropertyChanged(nameof(CanInstall));
                OnPropertyChanged(nameof(CanApply));
                OnPropertyChanged(nameof(CanUpdate));
            }
        }
    }

    public string BusyText
    {
        get => _busyText;
        private set => Set(ref _busyText, value);
    }

    public string LoadError
    {
        get => _loadError;
        private set { if (Set(ref _loadError, value)) OnPropertyChanged(nameof(HasLoadError)); }
    }

    public bool HasLoadError => !string.IsNullOrEmpty(_loadError);

    public bool HasSettings => _settings != null;

    // ---------------- команды ----------------
    // Действия вызываются напрямую из code-behind страницы (Click=...),
    // как во всех остальных страницах проекта — ICommand здесь не используется.

    /// <summary>Кнопка «Установить официальный драйвер».</summary>
    public bool CanInstall => !_busy && _driverState != RawAccelState.Active;

    /// <summary>Кнопка «Применить».</summary>
    public bool CanApply => !_busy && _driverState == RawAccelState.Active;

    /// <summary>Плашка «Доступно обновление драйвера Raw Accel».</summary>
    public bool CanUpdate => !_busy && _updateAvailable;

    // ---------------- кривые и параметры ----------------

    /// <summary>Типы кривых в терминах GUI Raw Accel (Linear в апстриме — classic+exp2).</summary>
    public IReadOnlyList<string> CurveNames { get; } = new[]
    {
        RawAccelSettings.CurveDisplayName(RawAccelCurve.Linear),
        RawAccelSettings.CurveDisplayName(RawAccelCurve.Classic),
        RawAccelSettings.CurveDisplayName(RawAccelCurve.Natural),
        RawAccelSettings.CurveDisplayName(RawAccelCurve.Jump),
        RawAccelSettings.CurveDisplayName(RawAccelCurve.Power),
    };

    public RawAccelCurve SelectedCurve
    {
        get => _curve;
        set
        {
            if (!Set(ref _curve, value)) return;
            if (_settings != null) _settings.SetCurve(value);
            OnPropertyChanged(nameof(SelectedCurveIndex));
        }
    }

    public int SelectedCurveIndex
    {
        get => (int)_curve;
        set
        {
            if (value < 0 || value >= CurveNames.Count) return;
            SelectedCurve = (RawAccelCurve)value;
        }
    }

    public double HorizontalSensMultiplier
    {
        get => _horizontalSens;
        set { if (Set(ref _horizontalSens, value) && _settings != null) _settings.HorizontalSensMultiplier = value; }
    }

    public double VerticalSensMultiplier
    {
        get => _verticalSens;
        set { if (Set(ref _verticalSens, value) && _settings != null) _settings.VerticalSensMultiplier = value; }
    }

    public double Acceleration
    {
        get => _acceleration;
        set { if (Set(ref _acceleration, value) && _settings != null) _settings.Acceleration = value; }
    }

    public double InputOffset
    {
        get => _inputOffset;
        set { if (Set(ref _inputOffset, value) && _settings != null) _settings.InputOffset = value; }
    }

    public double OutputOffset
    {
        get => _outputOffset;
        set { if (Set(ref _outputOffset, value) && _settings != null) _settings.OutputOffset = value; }
    }

    public double PowerExponent
    {
        get => _powerExponent;
        set { if (Set(ref _powerExponent, value) && _settings != null) _settings.PowerExponent = value; }
    }

    public double CapSpeed
    {
        get => _capSpeed;
        set { if (Set(ref _capSpeed, value) && _settings != null) _settings.CapSpeed = value; }
    }

    public double CapGain
    {
        get => _capGain;
        set { if (Set(ref _capGain, value) && _settings != null) _settings.CapGain = value; }
    }

    /// <summary>Привязка к углам. Апстрим валидирует диапазон 0..45 градусов.</summary>
    public double SnapAngle
    {
        get => _snapAngle;
        set { if (Set(ref _snapAngle, value) && _settings != null) _settings.SnapAngle = value; }
    }

    // ---------------- логика ----------------

    private string InstallDir
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_local.RawAccelInstallDir))
                return _local.RawAccelInstallDir;

            // Основной путь: распакованный Raw Accel едет в portable-сборке
            // рядом с exe (build-portable.ps1 кладёт его в Drivers\RawAccel),
            // поэтому установка не требует первого выхода в интернет.
            string bundled = BundledRawAccelDir();
            if (RawAccelService.IsUnpacked(bundled)) return bundled;

            // Фолбэк: пользователь ставил драйвер раньше сам. Забираем его
            // папку, там лежит его settings.json — подхватить надо именно его,
            // иначе настройки применятся не к тому экземпляру.
            string user = RawAccelService.DefaultInstallDir;
            return RawAccelService.IsUnpacked(user) ? user : bundled;
        }
    }

    /// <summary>
    /// Drivers\RawAccel рядом с exe. AppContext.BaseDirectory, а не App.DataDir:
    /// бинарники программы живут в %LOCALAPPDATA%\Programs\ImpactProConfig,
    /// а данные (настройки, логи) — в %LOCALAPPDATA%\ImpactProConfig.
    /// </summary>
    private static string BundledRawAccelDir() =>
        Path.Combine(AppContext.BaseDirectory, "Drivers", "RawAccel");

    /// <summary>
    /// Определение состояния драйвера и загрузка settings.json.
    /// Реестр читается обычным пользователем, поэтому проверка не требует прав.
    /// </summary>
    public void RefreshStatus()
    {
        _driverState = RawAccelService.QueryState();

        StatusText = _driverState switch
        {
            RawAccelState.Active => "Драйвер акселерации активен ✅",
            RawAccelState.ServiceOnly =>
                "Драйвер зарегистрирован, но не загружен. Перезагрузите компьютер или запустите installer.exe заново.",
            _ => "Драйвер Raw Accel не установлен",
        };

        VersionText = RawAccelService.ReadInstalledVersion(InstallDir);
        OnPropertyChanged(nameof(IsDriverActive));
        OnPropertyChanged(nameof(ShowInstallCard));

        LoadSettings();

        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanApply));
    }

    private void LoadSettings()
    {
        string path = RawAccelService.SettingsPath(InstallDir);
        var loaded = RawAccelSettings.Load(path, out string? error);

        _settings = loaded;
        LoadError = error ?? string.Empty;
        OnPropertyChanged(nameof(HasSettings));

        if (loaded == null)
        {
            App.Log($"RawAccel: settings not loaded: {LoadError}");
            return;
        }

        _curve = loaded.Curve;
        _horizontalSens = loaded.HorizontalSensMultiplier;
        _verticalSens = loaded.VerticalSensMultiplier;
        _acceleration = loaded.Acceleration;
        _inputOffset = loaded.InputOffset;
        _outputOffset = loaded.OutputOffset;
        _powerExponent = loaded.PowerExponent;
        _capSpeed = loaded.CapSpeed;
        _capGain = loaded.CapGain;
        _snapAngle = loaded.SnapAngle;

        OnPropertyChanged(nameof(SelectedCurve));
        OnPropertyChanged(nameof(SelectedCurveIndex));
        OnPropertyChanged(nameof(HorizontalSensMultiplier));
        OnPropertyChanged(nameof(VerticalSensMultiplier));
        OnPropertyChanged(nameof(Acceleration));
        OnPropertyChanged(nameof(InputOffset));
        OnPropertyChanged(nameof(OutputOffset));
        OnPropertyChanged(nameof(PowerExponent));
        OnPropertyChanged(nameof(CapSpeed));
        OnPropertyChanged(nameof(CapGain));
        OnPropertyChanged(nameof(SnapAngle));
    }

    /// <summary>Проверка обновлений Raw Accel через официальный GitHub API.</summary>
    public async Task CheckDriverUpdatesAsync()
    {
        try
        {
            var info = await RawAccelUpdateService.FetchLatestAsync();
            LatestVersionText = info.Version;
            UpdateAvailable = RawAccelUpdateService.IsNewer(info.Version, VersionText);
        }
        catch (Exception ex)
        {
            // Молча проглатывать нельзя: пишем в лог, баннер просто не покажем.
            App.Log($"RawAccel: update check failed: {ex.GetType().Name}: {ex.Message}");
            UpdateAvailable = false;
        }
    }

    /// <summary>
    /// Установка/обновление драйвера: скачиваем официальный архив, проверяем
    /// подпись rawaccel.sys, распаковываем и запускаем официальный installer.exe
    /// с повышенными правами.
    ///
    /// Используется и кнопкой «Установить официальный драйвер», и плашкой
    /// «Обновить» — апстрим обновляется тем жеinstaller.exe, отдельного
    /// механизма апдейта драйвера у него нет.
    /// </summary>
    public async Task InstallDriverAsync()
    {
        IsBusy = true;
        BusyText = "Проверяю обновления Raw Accel…";
        try
        {
            var info = await RawAccelUpdateService.FetchLatestAsync();

            BusyText = $"Скачиваю Raw Accel {info.Version}…";
            var progress = new Progress<(long Done, long Total)>(p =>
            {
                if (p.Total <= 0) return;
                BusyText = $"Скачиваю Raw Accel {info.Version}: {p.Done * 100 / p.Total}%";
            });

            string dir = InstallDir;
            await RawAccelUpdateService.DownloadAndExtractAsync(info, dir, progress);

            BusyText = "Запускаю официальный установщик. Подтвердите запрос в UAC…";
            if (!RawAccelInstaller.Run(dir, out string err))
            {
                LoadError = err;
                return;
            }

            LatestVersionText = info.Version;
            UpdateAvailable = false;
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
            App.Log($"RawAccel: install failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    /// <summary>
    /// Сохраняет настройки в официальный settings.json и применяет их через
    /// официальный writer.exe.
    /// </summary>
    public void ApplySettings()
    {
        if (_settings == null)
        {
            LoadError = "Настройки не загружены — нечего применять.";
            return;
        }

        // Апстрим валидирует snap angle в диапазоне 0..45; проверяем заранее,
        // иначе writer.exe покажет ошибку в отдельном окне.
        if (SnapAngle < 0 || SnapAngle > 45)
        {
            LoadError = "Привязка к углам: допустимо от 0 до 45 градусов.";
            return;
        }

        string path = RawAccelService.SettingsPath(InstallDir);
        try
        {
            _settings.Save(path);
        }
        catch (Exception ex)
        {
            LoadError = "Не удалось сохранить settings.json: " + ex.Message;
            App.Log($"RawAccel: save failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (!RawAccelApplier.Apply(InstallDir, path, out string error))
        {
            LoadError = error;
        }
    }

    // ---------------- INotifyPropertyChanged ----------------

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