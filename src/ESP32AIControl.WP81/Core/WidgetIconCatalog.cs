using System;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;

namespace ESP32AIControl.WP81.Core
{
    /// <summary>
    /// Small app-owned vector icon set for WP8.1. It does not depend on the phone's emoji font.
    /// </summary>
    public static class WidgetIconCatalog
    {
        private static readonly Color Accent = Color.FromArgb(255, 0, 183, 255);
        private static readonly Color Green = Color.FromArgb(255, 91, 190, 120);
        private static readonly Color Amber = Color.FromArgb(255, 255, 183, 77);

        public static FrameworkElement Create(string title, string widgetType)
        {
            string text = (title ?? string.Empty).ToLowerInvariant();
            string type = (widgetType ?? string.Empty).ToLowerInvariant();
            var canvas = new Canvas { Width = 26, Height = 26 };
            Brush accent = new SolidColorBrush(Accent);
            Brush green = new SolidColorBrush(Green);
            Brush amber = new SolidColorBrush(Amber);
            Brush muted = new SolidColorBrush(Color.FromArgb(255, 190, 198, 210));

            if (ContainsAny(text, "температур", "temperature", "термометр", "градус"))
            {
                Add(canvas, new Rectangle { Width = 7, Height = 15, RadiusX = 3, RadiusY = 3, Stroke = amber, StrokeThickness = 2 }, 9, 2);
                Add(canvas, new Ellipse { Width = 12, Height = 12, Fill = amber }, 6.5, 12);
                Add(canvas, new Rectangle { Width = 3, Height = 11, Fill = amber }, 11, 5);
            }
            else if (ContainsAny(text, "огур", "томат", "помид", "перец", "овощ", "растен", "теплиц", "greenhouse", "plant"))
            {
                Add(canvas, new Ellipse { Width = 13, Height = 8, Fill = green, RenderTransform = new RotateTransform { Angle = -35 }, RenderTransformOrigin = new Point(0.5, 0.5) }, 3, 5);
                Add(canvas, new Ellipse { Width = 13, Height = 8, Fill = green, RenderTransform = new RotateTransform { Angle = 35 }, RenderTransformOrigin = new Point(0.5, 0.5) }, 11, 5);
                Add(canvas, new Line { X1 = 13, Y1 = 10, X2 = 13, Y2 = 24, Stroke = green, StrokeThickness = 2.5 }, 0, 0);
                Add(canvas, new Line { X1 = 13, Y1 = 17, X2 = 6, Y2 = 13, Stroke = green, StrokeThickness = 2 }, 0, 0);
            }
            else if (ContainsAny(text, "двер", "door", "ворот"))
            {
                Add(canvas, new Rectangle { Width = 15, Height = 21, Stroke = accent, StrokeThickness = 2 }, 5, 3);
                Add(canvas, new Ellipse { Width = 2.8, Height = 2.8, Fill = amber }, 15, 13);
                Add(canvas, new Line { X1 = 2, Y1 = 24, X2 = 24, Y2 = 24, Stroke = muted, StrokeThickness = 1.5 }, 0, 0);
            }
            else if (ContainsAny(text, "форточ", "окн", "window", "жалюз", "клапан"))
            {
                Add(canvas, new Rectangle { Width = 20, Height = 17, Stroke = accent, StrokeThickness = 2 }, 3, 4);
                Add(canvas, new Line { X1 = 13, Y1 = 4, X2 = 13, Y2 = 21, Stroke = accent, StrokeThickness = 1.5 }, 0, 0);
                Add(canvas, new Line { X1 = 3, Y1 = 12, X2 = 23, Y2 = 12, Stroke = accent, StrokeThickness = 1.5 }, 0, 0);
            }
            else if (ContainsAny(text, "свет", "ламп", "light", "освещ"))
            {
                Add(canvas, new Ellipse { Width = 14, Height = 14, Stroke = amber, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(60, 255, 183, 77)) }, 6, 2);
                Add(canvas, new Line { X1 = 10, Y1 = 18, X2 = 16, Y2 = 18, Stroke = amber, StrokeThickness = 2 }, 0, 0);
                Add(canvas, new Line { X1 = 10, Y1 = 21, X2 = 16, Y2 = 21, Stroke = amber, StrokeThickness = 2 }, 0, 0);
                Add(canvas, new Line { X1 = 11, Y1 = 24, X2 = 15, Y2 = 24, Stroke = amber, StrokeThickness = 2 }, 0, 0);
            }
            else if (ContainsAny(text, "вентил", "fan", "насос", "pump", "мотор", "двигател"))
            {
                Add(canvas, new Ellipse { Width = 7, Height = 7, Fill = accent }, 9.5, 9.5);
                Add(canvas, new Ellipse { Width = 8, Height = 8, Stroke = accent, StrokeThickness = 2 }, 9, 9);
                Add(canvas, new Ellipse { Width = 7, Height = 11, Fill = accent }, 9.5, 1);
                Add(canvas, new Ellipse { Width = 7, Height = 11, Fill = accent, RenderTransform = new RotateTransform { Angle = 120, CenterX = 3.5, CenterY = 5.5 } }, 9.5, 14);
                Add(canvas, new Ellipse { Width = 7, Height = 11, Fill = accent, RenderTransform = new RotateTransform { Angle = 240, CenterX = 3.5, CenterY = 5.5 } }, 2, 8);
            }
            else if (type.Contains("switch") || type.Contains("toggle") || type.Contains("checkbox") || type.Contains("vbutton"))
            {
                Add(canvas, new Rectangle { Width = 21, Height = 12, RadiusX = 6, RadiusY = 6, Stroke = accent, StrokeThickness = 2 }, 2, 7);
                Add(canvas, new Ellipse { Width = 8, Height = 8, Fill = green }, 14, 9);
            }
            else if (type.Contains("button") || type.Contains("action"))
            {
                Add(canvas, new Ellipse { Width = 18, Height = 18, Stroke = accent, StrokeThickness = 2 }, 4, 4);
                Add(canvas, new Line { X1 = 13, Y1 = 7, X2 = 13, Y2 = 14, Stroke = accent, StrokeThickness = 2.5 }, 0, 0);
                Add(canvas, new Line { X1 = 9, Y1 = 11, X2 = 17, Y2 = 11, Stroke = accent, StrokeThickness = 2.5 }, 0, 0);
            }
            else
            {
                Add(canvas, new Rectangle { Width = 18, Height = 18, RadiusX = 3, RadiusY = 3, Stroke = accent, StrokeThickness = 2 }, 4, 4);
                Add(canvas, new Line { X1 = 8, Y1 = 10, X2 = 18, Y2 = 10, Stroke = muted, StrokeThickness = 1.5 }, 0, 0);
                Add(canvas, new Line { X1 = 8, Y1 = 15, X2 = 16, Y2 = 15, Stroke = muted, StrokeThickness = 1.5 }, 0, 0);
            }

            return canvas;
        }

        /// <summary>Remove emoji presentation characters from text; the app icon remains visible beside the title.</summary>
        public static string CleanTitle(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var result = new System.Text.StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) i++;
                    continue;
                }
                if (char.IsLowSurrogate(c) || c == '\uFE0F' || c == '\uFE0E' || c == '\u200D' ||
                    (c >= '\u2600' && c <= '\u27BF') || (c >= '\uFE00' && c <= '\uFE0F'))
                    continue;
                result.Append(c);
            }
            return result.ToString().Trim();
        }

        /// <summary>
        /// Detects supported emoji only at the start of the MQTT title. Matching is by
        /// exact Unicode sequence, not by words such as "door" or "cucumber".
        /// </summary>
        public static bool TryGetLeadingEmoji(string value, out string emoji, out string remainingTitle)
        {
            emoji = null;
            remainingTitle = value ?? string.Empty;
            if (string.IsNullOrEmpty(value)) return false;

            string[] known = new string[]
            {
                "\U0001F952", // cucumber
                "\U0001F345", // tomato
                "\U0001F336", // hot pepper
                "\U0001F331", // seedling
                "\U0001F33F", // herb
                "\U0001F321", // thermometer
                "\U0001F6AA", // door
                "\U0001FA9F", // window
                "\U0001F4A1", // light bulb
                "\U0001F4A7", // water drop
            };

            for (int i = 0; i < known.Length; i++)
            {
                string candidate = known[i];
                if (candidate.IndexOf(' ') >= 0) continue;
                if (value.StartsWith(candidate, StringComparison.Ordinal))
                {
                    int consumed = candidate.Length;
                    if (consumed < value.Length && value[consumed] == '\uFE0F') consumed++;
                    emoji = candidate;
                    remainingTitle = value.Substring(consumed).TrimStart();
                    return true;
                }
            }

            // WP8.1 devices have incomplete emoji font coverage. Treat any leading
            // Unicode emoji as an icon request, not as text that can become a square.
            // Keep the full sequence (variation selectors, skin tones, flags and ZWJ
            // sequences) out of the visible title; unsupported symbols get a safe vector fallback.
            int firstCodePoint;
            int consumed;
            ReadCodePoint(value, 0, out firstCodePoint, out consumed);
            if (!IsEmojiCodePoint(firstCodePoint)) return false;

            while (consumed < value.Length)
            {
                int nextCodePoint;
                int nextLength;
                ReadCodePoint(value, consumed, out nextCodePoint, out nextLength);

                if (nextCodePoint == 0xFE0E || nextCodePoint == 0xFE0F ||
                    (nextCodePoint >= 0x1F3FB && nextCodePoint <= 0x1F3FF) ||
                    nextCodePoint == 0x20E3)
                {
                    consumed += nextLength;
                    continue;
                }

                // Keep joined emoji (for example, family or person-with-tool) together.
                if (nextCodePoint == 0x200D)
                {
                    consumed += nextLength;
                    if (consumed < value.Length)
                    {
                        int joinedCodePoint;
                        int joinedLength;
                        ReadCodePoint(value, consumed, out joinedCodePoint, out joinedLength);
                        consumed += joinedLength;
                    }
                    continue;
                }

                // Regional-indicator flags consist of two code points.
                if (firstCodePoint >= 0x1F1E6 && firstCodePoint <= 0x1F1FF &&
                    nextCodePoint >= 0x1F1E6 && nextCodePoint <= 0x1F1FF)
                    consumed += nextLength;
                break;
            }

            emoji = value.Substring(0, consumed);
            remainingTitle = value.Substring(consumed).TrimStart();
            return true;
        }

        private static void ReadCodePoint(string value, int index, out int codePoint, out int length)
        {
            char first = value[index];
            if (char.IsHighSurrogate(first) && index + 1 < value.Length &&
                char.IsLowSurrogate(value[index + 1]))
            {
                codePoint = char.ConvertToUtf32(first, value[index + 1]);
                length = 2;
            }
            else
            {
                codePoint = first;
                length = 1;
            }
        }

        private static bool IsEmojiCodePoint(int codePoint)
        {
            return (codePoint >= 0x1F000 && codePoint <= 0x1FAFF) ||
                   (codePoint >= 0x2600 && codePoint <= 0x27BF) ||
                   (codePoint >= 0x2300 && codePoint <= 0x23FF) ||
                   codePoint == 0x00A9 || codePoint == 0x00AE ||
                   codePoint == 0x203C || codePoint == 0x2049 ||
                   codePoint == 0x2122 || codePoint == 0x2139 ||
                   codePoint == 0x3030 || codePoint == 0x303D ||
                   codePoint == 0x3297 || codePoint == 0x3299;
        }

        /// <summary>
        /// Displays the matching bundled Noto Emoji PNG in full color. The app package
        /// contains the complete pinned image set, so rendering works without internet.
        /// </summary>
        public static FrameworkElement CreateForEmoji(string emoji)
        {
            string assetName = GetEmojiAssetName(emoji);
            var image = new Image
            {
                Width = 26,
                Height = 26,
                Stretch = Windows.UI.Xaml.Media.Stretch.Uniform,
                Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(
                    new Uri("ms-appx:///Assets/Emoji/" + assetName))
            };
            var host = new Border
            {
                Width = 26,
                Height = 26,
                Child = image
            };
            image.ImageFailed += (sender, args) =>
            {
                // Some Unicode sequences have no standalone artwork in Noto.
                host.Child = CreateVectorForEmoji(emoji);
            };
            return host;
        }

        private static string GetEmojiAssetName(string emoji)
        {
            var name = new System.Text.StringBuilder();
            int index = 0;
            while (index < emoji.Length)
            {
                int codePoint;
                int length;
                ReadCodePoint(emoji, index, out codePoint, out length);
                index += length;

                // Presentation selectors are not part of Noto PNG file names.
                if (codePoint == 0xFE0E || codePoint == 0xFE0F)
                    continue;

                if (name.Length > 0) name.Append('_');
                name.Append(codePoint <= 0xFFFF
                    ? codePoint.ToString("x4")
                    : codePoint.ToString("x"));
            }
            return "emoji_u" + name.ToString() + ".png";
        }

        /// <summary>Vector fallback for a sequence with no bundled Noto artwork.</summary>
        private static FrameworkElement CreateVectorForEmoji(string emoji)
        {
            var canvas = new Canvas { Width = 26, Height = 26 };
            Brush green = new SolidColorBrush(Green);
            Brush red = new SolidColorBrush(Color.FromArgb(255, 232, 92, 82));
            Brush accent = new SolidColorBrush(Accent);
            Brush amber = new SolidColorBrush(Amber);
            Brush muted = new SolidColorBrush(Color.FromArgb(255, 190, 198, 210));

            if (emoji == "\U0001F952")
            {
                // Cucumber: rounded green body, small stem and characteristic dots.
                Add(canvas, new Ellipse { Width = 19, Height = 8, Fill = green, Stroke = new SolidColorBrush(Color.FromArgb(255, 55, 135, 74)), StrokeThickness = 1 }, 3, 9);
                Add(canvas, new Line { X1 = 2, Y1 = 13, X2 = 5, Y2 = 13, Stroke = green, StrokeThickness = 2 }, 0, 0);
                Add(canvas, new Ellipse { Width = 1.8, Height = 1.8, Fill = muted }, 8, 11);
                Add(canvas, new Ellipse { Width = 1.8, Height = 1.8, Fill = muted }, 14, 13);
                Add(canvas, new Ellipse { Width = 1.8, Height = 1.8, Fill = muted }, 18, 10);
            }
            else if (emoji == "\U0001F345")
            {
                Add(canvas, new Ellipse { Width = 18, Height = 18, Fill = red }, 4, 5);
                Add(canvas, new Ellipse { Width = 8, Height = 5, Fill = green, RenderTransform = new RotateTransform { Angle = -25 } }, 8, 2);
                Add(canvas, new Line { X1 = 13, Y1 = 3, X2 = 14, Y2 = 7, Stroke = green, StrokeThickness = 2 }, 0, 0);
            }
            else if (emoji == "\U0001F336")
            {
                Add(canvas, new Ellipse { Width = 17, Height = 7, Fill = red, RenderTransform = new RotateTransform { Angle = -35 }, RenderTransformOrigin = new Point(0.5, 0.5) }, 4, 9);
                Add(canvas, new Line { X1 = 17, Y1 = 7, X2 = 21, Y2 = 4, Stroke = green, StrokeThickness = 2 }, 0, 0);
            }
            else if (emoji == "\U0001F331" || emoji == "\U0001F33F")
            {
                Add(canvas, new Line { X1 = 13, Y1 = 24, X2 = 13, Y2 = 8, Stroke = green, StrokeThickness = 2 }, 0, 0);
                Add(canvas, new Ellipse { Width = 13, Height = 7, Fill = green, RenderTransform = new RotateTransform { Angle = -35 } }, 2, 4);
                Add(canvas, new Ellipse { Width = 13, Height = 7, Fill = green, RenderTransform = new RotateTransform { Angle = 35 } }, 11, 7);
            }
            else if (emoji == "\U0001F321")
            {
                Add(canvas, new Rectangle { Width = 7, Height = 15, RadiusX = 3, RadiusY = 3, Stroke = amber, StrokeThickness = 2 }, 9, 2);
                Add(canvas, new Ellipse { Width = 12, Height = 12, Fill = amber }, 6.5, 12);
                Add(canvas, new Rectangle { Width = 3, Height = 11, Fill = amber }, 11, 5);
            }
            else if (emoji == "\U0001F6AA" || emoji == "\U0001FA9F")
            {
                Add(canvas, new Rectangle { Width = 15, Height = 21, Stroke = accent, StrokeThickness = 2 }, 5, 3);
                Add(canvas, new Ellipse { Width = 2.8, Height = 2.8, Fill = amber }, 15, 13);
            }
            else if (emoji == "\U0001F4A1")
            {
                Add(canvas, new Ellipse { Width = 14, Height = 14, Stroke = amber, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(60, 255, 183, 77)) }, 6, 2);
                Add(canvas, new Line { X1 = 10, Y1 = 18, X2 = 16, Y2 = 18, Stroke = amber, StrokeThickness = 2 }, 0, 0);
                Add(canvas, new Line { X1 = 10, Y1 = 21, X2 = 16, Y2 = 21, Stroke = amber, StrokeThickness = 2 }, 0, 0);
            }
            else if (emoji == "\U0001F4A7")
            {
                Add(canvas, new Ellipse { Width = 12, Height = 16, Fill = accent, RenderTransform = new RotateTransform { Angle = 25 } }, 7, 4);
            }
            else
            {
                // Universal app-owned symbol for emoji not yet mapped to a dedicated drawing.
                // It deliberately uses shapes rather than a device font, so no tofu squares appear.
                Add(canvas, new Ellipse { Width = 20, Height = 20, Stroke = accent, StrokeThickness = 2 }, 3, 3);
                Add(canvas, new Ellipse { Width = 4, Height = 4, Fill = amber }, 7, 8);
                Add(canvas, new Ellipse { Width = 4, Height = 4, Fill = amber }, 15, 8);
                Add(canvas, new Line { X1 = 8, Y1 = 16, X2 = 18, Y2 = 16, Stroke = green, StrokeThickness = 2 }, 0, 0);
            }
            return canvas;
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            for (int i = 0; i < needles.Length; i++)
                if (value.Contains(needles[i])) return true;
            return false;
        }

        private static void Add(Canvas canvas, UIElement element, double left, double top)
        {
            Canvas.SetLeft(element, left);
            Canvas.SetTop(element, top);
            canvas.Children.Add(element);
        }
    }
}