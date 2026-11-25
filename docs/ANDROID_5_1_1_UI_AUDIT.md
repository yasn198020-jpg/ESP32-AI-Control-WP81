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
- No separate top-level Delete Widget destination was found in the inspected Android `MainActivity.kt`; the initial audit entry was incorrect.

The bottom NavigationBar exposes Dashboard, Marfa voice action, Commands (trained commands), History, and Delayed commands. The hamburger dropdown exposes service, settings, diagnostics and app actions. The top bar also exposes the current destination title, info/last-update toggle, connection state, and dashboard page tabs.

### WP81 navigation and current mismatch

The WP81 bottom bar exposes Home, Marfa voice action, Commands, History and Delayed. Its main section switcher has only Home, Graphs, Tasks and Settings; the menu opens additional settings pivots.

A verified navigation mismatch needs attention during the port:

- Android bottom item **Commands** opens destination 1, **TrainedCommandsScreen**.
- At the time of the initial audit, WP81 `NavigateCommands_Click` opened `SettingsSection` and selected the **Voice** settings pivot. This mismatch was corrected in visual port phase 3: the bottom Commands item now opens a dedicated trained-commands screen.
- WP81 Marfa bottom item remains a voice action, as in Android. Trained phrase storage, long-press training and phrase execution were added in phase 3 while retaining the existing WP81 voice recognizer and MQTT command transport.

The Commands/Voice-settings navigation collision is resolved; the remaining application destinations still require a complete screen-by-screen parity review.

### Priority order for the full port

1. **Navigation parity:** map all Android destinations to native WP8.1 screens/pivots and preserve the Marfa voice action on the bottom bar.
2. **Global shell:** top bar, bottom bar, menu, content margins, status indicator and consistent screen title.
3. **Screen parity:** dashboard, trained commands, MQTT, log, scenarios, voice settings, history/graphs, capabilities, cleanup and delayed commands.
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

This is the first visual shell pass, not completion of the full port. The next implementation passes must cover every Android destination, settings form, history/graph component, dialogs, selection controls, progress UI, and dynamic dashboard widgets. The WP81 Commands item was redirected to the dedicated trained-commands screen in phase 3; trained-command storage and interaction were also added.

No MQTT, UDP, WebSocket, voice recognition, scheduler, history persistence, or ESP32 command logic was changed in this phase. A device build and Lumia 830 visual check are still required.


## Dashboard renderer parity gap discovered during implementation

A direct comparison of Android `IoTManagerWidgetRenderer.kt` with WP81 `CreateWidgetVisual` shows that the current WP81 model is materially less expressive:

- Android widget rendering supports toggle, button, date/time/text/number inputs, range/servo sliders, select, progress line, circular progress, fill gauge, and chart1..chart6.
- Android reads widget definition/config JSON (unit, ranges, colors, thresholds, chart series and display options) and uses those values to draw each control.
- At the time of discovery, WP81 `WidgetState` retained only basic identity and value data, and the renderer handled only a few generic controls. Phase 2 added the Android-equivalent definition name, original CONFIG JSON, unit and configured topic to the WP81 model.
- Phase 2/3 flattened the per-widget card wrapper and added WP81 renderers for inputs, selectors, range controls, progress indicators, fill gauge and chart series. These are native XAML/C# equivalents; visual pixel parity and the full Android chart configuration still need device review.

Initial requirements (items 1–3 have received an implementation pass in phases 2 and 3; item 4 remains unverified):

1. Preserve the Android-equivalent widget definition and original config JSON through MQTT parsing.
2. Render all known Android widget families with WP8.1-native controls and drawing.
3. Match row spacing, icons, value units, configured ranges, threshold colors, charts and gauges.
4. Verify touch/long-press training behavior and every widget type on the Lumia 830.

Do not simplify the remaining differences to generic labels or static mockups. Device verification is still required.


## Visual port implementation — phase 2 (2026-11-26)

Implemented the first data-driven dashboard parity layer:

- WidgetState now retains DefinitionName, the original ConfigJson, and Unit; Clone() carries these fields so repository snapshots do not discard renderer metadata.
- MQTT config parsing now extracts unit / after and preserves the original CONFIG payload.
- DeviceRepository stores configuration metadata independently of live status/event values. MQTT control topics and payload semantics are unchanged.
- Dashboard rendering now uses the Android definition name to classify toggle/button/range/progress-line widgets, uses configured range min/max (including the servo default), displays units with telemetry values, and renders progress-line values with a native progress bar.
- Widget rows were flattened to remove the WP81-only rounded card around every item, bringing spacing and hierarchy closer to Android's compact dashboard rows.

Files changed in this phase:

- src/ESP32AIControl.WP81/Models/WidgetState.cs
- src/ESP32AIControl.WP81/Core/Protocol/Esp32Message.cs
- src/ESP32AIControl.WP81/Core/Protocol/Esp32JsonParser.cs
- src/ESP32AIControl.WP81/Core/State/DeviceRepository.cs
- src/ESP32AIControl.WP81/MainPage.xaml.cs

At the end of phase 2, the remaining dashboard work included text/date/time inputs, select options, threshold colors, circular progress, fill gauge and charts; these received an implementation pass in phase 3. Remaining dashboard gaps are detailed chart axes/labels, complete config-option parity, offline chart-history fallback and device verification. Remaining application-wide work includes final visual restyling of all settings/scenario screens, dialogs and back navigation. No WP8.1 build or Lumia 830 device test was available in this editing session; this phase must not be treated as build-verified.


## Visual port implementation — phase 3 (2026-11-26)

Dashboard renderer additions:

- Added Android-style `select` options, text/numeric/date/time inputs, progress-round arc, fill gauge, and chart1–chart6 chart surfaces.
- Added parsing of chart `status` arrays with up to three value series; chart CONFIG is kept intact and chart STATUS payloads remain available as original JSON for the renderer.
- Applied per-value threshold colors and configured icon names (`thermometer`, `water`, `walk`, `body`, `speedometer`, `sunny`) to telemetry rows.
- Rebuild dynamic controls when CONFIG metadata changes, and remove widgets whose definition is `nil`.
- Corrected inputDgt to render as a numeric text input rather than a range slider. Range/rangeServo continue to use configured bounds and scale factor `k`.

Trained-command/navigation additions:

- The bottom Commands destination now opens a dedicated trained-command page instead of the Voice settings pivot.
- Commands are stored in WP81 local app settings using the Android-compatible JSON field names: `phrase`, `deviceId`, `widgetId`, `value`, `variants`.
- Long-pressing supported value/control widgets opens a training surface; a phrase can create a new command or add the selected widget action to an existing phrase.
- Added open/close training for controls, read-value training for telemetry widgets, recording/typing of phrase variants, deletion of one action and clear-all confirmation.
- Recognized speech first checks pending training/variant capture, then executes matching trained commands. Read-value commands speak the latest value; action commands use the existing MQTT control or configured widget topic.

Files changed in phases 2–3 include the WP81 widget model, MQTT protocol parser/message, device repository, MQTT publish facade, and MainPage renderer/navigation.

Limitations: this is a functional port pass, not verified full parity. The matcher is a smaller WP81-compatible implementation than Android's full fuzzy matcher; chart rendering is a native approximation and does not yet reproduce every Android series label/time axis/style detail. The visual restyling of Marfa scenario hub, all settings/dialogs and Android-equivalent back navigation still need a full pass. No WP81 build or Lumia 830 device test was available in this session.


## Visual port implementation — phase 4 (2026-11-26)

- Added an application cleanup destination. It offers a confirmed deletion of measurements older than the configured retention period and a separate confirmed deletion of the persistent connection journal. It deliberately leaves MQTT settings, scenarios and trained commands untouched; WP8.1 does not have Android's cacheDir or its runtime-permission model.
- Added a capability audit destination based on the actual WP81 Package.appxmanifest declarations: internetClient, privateNetworkClientServer, picturesLibrary and DeviceCapability microphone. The UI clearly labels these as manifest declarations, not a live runtime grant check.
- Reorganized the menu into Commands, Service, Settings and About groups; added the Android-like text-size popup and applied the saved scale to native text/controls, dashboard rows, trained-command cards and popup content.
- Corrected the top-bar info action to match Android: it toggles per-widget “обновлено N сек/мин/ч/дн назад” labels and refreshes them every second only while enabled.
- Made font scaling non-cumulative by preserving each control's base font size and processing visual-tree children before parents.

Remaining limitations:

- This is a substantial functional parity pass, not a completed pixel-perfect port. The chart1–chart6 renderer is a compact WP8.1 drawing approximation; series labels, detailed time axes and all Android chart options are not yet reproduced. Some Android-only platform features have no direct WP8.1 equivalent and are represented by an explicit capability/feature explanation rather than a fake control.
- The WP81 voice phrase matcher is implemented natively but still requires on-device Russian recognition testing. TTS voice availability depends on the voices installed in Windows Phone.
- Settings, scenario, history and delayed-command screens retain existing WP81 application logic and controls; they have not all been fully restyled/visually inspected against Android screenshots yet.
- No Windows Phone 8.1 build or Lumia 830 device verification was available in this session. A build and on-device inspection remain mandatory before treating this branch as release-ready.


## Visual port implementation — phase 5 (2026-11-26)

- The top-bar info action now matches the Android behavior: it toggles last-updated labels under dashboard widgets, formats elapsed time in seconds/minutes/hours/days, and refreshes once per second only while enabled.
- The app-wide font scale now caches each control's original font size and walks the XAML visual tree child-first before resizing its parents, avoiding cumulative scaling after repeated changes.
- Rechecked the Android MQTT config source: `widget` remains the logical control type; `name` is the IoTManager renderer definition; `topic` is the exact raw publish topic; `after` is the display unit. WP81 stores these separately, matching the reference model.

Current branch remains a functional visual port in progress, not release-verified. No WP8.1 compiler or Lumia 830 hardware was available in this session, so the final compile/runtime pass must be performed with the WP8.1 toolchain and the phone.


## Visual port implementation — phase 6 (2026-11-26)

- Expanded the WP81 Voice settings pivot with a list of installed Windows speech-synthesis voices and a test phrase button.
- The selected voice ID is persisted in AppSettings and is used both by the test button and spoken replies to trained commands. If the saved voice is unavailable, the app falls back to an installed Russian voice and then the system default.
- The WP8.1 implementation does not expose the later SpeechSynthesizer.Options API for rate/pitch controls; those Android settings are not represented by fake sliders. The available system-voice selection and test path are ported instead.

Remaining work before release: compile with the Windows Phone 8.1 toolchain; inspect every screen on a 720x1280 Lumia 830; verify MQTT controls including the distinct /control and config-topic publish routes; exercise trained phrase capture/variants and Russian TTS; refine chart axis/series fidelity and the remaining settings/scenario/dialog visual details.


## Visual port implementation — phase 7 (2026-11-26)

- The Voice settings pivot now lists installed Windows speech-synthesis voices, persists the chosen voice ID and provides a test phrase. Trained-command replies use the selected voice, with Russian/system-default fallback when that saved voice is unavailable.
- Chart widgets now show the number of parsed points, configured series names and a chart-type caption (`chart1`…`chart6`) in addition to the native line/bar rendering.
- Windows Phone 8.1 does not expose the later SpeechSynthesizer.Options controls for rate/pitch; no unsupported rate/pitch sliders were added.


## Visual port implementation — phase 8 (2026-11-26)

Settings UI restyling pass:

- Added a dedicated Android-like category header template to the WP81 Settings Pivot, using consistent rounded dark category chips rather than the default plain Pivot text headers.
- Simplified the settings page heading and made the available categories explicit: connection, storage, saved profiles, voice, scenarios, local scenarios and log.
- Standardized inner settings page spacing and the dark rounded panel treatment for connection state and diagnostic log surfaces.
- Unified section heading size, spacing and muted description colors across the settings subsections.
- Restyled the persistent save/backup area as a consistent rounded action panel; preserved every existing named control and event handler so MQTT, profile, voice, scenario, import/export and logging behavior remain wired.
- Kept the seven existing settings categories and their current WP81 controls intact; this is a visual pass, not a claim that every Android setting has a one-to-one WP81 platform equivalent.

Still outstanding: inspect and restyle the hamburger popup's nested submenus and confirmation dialogs, compare every settings category against Android screenshots, and perform a Windows Phone 8.1 compile plus Lumia 830 runtime check. No build or device verification was available during this phase.


## Visual port implementation — phase 9 (2026-11-26)

- Restyled the hamburger menu's submenu rows to use a consistent 48px touch target, a muted Material-style leading icon and aligned title text. The menu's grouped sections and existing navigation actions are unchanged.
- Used Windows Phone 8.1-compatible layout properties; avoided newer UWP-only StackPanel spacing APIs.
- Verified the settings Pivot still contains all seven existing categories, that the Android-like header template is actually assigned, and that all XAML event-handler names in the page have matching code-behind methods.
- No Windows Phone build or device run was available; this source-level check is not a substitute for compilation.
