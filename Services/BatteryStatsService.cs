using System.IO;
using System.Text.Json;

namespace ImpactProConfig.Services;

/// <summary>Одна точка измерения: заряд в процентах и время замера.</summary>
internal sealed class BatterySample
{
    /// <summary>Время замера (UTC).</summary>
    public DateTimeOffset At { get; set; }

    /// <summary>Уровень заряда, %.</summary>
    public int Percent { get; set; }

    /// <summary>true — шёл разряд, false — зарядка.</summary>
    public bool Discharging { get; set; }
}

/// <summary>Файл battery_stats.json — история замеров и выведенная скорость разряда.</summary>
internal sealed class BatteryStatsFile
{
    /// <summary>Версия схемы: меняем, если формат поменяется.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>История точек. Старые подрезаем, чтобы файл не рос бесконечно.</summary>
    public List<BatterySample> Samples { get; set; } = [];
}

/// <summary>
/// Телеметрия батареи: копит историю разряда в battery_stats.json и считает
/// скорость расхода (%/ч) и примерное время до нуля.
///
/// Важное ограничение: скорость выводится ТОЛЬКО из точек, где заряд реально
/// падал. Точки на зарядке (растёт процент) в оценку не идут, иначе «осталось
/// X часов» получилось бы отрицательным. Если разрядных точек меньше двух —
/// показываем «нужно больше данных», а не выдуманное число.
/// </summary>
internal sealed class BatteryStatsService
{
    private const string FileName = "battery_stats.json";

    /// <summary>Сколько последних точек держим в файле (2 недели при замере раз в 5 мин).</summary>
    private const int MaxSamples = 4000;

    /// <summary>Минимальная разница заряда между точками, чтобы точка считалась разрядной.</summary>
    private const int MinDropPercent = 1;

    /// <summary>Ниже этого расхода считать «почти не разряжается».</summary>
    private const double MinDrainPerHour = 0.05;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(5);

    private readonly string _path;
    private readonly object _gate = new();

    private BatteryStatsFile _data = new();
    private DateTimeOffset _lastSampleAt = DateTimeOffset.MinValue;
    private bool _loaded;

    /// <summary>Кэш скорости разряда, %/ч. null — данных пока недостаточно.</summary>
    private double? _cachedRate;

    /// <summary>true — _cachedRate соответствует текущему состоянию _data.</summary>
    private bool _rateValid;

    /// <summary>Снимок кэша под _gate (иначе UI читал бы поле без синхронизации).</summary>
    private double? CachedRate
    {
        get { lock (_gate) return _cachedRate; }
    }

    public BatteryStatsService(string? directory = null)
    {
        // По умолчанию %LOCALAPPDATA%\ImpactProConfig — туда есть права на запись
        // у любого пользователя. Рядом с exe (Program Files) писать нельзя.
        _path = Path.Combine(directory ?? App.DataDir, FileName);
    }

    /// <summary>Сколько всего накоплено точек (для UI).</summary>
    public int SampleCount
    {
        get { lock (_gate) { EnsureLoaded(); return _data.Samples.Count; } }
    }

    /// <summary>
    /// Записать текущий уровень, если прошло достаточно времени с прошлой записи.
    /// Заряжающаяся мышь тоже пишется: по этим точкам видно, когда цикл начался.
    /// </summary>
    public void Record(int percent, bool charging)
    {
        if (percent < 0)
            return;

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            EnsureLoaded();

            if (now - _lastSampleAt < SampleInterval)
                return;
            _lastSampleAt = now;

            _data.Samples.Add(new BatterySample
            {
                At = now,
                Percent = percent,
                Discharging = !charging,
            });

            if (_data.Samples.Count > MaxSamples)
                _data.Samples.RemoveRange(0, _data.Samples.Count - MaxSamples);

            RecalculateRateLocked();
            QueueSave();
        }
    }

    /// <summary>
    /// Пересчёт скорости разряда по текущему циклу разряда. Вызывается под _gate.
    /// Цикл начинается с последней точки зарядки (Discharging == false): если брать
    /// первый и последний разряжающийся замер по всей истории, то время между ними
    /// включает все циклы зарядки между ними и оценка %/ч занижается в разы.
    /// </summary>
    private void RecalculateRateLocked()
    {
        var samples = _data.Samples;
        int firstIdx = -1;
        for (int i = samples.Count - 1; i >= 0; i--)
        {
            if (!samples[i].Discharging)
            {
                firstIdx = i + 1;   // начало текущего цикла разряда
                break;
            }
        }
        if (firstIdx < 0)
            firstIdx = 0;           // зарядки в истории не было — берём всё

        BatterySample? first = null, last = null;
        for (int i = firstIdx; i < samples.Count; i++)
        {
            var s = samples[i];
            if (!s.Discharging)
                continue;
            first ??= s;
            last = s;
        }

        if (first is null || last is null || ReferenceEquals(first, last))
        {
            _cachedRate = null;
            _rateValid = true;
            return;
        }

        double drop = first.Percent - last.Percent;
        double hours = (last.At - first.At).TotalHours;
        if (hours <= 0 || drop < MinDropPercent)
        {
            _cachedRate = null;
            _rateValid = true;
            return;
        }
        double rate = drop / hours;
        _cachedRate = rate < MinDrainPerHour ? null : rate;
        _rateValid = true;
    }

    /// <summary>
    /// Скорость разряда в %/час по текущему циклу разряда. null — данных пока
    /// недостаточно. Отдаёт кэш: пересчёт делается в Record, а не на каждый
    /// батарейный пакет (иначе UI сканировал бы 4000 сэмплов четыре раза).
    /// </summary>
    public double? DrainPerHour()
    {
        if (!_rateValid)
        {
            lock (_gate)
            {
                EnsureLoaded();
                RecalculateRateLocked();
            }
        }
        return CachedRate;
    }

    /// <summary>
    /// Оценка «осталось примерно ~N ч» для конкретного уровня заряда.
    /// Скорость берётся из истории, уровень — текущий, поэтому при полном заряде
    /// оценка честно увеличивается.
    /// </summary>
    public double? HoursLeftAt(int percent)
    {
        double? rate = DrainPerHour();
        if (rate is null || percent <= 0)
            return null;
        return percent / rate.Value;
    }

    /// <summary>Готова ли статистика к показу.</summary>
    public bool HasEnoughData => DrainPerHour() is not null;

    /// <summary>Человекочитаемый текст расхода, напр. «6.4 %/ч».</summary>
    public string DrainText =>
        DrainPerHour() is double rate
            ? $"{rate:0.#} %/ч"
            : "нужно больше данных";

    /// <summary>Человекочитаемый текст остатка, напр. «~12 ч активной игры».</summary>
    public string HoursLeftText(int percent) =>
        HoursLeftAt(percent) is double hours
            ? $"~{hours:0} ч активной игры"
            : "оценка появится после разряда";

    private void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;

        try
        {
            if (!File.Exists(_path))
                return;
            _data = JsonSerializer.Deserialize<BatteryStatsFile>(File.ReadAllText(_path))
                    ?? new BatteryStatsFile();
        }
        catch
        {
            // Битый файл — начинаем историю заново, телеметрия не критична.
            _data = new BatteryStatsFile();
        }
    }

    /// <summary>
    /// Ставит файл на перезапись в фоне. Снимок данных делается под _gate сразу,
    /// а сама запись идёт вне лока и вне UI-потока: раньше File.WriteAllText всей
    /// истории выполнялся на UI-потоке и блокировал интерфейс.
    /// </summary>
    private void QueueSave()
    {
        BatteryStatsFile snapshot;
        try
        {
            snapshot = new BatteryStatsFile
            {
                SchemaVersion = _data.SchemaVersion,
                Samples = new List<BatterySample>(_data.Samples),
            };
        }
        catch
        {
            return;
        }

        _ = Task.Run(() => SaveSnapshot(snapshot));
    }

    /// <summary>
    /// Запись снимка атомарно: сначала .tmp, затем подмена исходного файла.
    /// Обрыв записи (сбой питания, убитый процесс) больше не оставляет обрезанный
    /// battery_stats.json, который EnsureLoaded потом молча выбросил бы вместе
    /// со всей историей.
    /// </summary>
    private void SaveSnapshot(BatteryStatsFile snapshot)
    {
        string tmp = _path + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = false,   // файл машинный, отступы только раздувают его
            });
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Недоступная папка (Program Files без прав) — молча продолжаем в памяти.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* не критично */ }
        }
    }
}