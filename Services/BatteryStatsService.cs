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

            Save();
        }
    }

    /// <summary>Скорость разряда в %/час. null — данных пока недостаточно.</summary>
    public double? DrainPerHour()
    {
        lock (_gate)
        {
            EnsureLoaded();

            double? drop = null;
            double? hours = null;

            BatterySample? first = null;
            BatterySample? last = null;
            for (int i = 0; i < _data.Samples.Count; i++)
            {
                var s = _data.Samples[i];
                if (!s.Discharging)
                    continue;
                first ??= s;
                last = s;
            }

            if (first is null || last is null || ReferenceEquals(first, last))
                return null;

            drop = first.Percent - last.Percent;
            hours = (last.At - first.At).TotalHours;

            if (hours <= 0 || drop < MinDropPercent)
                return null;

            double rate = drop.Value / hours.Value;
            return rate < MinDrainPerHour ? null : rate;
        }
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

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_data,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Недоступная папка (Program Files без прав) — молча продолжаем в памяти.
        }
    }
}