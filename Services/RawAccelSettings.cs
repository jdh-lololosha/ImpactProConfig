using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ImpactProConfig.Services;

/// <summary>Тип кривой в терминах GUI Raw Accel.</summary>
internal enum RawAccelCurve
{
    /// <summary>Линейная. В settings.json это mode=classic при exponentClassic=2.</summary>
    Linear = 0,
    Classic = 1,
    Natural = 2,
    Jump = 3,
    Power = 4,
}

/// <summary>
/// Чтение и правка официального settings.json драйвера Raw Accel.
///
/// ПОЧЕМУ JsonNode, А НЕ ТИПИЗИРОВАННАЯ МОДЕЛЬ: в wrapper/wrapper.cpp корневой
/// DriverConfig и Profile объявлены с JsonObject(ItemRequired = Required::Always).
/// Если собрать объект заново из своих полей, любой ключ, о котором мы не знаем,
/// будет потерян, и rawaccel.exe откажется читать файл («Required property ... not
/// found in JSON»). Поэтому файл читается как дерево JSON, меняются только те
/// узлы, которыми управляет эта вкладка, остальное сохраняется как есть.
///
/// КЛЮЧИ НЕ ПРИДУМАНЫ: все строки ниже взяты из исходников апстрима
/// (wrapper/wrapper.cpp, [JsonProperty] и ToJObject), включая странные имена с
/// пробелами и двоеточием вроде "Whole or horizontal accel parameters".
///
/// ВАЖНО: само по себе наличие settings.json НИЧЕГО НЕ ПРИМЕНЯЕТ. Драйвер
/// хранит настройки только в памяти. Применение делает writer.exe
/// (документированный путь: «run writer.exe settings.json»), см. ApplyAsync.
/// </summary>
internal sealed class RawAccelSettings
{
    // ---- ключи корневого объекта ----
    private const string KVersion = "version";
    private const string KProfiles = "profiles";
    private const string KDevices = "devices";

    // ---- ключи Profile ----
    private const string KProfileName = "name";
    private const string KWholeAccel = "Whole or horizontal accel parameters";
    private const string KVerticalAccel = "Vertical accel parameters";
    private const string KOutputDpi = "Output DPI";
    private const string KLrRatio = "L/R output DPI ratio (left sens multiplier)";
    private const string KUdRatio = "U/D output DPI ratio (up sens multiplier)";
    private const string KYxRatio = "Y/X output DPI ratio (vertical sens multiplier)";
    private const string KRotation = "Degrees of rotation";
    private const string KSnap = "Degrees of angle snapping";
    private const string KSpeedCap = "Input Speed Cap";

    // ---- ключи AccelArgs ----
    private const string KMode = "mode";
    private const string KAccel = "acceleration";
    private const string KInputOffset = "inputOffset";
    private const string KOutputOffset = "outputOffset";
    private const string KExponentClassic = "exponentClassic";
    private const string KExponentPower = "exponentPower";
    private const string KCap = "Cap / Jump";
    private const string KCapMode = "Cap mode";
    private const string KGainVelocity = "Gain / Velocity";

    /// <summary>
    /// Соответствие «тип кривой в GUI» -> «mode в settings.json».
    /// Отдельного значения "linear" у апстрима НЕТ: GUI-кривая Linear — это
    /// mode=classic с exponentClassic=2 (grapher/Models/Options/AccelTypeOptions).
    /// Список значений mode: classic, jump, natural, synchronous, power, lut, noaccel.
    /// </summary>
    private static readonly Dictionary<RawAccelCurve, string> ModeByCurve = new()
    {
        [RawAccelCurve.Linear] = "classic",   // + exponentClassic = 2
        [RawAccelCurve.Classic] = "classic",
        [RawAccelCurve.Natural] = "natural",
        [RawAccelCurve.Jump] = "jump",
        [RawAccelCurve.Power] = "power",
    };

    /// <summary>Значение, при котором classic превращается в визуально линейную кривую.</summary>
    private const double LinearExponentClassic = 2.0;

    private readonly JsonObject _root;
    private readonly JsonObject _profile;
    private readonly JsonObject _accel;

    private RawAccelSettings(JsonObject root, JsonObject profile, JsonObject accel)
    {
        _root = root;
        _profile = profile;
        _accel = accel;
    }

    public string ProfileName => GetStr(_profile, KProfileName) ?? "p1";
    public string DriverVersion => GetStr(_root, KVersion) ?? string.Empty;

    /// <summary>Режим, реально записанный в файле, как строка апстрима.</summary>
    public string RawMode => GetStr(_accel, KMode) ?? "classic";

    /// <summary>Текущий тип кривой по значению файла.</summary>
    public RawAccelCurve Curve
    {
        get
        {
            string mode = RawMode;
            if (mode == "classic"
                && Math.Abs(GetDouble(_accel, KExponentClassic, 0) - LinearExponentClassic) < 1e-9)
                return RawAccelCurve.Linear;
            foreach (var kv in ModeByCurve)
            {
                if (kv.Value != mode) continue;
                // classic без признака Linear остаётся Classic.
                return kv.Key == RawAccelCurve.Linear ? RawAccelCurve.Classic : kv.Key;
            }
            return RawAccelCurve.Classic;
        }
    }

    /// <summary>Множитель чувствительности по горизонтали (L/R output DPI ratio).</summary>
    public double HorizontalSensMultiplier
    {
        get => GetDouble(_profile, KLrRatio, 1.0);
        set => SetDouble(_profile, KLrRatio, value);
    }

    /// <summary>Множитель чувствительности по вертикали (U/D output DPI ratio).</summary>
    public double VerticalSensMultiplier
    {
        get => GetDouble(_profile, KUdRatio, 1.0);
        set => SetDouble(_profile, KUdRatio, value);
    }

    /// <summary>Отношение вертикальной чувствительности к горизонтальной (Y/X ratio).</summary>
    public double VerticalRatio
    {
        get => GetDouble(_profile, KYxRatio, 1.0);
        set => SetDouble(_profile, KYxRatio, value);
    }

    public double Acceleration
    {
        get => GetDouble(_accel, KAccel, 0.005);
        set => SetDouble(_accel, KAccel, value);
    }

    /// <summary>Входное смещение кривой.</summary>
    public double InputOffset
    {
        get => GetDouble(_accel, KInputOffset, 0.0);
        set => SetDouble(_accel, KInputOffset, value);
    }

    /// <summary>Выходное смещение кривой.</summary>
    public double OutputOffset
    {
        get => GetDouble(_accel, KOutputOffset, 0.0);
        set => SetDouble(_accel, KOutputOffset, value);
    }

    /// <summary>Степень для режима power (экспонента).</summary>
    public double PowerExponent
    {
        get => GetDouble(_accel, KExponentPower, 0.05);
        set => SetDouble(_accel, KExponentPower, value);
    }

    /// <summary>Порог срабатывания cap (первая координата "Cap / Jump").</summary>
    public double CapSpeed
    {
        get => GetCap().x;
        set => SetCap(value, CapGain);
    }

    /// <summary>Предел усиления после cap (вторая координата "Cap / Jump").</summary>
    public double CapGain
    {
        get => GetCap().y;
        set => SetCap(CapSpeed, value);
    }

    /// <summary>
    /// Режим cap. Допустимые значения апстрима: in_out, input, output.
    /// </summary>
    public string CapMode
    {
        get => GetStr(_accel, KCapMode) ?? "in_out";
        set => SetStr(_accel, KCapMode, value);
    }

    /// <summary>Gain/Velocity — переключатель режима расчёта кривой.</summary>
    public bool GainVelocity
    {
        get => _accel[KGainVelocity]?.GetValue<bool>() ?? false;
        set => _accel[KGainVelocity] = value;
    }

    /// <summary>Вращение, градусы (апстрим принимает 0..360).</summary>
    public double Rotation
    {
        get => GetDouble(_profile, KRotation, 0.0);
        set => SetDouble(_profile, KRotation, value);
    }

    /// <summary>Привязка к углам, градусы. Апстрим валидирует 0..45 — зубчатое колесо
    /// намеренно ограничено 45, иначе writer вернёт ошибку.</summary>
    public double SnapAngle
    {
        get => GetDouble(_profile, KSnap, 0.0);
        set => SetDouble(_profile, KSnap, value);
    }

    /// <summary>Потолок скорости ввода.</summary>
    public double SpeedCap
    {
        get => GetDouble(_profile, KSpeedCap, 0.0);
        set => SetDouble(_profile, KSpeedCap, value);
    }

    public double OutputDpi
    {
        get => GetDouble(_profile, KOutputDpi, 1000.0);
        set => SetDouble(_profile, KOutputDpi, value);
    }

    /// <summary>Список профилей, найденных в файле.</summary>
    public IReadOnlyList<string> ProfileNames
    {
        get
        {
            var list = new List<string>();
            if (_root[KProfiles] is JsonArray arr)
                foreach (var p in arr)
                    if (p is JsonObject po && po[KProfileName]?.GetValue<string>() is { } n)
                        list.Add(n);
            return list;
        }
    }

    // ---------- Cap / Jump хранится как объект {x, y} ----------

    private (double x, double y) GetCap()
    {
        if (_accel[KCap] is JsonObject c)
            return (c["x"]?.GetValue<double>() ?? 15.0,
                    c["y"]?.GetValue<double>() ?? 1.5);
        return (15.0, 1.5);
    }

    private void SetCap(double x, double y) =>
        _accel[KCap] = new JsonObject { ["x"] = x, ["y"] = y };

    // ---------- чтение/запись примитивов ----------

    private static double GetDouble(JsonObject o, string key, double fallback)
    {
        var node = o[key];
        if (node is null) return fallback;
        try { return node.GetValue<double>(); }
        catch (Exception e) when (e is FormatException or InvalidOperationException)
        {
            return fallback;
        }
    }

    private static void SetDouble(JsonObject o, string key, double value) =>
        o[key] = Math.Round(value, 9);

    private static string? GetStr(JsonObject o, string key)
    {
        try { return o[key]?.GetValue<string>(); }
        catch (Exception e) when (e is FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void SetStr(JsonObject o, string key, string value) => o[key] = value;

    // ---------- загрузка/сохранение ----------

    /// <summary>
    /// Читает settings.json. Возвращает null, если файла нет или он не разобран:
    /// вызывающий обязан показать это пользователю, а не молча создавать пустой
    /// конфиг — иначе следующий Apply затрёт настройки драйвера.
    /// </summary>
    public static RawAccelSettings? Load(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                error = "Файл settings.json не найден. Запустите Raw Accel (rawaccel.exe) один раз, чтобы он создал конфигурацию.";
                return null;
            }

            string json = File.ReadAllText(path);
            var node = JsonNode.Parse(json) as JsonObject;
            if (node == null)
            {
                error = "settings.json не является JSON-объектом.";
                return null;
            }

            if (node[KProfiles] is not JsonArray profiles || profiles.Count == 0)
            {
                error = "В settings.json нет ни одного профиля.";
                return null;
            }

            if (profiles[0] is not JsonObject profile)
            {
                error = "Профиль в settings.json имеет неверный формат.";
                return null;
            }

            // Правим первый профиль: он же активный (SettingsManager.ActiveProfile
            // = ActiveConfig.profiles[0]).
            if (profile[KWholeAccel] is not JsonObject accel)
            {
                error = "В профиле нет раздела \"Whole or horizontal accel parameters\".";
                return null;
            }

            return new RawAccelSettings(node, profile, accel);
        }
        catch (JsonException ex)
        {
            error = "settings.json повреждён: " + ex.Message;
            return null;
        }
        catch (IOException ex)
        {
            error = "Не удалось прочитать settings.json: " + ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Сохраняет файл атомарно: сначала во временный файл рядом, затем
    /// File.Move с перезаписью. Такой же приём уже используется в
    /// BatteryStatsService — причина та же: читающий rawaccel.exe может поймать
    /// файл на середине записи и увидеть обрезанный JSON.
    /// </summary>
    public void Save(string path)
    {
        string json = _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        string dir = Path.GetDirectoryName(path)
                     ?? throw new IOException($"Некорректный путь: {path}");
        Directory.CreateDirectory(dir);

        string tmp = Path.Combine(dir, Path.GetFileName(path) + ".tmp");
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);

        App.Log($"RawAccel: settings.json saved ({new FileInfo(path).Length} bytes)");
    }

    /// <summary>Сериализует текущее состояние в строку — для отладки и тестов.</summary>
    public string ToJson() =>
        _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>Человекочитаемое имя типа кривой для UI.</summary>
    public static string CurveDisplayName(RawAccelCurve curve) => curve switch
    {
        RawAccelCurve.Linear => "Linear",
        RawAccelCurve.Classic => "Classic",
        RawAccelCurve.Natural => "Natural",
        RawAccelCurve.Jump => "Jump",
        RawAccelCurve.Power => "Power",
        _ => curve.ToString(),
    };

    /// <summary>Применяет тип кривой: mode +, для Linear, exponentClassic=2.</summary>
    public void SetCurve(RawAccelCurve curve)
    {
        SetStr(_accel, KMode, ModeByCurve[curve]);

        if (curve == RawAccelCurve.Linear)
        {
            // Linear в апстриме — это classic с экспонентой 2.
            SetDouble(_accel, KExponentClassic, LinearExponentClassic);
        }
        else if (curve == RawAccelCurve.Classic)
        {
            // Возвращаем классическую крутизну, иначе переключение обратно
            // оставит Linear вместо Classic.
            SetDouble(_accel, KExponentClassic, 3.0);
        }
    }

    /// <summary>Разбор строки из JSON в enum (для биндинга выпадающего списка).</summary>
    public static RawAccelCurve ParseCurve(string text) =>
        Enum.TryParse<RawAccelCurve>(text, ignoreCase: true, out var c)
            ? c
            : RawAccelCurve.Classic;

    /// <summary>Разбор числа из строки в культуре UI (у нас ru-RU, у апстрима — точка).</summary>
    public static double ParseNumber(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : 0.0;
}