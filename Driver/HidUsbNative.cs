using System.Runtime.InteropServices;
using System.Text;

namespace ImpactProConfig.Driver;

/// <summary>
/// P/Invoke-сигнатуры hidusb.dll, перенесённые из декомпилированного
/// DriverLib (UsbServer.cs / DataParser.cs / UsbFinder.cs) официалки.
///
/// ВНИМАНИЕ: здесь объявлены ТОЛЬКО функции жизненного цикла и БЕЗОПАСНОГО
/// ЧТЕНИЯ. Функции записи (CS_ProtocolDataUpdate, CS_ProtocolDataCompareUpdate,
/// CS_UsbServer_SetClearSetting, ...) сюда НЕ включены намеренно — они появятся
/// отдельным классом с гейтом только при реализации режима «Применить».
/// </summary>
public static class HidUsbNative
{
    private const string Dll = "HIDUsb.dll";

    // Делегаты колбэков. ДОЛЖНЫ храниться в полях — иначе GC собьёт
    // управляемый код во время вызова нативной стороны.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnUsbDataReceived(IntPtr pcmd, int cmdLength, IntPtr pdata, int dataLength);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void OnUsbChanged(bool isUsbPluged);

    #region UsbServer — жизненный цикл

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_UsbServer_Start(
        StringBuilder inputEndpoint,
        StringBuilder outputEndpoint,
        OnUsbDataReceived onUsbDataReceived);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_UsbServer_Exit();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_UsbServer_SetPCDriverStatus(bool isActived);

    #endregion

    #region UsbServer — БЕЗОПАСНОЕ ЧТЕНИЕ

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_UsbServer_ReadAllFlashData();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_UsbServer_ReadOnLine();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_UsbServer_ReadBatteryLevel();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_UsbServer_ReadVersion();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_UsbServer_ReadConfig();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_UsbServer_ReadCidMid();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_UsbServer_ReadCurrentDPI();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_UsbServer_ReadReportRate();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_UsbServer_ReadDPILed();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_UsbServer_ReadLedBar();

    // Сопряжение с ресивером 2.4G (Re-Pairing) — из декомпилята DriverLib/UsbServer.cs:
    // FormPair.DongleEnterPairing -> EnterDonglePairOnlyCid(cid), далее опрос
    // ReadDonglePairStatus() каждую секунду (ответ команды id=6 GetPairState).
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_UsbServer_EnterDonglePairOnlyCid(byte cid);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_UsbServer_ReadDonglePairStatus();

    #endregion

    /// <summary>Смена активного профиля — команда устройству (не запись во флеш).</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_UsbServer_SetCurrentConfig(int configId);

    #region UsbFinder — детект устройств

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_BSTR)]
    public static extern string[] CS_UsbFinder_FindHidDevicesByDeviceId(
        StringBuilder vid, StringBuilder pid, int interfaceId, int deviceId);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool CS_UsbFinder_GetDeviceOnLine(StringBuilder endpoint);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_StartUsbChanged(OnUsbChanged onUsbChanged, int delayTimeout_ms);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_StopUsbChanged();

    #endregion

    #region DataParser — разбор полученных данных

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_ProtocolDataParser(byte[] data, IntPtr fashDataMap);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool CS_isDeviceOnLine(byte[] data);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_GetCidMid(byte[] data, IntPtr deviceCidMid);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_GetDeviceBatteryStatus(byte[] data, IntPtr batteryStatus);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_GetDeviceStatusChanged(byte[] data, IntPtr deviceStatusChanged);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int CS_GetDeviceVersion(byte[] data);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_BufferToDPILed(byte[] data, IntPtr dpiLed);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_BufferToLedBar(byte[] data, IntPtr ledBar);

    #endregion

    // ===== ГЕЙТ ЗАПИСИ =====
    // Вызовы ниже — ЕДИНСТВЕННЫЕ функции записи в проекте. Они вызываются
    // ТОЛЬКО из DeviceSession.WriteFlashAsync: ручное «Применить» или
    // автоприменение по таймеру (MainViewModel.ApplyAsync).
    // При старте приложения эти пути не используются (см. ConnectReadOnlyAsync).

    /// <summary>Запись всего FlashDataMap в мышь (DataParser.Update официалки).</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CS_ProtocolDataUpdate(IntPtr fashDataMap);

    /// <summary>Версия донгла из дескриптора (UsbFinder.GetSlaveVersion официалки) — чтение.</summary>
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern bool CS_UsbFinder_GetSlaveVersion(StringBuilder endpoint, out int slaveVersion);

    // ===== ВЫСОКОУРОВНЕВЫЕ ОБЁРТКИ (без ветвления x86/x64 —
    // приложение 64-битное, вендорский hidusb.dll тоже 64-битный) =====

    public static void Start(string endpoint, OnUsbDataReceived callback)
    {
        var ep = new StringBuilder(endpoint);
        CS_UsbServer_Start(ep, ep, callback);
    }

    public static void Exit() => CS_UsbServer_Exit();

    public static string[] FindDevices(string vid, string pid, int interfaceId, int deviceId) =>
        CS_UsbFinder_FindHidDevicesByDeviceId(
            new StringBuilder(vid), new StringBuilder(pid), interfaceId, deviceId);

    public static bool IsOnLine(string endpoint) =>
        CS_UsbFinder_GetDeviceOnLine(new StringBuilder(endpoint));

    /// <summary>Версия прошивки донгла (int → формат IntToVersion). Только чтение.</summary>
    public static bool TryGetSlaveVersion(string endpoint, out int version) =>
        CS_UsbFinder_GetSlaveVersion(new StringBuilder(endpoint), out version);

    /// <summary>Парсинг полного дампа флеша (6912 байт) в FlashDataMap — как DataParser.ProtocolParser.</summary>
    public static FlashDataMap ParseFlashData(byte[] buffer)
    {
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<FlashDataMap>());
        try
        {
            CS_ProtocolDataParser(buffer, ptr);
            return Marshal.PtrToStructure<FlashDataMap>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public static BatteryStatus ParseBatteryStatus(byte[] buffer)
    {
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<BatteryStatus>());
        try
        {
            CS_GetDeviceBatteryStatus(buffer, ptr);
            return Marshal.PtrToStructure<BatteryStatus>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public static DeviceStatusChanged ParseStatusChanged(byte[] buffer)
    {
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<DeviceStatusChanged>());
        try
        {
            CS_GetDeviceStatusChanged(buffer, ptr);
            return Marshal.PtrToStructure<DeviceStatusChanged>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public static DPILed ParseDpiLed(byte[] buffer)
    {
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<DPILed>());
        try
        {
            CS_BufferToDPILed(buffer, ptr);
            return Marshal.PtrToStructure<DPILed>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public static LedBar ParseLedBar(byte[] buffer)
    {
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<LedBar>());
        try
        {
            CS_BufferToLedBar(buffer, ptr);
            return Marshal.PtrToStructure<LedBar>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public static DeviceInfo ParseCidMid(byte[] buffer)
    {
        IntPtr ptr = Marshal.AllocHGlobal(Marshal.SizeOf<DeviceInfo>());
        try
        {
            CS_GetCidMid(buffer, ptr);
            return Marshal.PtrToStructure<DeviceInfo>(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}
