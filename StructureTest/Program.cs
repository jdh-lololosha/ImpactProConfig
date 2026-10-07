using System.Runtime.InteropServices;
using ImpactProConfig.Driver;

// Офлайн-проверка структуры FlashDataMap против эталонного дампа.
// Никакого USB — только чтение файла и мемуар-десериализация.

const string BinPath = @"C:\Users\gygyh\AppData\Local\Ardor\Impact PRO\Device_Info\Device_CID16_MID6.bin";

var bytes = File.ReadAllBytes(BinPath);
Console.WriteLine($"Файл: {BinPath}");
Console.WriteLine($"Размер файла: {bytes.Length} байт");

int structSize = Marshal.SizeOf<FlashDataMap>();
Console.WriteLine($"Marshal.SizeOf<FlashDataMap>(): {structSize} байт");
Console.WriteLine(structSize == bytes.Length
    ? "==> РАЗМЕР СОШЁЛСЯ 1-в-1 ✓"
    : $"==> РАЗМЕР НЕ СОШЁЛСЯ ✗ (дельта {bytes.Length - structSize})");
Console.WriteLine();

if (structSize != bytes.Length)
{
    Console.WriteLine("Десериализация остановлена: размеры не совпадают.");
    return 1;
}

// Десериализация эталонного дампа
IntPtr ptr = Marshal.AllocHGlobal(bytes.Length);
try
{
    Marshal.Copy(bytes, 0, ptr, bytes.Length);
    var map = Marshal.PtrToStructure<FlashDataMap>(ptr)!;

    // --- MouseConfig ---
    Console.WriteLine("== MouseConfig ==");
    Console.WriteLine($"  reportRate (частота опроса): {map.mouseConfig.reportRate}");
    Console.WriteLine($"  maxDPI:    {map.mouseConfig.maxDPI}");
    Console.WriteLine($"  currentDPI: {map.mouseConfig.currentDPI}");
    Console.WriteLine($"  allLedOffTime: {map.mouseConfig.allLedOffTime}");
    Console.WriteLine();

    // --- DPI-профили ---
    Console.WriteLine("== DPIConfig[8] ==");
    for (int i = 0; i < map.dpiConfig.Length; i++)
    {
        var d = map.dpiConfig[i];
        var c = d.color ?? new byte[3];
        Console.WriteLine($"  [{i}] xDPI={d.xDPI,-4} yDPI={d.yDPI,-4} DPIex={d.DPIex,-4} color=({c[0]},{c[1]},{c[2]})");
    }
    Console.WriteLine();

    // --- Первые 6 слотов кнопок ---
    Console.WriteLine("== keys[0..5] (первые 6 слотов) ==");
    string[] classNames =
    {
        "CloseKey", "MouseKey", "ChangeDPI", "ACPAN", "FireKey",
        "Shortcut", "Macro", "ReportRate", "DecorLamp", "ChangeConfig", "DPILock"
    };
    for (int i = 0; i < 6; i++)
    {
        var k = map.keys[i];
        string cls = k.type < classNames.Length ? classNames[k.type] : $"Unknown({k.type})";
        Console.WriteLine($"  слот {i}: type={k.type} ({cls}) param1={k.param1} param2={k.param2}");
    }
    Console.WriteLine();

    // --- DPILed (индикатор) ---
    var led = map.dpiLed;
    Console.WriteLine("== DPILed (индикатор DPI) ==");
    Console.WriteLine($"  mode={led.mode} brightness={led.brightness} breathSpeed={led.breathSpeed} enable={led.enable}");
    Console.WriteLine();

    // --- LedBar ---
    var lb = map.ledBar;
    var lbc = lb.color ?? new byte[3];
    Console.WriteLine("== LedBar ==");
    Console.WriteLine($"  mode={lb.mode} color=({lbc[0]},{lbc[1]},{lbc[2]}) speed={lb.speed} brightness={lb.brightness} enable={lb.enable}");
    Console.WriteLine();

    // --- Проверка "живости" остальных массивов (что парсинг не поплыл) ---
    int nonEmptyKeys = map.keys.Count(k => k.type != 0 || k.param1 != 0 || k.param2 != 0);
    int nonEmptyMacros = map.macroKey.Count(m => m.nameLength != 0);
    int nonEmptyShortcuts = map.shortCutKey.Count(s => s.contextCount != 0);
    Console.WriteLine("== Целостность массивов ==");
    Console.WriteLine($"  непустых слотов кнопок: {nonEmptyKeys}/16");
    Console.WriteLine($"  непустых шорткатов:     {nonEmptyShortcuts}/16");
    Console.WriteLine($"  непустых макросов:      {nonEmptyMacros}/16");

    bool ok = structSize == bytes.Length && nonEmptyKeys > 0;
    Console.WriteLine();
    Console.WriteLine(ok ? "ИТОГ: ПРОВЕРКА ПРОЙДЕНА ✓" : "ИТОГ: ЕСТЬ ПРОБЛЕМЫ ✗");
    return ok ? 0 : 1;
}
finally
{
    Marshal.FreeHGlobal(ptr);
}
