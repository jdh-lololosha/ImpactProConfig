using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
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

    /// <summary>
    /// Версия графика. Инкремент при каждом изменении параметра кривой —
    /// это единственное, на что подписан CurveChart через
    /// <see cref="CurveRevision"/>. Так вместо 400 точек через границу
    /// управления на каждое движение ползунка проходит одно целое.
    /// </summary>
    private int _curveRevision;

    /// <summary>Акцентный цвет темы для линии графика.</summary>
    private Color _curveColor = Color.FromRgb(0xE8, 0x11, 0x23);

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

    /// <summary>
    /// Монотонный счётчик изменений параметров кривой. CurveChart подписан на
    /// него и перерисовывается по InvalidateVisual — без проброса
    /// INotifyPropertyChanged на каждый из десяти параметров.
    /// </summary>
    public int CurveRevision
    {
        get => _curveRevision;
        private set => Set(ref _curveRevision, value);
    }

    /// <summary>
    /// Формула для графика — из текущих значений ползунков.
    ///
    /// Возвращается всегда, даже без settings.json и без загруженного
    /// драйвера: показанный график — это предпросмотр того, что будет
    /// записано по кнопке «Применить». Возвращать null здесь можно было бы
    /// только если бы VM не знал текущих значений, но он их знает.
    /// </summary>
    public RawAccelCurveEngine.Args? GraphArgs
    {
        get
        {
            return new RawAccelCurveEngine.Args
            {
                Acceleration = _acceleration,
                InputOffset = _inputOffset,
                OutputOffset = _outputOffset,
                ExponentClassic = _exponentClassic,
                ExponentPower = _powerExponent,
                CapX = _capSpeed,
                CapY = _capGain,
                Gain = _gainVelocity,
            };
        }
    }

    /// <summary>
    /// Тип кривой для графика. null, когда драйвер не активен: рисовать
    /// формулу неоткуда, все параметры — заглушки.
    /// </summary>
    /// <summary>
    /// Тип кривой для графика.
    ///
    /// Сознательно НЕ зависит от IsDriverActive: формулы считаются локально,
    /// из значений ползунков, и не требуют ни драйвера, ни settings.json.
    /// Иначе график оставался бы пустым на машине, где драйвер установлен, но
    /// ещё не загружен (сервис Start=3 загружается только при следующей
    /// загрузке Windows) — то есть ровно тогда, когда пользователю и нужно
    /// видеть форму до применения.
    /// </summary>
    public RawAccelCurveMode? GraphMode => MapToEngine(_curve);

    /// <summary>Акцентный цвет темы: график должен совпадать с линиями ползунков.</summary>
    public Color CurveColor
    {
        get => _curveColor;
        private set => Set(ref _curveColor, value);
    }

    /// <summary>
    /// GUI-Linear в апстриме — это classic с exponent_classic = 2, отдельного
    /// режима "linear" в rawaccel нет (см. Services/RawAccelSettings.SetCurve).
    /// </summary>
    private double _exponentClassic = 3.0;

    /// <summary>
    /// Gain/Velocity — переключатель между ветками LEGACY и GAIN в апстриме
    /// (accel_args.gain). Берётся из settings.json при загрузке; в графике
    /// всегда используется GAIN-ветка, как в GUI Raw Accel по умолчанию.
    /// </summary>
    private bool _gainVelocity = true;

    /// <summary>
    /// Соответствие «тип кривой в GUI» -> «режим движка». Linear и Classic
    /// оба дают classic, но различаются экспонентой (2 против 3).
    /// </summary>
    private static RawAccelCurveMode MapToEngine(RawAccelCurve curve) => curve switch
    {
        RawAccelCurve.Jump => RawAccelCurveMode.Jump,
        RawAccelCurve.Natural => RawAccelCurveMode.Natural,
        RawAccelCurve.Power => RawAccelCurveMode.Power,
        _ => RawAccelCurveMode.Classic,
    };

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

            // Экспонента classic — часть определения кривой: GUI-Linear
            // это classic+exp2, GUI-Classic это classic+exp3. Держим её
            // синхронной с типом, иначе график покажет форму, которой
            // в settings.json нет.
            _exponentClassic = value == RawAccelCurve.Linear ? 2.0 : 3.0;
            OnPropertyChanged(nameof(SelectedCurveIndex));
            OnPropertyChanged(nameof(GraphMode));
            OnPropertyChanged(nameof(GraphArgs));
            CurveRevision++;
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
        set
        {
            if (!Set(ref _acceleration, value)) return;
            if (_settings != null) _settings.Acceleration = value;
            OnPropertyChanged(nameof(GraphArgs));
            CurveRevision++;
        }
    }

    public double InputOffset
    {
        get => _inputOffset;
        set
        {
            if (!Set(ref _inputOffset, value)) return;
            if (_settings != null) _settings.InputOffset = value;
            OnPropertyChanged(nameof(GraphArgs));
            CurveRevision++;
        }
    }

    public double OutputOffset
    {
        get => _outputOffset;
        set
        {
            if (!Set(ref _outputOffset, value)) return;
            if (_settings != null) _settings.OutputOffset = value;
            OnPropertyChanged(nameof(GraphArgs));
            CurveRevision++;
        }
    }

    public double PowerExponent
    {
        get => _powerExponent;
        set
        {
            if (!Set(ref _powerExponent, value)) return;
            if (_settings != null) _settings.PowerExponent = value;
            OnPropertyChanged(nameof(GraphArgs));
            CurveRevision++;
        }
    }

    public double CapSpeed
    {
        get => _capSpeed;
        set
        {
            if (!Set(ref _capSpeed, value)) return;
            if (_settings != null) _settings.CapSpeed = value;
            OnPropertyChanged(nameof(GraphArgs));
            CurveRevision++;
        }
    }

    public double CapGain
    {
        get => _capGain;
        set
        {
            if (!Set(ref _capGain, value)) return;
            if (_settings != null) _settings.CapGain = value;
            OnPropertyChanged(nameof(GraphArgs));
            CurveRevision++;
        }
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
        SyncCurveColorFromTheme();
        CurveRevision++;
    }

    /// <summary>
    /// Цвет линии графика берём из ресурса темы, чтобы он совпадал с
    /// подсветкой ползунков. Ресурс динамический: тему можно сменить, не
    /// перезапуская приложение.
    /// </summary>
    public void SyncCurveColorFromTheme()
    {
        if (System.Windows.Application.Current?.TryFindResource("ImpactAccentColor") is Color c)
        {
            CurveColor = c;
            CurveRevision++;
        }
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

        // Экспонента classic и Gain берём из файла: без этого график показал бы
        // classic+exp3 там, где в settings.json лежит classic+exp2 (GUI-Linear).
        _exponentClassic = _curve == RawAccelCurve.Linear ? 2.0 : 3.0;
        _gainVelocity = loaded.GainVelocity;

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
        OnPropertyChanged(nameof(GraphArgs));
        OnPropertyChanged(nameof(GraphMode));
        OnPropertyChanged(nameof(CurveColor));
        CurveRevision++;
    }

    /// <summary>Проверка обновлений Raw Accel через официальный GitHub API.</summary>
    public async Task CheckDriverUpdatesAsync()
    {
        try
        {
            var info = await RawAccelUpdateService.FetchLatestAsync();
            LatestVersionText = info.Version;

            // Сравниваем тег релиза с меткой, которую мы сами записали при
            // установке, а НЕ с FileVersion из rawaccel.exe. В официальном
            // релизе v1.7.1 все бинарники несут 1.7.0, поэтому сравнение с
            // FileVersion всегда давало «доступно обновление 1.7.1»: плашка
            // висела бы у пользователя с самой свежей сборкой.
            string installed = RawAccelVersionStamp.Read(InstallDir);
            UpdateAvailable = RawAccelVersionStamp.ShouldOfferUpdate(info.TagName, installed);
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

            // Метку ставим сразу после распаковки, а не после installer.exe:
            // даже если пользователь отменит UAC, бинарники на диске уже именно
            // этой версии, и повторно предлагать «обновление» на тот же тег
            // не надо.
            RawAccelVersionStamp.Write(dir, info.TagName);

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