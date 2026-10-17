# SoundDeck

Утилита управления звуком для Windows 11 — WinUI 3, .NET 8, Windows App SDK 1.8 (unpackaged).

## Что умеет (этап 1 — микшер)

- Иконка в трее (H.NotifyIcon), рисуется в рантайме под DPI таскбара; ЛКМ — микшер, ПКМ — меню в стиле Windows 11.
- Флайаут над треем: акрил, выезжает из-под таскбара. Сверху — устройство вывода (выпадающий список, смена устройства
  по умолчанию) и общая громкость; ниже — карточка на каждое приложение со звуком.
- В каждой строке: индикатор уровня, иконка (клик — mute), ползунок, процент. Список обновляется вживую при запуске
  и закрытии приложений и при смене устройства.
- Автозапуск через HKCU\…\Run (`--autostart` — тихий старт в трей). Повторный запуск exe открывает микшер.

## Структура

- `src/SoundDeck.Core` — звуковое ядро без UI (Core Audio через COM напрямую):
  - `AudioService` — устройство вывода по умолчанию, `Master`, живой список `Apps`, события `AppAdded/AppRemoved/DeviceChanged`,
    `GetOutputDevices()` / `SetDefaultOutput(id)`; поиск для хоткеев: `FindApp("spotify")`, `FindAppByProcessId(pid)`
    (по exe — работает для активного окна браузера/Electron).
  - `AudioApp` — все сессии одного exe в одной громкости: `SetVolume/AdjustVolume/SetMute/ToggleMute/Peak`.
  - `MasterVolume` — громкость устройства, те же методы.
  - Все изменения принимают `Guid context`; события его возвращают, так UI отличает свои изменения от внешних.
- `src/SoundDeck.App` — трей, флайаут-микшер, меню трея, автозапуск.
- `tools/make-icons.ps1` — генерирует иконку приложения (`Assets/SoundDeck.ico`).

## Сборка и запуск

```
dotnet build SoundDeck.sln -p:Platform=x64
src\SoundDeck.App\bin\x64\Debug\net8.0-windows10.0.22621.0\win-x64\SoundDeck.exe
```
