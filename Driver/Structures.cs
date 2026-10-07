using System.Runtime.InteropServices;

namespace ImpactProConfig.Driver;

// Структуры перенесены 1-в-1 из декомпилированного DriverLib официалки.
// Layout — стандартный Sequential (как у вендора), размеры контролирует Marshal.

[StructLayout(LayoutKind.Sequential)]
public struct MouseConfig
{
    public byte reportRate;
    public byte maxDPI;
    public byte currentDPI;
    public byte xSpindown;
    public byte ySpindown;
    public byte silenceHeight;
    public byte keyDebounceTime;
    public byte motionSyncEnable;
    public byte allLedOffTime;
    public byte linearCorrectionEnable;
    public byte rippleControlEnable;
    public byte moveOffLedEnable;
    public byte sensorCustomSleepTimeEnable;
    public byte sensorSleepTime;
    public byte sensorPowerSavingModeEnable;
}

[StructLayout(LayoutKind.Sequential)]
public struct DPIConfig
{
    public byte xDPI;
    public byte yDPI;
    public byte DPIex;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public byte[] color;
}

[StructLayout(LayoutKind.Sequential)]
public struct DPILed
{
    public byte mode;
    public byte brightness;
    public byte breathSpeed;
    public byte enable;
}

[StructLayout(LayoutKind.Sequential)]
public struct LedBar
{
    public byte mode;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public byte[] color;

    public byte speed;
    public byte brightness;
    public byte enable;
}

[StructLayout(LayoutKind.Sequential)]
public struct KeyFunMap
{
    public byte type;
    public byte param1;
    public byte param2;
}

[StructLayout(LayoutKind.Sequential)]
public struct MacroContext
{
    public byte keyState;
    public byte type;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
    public byte[] value;

    public uint delay;
}

[StructLayout(LayoutKind.Sequential)]
public struct ShortCutKey
{
    public byte contextCount;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
    public MacroContext[] context;
}

[StructLayout(LayoutKind.Sequential)]
public struct MacroKey
{
    public byte nameLength;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 30)]
    public byte[] name;

    public byte contextCount;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 70)]
    public MacroContext[] context;
}

[StructLayout(LayoutKind.Sequential)]
public struct FlashDataMap
{
    public MouseConfig mouseConfig;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public DPIConfig[] dpiConfig;

    public DPILed dpiLed;

    public LedBar ledBar;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public KeyFunMap[] keys;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public ShortCutKey[] shortCutKey;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
    public MacroKey[] macroKey;

    public static FlashDataMap CreateEmpty()
    {
        return new FlashDataMap
        {
            mouseConfig = default,
            dpiConfig = new DPIConfig[8],
            dpiLed = default,
            ledBar = default,
            keys = new KeyFunMap[16],
            shortCutKey = new ShortCutKey[16],
            macroKey = new MacroKey[16]
        };
    }

    /// <summary>Глубокая копия (через marshal — как у вендора при работе с map).</summary>
    public static FlashDataMap Clone(FlashDataMap src)
    {
        int size = Marshal.SizeOf<FlashDataMap>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(src, ptr, false);
            return Marshal.PtrToStructure<FlashDataMap>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>Сериализация ровно как ConfigFile.FlashDataMapToByte официалки (для «Экспорт»).</summary>
    public static byte[] ToBytes(FlashDataMap map)
    {
        int size = Marshal.SizeOf<FlashDataMap>();
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(map, ptr, false);
            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, size);
            return bytes;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>Разбор файла как ConfigFile.ByteToFlashDataMap официалки (для «Импорт»).</summary>
    public static FlashDataMap FromBytes(byte[] data)
    {
        int size = Marshal.SizeOf<FlashDataMap>();
        if (data == null || data.Length < size)
            throw new ArgumentException($"Файл слишком мал: {data?.Length ?? 0} байт, ожидается {size}.");

        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(data, 0, ptr, size);
            return Marshal.PtrToStructure<FlashDataMap>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>Побайтовое сравнение двух map (проверка после записи).</summary>
    public static int CountDifferingBytes(FlashDataMap a, FlashDataMap b)
    {
        var ba = ToBytes(a);
        var bb = ToBytes(b);
        int diff = 0;
        for (int i = 0; i < ba.Length && i < bb.Length; i++)
            if (ba[i] != bb[i])
                diff++;
        return diff;
    }
}
