# WP8.1 full-color emoji smoke test

The WP8.1 build prepares the pinned Noto Emoji PNG set and packages it in
`Assets/Emoji`. The first build needs internet access; installed app rendering
uses packaged images and does not need internet for emoji.

Test by setting the widget title/description to each exact prefix below. The
emoji should appear as a colored image at the start of the title, with the text
after it preserved.

| Case | Test title | What to verify |
|---|---|---|
| Basic face | `😀 Happy` | Yellow face image, no tofu square |
| Newer face | `🫠 Melting` | Newer Unicode emoji image |
| Flag | `🇫🇮 Finland` | Finland flag, not two letter symbols |
| Family | `👨‍👩‍👧‍👦 Family` | One family image, not separate people |
| Skin tone + ZWJ | `👩🏽‍🚀 Astronaut` | One combined astronaut image |
| Keycap | `1️⃣ One` | One keycap image; leading digit is consumed only as emoji |
| Tag sequence | `🏴‍☠️ Pirate` | One pirate flag image if present in the Noto set |
| Rainbow flag | `🏳️‍🌈 Pride` | One joined rainbow flag image |

Also verify ordinary titles such as `Door`, `Temperature 20°`, and `Widget 1`
are unchanged. MQTT values, command execution and scheduling are outside this
renderer and should behave exactly as before.

If a case falls back to the generic vector symbol or is blank, record the exact
test title and send a screenshot; that indicates a filename/sequence mapping
needs adjustment. The build result alone does not prove on-device rendering.
