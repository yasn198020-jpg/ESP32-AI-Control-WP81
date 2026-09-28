# ESP32-AI-Control-WP81

Native Windows Phone 8.1 client for ESP32-AI-Control, designed for Nokia Lumia 830 (RM-984).

## Principles

- Native Windows Phone 8.1 / WinRT XAML application.
- ESP32 MQTT protocol remains unchanged.
- No Android code or Android dependencies are used.
- MQTT transport, protocol parsing, state, history, graphs, scheduling, voice and export are separated.
- Application version: 2.0.

## Included functionality

### MQTT and ESP32
- Native MQTT 3.1.1 transport over StreamSocket.
- HELLO handshake and /prefix/# subscription.
- ESP32 status/event/config parsing.
- Dynamic pages and controls from the device configuration.
- Control publish using device/widget=value.
- Config parser accepts both the newer topic/descr/widget form and the IoTManager id/type/subtype/page/descr/widget form.

### Dynamic widgets
- Dynamic pages from MQTT.
- Toggle/switch/check controls.
- Output/action buttons.
- Numeric slider controls.
- Read-only measurement values.
- Widget ordering and page relocation when configuration changes.

### Measurement history and graphs
- Numeric measurements are sampled according to a configurable interval instead of every MQTT arrival.
- Local persistent history in application storage.
- Configurable retention period.
- Date-range graph screen.
- Manual Word-compatible .doc export for a selected widget or the whole history.
- Automatic export modes: never, 1 day, week, month.

### Scheduling and voice
- Persistent scheduled command list.
- Add, edit, cancel and inspect scheduled commands.
- Delayed voice commands such as: Marfa, open the greenhouse in 20 minutes.
- Delay is scheduled instead of executed immediately.
- Voice recognizer is explicitly disposed after recognition.
- Duplicate execution protection: a task is marked Executing before its command is sent.

## Storage files

The app stores local data in the WP8.1 application LocalFolder:

- app-settings.json — MQTT, graph, retention, export and voice settings.
- measurements.tsv — measurement history.
- scheduled-commands.json — persistent scheduled commands.
- .doc files — Word-compatible exports.

## Background execution

While the foreground app is active, the scheduler checks due commands every few seconds. Persistent tasks are recovered when the app starts again.
Windows Phone 8.1 background execution has OS scheduling and lifetime limits, so exact second-level execution is intentionally kept in the foreground scheduler rather than promised from a background agent.

## Greenhouse functionality

The client can work with the IoTManager greenhouse configuration discussed for temperature, window, door, limit switches, timers and manual/automatic controls. The automation itself remains on the ESP32/IoTManager side; the WP8.1 app displays and controls those widgets without changing the ESP32 protocol.

## Current status

The WP8.1 solution and project configuration remain compatible with the working VS/MSBuild build chain. The repository now contains the integrated feature foundation; final device-side verification should still be performed on the working WP8.1 build environment and Lumia target.