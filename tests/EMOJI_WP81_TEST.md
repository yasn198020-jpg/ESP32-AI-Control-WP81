# WP8.1 emoji icon smoke test

Emoji icons are drawn with C# vector shapes in `WidgetIconCatalog.cs`.
The build does not download fonts or image assets and the app does not need
network access for these icons. This is a lightweight fallback, not a complete
Android-style emoji font; unsupported emoji sequences use a generic colored
symbol.

Test by setting a widget title to each prefix below. The leading emoji should
be replaced by an app-owned colored icon and the remaining title text should
stay visible.

| Case | Test title | What to verify |
|---|---|---|
| Cucumber | `🥒 Cucumbers` | Green cucumber icon |
| Tomato | `🍅 Tomatoes` | Red tomato icon |
| Pepper | `🌶️ Pepper` | Red pepper icon |
| Seedling | `🌱 Seedling` | Green plant icon |
| Thermometer | `🌡️ Temperature` | Thermometer icon |
| Door | `🚪 Door` | Door icon |
| Window | `🪟 Window` | Window icon |
| Light | `💡 Light` | Bulb icon |
| Water | `💧 Water` | Blue drop icon |
| Unsupported sequence | `👨‍👩‍👧‍👦 Family` | Safe generic colored icon; no font download |

Also verify ordinary titles such as `Door`, `Temperature 20°`, and
`Widget 1` remain unchanged. MQTT values, command execution and scheduling
are outside this renderer and should behave exactly as before.

The build result alone does not prove on-device rendering; test on Lumia 830.
