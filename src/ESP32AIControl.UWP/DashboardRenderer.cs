using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Controls.Primitives;
using Newtonsoft.Json.Linq;

namespace ESP32AIControl.UWP
{
    // Renders Android's page-based dashboard from a device/widget snapshot.
    // Sending commands is exposed as an event so MQTT transport can be attached independently.
    public sealed class DashboardRenderer
    {
        private readonly StackPanel _host;
        private readonly DashboardSnapshot _snapshot;
        private readonly Action<string> _selectPage;
        private readonly Action<DeviceModel, WidgetModel, string> _send;
        private string _activePage;
        private bool _showLastUpdated;
        private readonly Dictionary<string, string> _inputDrafts = new Dictionary<string, string>();
        public bool ShowLastUpdated { get { return _showLastUpdated; } set { _showLastUpdated = value; } }

        private static readonly Brush Primary = new SolidColorBrush(Color.FromArgb(255, 241, 240, 247));
        private static readonly Brush Muted = new SolidColorBrush(Color.FromArgb(255, 172, 173, 184));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromArgb(255, 208, 188, 255));
        private static readonly Brush Card = new SolidColorBrush(Color.FromArgb(255, 48, 51, 60));
        private static readonly Brush Panel = new SolidColorBrush(Color.FromArgb(255, 43, 45, 53));

        public DashboardRenderer(StackPanel host, DashboardSnapshot snapshot,
            Action<string> selectPage, Action<DeviceModel, WidgetModel, string> send)
        {
            _host = host; _snapshot = snapshot; _selectPage = selectPage; _send = send;
        }

        public string ActivePage { get { return _activePage; } set { _activePage = value; } }

        public void Render()
        {
            _host.Children.Clear();
            var devices = _snapshot.Devices.ToList();
            if (devices.Count == 0)
            {
                _host.Children.Add(Text("Нет конфигурации. Подключитесь к MQTT и нажмите HELLO.", 16, Primary, true));
                _host.Children.Add(Text("После получения конфигурации устройства и его элементы появятся здесь.", 13, Muted, true));
                return;
            }

            var pages = devices.SelectMany(d => d.Widgets)
                .Select(w => (w.Page ?? "").Trim()).Where(p => p.Length > 0)
                .Distinct(StringComparer.Ordinal).ToList();
            if (String.IsNullOrEmpty(_activePage) || !pages.Contains(_activePage))
                _activePage = pages.FirstOrDefault() ?? "";

            if (pages.Count > 0)
            {
                var tabs = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled,
                    VerticalScrollMode = ScrollMode.Disabled, Margin = new Thickness(0, 0, 0, 10) };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                foreach (var page in pages)
                {
                    var selected = page == _activePage;
                    var hasGear = page.Contains("⚙") || page.Contains("⚙️") || page.Contains("⚙︎");
                    var displayPage = page.Replace("⚙️", "").Replace("⚙︎", "").Replace("⚙", "").Trim();
                    var button = new Button { Content = (hasGear ? "⚙  " : "") + (String.IsNullOrWhiteSpace(displayPage) ? page : displayPage),
                        Background = selected ? new SolidColorBrush(Color.FromArgb(255, 98, 98, 98)) : Panel,
                        Foreground = Primary, Padding = new Thickness(16, 10, 16, 10),
                        BorderThickness = new Thickness(0), MinWidth = 0 };
                    var pageCopy = page;
                    button.Margin = new Thickness(0, 0, 6, 0);
                    button.Click += (s, e) => { _activePage = pageCopy; _selectPage(pageCopy); Render(); };
                    row.Children.Add(button);
                }
                tabs.Content = row;
                _host.Children.Add(tabs);
            }

            var entries = devices.SelectMany(d => d.Widgets.Select(w => new { Device = d, Widget = w }))
                .Where(x => x.Widget.EffectiveDefinition != "nil" && !String.IsNullOrWhiteSpace(x.Widget.Page)
                    && x.Widget.Page.Trim() == _activePage)
                .OrderBy(x => x.Widget.Order).ThenBy(x => x.Widget.Title).ThenBy(x => x.Device.Id)
                .ToList();

            if (entries.Count == 0)
            {
                _host.Children.Add(Text("На этой странице пока нет элементов.", 14, Muted, true));
                return;
            }

            foreach (var entry in entries)
            {
                var widget = entry.Widget;
                var definition = widget.EffectiveDefinition;
                var card = new Border { Background = Card, CornerRadius = new CornerRadius(10),
                    Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(14, 10, 14, 10) };
                var body = new StackPanel();
                card.Child = body;
                if (definition == "toggle")
                {
                    var row = TwoColumnRow(widget.Title);
                    var toggle = new ToggleSwitch { IsOn = IsOn(widget.Value), OnContent = "", OffContent = "", MinWidth = 60 };
                    Grid.SetColumn(toggle, 1);
                    toggle.Toggled += (s, e) => _send(entry.Device, widget, toggle.IsOn ? "1" : "0");
                    row.Children.Add(toggle); body.Children.Add(row);
                }
                else if (definition == "button" || definition == "vbtn" || definition == "btn")
                {
                    var row = TwoColumnRow(widget.Title);
                    var button = ActionButton("Нажать");
                    button.Click += (s, e) => _send(entry.Device, widget, "1");
                    Grid.SetColumn(button, 1); row.Children.Add(button); body.Children.Add(row);
                }
                else if (definition == "range" || definition == "rangeservo")
                {
                    var min = widget.ConfigNumber("min", 0);
                    var max = Math.Max(min + 1, widget.ConfigNumber("max", definition == "rangeservo" ? 180 : 100));
                    var factor = widget.ConfigNumber("k", 1);
                    if (factor == 0) factor = 1;
                    double raw; Double.TryParse((widget.Value ?? "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out raw);
                    var initial = Math.Max(min, Math.Min(max, raw * factor));
                    var valueText = Text(Format(initial) + widget.Unit, 16, Primary, false);
                    body.Children.Add(new Grid { Children = { Text(widget.Title, 17, Primary, false), valueText } });
                    var slider = new Slider { Minimum = min, Maximum = max, Value = initial, StepFrequency = Math.Max((max-min)/100.0, 0.1) };
                    slider.ValueChanged += (s, e) => valueText.Text = Format(e.NewValue) + widget.Unit;
                    slider.PointerCaptureLost += (s, e) => _send(entry.Device, widget, Format(slider.Value / factor));
                    body.Children.Add(slider);
                    var rangeText = Text("Диапазон " + Format(min) + " … " + Format(max), 12, Muted, false);
                    body.Children.Add(rangeText);
                }
                else if (definition == "select")
                {
                    body.Children.Add(Text(widget.Title, 17, Primary, false));
                    var config = widget.ReadConfig();
                    var options = ReadOptions(config);
                    if (options.Count == 0) { options.Add("Выключен"); options.Add("Включен"); }
                    int selected; if (!Int32.TryParse(widget.Value, out selected)) selected = (int)widget.ConfigNumber("status", 0);
                    var combo = new ComboBox { ItemsSource = options, SelectedIndex = Math.Max(0, Math.Min(options.Count - 1, selected)), HorizontalAlignment = HorizontalAlignment.Stretch };
                    combo.SelectionChanged += (s, e) => { if (combo.SelectedIndex >= 0) _send(entry.Device, widget, combo.SelectedIndex.ToString(CultureInfo.InvariantCulture)); };
                    body.Children.Add(combo);
                }
                else if (definition == "inputdate" || definition == "inputtm")
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    if (definition == "inputdate")
                    {
                        var picker = new DatePicker { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 150 };
                        var send = ActionButton("Отправить");
                        send.Click += (s, e) => _send(entry.Device, widget, picker.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
                        row.Children.Add(picker); row.Children.Add(send);
                    }
                    else
                    {
                        var picker = new TimePicker { ClockIdentifier = "24HourClock", MinuteIncrement = 1, MinWidth = 130 };
                        var send = ActionButton("Отправить");
                        send.Click += (s, e) => _send(entry.Device, widget, picker.Time.ToString(@"hh\\:mm", CultureInfo.InvariantCulture));
                        row.Children.Add(picker); row.Children.Add(send);
                    }
                    body.Children.Add(Text(widget.Title, 17, Primary, false));
                    body.Children.Add(row);
                }
                else if (definition == "input" || definition == "inputtxt" || definition == "inputdgt")
                {
                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var draftKey = entry.Device.Id + "/" + widget.Id;
                    string draft; _inputDrafts.TryGetValue(draftKey, out draft);
                    if (draft != null && draft == (widget.Value ?? "")) { _inputDrafts.Remove(draftKey); draft = null; }
                    var input = new TextBox { PlaceholderText = widget.Title, Text = draft ?? (widget.Value ?? ""), MinWidth = 80 };
                    input.TextChanged += (s, e) => _inputDrafts[draftKey] = input.Text;
                    if (definition == "inputdgt") input.InputScope = new Windows.UI.Xaml.Input.InputScope { Names = { new Windows.UI.Xaml.Input.InputScopeName(Windows.UI.Xaml.Input.InputScopeNameValue.Number) } };
                    row.Children.Add(input);
                    var send = ActionButton("Отправить");
                    send.Click += (s, e) => { if (String.IsNullOrWhiteSpace(input.Text)) input.PlaceholderText = "Введите значение"; else { _send(entry.Device, widget, input.Text.Trim()); } };
                    Grid.SetColumn(send, 1); row.Children.Add(send); body.Children.Add(row);
                }
                else if (definition == "progressline" || definition == "progress-line")
                {
                    double value; Double.TryParse((widget.Value ?? "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
                    var max = Math.Max(0.0001, widget.ConfigNumber("max", 100));
                    body.Children.Add(Text(widget.Title + "   " + Format(value) + " / " + Format(max), 16, Primary, false));
                    body.Children.Add(new ProgressBar { Minimum = 0, Maximum = max, Value = Math.Max(0, Math.Min(max, value)), Height = Math.Max(2, widget.ConfigNumber("stroke", 10)) });
                }
                else if (definition == "progressround" || definition == "progress-round" || definition == "fillgauge")
                {
                    double value;
                    Double.TryParse((widget.Value ?? "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
                    var max = definition == "fillgauge" ? 100 : Math.Max(0.0001, widget.ConfigNumber("max", 100));
                    var gauge = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                    gauge.Children.Add(RenderCircularGauge(value, max, definition == "fillgauge", widget.ReadConfig()));
                    var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
                    details.Children.Add(Text(widget.Title, 17, Primary, false));
                    details.Children.Add(Text(Format(value) + " / " + Format(max) + (definition == "fillgauge" ? " %" : ""), 16, Accent, false));
                    gauge.Children.Add(details);
                    body.Children.Add(gauge);
                }
                else if (definition.StartsWith("chart"))
                {
                    body.Children.Add(RenderChart(widget));
                }
                else
                {
                    body.Children.Add(Text(widget.Title, 16, Primary, false));
                    double numericValue;
                    var suffix = String.IsNullOrWhiteSpace(widget.Unit) ? Convert.ToString(config.ContainsKey("after") ? config["after"] : "") : widget.Unit;
                    var valueBrush = Double.TryParse((widget.Value ?? "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out numericValue)
                        ? WidgetValueBrush(config, numericValue) : Accent;
                    body.Children.Add(Text(String.IsNullOrWhiteSpace(widget.Value) ? "—" : widget.Value + (String.IsNullOrWhiteSpace(suffix) ? "" : " " + suffix), 21, valueBrush, false));
                }

                if (_showLastUpdated && widget.LastUpdatedUnixMs > 0)
                {
                    var age = Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - widget.LastUpdatedUnixMs) / 1000);
                    string ageText = age < 60 ? age + " сек назад" : age < 3600 ? (age/60) + " мин назад" : age < 86400 ? (age/3600) + " ч назад" : (age/86400) + " дн назад";
                    body.Children.Add(Text("обновлено " + ageText, 10, Muted, false));
                }
                if (!entry.Device.Online) body.Children.Add(Text("Офлайн", 12, new SolidColorBrush(Color.FromArgb(255, 255, 138, 128)), true));
                if (!String.IsNullOrWhiteSpace(widget.CommandState))
                {
                    string state = widget.CommandState.ToUpperInvariant();
                    string message = state == "PENDING" ? "Ожидаю подтверждение ESP32: " + widget.ExpectedCommandValue :
                        state == "CONFIRMED" ? "ESP32 подтвердил выполнение" :
                        state == "TIMED_OUT" ? "Ответ ESP32 не получен" :
                        state == "SEND_FAILED" ? "Команда не отправлена" : "";
                    if (message.Length > 0) body.Children.Add(Text(message, 12, Muted, true));
                }
                _host.Children.Add(card);
            }
        }


        private static Grid TwoColumnRow(string title)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(Text(title, 17, Primary, false));
            return grid;
        }

        private static UIElement RenderChart(WidgetModel widget)
        {
            var panel = new StackPanel();
            var config = widget.ReadConfig();
            panel.Children.Add(Text(widget.Title, 17, Primary, false));
            try
            {
                var root = JObject.Parse(widget.Value ?? "{}");
                var status = root["status"] as JArray;
                int maxCount = Math.Max(20, Math.Min(600, (int)widget.ConfigNumber("maxCount", 240)));
                var series = new List<List<double>>();
                if (status != null)
                {
                    foreach (var point in status.OfType<JObject>())
                    {
                        for (int n = 1; n <= 3; n++)
                        {
                            var token = point["y" + n];
                            double number;
                            if (token != null && Double.TryParse(Convert.ToString(token, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out number))
                            {
                                while (series.Count < n) series.Add(new List<double>());
                                series[n - 1].Add(number);
                            }
                        }
                    }
                }
                series = series.Select(values => values.Skip(Math.Max(0, values.Count - maxCount)).ToList()).Where(v => v.Count > 0).ToList();
                if (series.Count == 0)
                {
                    panel.Children.Add(Text("Нет точек графика", 13, Muted, true));
                    return panel;
                }
                var isBar = String.Equals(Convert.ToString(config.ContainsKey("type") ? config["type"] : ""), "bar", StringComparison.OrdinalIgnoreCase)
                    || widget.EffectiveDefinition == "chart3" || widget.EffectiveDefinition == "chart4";
                var canvas = new Canvas { Height = 150, MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Stretch };
                canvas.SizeChanged += (s, e) => DrawChart(canvas, series, isBar, (int)widget.ConfigNumber("pointRadius", 2) > 0);
                DrawChart(canvas, series, isBar, (int)widget.ConfigNumber("pointRadius", 2) > 0);
                panel.Children.Add(canvas);
                object namesRaw;
                if (config.TryGetValue("series", out namesRaw))
                {
                    var names = namesRaw as JArray;
                    if (names != null && names.Count > 0)
                        panel.Children.Add(Text(String.Join(" • ", names.Select(x => Convert.ToString(x))), 12, Muted, true));
                }
                panel.Children.Add(Text("Точек: " + series.Max(v => v.Count), 12, Muted, false));
            }
            catch { panel.Children.Add(Text("Данные графика пока недоступны", 13, Muted, true)); }
            return panel;
        }

        private static void DrawChart(Canvas canvas, List<List<double>> series, bool bars, bool showPoints)
        {
            canvas.Children.Clear();
            double width = Math.Max(240, canvas.ActualWidth);
            const double height = 150, left = 8, top = 8, bottom = 18;
            var all = series.SelectMany(v => v).ToList();
            if (all.Count == 0) return;
            double min = all.Min(), max = all.Max();
            if (Math.Abs(max - min) < 0.000001) { max += 1; min -= 1; }
            var palette = new[] { Accent, new SolidColorBrush(Color.FromArgb(255, 129, 199, 132)), new SolidColorBrush(Color.FromArgb(255, 255, 183, 77)) };
            var baseline = new Line { X1 = left, X2 = width - 4, Y1 = height - bottom, Y2 = height - bottom, Stroke = Muted, StrokeThickness = 1, Opacity = 0.55 };
            canvas.Children.Add(baseline);
            for (int s = 0; s < series.Count; s++)
            {
                var values = series[s];
                int count = values.Count;
                double step = (width - left - 8) / Math.Max(1, count - 1);
                if (bars)
                {
                    double barWidth = Math.Max(1.5, Math.Min(9, (width - left - 8) / Math.Max(1, count) - 1));
                    for (int i = 0; i < count; i++)
                    {
                        double y = top + (max - values[i]) / (max - min) * (height - top - bottom);
                        var bar = new Rectangle { Width = barWidth, Height = Math.Max(1, height - bottom - y), Fill = palette[s % palette.Length], Opacity = 0.85 };
                        Canvas.SetLeft(bar, left + i * ((width - left - 8) / Math.Max(1, count)) );
                        Canvas.SetTop(bar, y); canvas.Children.Add(bar);
                    }
                }
                else
                {
                    var line = new Polyline { Stroke = palette[s % palette.Length], StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
                    for (int i = 0; i < count; i++)
                    {
                        double x = left + i * step;
                        double y = top + (max - values[i]) / (max - min) * (height - top - bottom);
                        line.Points.Add(new Windows.Foundation.Point(x, y));
                        if (showPoints && count <= 80)
                        {
                            var dot = new Ellipse { Width = 5, Height = 5, Fill = palette[s % palette.Length] };
                            Canvas.SetLeft(dot, x - 2.5); Canvas.SetTop(dot, y - 2.5); canvas.Children.Add(dot);
                        }
                    }
                    canvas.Children.Add(line);
                }
            }
        }

        private static UIElement RenderCircularGauge(double value, double max, bool fill, IDictionary<string, object> config)
        {
            var canvas = new Canvas { Width = 86, Height = 86 };
            var ringKey = fill ? "circleColor" : "color";
            object ringRaw; var ringColor = config.TryGetValue(ringKey, out ringRaw) ? ParseColor(Convert.ToString(ringRaw)) : (fill ? Colors.Cyan : Color.FromArgb(255, 208, 188, 255));
            var track = new Ellipse { Width = 72, Height = 72, Stroke = new SolidColorBrush(Color.FromArgb(255, 75, 78, 88)), StrokeThickness = fill ? 7 : (config.ContainsKey("stroke") ? Math.Max(4, Convert.ToDouble(config["stroke"], CultureInfo.InvariantCulture)) : 20) };
            Canvas.SetLeft(track, 7); Canvas.SetTop(track, 7); canvas.Children.Add(track);
            double fraction = Math.Max(0, Math.Min(1, value / Math.Max(0.0001, max)));
            double angle = fraction * 359.9;
            bool semicircle = !fill && Convert.ToString(config.ContainsKey("semicircle") ? config["semicircle"] : "0") == "1";
            double startAngle = semicircle ? 180.0 : -90.0;
            if (semicircle) angle = fraction * 179.9;
            double cx = 43, cy = 43, radius = 36;
            double radians = (startAngle + angle) * Math.PI / 180.0;
            var end = new Windows.Foundation.Point(cx + radius * Math.Cos(radians), cy + radius * Math.Sin(radians));
            var startRadians = startAngle * Math.PI / 180.0;
            var figure = new PathFigure { StartPoint = new Windows.Foundation.Point(cx + radius * Math.Cos(startRadians), cy + radius * Math.Sin(startRadians)), IsClosed = false };
            figure.Segments.Add(new ArcSegment { Point = end, Size = new Windows.Foundation.Size(radius, radius), IsLargeArc = angle > 180, SweepDirection = semicircle ? SweepDirection.Counterclockwise : SweepDirection.Clockwise });
            var geometry = new PathGeometry(); geometry.Figures.Add(figure);
            var arc = new Windows.UI.Xaml.Shapes.Path { Data = geometry, Stroke = new SolidColorBrush(ringColor), StrokeThickness = track.StrokeThickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            canvas.Children.Add(arc);
            var valueText = Text(Format(value), 18, Primary, false);
            valueText.Width = 86; valueText.TextAlignment = TextAlignment.Center;
            Canvas.SetTop(valueText, 32); canvas.Children.Add(valueText);
            return canvas;
        }

        private static Color ParseColor(string raw)
        {
            string value = (raw ?? "").Trim();
            if (value.StartsWith("#")) value = value.Substring(1);
            uint number;
            if (UInt32.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number))
            {
                if (value.Length == 6) return Color.FromArgb(255, (byte)(number >> 16), (byte)(number >> 8), (byte)number);
                if (value.Length == 8) return Color.FromArgb((byte)(number >> 24), (byte)(number >> 16), (byte)(number >> 8), (byte)number);
            }
            switch (value.ToLowerInvariant())
            {
                case "red": return Colors.Red;
                case "green": return Colors.Green;
                case "blue": return Colors.Blue;
                case "yellow": return Colors.Yellow;
                case "orange": return Colors.Orange;
                case "cyan": return Colors.Cyan;
                case "white": return Colors.White;
                case "black": return Colors.Black;
                case "magenta": return Colors.Magenta;
                default: return Color.FromArgb(255, 144, 202, 249);
            }
        }

        private static Brush WidgetValueBrush(IDictionary<string, object> config, double value)
        {
            object raw;
            string selected = null;
            double selectedLevel = Double.NegativeInfinity;
            if (config.TryGetValue("color", out raw))
            {
                var thresholds = raw as JArray;
                if (thresholds != null)
                {
                    foreach (var item in thresholds.OfType<JObject>())
                    {
                        double level;
                        if (Double.TryParse(Convert.ToString(item["level"], CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out level)
                            && level <= value && level >= selectedLevel)
                        { selectedLevel = level; selected = Convert.ToString(item["value"]); }
                    }
                }
                else selected = Convert.ToString(raw);
            }
            if (!String.IsNullOrWhiteSpace(selected))
            {
                try
                {
                    return new SolidColorBrush(ParseColor(selected));
                }
                catch { }
            }
            return Accent;
        }

        private static TextBlock Text(string value, double size, Brush brush, bool wrap)
        {
            return new TextBlock { Text = value, FontSize = size, Foreground = brush,
                TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, Margin = new Thickness(0, 3, 0, 3) };
        }

        private static Button ActionButton(string caption)
        {
            return new Button { Content = caption, Background = Accent, Foreground = new SolidColorBrush(Color.FromArgb(255, 32, 33, 38)),
                Padding = new Thickness(16, 7, 16, 7), BorderThickness = new Thickness(0), MinWidth = 0 };
        }

        private static bool IsOn(string value)
        {
            var v = (value ?? "").Trim().ToLowerInvariant();
            return v == "1" || v == "true" || v == "on" || v == "включено";
        }

        private static string Format(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static IEnumerable<T> TakeLastCompat<T>(IEnumerable<T> source, int count) { return source.Skip(Math.Max(0, source.Count() - count)); }

        private static List<string> ReadOptions(IDictionary<string, object> config)
        {
            object raw;
            var result = new List<string>();
            if (config.TryGetValue("options", out raw))
            {
                var list = raw as Newtonsoft.Json.Linq.JArray;
                if (list != null) foreach (var item in list) result.Add(Convert.ToString(item));
            }
            return result;
        }
    }
}
