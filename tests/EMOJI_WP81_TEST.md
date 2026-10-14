# WP8.1 Noto Emoji rendering test

The app bundles the official Noto Emoji PNG artwork locally under
`src/ESP32AIControl.WP81/Assets/NotoEmoji/`. The current asset index contains
3,750 Unicode-named emoji images plus 259 country flags. Assets are included
in the app package; build and runtime do not download them. The upstream
license and attribution are kept alongside the images.

The renderer derives asset filenames from Unicode code points, ignores
presentation selectors, and supports sequence filenames for skin tones,
keycaps and ZWJ emoji. Country flags are resolved through ISO country-code
images. If a sequence has no matching upstream image, the existing vector
fallback prevents a missing-image box.

Test by setting a widget title to each prefix below. The leading emoji should
be replaced by Noto artwork and the remaining title text should stay visible.

| Case | Test title | What to verify |
|---|---|---|
| Cucumber | `🥒 Cucumbers` | Noto cucumber image |
| Tomato | `🍅 Tomatoes` | Noto tomato image |
| Pepper | `🌶️ Pepper` | Noto pepper image |
| Seedling | `🌱 Seedling` | Noto seedling image |
| Thermometer | `🌡️ Temperature` | Noto thermometer image |
| Door | `🚪 Door` | Noto door image |
| Window | `🪟 Window` | Noto window image |
| Light | `💡 Light` | Noto light-bulb image |
| Finland flag | `🇫🇮 Finland` | Finnish flag image |
| US flag | `🇺🇸 United States` | US flag image |
| Skin tone | `👍🏽 Like` | Matching skin-tone image |
| Family ZWJ sequence | `👨‍👩‍👧‍👦 Family` | Combined family image |
| Keycap | `1️⃣ One` | Keycap image |
| Heart with selector | `❤️ Heart` | Heart image |

Also verify ordinary titles such as `Door`, `Temperature 20°`, and
`Widget 1` remain unchanged. MQTT values, command execution and scheduling
are outside this renderer and must behave exactly as before.

A green build does not prove on-device rendering; test on Lumia 830.
