# ARDOR GAMING Impact PRO Config

[English](#english) · [Русский](#русский)

Unofficial configuration utility for the **ARDOR GAMING Impact PRO** gaming mouse — a dark-themed WPF (.NET 8) replacement for the vendor software, with a live OSD overlay and low-battery notifications.

[![Vibe-coded with AI](https://img.shields.io/badge/%E2%9C%A8_vibe--coded_with_AI-ff69b4?style=for-the-badge&logo=openai&logoColor=white)](https://github.com/jdh-lololosha/ImpactProConfig)

> **✨ Vibe-coded with AI.** This project was written with the help of neural networks (OpenCode), under the attentive human guidance and testing on real hardware.

---

<a name="english"></a>
## English

### Features

- **Full device configuration** — DPI levels, report rate, polling, scroll/sensitivity parameters.
- **Button mapping** — assign any action to each button, including the special **“Show mouse status (OSD)”** action.
- **OSD overlay** — press an assigned button and a glass-style overlay shows battery %, charge state, connection type (2.4G / Type-C) and estimated runtime.
- **Low battery toast** — Windows toast notification when the charge drops below 15% (once per discharge session; resets above 20% or when plugged in).
- **Honest multi-monitor support** — monitors are enumerated via Win32 `EnumDisplayMonitors` / `GetMonitorInfo` (resolution and primary flag included), and the OSD is placed strictly inside the **work area (rcWork)** of the selected monitor.
- **Import / export** — vendor-format config files (10 428 bytes), plus local JSON profiles.
- **No flash writes at startup** — the mouse is written to only when you press **Apply**; all other changes are local.
- **Live battery tray icon** — the taskbar icon is drawn in real time: a colour-coded charge bar (green / yellow / red) with a bolt while charging. Hover tooltip reads `Impact PRO: [XX]% • Wireless/Wired`. Right-click menu: Open, Profile 1..4, Exit.
- **Battery telemetry** — discharge history is recorded to `battery_stats.json`, giving a measured drain rate in %/h and an estimate like `~12 h active gaming`. Both come from actual observations, not a hardcoded table; until the mouse has discharged, the card says more data is needed.
- **In-app updates** — a background check against the GitHub Releases API. If a newer tag exists, an InfoBar offers **Download and update**, which fetches the `.msi` from the release and starts the installer.
- **Theme accents** — five accent palettes (Ardor Red, Sakura Pink, Cyberpunk Cyan, Toxic Green, Deep Violet). Sliders, the active-DPI frame, the mouse podium glow and buttons repaint instantly.
- **Mouse body image** — pick Black / White / Pink, or let **Auto** follow the device's MID. The image on the Buttons tab and the podium glow switch immediately.
- **Hot-plug cable ↔ receiver** — plugging or unplugging the USB cable switches the active connection in the background without restarting. When both interfaces are present the cable wins (charging + no radio overhead); pulling it falls back to the 2.4G receiver. The status bar reads `Подключено (провод)` / `Подключено (ресивер)`, and 2000/4000 Hz report rates are only offered on cable.
- **Dark-only UI** with the Ardor red accent (#E81123 / #FF2E2E).

### How "Auto (by MID)" actually works

The vendor treats `dev1` / `dev2` / `dev3` as **Config.ini slots, not colours**. `Config.ini` holds `DeviceTotal=3` with `[Device1] MID=4`, `[Device2] MID=5`, `[Device3] MID=6`; `FormHomePage` reads the mouse's MID and picks the matching image. There is no MID→colour table anywhere in the vendor files, so Auto maps `MID 4→dev1, 5→dev2, 6→dev3` and logs an unknown MID rather than silently guessing. This app now reads MID from the device (command 16, `CS_UsbServer_ReadCidMid`) — the previous version declared that command but never called it.

### Requirements

- Windows 10 (1809 / build 17763) or later, x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (included in the self-contained build)
- ARDOR GAMING Impact PRO mouse (2.4G receiver or USB Type-C)

### Installation

Download **ImpactProConfig-v1.1.0-Setup.msi** from the [Releases](../../releases) page and run it. The installer creates:

- the application in `Program Files` (or per-user location),
- a desktop shortcut,
- a Start Menu shortcut.

`hidusb.dll` (the protocol transport library) is installed next to the executable — the application will not work without it.

### Build from source

```powershell
git clone https://github.com/jdh-lololosha/ImpactProConfig.git
cd ImpactProConfig
dotnet build -c Release
```

### Disclaimer

This is third-party software. It communicates with the mouse through the same HID protocol used by the official driver; use it at your own risk.

### License

[CC BY-NC 4.0 International](LICENSE) — free for non-commercial use with attribution.

---

<a name="русский"></a>
## Русский

> **✨ Навайбкожено при помощи нейросетей (OpenCode)** под чутким человеческим руководством и тестированием на реальном железе.

### Возможности

- **Полная настройка мыши** — уровни DPI, частота опроса, параметры прокрутки и чувствительности.
- **Назначение кнопок** — любое действие для каждой кнопки, включая отдельное действие **«Показать статус мыши (OSD)»**.
- **OSD-оверлей** — нажимаете назначенную кнопку, и на экране появляется тёмное стеклянное окно со статусом: заряд %, режим работы (заряжается / от батареи), тип подключения (2.4G / Type-C) и оценка времени работы.
- **Уведомление о низком заряде** — всплывающее уведомление Windows при заряде ниже 15% (один раз за разряд; сброс выше 20% или при подключении кабеля).
- **Честная работа с мониторами** — перечисление через Win32 `EnumDisplayMonitors` / `GetMonitorInfo` (с разрешением и флагом основного), OSD размещается строго в **рабочей области (rcWork)** выбранного монитора.
- **Импорт / экспорт** — конфиги формата официалки (10 428 байт) и локальные JSON-профили.
- **Нет записи во флеш при запуске** — мышь записывается только по кнопке **«Применить»**, всё остальное хранится локально.
- **Живая иконка батареи в трее** — значок рисуется в реальном времени: цветная полоска заряда (зелёный / жёлтый / красный) и знак ⚡ при зарядке. Подсказка при наведении: `Impact PRO: [XX]% • Беспроводной/Провод`. Меню трея: Открыть, Профиль 1..4, Выход.
- **Телеметрия батареи** — история разряда пишется в `battery_stats.json`, из неё считается реальная скорость расхода (%/ч) и оценка вида «~12 ч активной игры». Цифры берутся из наблюдений, а не из таблицы: пока мышь не разряжалась, карточка честно пишет «нужно больше данных».
- **Обновление внутри приложения** — фоновая проверка через GitHub Releases API. Если тег новее текущей версии, внизу окна появляется плашка **«Скачать и обновить»**: она качает `.msi` из релиза и запускает установщик.
- **Цветовые темы** — пять акцентных палитр (Ardor Red, Sakura Pink, Cyberpunk Cyan, Toxic Green, Deep Violet). Слайдеры, рамка активного DPI, подиум и кнопки перекрашиваются мгновенно.
- **Образ корпуса мыши** — выбор Чёрный / Белый / Розовый либо **Авто** по MID устройства. Картинка на вкладке «Кнопки» и свечение подиума меняются сразу.
- **Хот-плаг провод ↔ ресивер** — вставка или извлечение кабеля переключает активное подключение в фоне, без перезапуска. Если доступны оба интерфейса, приоритет у кабеля (зарядка и нет нагрузки на радиоканал); выдернули — приложение уходит на ресивер 2.4G. В статус-баре: «Подключено (провод)» / «Подключено (ресивер)», частоты 2000/4000 Гц доступны только на проводе.
- **Тёмная тема** с акцентом Ardor red (#E81123 / #FF2E2E).

### Как на самом деле работает «Авто (по MID)»

У вендора `dev1` / `dev2` / `dev3` — это **слоты Config.ini, а не цвета**. В `Config.ini` записано `DeviceTotal=3` и секции `[Device1] MID=4`, `[Device2] MID=5`, `[Device3] MID=6`; `FormHomePage` читает MID мыши и берёт картинку по совпадению. Таблицы MID→цвет в файлах вендора нет нигде, поэтому «Авто» сопоставляет `MID 4→dev1, 5→dev2, 6→dev3`, а неизвестный MID пишет в лог, а не молча подменяет картинку. Эта версия научилась читать MID с устройства (команда 16, `CS_UsbServer_ReadCidMid`) — в предыдущей команда была объявлена, но ни разу не вызывалась.

### Требования

- Windows 10 (1809 / сборка 17763) или новее, x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (включена в self-contained сборку)
- Мышь ARDOR GAMING Impact PRO (приёмник 2.4G или кабель USB Type-C)

### Установка

Скачайте **ImpactProConfig-v1.1.0-Setup.msi** со страницы [Releases](../../releases) и запустите. Установщик создаёт:

- приложение в `Program Files` (или в пользовательской папке),
- ярлык на рабочем столе,
- ярлык в меню «Пуск».

`hidusb.dll` (библиотека протокола) устанавливается рядом с exe — без неё приложение работать не будет.

### Сборка из исходников

```powershell
git clone https://github.com/jdh-lololosha/ImpactProConfig.git
cd ImpactProConfig
dotnet build -c Release
```

### Отказ от ответственности

Это стороннее программное обеспечение. Оно общается с мышью по тому же HID-протоколу, что и официальный драйвер; используйте на свой страх и риск.

### Лицензия

[CC BY-NC 4.0 International](LICENSE) — свободно для некоммерческого использования с указанием авторства.
