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
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                foreach (var page in pages)
                {
                    var selected = page == _activePage;
                    var button = new Button { Content = page.Replace("⚙️", "").Replace("⚙", "").Trim(),
                        Background = selected ? new SolidColorBrush(Color.FromArgb(255, 98, 98, 98)) : Panel,
                        Foreground = Primary, Padding = new Thickness(16, 10, 16, 10),
                        BorderThickness = new Thickness(0), MinWidth = 0 };
                    var pageCopy = page;
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
                    int selected; Int32.TryParse(widget.Value, out selected);
                    var combo = new ComboBox { ItemsSource = options, SelectedIndex = Math.Max(0, Math.Min(options.Count - 1, selected)), HorizontalAlignment = HorizontalAlignment.Stretch };
                    combo.SelectionChanged += (s, e) => { if (combo.SelectedIndex >= 0) _send(entry.Device, widget, combo.SelectedIndex.ToString(CultureInfo.InvariantCulture)); };
                    body.Children.Add(combo);
                }
                else if (definition == "input" || definition == "inputtxt" || definition == "inputdgt" ||
                         definition == "inputdate" || definition == "inputtm")
                {
                    var row = new Grid();
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var input = new TextBox { PlaceholderText = widget.Title, Text = widget.Value ?? "", MinWidth = 80 };
                    if (definition == "inputdgt") input.InputScope = new Windows.UI.Xaml.Input.InputScope { Names = { new Windows.UI.Xaml.Input.InputScopeName(Windows.UI.Xaml.Input.InputScopeNameValue.Number) } };
                    row.Children.Add(input);
                    var send = ActionButton("Отправить");
                    send.Click += (s, e) => { if (String.IsNullOrWhiteSpace(input.Text)) input.Header = "Введите значение"; else _send(entry.Device, widget, input.Text.Trim()); };
                    Grid.SetColumn(send, 1); row.Children.Add(send); body.Children.Add(row);
                }
                else if (definition == "progressline" || definition == "progress-line")
                {
                    double value; Double.TryParse((widget.Value ?? "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
                    var isFill = definition == "fillgauge";\n                    var max = isFill ? 100 : Math.Max(0.0001, widget.ConfigNumber("max", 100));
                    body.Children.Add(Text(widget.Title + "   " + Format(value) + " / " + Format(max), 16, Primary, false));
                    body.Children.Add(new ProgressBar { Minimum = 0, Maximum = max, Value = Math.Max(0, Math.Min(max, value)), Height = 8 });
                }
                else
                {
                    body.Children.Add(Text(widget.Title, 16, Primary, false));
                    body.Children.Add(Text(String.IsNullOrWhiteSpace(widget.Value) ? "—" : widget.Value + widget.Unit, 21, Accent, false));
                }

                if (widget.LastUpdatedUnixMs > 0)
                {
                    var age = Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - widget.LastUpdatedUnixMs) / 1000);
                    string ageText = age < 60 ? age + " сек назад" : age < 3600 ? (age/60) + " мин назад" : age < 86400 ? (age/3600) + " ч назад" : (age/86400) + " дн назад";
                    body.Children.Add(Text("обновлено " + ageText, 10, Muted, false));
                }
                if (!entry.Device.Online) body.Children.Add(Text("Офлайн", 12, new SolidColorBrush(Color.FromArgb(255, 255, 138, 128)), true));\n                _host.Children.Add(card);
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
            panel.Children.Add(Text(widget.Title, 17, Primary, false));
            try
            {
                var root = JObject.Parse(widget.Value ?? "{}");
                var status = root["status"] as JArray;
                if (status == null || status.Count == 0)
                {
                    panel.Children.Add(Text("Нет точек графика", 13, Muted, true));
                    return panel;
                }
                var series = new List<List<double>>();
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
                foreach (var values in series)
                {
                    if (values.Count == 0) continue;
                    var min = values.Min(); var max = values.Max();
                    var range = Math.Max(0.0001, max - min);
                    var bars = new StackPanel { Orientation = Orientation.Horizontal, Height = 72, VerticalAlignment = VerticalAlignment.Bottom };
                    foreach (var value in TakeLastCompat(values, 60))
                    {
                        var bar = new Border { Background = Accent, Width = Math.Max(2, 260.0 / Math.Min(60, values.Count) - 2),
                            Height = Math.Max(3, 68 * (value - min) / range + 3), VerticalAlignment = VerticalAlignment.Bottom,
                            Margin = new Thickness(1, 0, 1, 0), CornerRadius = new CornerRadius(2) };
                        bars.Children.Add(bar);
                    }
                    panel.Children.Add(bars);
                }
                panel.Children.Add(Text("Точек: " + status.Count, 12, Muted, false));
            }
            catch { panel.Children.Add(Text("Данные графика пока недоступны", 13, Muted, true)); }
            return panel;
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
