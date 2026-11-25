# Android 5.1.1 UI -> WP8.1 Port Audit

Date: 2026-11-26

## Scope and source branches

- Android reference: `yasn198020-jpg/ESP32-AI-Control`, branch `compat/android-5.1.1-api22-vosk`.
- Windows Phone target: `yasn198020-jpg/ESP32-AI-Control-WP81`, branch `ui/lumia830-dashboard-test`.
- Target device: Nokia Lumia 830, 720x1280.
- This phase is an audit only. No application behavior or ESP32 protocol is changed.

## Important implementation distinction

The Android project currently builds its main UI with Kotlin + Jetpack Compose Material 3 APIs, while targeting Android 5.1.1 / API 22. The WP8.1 project is a native C# / WinRT XAML application targeting Windows Phone 8.1.

Therefore this is a visual and interaction port, not a source-code or widget-library port. Recreate the Android reference's actual layouts, hierarchy, colors, typography, spacing, states, and interaction patterns using WP8.1-compatible XAML/C# controls. Do not introduce Android libraries or change the MQTT/data layer.

## First inventory: WP8.1 target

The current WP8.1 shell is concentrated in:

- `src/ESP32AIControl.WP81/MainPage.xaml`
- `src/ESP32AIControl.WP81/MainPage.xaml.cs`
- `src/ESP32AIControl.WP81/App.xaml`

Observed current shell:

- Dark theme, background around `#202127`, purple accent `#8065E8`, dark cards and fields.
- Fixed top bar (menu, page title, info, connection indicator).
- Fixed five-item bottom navigation: Home, Marfa, Commands, History, Delayed.
- Main content sections include Home/dashboard, Graphs/History, Tasks/Delayed commands, Settings.
- Settings are currently a nested Pivot with Connection, Storage, Saved profiles, Voice, Scenarios, Local scenarios, and Log.
- The dashboard uses a Pivot for dynamic ESP32 pages.
- Graph screen uses date pickers, widget selector, buttons and a Canvas chart.
- Task screen combines a command form and a list.
- Shared implicit styles exist for TextBlock, Button, TextBox, PasswordBox, ComboBox, DatePicker, TimePicker, Slider, CheckBox, ListView and ListViewItem.

The project README also confirms existing WP8.1 functionality that must be preserved: MQTT 3.1.1, device discovery/fallback transport, dynamic widgets, local measurement history, graph date ranges, Word-compatible export, scheduled commands, voice commands and greenhouse controls.

## Android reference observations

The inspected Android entry point is:

- `app/src/main/java/com/yasn198020/aicontrol/MainActivity.kt`

Observed structure and design traits:

- Compose `Scaffold` owns the app-level screen structure.
- Material components are used for the application shell and screen content.
- The app uses a dark palette around `#202020` / `#24252A`, with a fixed bottom `NavigationBar`.
- Top-level navigation is state-driven; content changes inside the Scaffold rather than opening unrelated native-style pages for every section.
- Cards, grouped columns, scrollable content, sliders, text fields, selection controls, and dialogs form the repeated visual language.
- Text scale is an explicit user preference, persisted in settings.
- MQTT/runtime ownership is separated from the composable UI and must remain separate in the WP port.

Important: the Android code imports `androidx.compose.material3.*`; this is the project's current implementation, not a literal stock Android 5.1 system UI. For a faithful port, match the app's own current appearance and behavior, while keeping the implementation compatible with WP8.1.

## Initial screen correspondence

| Android reference area/pattern | WP8.1 target area | Porting work |
|---|---|---|
| Scaffold + top app bar | MainPage top bar | Match title, actions, status placement, dimensions and page transitions |
| Bottom NavigationBar | Existing five-item bottom navigation | Match selected/unselected states, icon sizing, labels, touch targets and spacing |
| Dashboard content | HomeSection + WidgetPages | Match dynamic widget cards and page layout without changing widget binding/commands |
| History/graph content | GraphsSection | Match selector/date controls, chart surface, summary and scrolling |
| Delayed commands | TasksSection | Match form fields, action hierarchy, selected-row state and list item design |
| Settings destinations | SettingsSection + SettingsPivot | Reorganize visually to match Android's navigation and grouped settings patterns |
| Dialogs/confirmation surfaces | C# dialog call sites | Replace inconsistent platform dialogs with a shared Android-like dialog style |
| Reusable cards/fields/buttons | XAML resource styles/templates | Create shared WP8.1 styles/templates instead of styling each screen independently |

## Design system to extract before implementation

Create a measured reference sheet from the Android source for:

1. Palette: background, surface/card, elevated surface, primary/accent, text primary/secondary, divider, success/warning/error.
2. Typography: page title, section title, card title, body, secondary/help, numeric measurement.
3. Geometry: app bar height, bottom bar height, page margins, card radius, card padding, control height, inter-item spacing.
4. Navigation: selected state, icon/label rules, back/menu behavior, destination order.
5. Reusable components: card, setting row, labeled field, switch/checkbox row, slider row, primary/secondary button, list item, dialog, status/progress indicator.
6. Interaction: tap target size, scroll behavior, selection feedback, confirmation/cancel flow, keyboard behavior.

Do not copy Material 3 defaults blindly. Use exact values and patterns found in this Android project and adapt only where the Lumia 830 screen or WP8.1 platform requires it.

## Risks and constraints

- WP8.1 XAML controls do not provide all Compose Material components; some will need custom UserControls/templates.
- Shadows/elevation, ripple feedback, text field labels, switches, and dialog surfaces may need custom templates or visual states.
- Lumia 830 has a 720x1280 portrait display; layout density and touch targets must be checked on-device.
- Preserve all existing control names/event handlers until a screen is deliberately migrated, to avoid breaking code-behind.
- Do not change MQTT, UDP/WebSocket fallback, protocol parsing, persistence formats, history sampling, scheduling, voice logic, or ESP32 command semantics as part of UI work.

## Next audit tasks

1. Enumerate every Android top-level destination and each nested settings screen from the current Android branch.
2. Enumerate all WP8.1 dialogs, flyouts, pivots, and dynamically created widget templates.
3. Build a complete Android -> WP8.1 screen/component matrix with source file references.
4. Capture exact Android colors, dimensions, typography and component states.
5. Only then implement the shared WP8.1 theme and migrate screens in small buildable stages.

## Current status

Initial repository and shell audit completed. No application code changed. This document is the baseline for the full interface port.


## Expanded navigation comparison (verified from source)

### Android top-level destinations

The Android MainActivity currently has these destinations:

- 0: Dashboard / live devices.
- 1: Trained commands.
- 2: MQTT connection.
- 3: Diagnostic log.
- 4: Marfa / ESP32 scenario hub.
- 5: Voice settings.
- 6: History and graphs.
- 7: Permission audit.
- 8: Application cleanup.
- 9: Delayed Marfa commands.
- 10: Delete widget.

The bottom NavigationBar exposes Dashboard, Marfa voice action, Commands (trained commands), History, and Delayed commands. The hamburger dropdown exposes service, settings, diagnostics and app actions. The top bar also exposes the current destination title, info/last-update toggle, connection state, and dashboard page tabs.

### WP81 navigation and current mismatch

The WP81 bottom bar exposes Home, Marfa voice action, Commands, History and Delayed. Its main section switcher has only Home, Graphs, Tasks and Settings; the menu opens additional settings pivots.

A verified navigation mismatch needs attention during the port:

- Android bottom item **Commands** opens destination 1, **TrainedCommandsScreen**.
- WP81 `NavigateCommands_Click` currently opens `SettingsSection` and sets `SettingsPivot.SelectedIndex = 3`, which is the **Voice** settings pivot, not a dedicated trained-commands destination.
- WP81 Marfa bottom item is a voice action, as in Android, but the WP81 target does not currently expose the Android trained-commands destination as a corresponding screen in the inspected XAML.

This should be resolved as part of navigation parity, not by changing voice recognition or command execution logic.

### Priority order for the full port

1. **Navigation parity first:** match Android destinations, labels, menu grouping, selected states and back behavior. Do not route Commands to Voice settings.
2. **Global shell:** top bar, bottom bar, menu, content margins, status indicator and consistent screen title.
3. **Screen parity:** dashboard, trained commands, MQTT, log, scenarios, voice settings, history/graphs, permissions, cleanup, delayed commands and widget deletion.
4. **Reusable controls:** card, field, setting row, toggle, slider, button, list row, dialog and progress indicator.
5. **Polish and device verification:** typography, icon assets, touch targets, scroll/keyboard behavior and 720x1280 Lumia layout.



## Visual port implementation — phase 1 (2026-11-26)

Applied to the WP81 branch:

- Main shell background normalized to Android's `#202020`.
- Bottom navigation surface aligned to Android's `#24252A`.
- Primary accent updated to Material 3 dark primary `#D0BCFF` in XAML resources and dynamic C# navigation/menu controls.
- Top app bar height changed from 60 to 84 effective XAML units to match Android's 84dp app header.
- Bottom navigation height changed from 68 to 80 to better match Android NavigationBar proportions.
- Bottom navigation glyphs aligned with Android's symbols: `▲`, `🎙`, `🎤`, `📊`, `⏱`.
- Button and input minimum dimensions increased for touch usability.
- Navigation popup offset adjusted for the taller app bar.

Files changed:

- `src/ESP32AIControl.WP81/MainPage.xaml`
- `src/ESP32AIControl.WP81/MainPage.xaml.cs`

This is the first visual shell pass, not completion of the full port. The next implementation passes must cover every Android destination, settings form, history/graph component, dialogs, selection controls, progress UI, and dynamic dashboard widgets. The WP81 Commands bottom item still routes to the existing voice/settings surface because a trained-command repository/screen was not found in the WP81 project; do not claim feature parity until that gap is resolved.

No MQTT, UDP, WebSocket, voice recognition, scheduler, history persistence, or ESP32 command logic was changed in this phase. A device build and Lumia 830 visual check are still required.


## Dashboard renderer parity gap discovered during implementation

A direct comparison of Android `IoTManagerWidgetRenderer.kt` with WP81 `CreateWidgetVisual` shows that the current WP81 model is materially less expressive:

- Android widget rendering supports toggle, button, date/time/text/number inputs, range/servo sliders, select, progress line, circular progress, fill gauge, and chart1..chart6.
- Android reads widget definition/config JSON (unit, ranges, colors, thresholds, chart series and display options) and uses those values to draw each control.
- WP81 `WidgetState` currently contains only DeviceId, Id, Description, WidgetType, Page, Order, Value and UpdatedAt. The current WP81 visual factory renders a generic row, switch, action button, or slider; it cannot reproduce Android's full widget-specific visuals without first carrying over the widget definition/config metadata.
- The Android dashboard uses mostly flat rows with 16dp horizontal inset and 6–8dp vertical spacing; the WP81 version currently wraps each widget row in its own rounded card. This is a visible mismatch.

Required next dashboard work:

1. Extend WP81's parsed widget model to retain the Android-equivalent widget definition and configuration JSON, without changing MQTT command semantics.
2. Port the Android widget renderer types one by one to WP8.1 XAML/C#.
3. Match exact row spacing, icons, value chips, controls, charts, gauges, progress and threshold colors.
4. Verify touch/long-press training behavior and every widget type on the Lumia 830.

Do not simplify the Android widget set to generic labels or static mockups. The renderer port is required for genuine visual parity.
