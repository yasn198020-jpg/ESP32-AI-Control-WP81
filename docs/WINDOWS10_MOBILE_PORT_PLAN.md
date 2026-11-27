# Windows 10 Mobile port

## Source of truth

- Android reference repository: `yasn198020-jpg/ESP32-AI-Control`
- Android reference branch: `compat/android-5.1.1-api22-vosk`
- Primary legacy UI source: `app-kitkat/src/main/java/com/yasn198020/aicontrol/LegacyMainActivity.kt`
- Dedicated home-screen implementation: `HomeScreenActivity` and its related resources in the same Android branch.
- Windows implementation branch: `feature/windows10-mobile-android-ui-port`

The existing Windows Phone 8.1 project is retained as-is. The Windows 10 Mobile application is a separate UWP project under `src/ESP32AIControl.UWP`.

## Product requirements

- Match the Android 5.1.1 application structure and visual behavior as closely as the platform allows.
- Do not bind the UI or business logic to Lumia 830, RM-984, 720x1280, or any single DPI.
- Use responsive layouts and runtime size adaptation.
- Keep MQTT/protocol, device state, history, scenarios, scheduling, voice and presentation as separate layers.
- Preserve the Android dashboard's configurable pages and widgets, including the custom home-screen experience.
- Do not modify the Android reference branch.

## Android navigation reference

Five primary navigation actions:
1. Главная
2. Марфа (persistent listening control)
3. Команды
4. История
5. Отложенные

Additional destinations are exposed from the menu: Сценарии, Журнал, Разрешения, Очистка, Рабочий стол, MQTT, UDP, Голос, Размер текста and application information/tools.

## Migration sequence

1. UWP shell, adaptive navigation and screen routing.
2. Dashboard and configurable widget/page model.
3. Marfa, trained commands and voice settings.
4. History, graphs and exports.
5. Scenarios and delayed commands.
6. MQTT, UDP discovery, protocol and device-state services.
7. Device-size testing, accessibility, lifecycle and package validation.

## Current implementation status

The first commit introduces only a UWP project skeleton and navigation shell. MQTT, voice recognition, TTS, history persistence, scenarios and ESP32 communication are not yet implemented in this UWP project.
