# WP8.1 Noto Emoji icon smoke test

The app bundles selected official Noto Emoji PNG artwork under
`src/ESP32AIControl.WP81/Assets/NotoEmoji/`. The images are included in the
app package and loaded locally; the build and runtime do not download them.
The upstream license and attribution are kept alongside the images.

Test by setting a widget title to each prefix below. The leading emoji should
be replaced by its bundled Noto Emoji artwork and the remaining title text
should stay visible.

| Case | Test title | What to verify |
|---|---|---|
| Cucumber | `🥒 Cucumbers` | Noto cucumber image |
| Tomato | `🍅 Tomatoes` | Noto tomato image |
| Pepper | `🌶️ Pepper` | Noto pepper image |
| Seedling | `🌱 Seedling` | Noto seedling image |
| Herb | `🌿 Herb` | Noto herb image |
| Thermometer | `🌡️ Temperature` | Noto thermometer image |
| Door | `🚪 Door` | Noto door image |
| Window | `🪟 Window` | Noto window image |
| Light | `💡 Light` | Noto light-bulb image |
| Water | `💧 Water` | Noto water-drop image |
| Unsupported sequence | `👨‍👩‍👧‍👦 Family` | Safe vector fallback |

Also verify ordinary titles such as `Door`, `Temperature 20°`, and
`Widget 1` remain unchanged. MQTT values, command execution and scheduling
are outside this renderer and must behave exactly as before.

A green build does not prove on-device rendering; test on Lumia 830.
