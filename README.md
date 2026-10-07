# ARDOR GAMING Impact PRO Config

[English](#english) · [Русский](#русский)

Unofficial configuration utility for the **ARDOR GAMING Impact PRO** gaming mouse — a dark-themed WPF (.NET 8) replacement for the vendor software, with a live OSD overlay and low-battery notifications.

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
- **Dark-only UI** with the Ardor red accent (#E81123 / #FF2E2E).

### Requirements

- Windows 10 (1809 / build 17763) or later, x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (included in the self-contained build)
- ARDOR GAMING Impact PRO mouse (2.4G receiver or USB Type-C)

### Installation

Download **ImpactProConfig-v1.0.0-Setup.msi** from the [Releases](../../releases) page and run it. The installer creates:

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

### Возможности

- **Полная настройка мыши** — уровни DPI, частота опроса, параметры прокрутки и чувствительности.
- **Назначение кнопок** — любое действие для каждой кнопки, включая отдельное действие **«Показать статус мыши (OSD)»**.
- **OSD-оверлей** — нажимаете назначенную кнопку, и на экране появляется тёмное стеклянное окно со статусом: заряд %, режим работы (заряжается / от батареи), тип подключения (2.4G / Type-C) и оценка времени работы.
- **Уведомление о низком заряде** — всплывающее уведомление Windows при заряде ниже 15% (один раз за разряд; сброс выше 20% или при подключении кабеля).
- **Честная работа с мониторами** — перечисление через Win32 `EnumDisplayMonitors` / `GetMonitorInfo` (с разрешением и флагом основного), OSD размещается строго в **рабочей области (rcWork)** выбранного монитора.
- **Импорт / экспорт** — конфиги формата официалки (10 428 байт) и локальные JSON-профили.
- **Нет записи во флеш при запуске** — мышь записывается только по кнопке **«Применить»**, всё остальное хранится локально.
- **Тёмная тема** с акцентом Ardor red (#E81123 / #FF2E2E).

### Требования

- Windows 10 (1809 / сборка 17763) или новее, x64
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (включена в self-contained сборку)
- Мышь ARDOR GAMING Impact PRO (приёмник 2.4G или кабель USB Type-C)

### Установка

Скачайте **ImpactProConfig-v1.0.0-Setup.msi** со страницы [Releases](../../releases) и запустите. Установщик создаёт:

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
