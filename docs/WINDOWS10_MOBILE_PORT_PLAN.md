# Windows 10 Mobile port

## Source of truth

- Android reference repository: `yasn198020-jpg/ESP32-AI-Control`
- Android reference branch: `compat/android-5.1.1-api22-vosk`
- Main Android application UI: `app-kitkat/src/main/java/com/yasn198020/aicontrol/LegacyMainActivity.kt`
- Windows implementation branch: `feature/windows10-mobile-android-ui-port`
- UWP project: `src/ESP32AIControl.UWP`

The existing Windows Phone 8.1 project is retained unchanged. The Windows 10 Mobile application is a separate UWP project.

## Product requirements

- Reproduce the Android 5.1.1 application's complete main functionality and navigation as closely as Windows 10 Mobile allows.
- Do not bind UI or business logic to Lumia 830, RM-984, 720x1280, or any single DPI.
- Use responsive layouts and runtime size adaptation.
- Separate presentation, application state, device communication, storage, scheduling and voice services.
- Keep ESP32/MQTT/UDP behavior independent from phone model and screen size.
- The custom photo-based personal home screen is explicitly deferred until the rest of the application has been ported and tested.
- Do not modify the Android reference branch.

## Main Android navigation reference

Five primary navigation actions:
1. Главная — standard application dashboard/overview for the first migration phase; custom photo home screen is deferred.
2. Марфа — assistant screen and persistent listening control.
3. Команды — trained commands.
4. История — history and graphs.
5. Отложенные — scheduled/delayed commands.

Additional destinations exposed from the Android menu:
- Сценарии
- Журнал
- Голос
- MQTT подключение
- UDP устройства
- Настройки приложения
- Управление/удаление элементов
- Разрешения и аудит
- Очистка приложения
- Проверка и обновление приложения
- Размер текста and other utility actions

## Complete application migration sequence

1. Finish UWP application shell, responsive navigation, routing and common visual components.
2. Main application dashboard and device/widget list (not the custom photo home screen).
3. Trained commands: list, add/edit/train, execute and result feedback.
4. Marfa: text/voice input flow, command preview/confirmation, responses, listening state and service integration.
5. Voice settings: speech engine/settings, presets, tuning controls and test/preview.
6. History: event/measurement list, graph configuration, graph display, duration/interval controls and export.
7. Delayed commands: scheduling, editing, cancellation and execution status.
8. Scenarios: condition/action editor, ordering, validation, save/load and execution.
9. MQTT settings and connection lifecycle; device state and command transport.
10. UDP device discovery and device management.
11. Journal/log, permissions audit, cleanup, update flow and application settings.
12. Integration, persistence, lifecycle/background behavior, accessibility and package validation across screen sizes.

## Deferred feature

The Android `HomeScreenActivity` custom photo desktop, including background photo selection, transparent touch zones, sensor overlays, conditional text, bars, weather/date overlays and its editor, is not part of the initial port. It will be addressed only after the complete main application has been migrated.

## Current implementation status

- Stage 1: UWP application shell and responsive navigation — initial implementation committed; not yet validated by a local Visual Studio build.
- Stage 2: Main dashboard presentation — implemented page tabs (including gear-marked page names), empty-state handling, stable widget ordering, toggle/button/range/select/text/number/date/time inputs, progress bars, round/fill gauge-style indicators, chart payload rendering, update age labels, offline labels and command acknowledgement labels. Device/widget snapshots are accepted through `SetDeviceSnapshot`; UI actions are emitted as `DeviceCommandRequested`. The Android custom photo desktop remains deferred. The MQTT snapshot adapter and real command transport are intentionally left for stage 9; the dashboard has not yet been validated by a local Visual Studio build.
- Stages 3–12: not yet implemented.
- Deferred: custom photo-based home screen and its editor remain out of the initial migration sequence.
