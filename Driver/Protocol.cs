using System.Runtime.InteropServices;

namespace ImpactProConfig.Driver;

// Протокольные типы 1-в-1 из декомпилированного DriverLib официалки.

public enum UsbCommandID : byte
{
    EncryptionData = 1,
    PCDriverStatus = 2,
    DeviceOnLine = 3,
    BatteryLevel = 4,
    DongleEnterPair = 5,
    GetPairState = 6,
    WriteFlashData = 7,
    ReadFlashData = 8,
    ClearSetting = 9,
    StatusChanged = 10,
    SetDeviceVidPid = 11,
    SetDeviceDescriptorString = 12,
    EnterUsbUpdateMode = 13,
    GetCurrentConfig = 14,
    SetCurrentConfig = 15,
    ReadCIDMID = 16,
    EnterMTKMode = 17,
    ReadVersionID = 18,
    Set4KDongleRGB = 20,
    Get4KDongleRGBValue = 21,
    SetLongRangeMode = 22,
    GetLongRangeMode = 23
}

public enum REPORT_RATE : byte
{
    R_1000 = 1,
    R_500 = 2,
    R_250 = 4,
    R_125 = 8,
    R_2000 = 0x10,
    R_4000 = 0x20
}

public enum KEY_CLASS : byte
{
    KC_CloseKey,
    KC_MouseKey,
    KC_ChangeDPIKey,
    KC_MouseACPANKey,
    KC_MouseFireKey,
    KC_ShortcutKey,
    KC_MacroKey,
    KC_ChangeReportRateKey,
    KC_DecorativeLampKey,
    KC_ChangeConfigKey,
    KC_DPILockKey
}

public enum MouseKey : byte
{
    LeftKey = 1,
    RightKey = 2,
    MiddleKey = 4,
    BackKey = 8,
    ForwardKey = 0x10
}

public enum ChangeDPIKey : byte
{
    Loop = 1,
    Add = 2,
    Dec = 3
}

public struct DeviceInfo
{
    public byte CID;
    public byte MID;
    public byte DeviceType;
}

public struct BatteryStatus
{
    public byte level;
    public byte isCharging;
    public ushort BatVoltage;
}

public struct DeviceStatusChanged
{
    public byte isDPIChanged;
    public byte isReportRateChanged;
    public byte isConfigChanged;
    public byte isDPILedChanged;
    public byte isLogoLedChanged;
    public byte isLedBarChanged;
    public byte isBatteryLevelChanged;
}

// USB-пакет ответа (разбор байт — 1-в-1 из UsbServer.UserUsbDataReceived).
public struct UsbCommand
{
    public byte ReportId;
    public byte id;
    public byte CommandStatus;
    public int address;
    public byte[]? command;
    public byte[]? receivedData;
}
