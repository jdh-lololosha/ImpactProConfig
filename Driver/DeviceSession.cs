using System.IO;
using System.Runtime.InteropServices;

namespace ImpactProConfig.Driver;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    ConnectedReadOnly,
    /// <summary>Связь оборвалась (кабель выдернут), идёт поиск активного эндпоинта.</summary>
    Reconnecting,
    Error
}

public sealed class FlashReadResult
{
    public required FlashDataMap Map { get; init; }
    public BatteryStatus Battery { get; set; }
    public int Version { get; set; }
    public DeviceInfo Device { get; set; }
}

/// <summary>Результат «Применить»: запись + побайтовая проверка перечитанным флешем.</summary>
public sealed class WriteResult
{
    public bool Success { get; init; }
    public int DiffBytes { get; init; }
    public string? Error { get; init; }
    public FlashDataMap VerifiedMap { get; init; }
}

/// <summary>Итог сопряжения мыши с ресивером (2.4G Re-Pairing).</summary>
public enum PairResult
{
    Success,        // GetPairState вернул 3 (вендор: PairSuccess)
    Fail,           // GetPairState вернул 2 (вендор: PairFail)
    Timeout,        // 30 секунд без ответа
    DeviceLost,     // донгл/устройство пропало из списка HID (Fail по FormPair)
    NotConnected,   // нет активной сессии
    WrongMode,      // подключение по кабелю — вендор разрешает только wireless
    Cancelled,
    Error
}

/// <summary>
/// Сессия с мышью. ГАРАНТИЯ БЕЗОПАСНОСТИ:
///  - при старте выполняются ТОЛЬКО функции чтения (см. HidUsbNative);
///  - ни одной записи во флеш: нет ни одного вызова записывающих
///    функций вендора (CS_ProtocolDataUpdate / CompareUpdate / SetClearSetting / ...);
///  - все USB-вызовы идут из Task.Run — UI не блокируется.
///
/// Последовательность подключения повторяет официалку (FormMain.DeviceConnect):
///  FindHidDevicesByDeviceId -> GetDeviceOnLine -> Start -> SetPCDriverStatus(true)
///  -> ReadAllFlashData -> ожидание полного дампа (6912 байт) -> парсинг.
/// </summary>
public sealed class DeviceSession : IDisposable
{
    // VID и PID из Config.ini официалки + спецификации устройства.
    // VID = 3554, кабель = F59A, ресивер = F53C.
    public const string Vid = "3554";
    public const string ReceiverPid = "F53C";
    public const string CablePid = "F59A";

    /// <summary>
    /// Порядок поиска устройства. ПРИОРИТЕТ У КАБЕЛЯ: на проводе мышь заряжается
    /// и радиоканал не тратится на передачу, поэтому при одновременном
    /// наличии обоих эндпоинтов выбираем F59A. Ресивер — запасной вариант.
    /// </summary>
    public static readonly string[] Pids = { CablePid, ReceiverPid };
    public const int InterfaceId = 1;
    public const int DeviceId = 5;

    private const int FlashDataLength = 6912;   // MAX_FLASH_SIZE из DataParser
    private const int ReadTimeoutMs = 8000;

    // Делегаты держим в полях, чтобы GC не убил их во время нативных вызовов.
    private readonly HidUsbNative.OnUsbDataReceived _dataReceived;
    private readonly HidUsbNative.OnUsbChanged _usbChanged;

    private readonly object _sync = new();
    private TaskCompletionSource<FlashReadResult>? _flashTcs;
    private TaskCompletionSource<BatteryStatus>? _batteryTcs;
    private TaskCompletionSource<int>? _versionTcs;
    private TaskCompletionSource<DeviceInfo>? _cidMidTcs;
    private bool _started;
    private bool _disposed;

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string? Endpoint { get; private set; }
    public string? ConnectedPid { get; private set; }

    /// <summary>true — активен кабель (F59A), false — ресивер (F53C).</summary>
    public bool IsCable => ConnectedPid == CablePid;

    /// <summary>
    /// Максимальная частота опроса для текущего подключения.
    /// На ресивере радиоканал ограничен 1000 Гц (вендор так и делает),
    /// на проводе доступны 2000/4000 Гц.
    /// </summary>
    public REPORT_RATE MaxReportRate => IsCable ? REPORT_RATE.R_4000 : REPORT_RATE.R_1000;

    /// <summary>Сколько раз подряд сессия переподключалась за один запуск (для диагностики).</summary>
    public int ReconnectCount { get; private set; }

    /// <summary>Полный дамп флеша, полученный последним (только чтение).</summary>
    public FlashDataMap FlashData { get; private set; }

    public event Action<ConnectionState>? StateChanged;
    public event Action<FlashDataMap>? FlashDataUpdated;
    public event Action<BatteryStatus>? BatteryUpdated;
    public event Action<byte>? CurrentDpiChanged;     // address=4, 1 байт
    public event Action<byte>? ReportRateChanged;     // address=0, 1 байт
    public event Action<DPILed>? DpiLedUpdated;
    public event Action<LedBar>? LedBarUpdated;
    public event Action<DeviceStatusChanged>? StatusChanged;
    public event Action<byte>? ProfileChanged;        // id=14, индекс профиля 0..3
    public event Action<DeviceInfo>? DeviceInfoUpdated;   // id=16, CID/MID/DeviceType
    public event Action<byte>? PairStateUpdated;          // id=6, сопряжение: 1=процесс, 2=fail, 3=success

    /// <summary>CID/MID/тип последнего успешного чтения (команда 16). CID нужен для сопряжения.</summary>
    public DeviceInfo Device { get; private set; }

    /// <summary>true — идёт сопряжение: watchdog и пересканы подавлены, чтобы не рвать сессию.</summary>
    public bool PairingActive => _pairingActive;

    public DeviceSession()
    {
        _dataReceived = OnUsbDataReceived;
        _usbChanged = OnUsbChangedEvent;

        // Watchdog хот-плага: страховка на случай, если WM_DEVICECHANGE и
        // вендорский watcher не пришли (или пришли раньше, чем HID-список
        // обновился). Раз в секунду сверяем активный эндпоинт с реальным
        // списком устройств (только presence через SetupAPI — безопасно
        // при активной сессии).
        _watchdog = new System.Threading.Timer(
            _ => WatchdogTick(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
    }

    private readonly System.Threading.Timer _watchdog;

    /// <summary>
    /// Периодическая сверка «что подключено на самом деле».
    ///  - активный эндпоинт пропал из списка -> Reconnecting + перескан;
    ///  - появился более приоритетный кабель при активном ресивере -> перескан;
    ///  - устройство появилось без USB-события (пропущенный DBT) -> перескан.
    /// </summary>
    private void WatchdogTick()
    {
        try
        {
            if (_rescanning || _pairingActive || State == ConnectionState.Connecting)
                return;

            string? preferred = FindDevice(out string pid);

            if (State == ConnectionState.ConnectedReadOnly)
            {
                // Всё честно: активный эндпоинт по-прежнему в списке,
                // и FindDevice (кабель первым) возвращает именно его.
                if (preferred != null &&
                    string.Equals(Endpoint, preferred, StringComparison.Ordinal) &&
                    string.Equals(pid, ConnectedPid, StringComparison.Ordinal))
                    return;

                Log($"watchdog: активный ep пропал (был {ConnectedPid}, сейчас {pid ?? "никого"}) -> Reconnecting");
                SetState(ConnectionState.Reconnecting);
                _ = Task.Run(() => RescanAsync(CancellationToken.None));
                return;
            }

            if (State == ConnectionState.Reconnecting)
            {
                // Застряли в переподключении без активного рескана — толкаем.
                _ = Task.Run(() => RescanAsync(CancellationToken.None));
                return;
            }

            // Disconnected/Error: устройство появилось, а события USB не пришло.
            // До ПЕРВОЙ попытки подключения не лезем: иначе watchdog на старте
            // гонится с InitializeAsync и делает второй Start на том же хэндле.
            // И не лезем, если это тот же эндпоинт, где мышь уже была OFFLINE —
            // иначе статус бы мигал «Подключение…/Отключено» каждые секунды.
            if (preferred != null &&
                _anyConnectAttempted &&
                !string.Equals(preferred, _offlineEp, StringComparison.Ordinal) &&
                Environment.TickCount64 - _lastRescan > 3000)
            {
                Log($"watchdog: pid={pid} появился без события USB -> Rescan");
                _ = Task.Run(() => RescanAsync(CancellationToken.None));
            }
        }
        catch (DllNotFoundException)
        {
            // hidusb.dll нет — не крашим фоновый поток.
        }
        catch (Exception ex)
        {
            Log($"watchdog: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void SetState(ConnectionState s)
    {
        State = s;
        Log($"state -> {s}" + (Endpoint != null ? $" ep={Endpoint} pid={ConnectedPid}" : ""));
        StateChanged?.Invoke(s);
    }

    // Компактный трассировочный лог: что читаем и что пришло. Полезен и как
        // доказательство отсутствия записей — здесь фиксируются только Read*-события.
    private static void Log(string msg)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(App.DataDir, "session.log"),
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch
        {
            // Лог не критичен.
        }
    }

    /// <summary>
    /// Поиск устройства по обоим PID (ресивер и кабель). Только чтение descriptor'ов.
    /// </summary>
    public string? FindDevice(out string pid)
    {
        foreach (var p in Pids)
        {
            string[] endpoints;
            try
            {
                endpoints = HidUsbNative.FindDevices(Vid, p, InterfaceId, DeviceId);
            }
            catch (DllNotFoundException)
            {
                throw;
            }

            if (endpoints is { Length: > 0 } && endpoints[0] is { Length: > 0 } ep)
            {
                pid = p;
                return ep;
            }
        }

        pid = string.Empty;
        return null;
    }

    /// <summary>
    /// Безопасное подключение: только чтение состояния и флеша.
    /// Полностью в фоне (вызывать из Task.Run).
    ///
    /// Сериализация обязательна: watchdog, USB-события и InitializeAsync могут
    /// прийти одновременно, а двойной Start на одном хэндле повреждает сессию
    /// вендорской библиотеки (в логе это выглядело как два Connecting подряд).
    /// </summary>
    public async Task<FlashReadResult?> ConnectReadOnlyAsync(CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct);
        try
        {
            _anyConnectAttempted = true;

            // Уже живая сессия — повторный Start не нужен.
            if (_started && State == ConnectionState.ConnectedReadOnly)
            {
                Log("connect: сессия уже активна — повторный Start пропущен");
                return null;
            }

            SetState(ConnectionState.Connecting);

            return await Task.Run(() =>
            {
            string? endpoint = FindDevice(out string pid);
            if (endpoint == null)
            {
                Log("scan: устройство 3554:F53C/F59A не найдено");
                StopInternal();                     // чистим даже то, что было раньше
                SetState(ConnectionState.Disconnected);
                return null;
            }

            Log($"scan: найдено pid={pid} ep={endpoint}");

            // Официальная последовательность (FormMain.DeviceConnect), только чтение.
            if (!HidUsbNative.IsOnLine(endpoint))
            {
                // Эндпоинт есть, но мышь не онлайн — это не ошибка, просто не
                // подключаемся. Главное: НЕ помечаем сессию как «подключённую» —
                // иначе статус соврёт (залипший «провод» при мёртвом кабеле).
                Log("scan: устройство найдено, но мышь OFFLINE");
                _offlineEp = endpoint;              // не пытаемся переподключаться по таймеру
                StopInternal();
                SetState(ConnectionState.Disconnected);
                return null;
            }

            // Живой эндпоинт подтверждён — только теперь идентифицируем сессию.
            Endpoint = endpoint;
            ConnectedPid = pid;
            Log("scan: мышь ONLINE -> Start(только чтение)");

            _flashTcs = new TaskCompletionSource<FlashReadResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _batteryTcs = new TaskCompletionSource<BatteryStatus>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _versionTcs = new TaskCompletionSource<int>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _cidMidTcs = new TaskCompletionSource<DeviceInfo>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            HidUsbNative.Start(endpoint, _dataReceived);
            _started = true;

            // Уведомление драйвера о работе ПО — не запись во флеш (команда статуса).
            HidUsbNative.CS_UsbServer_SetPCDriverStatus(true);

            // ЕДИНСТВЕННЫЙ запрос данных при старте — только чтение флеша.
            HidUsbNative.CS_UsbServer_ReadAllFlashData();
            HidUsbNative.CS_UsbServer_ReadBatteryLevel();
            HidUsbNative.CS_UsbServer_ReadVersion();
            HidUsbNative.CS_UsbServer_ReadConfig();     // индекс активного профиля
            HidUsbNative.CS_UsbServer_ReadCidMid();     // CID/MID — выбор образа корпуса

            using var reg = ct.Register(() =>
            {
                _flashTcs.TrySetCanceled(ct);
                _batteryTcs.TrySetCanceled(ct);
                _versionTcs.TrySetCanceled(ct);
                _cidMidTcs.TrySetCanceled(ct);
            });

            try
            {
                // Ждём полный дамп. Батарея/версия могут прийти позже — не блокируем ими.
                var flashTask = _flashTcs.Task;
                if (!flashTask.Wait(ReadTimeoutMs, ct))
                {
                    Log("scan: таймаут чтения флеша -> teardown");
                    StopInternal();                 // не оставляем мёртвый хэндл
                    SetState(ConnectionState.Error);
                    return null;
                }

                var result = flashTask.Result;
                if (_batteryTcs!.Task is { IsCompletedSuccessfully: true } batTask)
                    result.Battery = batTask.Result;
                if (_versionTcs!.Task is { IsCompletedSuccessfully: true } verTask)
                    result.Version = verTask.Result;
                if (_cidMidTcs!.Task is { IsCompletedSuccessfully: true } cidTask)
                {
                    result.Device = cidTask.Result;
                    Device = result.Device;
                    Log($"READ cidmid: CID={result.Device.CID} MID={result.Device.MID} type={result.Device.DeviceType}");
                    DeviceInfoUpdated?.Invoke(result.Device);
                }
                else
                {
                    Log("READ cidmid: ответа нет — образ корпуса останется ручным");
                }

                FlashData = result.Map;
                _offlineEp = null;
                SetState(ConnectionState.ConnectedReadOnly);
                FlashDataUpdated?.Invoke(result.Map);
                return result;
            }
            catch (OperationCanceledException)
            {
                SetState(ConnectionState.Disconnected);
                return null;
            }
            catch (Exception)
            {
                SetState(ConnectionState.Error);
                return null;
            }
            }, ct);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// ГЕЙТ ЗАПИСИ. Вызывается ТОЛЬКО по явному нажатию «Применить».
    /// Путь повторяет официалку: DataParser.Update(gFlashDataMap) ->
    /// CS_ProtocolDataUpdate(ptr). После записи флеш перечитывается и
    /// сравнивается побайтово с записанным map.
    /// </summary>
    public Task<WriteResult> WriteFlashAsync(FlashDataMap map, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            int size = Marshal.SizeOf<FlashDataMap>();
            Log($"WRITE: «Применить» -> CS_ProtocolDataUpdate (структура {size} байт)");

            try
            {
                IntPtr ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(map, ptr, false);
                    HidUsbNative.CS_ProtocolDataUpdate(ptr);
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
            catch (Exception ex)
            {
                Log($"WRITE: исключение {ex.GetType().Name}: {ex.Message}");
                return new WriteResult { Success = false, Error = ex.Message };
            }

            // Проверка: перечитываем флеш (до 2 попыток) и сравниваем.
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    _flashTcs = new TaskCompletionSource<FlashReadResult>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    HidUsbNative.CS_UsbServer_ReadAllFlashData();

                    if (!_flashTcs.Task.Wait(ReadTimeoutMs, ct))
                    {
                        Log($"WRITE verify: таймаут чтения (попытка {attempt}/2)");
                        continue;
                    }

                    var reread = _flashTcs.Task.Result;
                    int diff = FlashDataMap.CountDifferingBytes(map, reread.Map);
                    Log($"WRITE verify: чтение OK, расхождение с записанным: {diff} байт");

                    FlashData = reread.Map;
                    FlashDataUpdated?.Invoke(reread.Map);
                    return new WriteResult { Success = true, DiffBytes = diff, VerifiedMap = reread.Map };
                }
                catch (Exception ex)
                {
                    Log($"WRITE verify: попытка {attempt}: {ex.Message}");
                }
            }

            return new WriteResult
            {
                Success = false,
                Error = "Запись выполнена, но перечитать флеш не удалось"
            };
        }, ct);
    }

    /// <summary>
    /// Смена профиля: команда SetCurrentConfig + перечитывание флеша нового
    /// профиля (без записи флеша). Повторяет FormMain.customComboBox_Config_OnSelectedIndexChanged.
    /// </summary>
    public Task<WriteResult> SwitchProfileAsync(int configIndex, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            Log($"PROFILE: CS_UsbServer_SetCurrentConfig({configIndex}) + перечитывание флеша");
            try
            {
                HidUsbNative.CS_UsbServer_SetCurrentConfig(configIndex);
            }
            catch (Exception ex)
            {
                Log($"PROFILE: исключение {ex.GetType().Name}: {ex.Message}");
                return new WriteResult { Success = false, Error = ex.Message };
            }

            try
            {
                _flashTcs = new TaskCompletionSource<FlashReadResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                HidUsbNative.CS_UsbServer_ReadAllFlashData();

                if (!_flashTcs.Task.Wait(ReadTimeoutMs, ct))
                {
                    Log("PROFILE: таймаут чтения флеша нового профиля");
                    return new WriteResult { Success = false, Error = "Таймаут чтения профиля" };
                }

                var reread = _flashTcs.Task.Result;
                FlashData = reread.Map;
                FlashDataUpdated?.Invoke(reread.Map);
                HidUsbNative.CS_UsbServer_ReadConfig(); // подтвердить индекс профиля в устройстве
                Log($"PROFILE: профиль {configIndex} перечитан");
                return new WriteResult { Success = true, VerifiedMap = reread.Map };
            }
            catch (Exception ex)
            {
                Log($"PROFILE: {ex.Message}");
                return new WriteResult { Success = false, Error = ex.Message };
            }
        }, ct);
    }

    /// <summary>Отслеживание вставки/выдёргивания USB (по образцу официалки, 600 мс).</summary>
    public void StartUsbWatcher()
    {
        try
        {
            HidUsbNative.CS_StartUsbChanged(_usbChanged, 600);
        }
        catch
        {
            // Watcher не критичен для чтения — глушим ошибки.
        }
    }

    private void OnUsbChangedEvent(bool plugged)
    {
        // Во время сопряжения донгл может перечисляться заново — это не повод
        // рвать сессию (вендор в FormPair гасит свои таймеры на это время).
        if (_pairingActive)
        {
            Log($"usb: changed plugged={plugged} во время сопряжения — пропускаем");
            return;
        }

        // Вставка и извлечение обрабатываются одинаково: перескан сам решит,
        // что делать. Эндпоинт на месте -> дешёвый no-op; пропал -> полный
        // teardown и переключение на второй интерфейс (кабель <-> ресивер).
        Log($"usb: changed plugged={plugged} -> Rescan");
        _ = Task.Run(() => RescanAfterUsbEventAsync());
    }

    /// <summary>
    /// Быстрый фоновый перескан устройств. Вызывается по WM_DEVICECHANGE, по
    /// вендорскому watcher'у и по watchdog'у. Не блокирует UI.
    ///
    /// Честный алгоритм (приоритет кабеля):
    ///  1. Есть F59A (провод) -> подключаемся к нему, статус «провод», зарядка.
    ///  2. F59A нет/не отвечает -> пробуем F53C (ресивер) -> «ресивер», 1000 Гц.
    ///  3. Ничего нет -> полный teardown, статус «Отключено».
    /// </summary>
    public async Task RescanAsync(CancellationToken ct = default)
    {
        // Сопряжение идёт: активная сессия не трогаем до его завершения.
        if (_pairingActive)
            return;

        // Не запускаем несколько пересканов одновременно.
        lock (_sync)
        {
            if (_rescanning)
                return;
            _rescanning = true;
            _lastRescan = Environment.TickCount64;
        }

        try
        {
            // Короткая задержка: сразу после DBT_DEVICEREMOVECOMPLETE HID-список
            // может быть ещё не обновлен. Даём Windows время на обработку.
            await Task.Delay(150, ct);

            // FindDevice перебирает Pids = {F59A, F53C}: кабель первым.
            // Presence-проверка через SetupAPI: выдернутый кабель в списке уже
            // не значится — старый хэндл честно закрываем ниже.
            string? endpoint = FindDevice(out string pid);
            if (endpoint == null)
            {
                if (State != ConnectionState.Disconnected || ConnectedPid != null || Endpoint != null)
                {
                    Log("rescan: ни кабеля, ни ресивера -> teardown + Disconnected");
                    StopInternal();                 // закрыть хэндл, обнулить ConnectedPid
                    SetState(ConnectionState.Disconnected);
                }
                _offlineEp = null;
                _retries = 0;
                return;
            }

            // Предпочтительный эндпоинт уже активен — перезапуск не нужен.
            if (State == ConnectionState.ConnectedReadOnly &&
                string.Equals(Endpoint, endpoint, StringComparison.Ordinal) &&
                string.Equals(pid, ConnectedPid, StringComparison.Ordinal))
            {
                return;
            }

            Log($"rescan: найден pid={pid} ep={endpoint} (был: {ConnectedPid ?? "никого"})");
            ReconnectCount++;
            if (State == ConnectionState.ConnectedReadOnly)
                SetState(ConnectionState.Reconnecting);

            // Полный teardown старой сессии: хэндл закрыт, ConnectedPid=null.
            StopInternal();

            var result = await ConnectReadOnlyAsync(ct);
            if (result != null)
            {
                _retries = 0;
                return;
            }

            // Другой поток успел подключить сессию параллельно — она здорова.
            if (State == ConnectionState.ConnectedReadOnly)
            {
                _retries = 0;
                return;
            }

            // Не подключились: Disconnected (устройства/мышь нет) — ждём нового
            // события; прочие состояния — ограниченная серия повторов.
            if (State == ConnectionState.Disconnected || _retries++ >= MaxRetries)
            {
                Log($"rescan: переподключение не удалось (попытка {_retries}) — ждём события USB");
                _retries = 0;
                return;
            }

            Log("rescan: повтор через 1 с");
            await Task.Delay(1000, ct);
            _ = Task.Run(() => RescanAsync(CancellationToken.None));
        }
        catch (OperationCanceledException)
        {
            // Отмена — не ошибка.
        }
        catch (Exception ex)
        {
            Log($"rescan: исключение {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lock (_sync)
            {
                _rescanning = false;
            }
        }
    }

    /// <summary>Закрыть текущую USB-сессию без изменения состояния (для переподключения).</summary>
    private void StopInternal()
    {
        if (_started)
        {
            try
            {
                HidUsbNative.CS_UsbServer_SetPCDriverStatus(false);
                HidUsbNative.Exit();
            }
            catch
            {
                // Ошибки закрытия не критичны.
            }
            _started = false;
        }

        // Очищаем идентификацию сессии ВСЕГДА, даже если хэндл уже закрыт или
        // никогда не открывался. Иначе ConnectedPid остаётся=F59A после смерти
        // кабеля и ConnectionText залипает в «Подключено (провод)».
        Endpoint = null;
        ConnectedPid = null;
    }

    private bool _rescanning;
    private int _retries;                       // неудачные попытки переподключения
    private long _lastRescan;                   // TickCount64 последнего перескана
    private string? _offlineEp;                 // эндпоинт, где мышь была OFFLINE
    private const int MaxRetries = 5;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private volatile bool _anyConnectAttempted; // была ли хоть одна попытка подключения
    private volatile bool _pairingActive;       // идёт сопряжение: не рвём сессию пересканом

    /// <summary>
    /// Перескан по явному событию USB (WM_DEVICECHANGE / вендорский watcher).
    /// Сбрасывает memo OFFLINE: после физической вставки пробуем снова,
    /// даже если раньше на этом эндпоинте мышь была не онлайн.
    /// </summary>
    public Task RescanAfterUsbEventAsync()
    {
        _offlineEp = null;
        return RescanAsync(CancellationToken.None);
    }

    // ===== Сопряжение с ресивером (2.4G Re-Pairing) =====

    /// <summary>
    /// Сопряжение мыши с USB-ресивером. Путь 1-в-1 из FormPair (декомпилят):
    ///  1) CS_UsbServer_EnterDonglePairOnlyCid(cid) — донгл входит в pairing mode;
    ///  2) раз в секунду CS_UsbServer_ReadDonglePairStatus() — как PairingTimer вендора;
    ///  3) ответ команды id=6 (GetPairState): data[0]=1 в процессе, 2 fail, 3 success;
    ///  4) устройство пропало из списка HID -> DeviceLost (по PairingTimer_Tick).
    /// Таймаут 30 с. Только фон (async). Сопряжение только в wireless-режиме:
    /// вендор при кабеле (isUSB) не пускает в паринг (LanguageFile Dialogs[46]).
    /// </summary>
    public async Task<PairResult> PairWithReceiverAsync(CancellationToken ct = default)
    {
        if (State != ConnectionState.ConnectedReadOnly)
        {
            Log("pair: нет активной сессии");
            return PairResult.NotConnected;
        }

        if (IsCable)
        {
            Log("pair: активен кабель — сопряжение только через ресивер (wireless mode)");
            return PairResult.WrongMode;
        }

        // CID продукта из команды 16. Если чтение не ответило — вендорский
        // дефолт Config.ini: Impact PRO16 -> CID = 16.
        if (Device.CID == 0)
            Log("pair: CID не прочитан — используем продуктовый CID из Config.ini");
        byte cid = Device.CID != 0 ? Device.CID : Services.MouseSkin.ExpectedCid;

        _pairingActive = true;
        try
        {
            var done = new TaskCompletionSource<byte>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            void OnPairState(byte s)
            {
                if (s == 2 || s == 3)
                    done.TrySetResult(s);          // 2 = Fail, 3 = Success
            }

            PairStateUpdated += OnPairState;
            try
            {
                Log($"pair: EnterDonglePairOnlyCid(cid={cid})");
                HidUsbNative.CS_UsbServer_EnterDonglePairOnlyCid(cid);

                long deadline = Environment.TickCount64 + 30_000;
                while (true)
                {
                    await Task.Delay(1000, ct).ConfigureAwait(false);

                    if (done.Task.IsCompleted)
                        return done.Task.Result == 3 ? PairResult.Success : PairResult.Fail;

                    if (Environment.TickCount64 >= deadline)
                    {
                        Log("pair: таймаут 30 с");
                        return PairResult.Timeout;
                    }

                    if (FindDevice(out _) == null)
                    {
                        Log("pair: устройство пропало из списка HID");
                        return PairResult.DeviceLost;
                    }

                    HidUsbNative.CS_UsbServer_ReadDonglePairStatus();
                }
            }
            finally
            {
                PairStateUpdated -= OnPairState;
            }
        }
        catch (OperationCanceledException)
        {
            return PairResult.Cancelled;
        }
        catch (Exception ex)
        {
            Log($"pair: исключение {ex.GetType().Name}: {ex.Message}");
            return PairResult.Error;
        }
        finally
        {
            _pairingActive = false;
        }
    }

    // ===== Разбор ответов — 1-в-1 по логике FormMain.onUsbDataReceived =====

    private void OnUsbDataReceived(IntPtr pcmd, int cmdLength, IntPtr pdata, int dataLength)
    {
        if (cmdLength <= 0)
            return;

        var cmdBytes = new byte[cmdLength];
        Marshal.Copy(pcmd, cmdBytes, 0, cmdLength);
        if (cmdBytes.Length < 6)
            return;

        var command = new UsbCommand
        {
            ReportId = cmdBytes[0],
            id = cmdBytes[1],
            CommandStatus = cmdBytes[2],
            address = (cmdBytes[3] << 8) | cmdBytes[4]
        };

        byte[]? data = null;
        if (dataLength > 0)
        {
            data = new byte[dataLength];
            Marshal.Copy(pdata, data, 0, dataLength);
        }
        command.receivedData = data;

        if (data == null || data.Length == 0)
            return;

        var id = (UsbCommandID)command.id;

        // Полный дамп флеша: address==0, ровно 6912 байт.
        if (command.address == 0 && data.Length == FlashDataLength)
        {
            try
            {
                var map = HidUsbNative.ParseFlashData(data);
                Log($"READ flash: {data.Length} байт -> FlashDataMap 10428 (parсинг OK)");
                var result = new FlashReadResult { Map = map, Battery = default };
                _flashTcs?.TrySetResult(result);
            }
            catch
            {
                Log("READ flash: ОШИБКА парсинга");
                _flashTcs?.TrySetException(new InvalidOperationException("Ошибка парсинга флеша"));
            }
            return;
        }

        switch (id)
        {
            case UsbCommandID.BatteryLevel:
                var bat = HidUsbNative.ParseBatteryStatus(data);
                Log($"READ battery: level={bat.level} charging={bat.isCharging} voltage={bat.BatVoltage}");
                _batteryTcs?.TrySetResult(bat);
                BatteryUpdated?.Invoke(bat);
                break;

            case UsbCommandID.ReadCIDMID:
                var info = HidUsbNative.ParseCidMid(data);
                Log($"READ cidmid: CID={info.CID} MID={info.MID} type={info.DeviceType}");
                Device = info;
                _cidMidTcs?.TrySetResult(info);
                DeviceInfoUpdated?.Invoke(info);
                break;

            case UsbCommandID.GetPairState:
                // Ответ ReadDonglePairStatus (FormPair.PairingUsbDataReceived):
                // data[0]: 1 = в процессе, 2 = fail, 3 = success.
                if (data.Length >= 1)
                {
                    Log($"pair: GetPairState -> {data[0]}");
                    PairStateUpdated?.Invoke(data[0]);
                }
                break;

            case UsbCommandID.ReadVersionID:
                int ver = HidUsbNative.CS_GetDeviceVersion(data);
                _versionTcs?.TrySetResult(ver);
                break;

            case UsbCommandID.GetCurrentConfig:
                // Ответ ReadConfig: data[0] = индекс активного профиля 0..3
                // (FormMain: ConfigParam[1] сравнивается с SelectIndex комбобокса).
                if (data.Length >= 1)
                {
                    Log($"READ config: активный профиль {data[0]}");
                    ProfileChanged?.Invoke(data[0]);
                }
                break;

            case UsbCommandID.DeviceOnLine:
                // Смена DPI/частоты приходит как одиночный байт (FormMain: address=0/4, len=1).
                if (data.Length == 1 && command.address == 4)
                {
                    var map = FlashData;
                    map.mouseConfig.currentDPI = data[0];
                    FlashData = map;
                    CurrentDpiChanged?.Invoke(data[0]);
                }
                else if (data.Length == 1 && command.address == 0)
                {
                    var map = FlashData;
                    map.mouseConfig.reportRate = data[0];
                    FlashData = map;
                    ReportRateChanged?.Invoke(data[0]);
                }
                break;

            case UsbCommandID.StatusChanged:
                var changed = HidUsbNative.ParseStatusChanged(data);
                StatusChanged?.Invoke(changed);
                if (changed.isDPILedChanged != 0)
                    HidUsbNative.CS_UsbServer_ReadDPILed();
                if (changed.isBatteryLevelChanged != 0)
                    HidUsbNative.CS_UsbServer_ReadBatteryLevel();
                break;

            case UsbCommandID.EncryptionData:
                // DPILed: address=76, 8 байт; LedBar: address=160, 9 байт (FormMain).
                if (command.address == 76 && data.Length == 8)
                {
                    var led = HidUsbNative.ParseDpiLed(data);
                    var map = FlashData;
                    map.dpiLed = led;
                    FlashData = map;
                    DpiLedUpdated?.Invoke(led);
                }
                else if (command.address == 160 && data.Length == 9)
                {
                    var bar = HidUsbNative.ParseLedBar(data);
                    var map = FlashData;
                    map.ledBar = bar;
                    FlashData = map;
                    LedBarUpdated?.Invoke(bar);
                }
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            _watchdog.Dispose();
        }
        catch
        {
            // Таймер не критичен.
        }

        StopInternal();

        try
        {
            HidUsbNative.CS_StopUsbChanged();
        }
        catch
        {
            // ignore
        }
    }
}
