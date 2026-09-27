# ESP32-AI-Control-WP81

Native Windows Phone 8.1 client for ESP32-AI-Control, designed for Nokia Lumia 830 (RM-984).

## Principles

- Native Windows Phone 8.1 / WinRT XAML application.
- ESP32 MQTT protocol remains unchanged.
- No Android code or Android dependencies are used.
- MQTT transport, protocol parsing, state, history, graphs, scheduling, voice and export are separated.

## Architecture

Core
- Mqtt - native MQTT 3.1.1 client over StreamSocket.
- Protocol - ESP32 topic and JSON protocol.
- State - device/widget state repository.
- History - measurement storage abstraction.
- Scenarios - command execution model.
- Scheduling - delayed and scheduled commands.
- Voice - WP8.1 voice command adapter.
- Export - document export abstraction.
- Diagnostics - application and MQTT logs.

UI
- XAML pages and controls.
- UI observes application state and never parses MQTT topics directly.

Background
- WP8.1-compatible background tasks for scheduled work where the OS permits them.

## Current milestone

1. Solution and WP8.1 application shell.
2. Native MQTT 3.1.1 transport.
3. ESP32 topic/parser layer.
4. Central device state model.
5. HELLO handshake and control publish path.

Later milestones add dynamic widgets, logs, graphs, history, Word export, scheduling and voice commands.
