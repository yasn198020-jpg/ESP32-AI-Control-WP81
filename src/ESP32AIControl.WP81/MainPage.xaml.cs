using System;
using System.Collections.Generic;
using System.Globalization;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using ESP32AIControl.WP81.Core.Mqtt;
using ESP32AIControl.WP81.Core.Protocol;
using ESP32AIControl.WP81.Core.State;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81
{
    public sealed partial class MainPage : Page
    {
        private readonly MqttSettings _settings;
        private readonly DeviceRepository _devices;
        private readonly Esp32MqttService _mqtt;

        private readonly Dictionary<string, WidgetVisual> _visuals =
            new Dictionary<string, WidgetVisual>();

        private readonly Dictionary<string, PageVisual> _pages =
            new Dictionary<string, PageVisual>();

        private bool _syncingControls;

        public MainPage()
        {
            InitializeComponent();

            _settings = new MqttSettings();
            _devices = new DeviceRepository();
            _mqtt = new Esp32MqttService(_settings, _devices);

            _mqtt.MessageReceived += Mqtt_MessageReceived;
            _mqtt.ConnectionStateChanged += Mqtt_ConnectionStateChanged;
            _mqtt.WidgetChanged += Mqtt_WidgetChanged;
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ApplySettingsFromUi();
                await _mqtt.ConnectAsync();
                Log("TX /" + _settings.Prefix + " HELLO");
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
            }
        }

        private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            await _mqtt.DisconnectAsync();
        }

        private async void HelloButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_mqtt.State != MqttConnectionState.Connected)
                {
                    Log("MQTT не подключён.");
                    return;
                }

                await _mqtt.PublishHelloAsync();
                Log("TX /" + _settings.Prefix + " HELLO");
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message);
            }
        }

        private void ApplySettingsFromUi()
        {
            _settings.Host = HostBox.Text.Trim();
            _settings.Port = PortBox.Text.Trim();
            _settings.Prefix = PrefixBox.Text.Trim().Trim('/');
            _settings.UserName = UserNameBox.Text.Trim();
            _settings.Password = PasswordBox.Password;
        }

        private async void Mqtt_MessageReceived(object sender, Esp32Message e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                Log("RX [" + e.Kind + "] " + e.Topic + " = " + e.Value);
            });
        }

        private async void Mqtt_WidgetChanged(object sender, WidgetState e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                UpdateWidgetVisual(e);
                Log("WIDGET " + e.DeviceId + "/" + e.Id + " = " + e.Value);
            });
        }

        private async void Mqtt_ConnectionStateChanged(object sender, MqttConnectionStateChangedEventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                ConnectionText.Text = "MQTT: " + e.State;
            });
        }

        private void UpdateWidgetVisual(WidgetState state)
        {
            if (state == null || string.IsNullOrEmpty(state.Id))
                return;

            WidgetVisual visual;
            if (!_visuals.TryGetValue(state.Key, out visual))
            {
                visual = CreateWidgetVisual(state);
                _visuals[state.Key] = visual;
                InsertWidgetVisual(visual);
            }
            else
            {
                var newPage = NormalizePage(state.Page);
                if (!string.Equals(visual.PageName, newPage, StringComparison.Ordinal))
                    MoveWidgetVisual(visual, newPage);

                visual.State = state;
            }

            _syncingControls = true;
            try
            {
                ApplyValueToVisual(visual, state.Value);
            }
            finally
            {
                _syncingControls = false;
            }
        }

        private WidgetVisual CreateWidgetVisual(WidgetState state)
        {
            var pageName = NormalizePage(state.Page);
            var visual = new WidgetVisual
            {
                State = state,
                PageName = pageName
            };

            visual.Container = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 12),
                Tag = state.Key
            };

            visual.Title = new TextBlock
            {
                Text = GetTitle(state),
                FontSize = 18,
                TextWrapping = TextWrapping.Wrap
            };

            visual.ValueText = new TextBlock
            {
                Text = string.IsNullOrEmpty(state.Value) ? "—" : state.Value,
                FontSize = 24,
                Margin = new Thickness(0, 2, 0, 5)
            };

            visual.Container.Children.Add(visual.Title);
            visual.Container.Children.Add(visual.ValueText);

            var type = NormalizeType(state.WidgetType);

            if (type == "switch" || type == "toggle" || type == "checkbox")
            {
                var check = new CheckBox
                {
                    Content = "Включено",
                    Margin = new Thickness(0, 0, 0, 2)
                };
                check.Checked += (s, e) => PublishSwitchValue(visual, true);
                check.Unchecked += (s, e) => PublishSwitchValue(visual, false);
                visual.Control = check;
                visual.Container.Children.Add(check);
            }
            else if (type == "button" || type == "action")
            {
                var button = new Button
                {
                    Content = "Выполнить",
                    MinWidth = 160
                };
                button.Click += async (s, e) =>
                {
                    await PublishControl(visual, "1");
                };
                visual.Control = button;
                visual.Container.Children.Add(button);
            }
            else if (type == "slider" || type == "range")
            {
                var slider = new Slider
                {
                    Minimum = 0,
                    Maximum = 100,
                    StepFrequency = 1,
                    Width = 280
                };
                slider.ValueChanged += async (s, e) =>
                {
                    if (_syncingControls)
                        return;

                    await PublishControl(visual, e.NewValue.ToString(CultureInfo.InvariantCulture));
                };
                visual.Control = slider;
                visual.Container.Children.Add(slider);
            }
            else
            {
                var info = new TextBlock
                {
                    Text = "Только отображение",
                    Opacity = 0.65,
                    FontSize = 13
                };
                visual.Control = info;
                visual.Container.Children.Add(info);
            }

            return visual;
        }

        private void InsertWidgetVisual(WidgetVisual visual)
        {
            var page = GetOrCreatePageVisual(visual.PageName);
            InsertWidgetIntoPage(page, visual);
        }

        private void InsertWidgetIntoPage(PageVisual page, WidgetVisual visual)
        {
            var insertIndex = page.Content.Children.Count;
            var state = visual.State;

            for (var i = 0; i < page.Content.Children.Count; i++)
            {
                var existing = page.Content.Children[i] as FrameworkElement;
                if (existing == null)
                    continue;

                var key = existing.Tag as string;
                WidgetVisual other;
                if (!string.IsNullOrEmpty(key) &&
                    _visuals.TryGetValue(key, out other) &&
                    other.State != null &&
                    other.State.Order > state.Order)
                {
                    insertIndex = i;
                    break;
                }
            }

            page.Content.Children.Insert(insertIndex, visual.Container);
        }

        private void MoveWidgetVisual(WidgetVisual visual, string newPageName)
        {
            PageVisual oldPage;
            if (_pages.TryGetValue(visual.PageName, out oldPage))
                oldPage.Content.Children.Remove(visual.Container);

            visual.PageName = newPageName;
            var newPage = GetOrCreatePageVisual(newPageName);
            InsertWidgetIntoPage(newPage, visual);
            CleanupEmptyPages();
        }

        private PageVisual GetOrCreatePageVisual(string pageName)
        {
            PageVisual page;
            if (_pages.TryGetValue(pageName, out page))
                return page;

            page = new PageVisual
            {
                Name = pageName,
                Content = new StackPanel
                {
                    Margin = new Thickness(0, 0, 0, 8)
                }
            };

            var pivotItem = new PivotItem
            {
                Header = pageName,
                Content = page.Content,
                Tag = pageName
            };
            page.Item = pivotItem;

            _pages[pageName] = page;

            var insertIndex = WidgetPages.Items.Count;
            for (var i = 0; i < WidgetPages.Items.Count; i++)
            {
                var existing = WidgetPages.Items[i] as PivotItem;
                var existingName = existing == null ? string.Empty : existing.Tag as string;
                if (ComparePageNames(pageName, existingName) < 0)
                {
                    insertIndex = i;
                    break;
                }
            }

            WidgetPages.Items.Insert(insertIndex, pivotItem);

            return page;
        }

        private void CleanupEmptyPages()
        {
            var emptyPages = new List<string>();
            foreach (var pair in _pages)
            {
                if (pair.Value.Content.Children.Count == 0)
                    emptyPages.Add(pair.Key);
            }

            foreach (var pageName in emptyPages)
            {
                PageVisual page;
                if (!_pages.TryGetValue(pageName, out page))
                    continue;

                WidgetPages.Items.Remove(page.Item);
                _pages.Remove(pageName);
            }
        }

        private string GetTitle(WidgetState state)
        {
            if (!string.IsNullOrWhiteSpace(state.Description))
                return state.Description;

            if (!string.IsNullOrWhiteSpace(state.Page))
                return state.Page + " • " + state.Id;

            return state.DeviceId + " • " + state.Id;
        }

        private static string NormalizeType(string value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string NormalizePage(string value)
        {
            var page = (value ?? string.Empty).Trim();
            return string.IsNullOrEmpty(page) ? "Основная" : page;
        }

        private static int ComparePageNames(string left, string right)
        {
            int leftNumber;
            int rightNumber;

            if (int.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out leftNumber) &&
                int.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out rightNumber))
            {
                return leftNumber.CompareTo(rightNumber);
            }

            return string.Compare(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private void ApplyValueToVisual(WidgetVisual visual, string value)
        {
            if (visual.ValueText != null)
                visual.ValueText.Text = string.IsNullOrEmpty(value) ? "—" : value;

            var check = visual.Control as CheckBox;
            if (check != null)
            {
                bool enabled = ParseBoolean(value);
                check.IsChecked = enabled;
                return;
            }

            var slider = visual.Control as Slider;
            if (slider != null)
            {
                double number;
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                {
                    slider.Value = Math.Max(slider.Minimum, Math.Min(slider.Maximum, number));
                }
            }
        }

        private static bool ParseBoolean(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            switch (value.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "on":
                case "yes":
                case "open":
                    return true;
                default:
                    return false;
            }
        }

        private async void PublishSwitchValue(WidgetVisual visual, bool enabled)
        {
            if (_syncingControls)
                return;

            await PublishControl(visual, enabled ? "1" : "0");
        }

        private async System.Threading.Tasks.Task PublishControl(WidgetVisual visual, string value)
        {
            try
            {
                if (_mqtt.State != MqttConnectionState.Connected)
                {
                    Log("MQTT не подключён: control " + visual.State.Id);
                    return;
                }

                await _mqtt.PublishControlAsync(
                    visual.State.DeviceId,
                    visual.State.Id,
                    value);

                Log("TX control " + visual.State.DeviceId + "/" +
                    visual.State.Id + " = " + value);
            }
            catch (Exception ex)
            {
                Log("ERROR control: " + ex.Message);
            }
        }

        private void Log(string text)
        {
            LogText.Text = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text +
                           Environment.NewLine + LogText.Text;
        }

        private sealed class WidgetVisual
        {
            public WidgetState State { get; set; }
            public string PageName { get; set; }
            public StackPanel Container { get; set; }
            public TextBlock Title { get; set; }
            public TextBlock ValueText { get; set; }
            public FrameworkElement Control { get; set; }
        }

        private sealed class PageVisual
        {
            public string Name { get; set; }
            public StackPanel Content { get; set; }
            public PivotItem Item { get; set; }
        }
    }
}