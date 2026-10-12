using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;
using Windows.Media.SpeechRecognition;
using Windows.Storage.Pickers;
using ESP32AIControl.WP81.Core;
using ESP32AIControl.WP81.Core.Export;
using ESP32AIControl.WP81.Core.History;
using ESP32AIControl.WP81.Core.Mqtt;
using ESP32AIControl.WP81.Core.Protocol;
using ESP32AIControl.WP81.Core.Scenarios;
using ESP32AIControl.WP81.Core.Scheduling;
using ESP32AIControl.WP81.Core.Voice;
using ESP32AIControl.WP81.Core.State;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81
{
    public sealed partial class MainPage : Page
    {
        private readonly AppSettingsStore _settingsStore;
        private AppSettings _appSettings;
        private readonly MqttSettings _settings;
        private readonly DeviceRepository _devices;
        private readonly Esp32MqttService _mqtt;
        private readonly LocalMeasurementStore _measurementStore;
        private readonly MeasurementRecorder _recorder;
        private readonly RtfHistoryExporter _exporter;
        private readonly Esp32CommandExecutor _commandExecutor;
        private readonly ScheduledCommandService _scheduler;
        private readonly Wp81VoiceCommandService _voice;
        private VoiceCommandParser _voiceParser;

        private readonly Dictionary<string, WidgetVisual> _visuals =
            new Dictionary<string, WidgetVisual>();
        private readonly Dictionary<string, PageVisual> _pages =
            new Dictionary<string, PageVisual>();
        private readonly Dictionary<string, WidgetState> _widgetIndex =
            new Dictionary<string, WidgetState>();
        private readonly List<ScheduledCommand> _scheduleRows =
            new List<ScheduledCommand>();
        private readonly List<MeasurementPoint> _lastGraphPoints =
            new List<MeasurementPoint>();

        private DispatcherTimer _autoExportTimer;
        private bool _syncingControls;
        private bool _initializing;
        private bool _autoExportRunning;

        public MainPage()
        {
            InitializeComponent();
            UpdateNavigationState();

            _settingsStore = new AppSettingsStore();
            _appSettings = new AppSettings();

            _settings = new MqttSettings();
            _devices = new DeviceRepository();
            _mqtt = new Esp32MqttService(_settings, _devices);

            _measurementStore = new LocalMeasurementStore();
            _recorder = new MeasurementRecorder(
                _measurementStore,
                delegate { return Math.Max(1, _appSettings.MeasurementIntervalSeconds); });

            _exporter = new RtfHistoryExporter();
            _commandExecutor = new Esp32CommandExecutor(_mqtt, _devices);
            _scheduler = new ScheduledCommandService(_commandExecutor);
            _voice = new Wp81VoiceCommandService();
            _voiceParser = new VoiceCommandParser(string.Empty, string.Empty);

            _mqtt.MessageReceived += Mqtt_MessageReceived;
            _mqtt.ConnectionStateChanged += Mqtt_ConnectionStateChanged;
            _mqtt.WidgetChanged += Mqtt_WidgetChanged;
            _scheduler.TaskChanged += Scheduler_TaskChanged;
            _voice.CommandRecognized += Voice_CommandRecognized;

            Loaded += MainPage_Loaded;
            Unloaded += MainPage_Unloaded;
        }

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initializing) return;
            _initializing = true;

            try
            {
                _appSettings = await _settingsStore.LoadAsync();
                ApplySettingsToMqtt();
                _commandExecutor.SetScenarioScript(_appSettings.IoTManagerScenario);
                ApplySettingsToUi();
                await _scheduler.InitializeAsync();

                GraphFrom.Date = DateTimeOffset.Now.AddDays(-1);
                GraphTo.Date = DateTimeOffset.Now;

                _autoExportTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
                _autoExportTimer.Tick += AutoExportTimer_Tick;
                _autoExportTimer.Start();

                await CleanupHistoryAsync();
                await RefreshScheduleAsync();
                RefreshGraphWidgets();
                Log("Приложение и локальное хранилище готовы.");
            }
            catch (Exception ex)
            {
                Log("INIT ERROR: " + ex.Message);
            }
            finally
            {
                _initializing = false;
            }
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_autoExportTimer != null)
                _autoExportTimer.Stop();
        }

        private void ShowSection(int index)
        {
            MainPivot.SelectedIndex = index;
            UpdateNavigationState();
        }

        private void MainPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateNavigationState();
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = new MenuFlyout();

            var home = new MenuFlyoutItem { Text = "Главная" };
            home.Click += delegate { ShowSection(0); };
            menu.Items.Add(home);

            var graphs = new MenuFlyoutItem { Text = "Графики и история" };
            graphs.Click += delegate { ShowSection(1); };
            menu.Items.Add(graphs);

            var tasks = new MenuFlyoutItem { Text = "Запланированные команды" };
            tasks.Click += delegate { ShowSection(2); };
            menu.Items.Add(tasks);

            var settings = new MenuFlyoutItem { Text = "Настройки MQTT и приложения" };
            settings.Click += delegate { ShowSection(3); };
            menu.Items.Add(settings);

            menu.ShowAt(MenuButton);
        }

        private async void TopInfoButton_Click(object sender, RoutedEventArgs e)
        {
            var state = ConnectionText == null ? "Состояние MQTT неизвестно." : ConnectionText.Text;
            var dialog = new Windows.UI.Popups.MessageDialog(
                state + Environment.NewLine +
                "Параметры MQTT, сценарий IoTManager и журнал доступны в разделе «Настройки».",
                "ESP32 AI Control");
            await dialog.ShowAsync();
        }

        private void UpdateNavigationState()
        {
            if (PageSubtitle == null || MainPivot == null) return;

            if (MainPivot.SelectedIndex == 0)
                PageSubtitle.Text = "Dashboard";
            else if (MainPivot.SelectedIndex == 1)
                PageSubtitle.Text = "Графики";
            else if (MainPivot.SelectedIndex == 2)
                PageSubtitle.Text = "Задачи";
            else
                PageSubtitle.Text = "Настройки";

            SetNavigationButton(NavHomeButton, MainPivot.SelectedIndex == 0);
            SetNavigationButton(NavMarfaButton, false);
            SetNavigationButton(NavGraphsButton, MainPivot.SelectedIndex == 1);
            SetNavigationButton(NavTasksButton, MainPivot.SelectedIndex == 2);
            SetNavigationButton(NavSettingsButton, MainPivot.SelectedIndex == 3);
        }

        private void SetNavigationButton(Button button, bool isSelected)
        {
            if (button == null) return;
            button.Background = new SolidColorBrush(isSelected
                ? Windows.UI.Color.FromArgb(255, 0, 164, 239)
                : Windows.UI.Color.FromArgb(255, 37, 40, 50));
            button.Foreground = new SolidColorBrush(isSelected
                ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
                : Windows.UI.Color.FromArgb(255, 190, 194, 203));
            button.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        }

        private void NavigateHome_Click(object sender, RoutedEventArgs e) { ShowSection(0); }
        private void NavigateMarfa_Click(object sender, RoutedEventArgs e) { VoiceButton_Click(sender, e); }
        private void NavigateGraphs_Click(object sender, RoutedEventArgs e) { ShowSection(1); }
        private void NavigateTasks_Click(object sender, RoutedEventArgs e) { ShowSection(2); }
        private void NavigateSettings_Click(object sender, RoutedEventArgs e) { ShowSection(3); }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ReadMqttSettingsFromUi();
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
            try { await _mqtt.DisconnectAsync(); }
            catch (Exception ex) { Log("ERROR: " + ex.Message); }
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

        private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ReadMqttSettingsFromUi();

                int interval;
                if (!int.TryParse(MeasurementIntervalBox.Text, out interval))
                    throw new InvalidOperationException("Интервал записи должен быть числом.");
                int retention;
                if (!int.TryParse(RetentionDaysBox.Text, out retention))
                    throw new InvalidOperationException("Период хранения должен быть числом.");

                _appSettings.Host = _settings.Host;
                _appSettings.Port = _settings.Port;
                _appSettings.Prefix = _settings.Prefix;
                _appSettings.UserName = _settings.UserName;
                _appSettings.Password = _settings.Password;
                _appSettings.ClientId = _settings.ClientId;
                _appSettings.MeasurementIntervalSeconds = Math.Max(1, interval);
                _appSettings.RetentionDays = Math.Max(1, retention);
                _appSettings.AutoExportPeriod = GetAutoExportTag();
                _appSettings.VoiceOpenCommand = VoiceOpenCommandBox.Text.Trim();
                _appSettings.VoiceCloseCommand = VoiceCloseCommandBox.Text.Trim();
                _appSettings.IoTManagerScenario = ScenarioTextBox.Text ?? string.Empty;

                _voiceParser = new VoiceCommandParser(
                    _appSettings.VoiceOpenCommand,
                    _appSettings.VoiceCloseCommand);
                _commandExecutor.SetScenarioScript(_appSettings.IoTManagerScenario);

                await _settingsStore.SaveAsync(_appSettings);
                await CleanupHistoryAsync();

                SettingsStatus.Text = "Настройки сохранены.";
                Log("Настройки сохранены.");
            }
            catch (Exception ex)
            {
                SettingsStatus.Text = "Ошибка: " + ex.Message;
                Log("SETTINGS ERROR: " + ex.Message);
            }
        }

        private void ApplySettingsToMqtt()
        {
            _settings.Host = _appSettings.Host;
            _settings.Port = _appSettings.Port;
            _settings.Prefix = _appSettings.Prefix;
            _settings.UserName = _appSettings.UserName;
            _settings.Password = _appSettings.Password;
            _settings.ClientId = string.IsNullOrEmpty(_appSettings.ClientId)
                ? "ESP32-AI-Control-WP81" : _appSettings.ClientId;

            _voiceParser = new VoiceCommandParser(
                _appSettings.VoiceOpenCommand,
                _appSettings.VoiceCloseCommand);
        }

        private void ApplySettingsToUi()
        {
            HostBox.Text = _appSettings.Host;
            PortBox.Text = _appSettings.Port;
            PrefixBox.Text = _appSettings.Prefix;
            UserNameBox.Text = _appSettings.UserName;
            PasswordBox.Password = _appSettings.Password;
            MeasurementIntervalBox.Text = _appSettings.MeasurementIntervalSeconds.ToString(CultureInfo.InvariantCulture);
            RetentionDaysBox.Text = _appSettings.RetentionDays.ToString(CultureInfo.InvariantCulture);
            VoiceOpenCommandBox.Text = _appSettings.VoiceOpenCommand;
            VoiceCloseCommandBox.Text = _appSettings.VoiceCloseCommand;
            ScenarioTextBox.Text = _appSettings.IoTManagerScenario;

            foreach (var item in AutoExportBox.Items)
            {
                var combo = item as ComboBoxItem;
                if (combo != null && string.Equals((string)combo.Tag, _appSettings.AutoExportPeriod, StringComparison.OrdinalIgnoreCase))
                {
                    AutoExportBox.SelectedItem = combo;
                    break;
                }
            }
        }

        private void ReadMqttSettingsFromUi()
        {
            _settings.Host = HostBox.Text.Trim();
            _settings.Port = PortBox.Text.Trim();
            _settings.Prefix = PrefixBox.Text.Trim().Trim('/');
            _settings.UserName = UserNameBox.Text.Trim();
            _settings.Password = PasswordBox.Password;
        }

        private async void ImportScenarioButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.ViewMode = PickerViewMode.List;
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.FileTypeFilter.Add(".json");
                picker.FileTypeFilter.Add(".txt");

                var file = await picker.PickSingleFileAsync();
                if (file == null)
                    return;

                var scenario = await Windows.Storage.FileIO.ReadTextAsync(file);
                ScenarioTextBox.Text = scenario;
                _appSettings.IoTManagerScenario = scenario;
                _commandExecutor.SetScenarioScript(scenario);
                await _settingsStore.SaveAsync(_appSettings);

                SettingsStatus.Text = "Сценарий IoTManager загружен.";
                Log("SCENARIO IMPORT: " + file.Name);
            }
            catch (Exception ex)
            {
                SettingsStatus.Text = "Ошибка импорта сценария: " + ex.Message;
                Log("SCENARIO IMPORT ERROR: " + ex.Message);
            }
        }

        private void TestScenarioButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _commandExecutor.SetScenarioScript(ScenarioTextBox.Text ?? string.Empty);

                Log("SCENARIO READ: разобрано правил = " +
                    _commandExecutor.GetScenarioRuleCount().ToString(CultureInfo.InvariantCulture));

                var open = VoiceOpenCommandBox.Text.Trim();
                var close = VoiceCloseCommandBox.Text.Trim();

                if (string.IsNullOrEmpty(open) && string.IsNullOrEmpty(close))
                    throw new InvalidOperationException("Заполните команды открытия/закрытия.");

                if (!string.IsNullOrEmpty(open))
                    LogScenarioPlan("OPEN", open);

                if (!string.IsNullOrEmpty(close))
                    LogScenarioPlan("CLOSE", close);

                SettingsStatus.Text = "План проверен. См. диагностику MQTT.";
            }
            catch (Exception ex)
            {
                SettingsStatus.Text = "Ошибка проверки: " + ex.Message;
                Log("SCENARIO TEST ERROR: " + ex.Message);
            }
        }

        private void LogScenarioPlan(string name, string command)
        {
            Log("SCENARIO TEST " + name + ": " + command);

            var plan = _commandExecutor.BuildPlan(command);

            for (var i = 0; i < plan.Actions.Count; i++)
            {
                var action = plan.Actions[i];
                Log("PLAN " + (action.IsDependency ? "DEP " : "ACT ") + action.Command +
                    (string.IsNullOrEmpty(action.Reason) ? string.Empty : " | " + action.Reason));
            }
        }

        private string GetAutoExportTag()
        {
            var item = AutoExportBox.SelectedItem as ComboBoxItem;
            return item == null ? "Never" : (string)item.Tag;
        }

        private async void Mqtt_MessageReceived(object sender, Esp32Message e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
            {
                Log("RX [" + e.Kind + "] " + e.Topic + " = " + e.Value);
            });
        }

        private async void Mqtt_WidgetChanged(object sender, WidgetState e)
        {
            try
            {
                await _recorder.ObserveAsync(e);
            }
            catch (Exception ex)
            {
                Log("HISTORY ERROR: " + ex.Message);
            }

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
            {
                UpdateWidgetVisual(e);
                UpdateGraphWidget(e);
                Log("WIDGET " + e.DeviceId + "/" + e.Id + " = " + e.Value);
            });
        }

        private async void Mqtt_ConnectionStateChanged(object sender, MqttConnectionStateChangedEventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
            {
                ConnectionText.Text = "MQTT: " + e.State;
                if (TopConnectionDot != null)
                    TopConnectionDot.Foreground = new SolidColorBrush(
                        e.State == MqttConnectionState.Connected
                            ? Windows.UI.Color.FromArgb(255, 76, 175, 80)
                            : Windows.UI.Color.FromArgb(255, 244, 67, 54));
            });
        }

        private WidgetVisual CreateWidgetVisual(WidgetState state)
        {
            var pageName = NormalizePage(state.Page);
            var visual = new WidgetVisual
            {
                State = state,
                PageName = pageName
            };

            // Match Android's compact dashboard rows: 16dp horizontal spacing,
            // small vertical rhythm, and no oversized fixed-width controls.
            visual.Container = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 2),
                Tag = state.Key
            };

            var type = NormalizeType(state.WidgetType);
            var isSwitch = type == "switch" || type == "toggle" ||
                           type == "checkbox" || type == "vbutton";
            var isAction = type == "button" || type == "action" || type == "buttonout";
            var isSlider = type == "slider" || type == "range" || type == "inputdgt";

            var shell = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 28, 29, 36)),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 9, 14, 9),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 49, 58))
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

            var icon = new TextBlock
            {
                Text = isSwitch ? "◉" : (isAction ? "●" : "◆"),
                FontSize = 21,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 101, 232)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            visual.Title = new TextBlock
            {
                Text = GetTitle(state),
                FontSize = 16,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 243, 244, 246)),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 6, 0)
            };
            Grid.SetColumn(visual.Title, 1);
            row.Children.Add(visual.Title);

            visual.ValueText = new TextBlock
            {
                Text = string.IsNullOrEmpty(state.Value) ? "—" : state.Value,
                FontSize = 18,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 98, 168, 255)),
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            if (isSwitch)
            {
                var toggle = new ToggleSwitch
                {
                    OnContent = "Вкл.",
                    OffContent = "Выкл.",
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 209, 213, 219)),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    MinWidth = 82
                };
                toggle.Toggled += delegate { PublishSwitchValue(visual, toggle.IsOn); };
                visual.Control = toggle;
                Grid.SetColumn(toggle, 2);
                row.Children.Add(toggle);
                visual.ValueText.Visibility = Visibility.Collapsed;
            }
            else if (isAction)
            {
                var button = new Button
                {
                    Content = "Нажать",
                    MinWidth = 76,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 101, 232)),
                    Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 101, 232)),
                    Padding = new Thickness(8, 5, 8, 5),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0)
                };
                button.Click += async delegate { await PublishControl(visual, "1"); };
                visual.Control = button;
                Grid.SetColumn(button, 2);
                row.Children.Add(button);
                visual.ValueText.Visibility = Visibility.Collapsed;
            }
            else
            {
                Grid.SetColumn(visual.ValueText, 2);
                row.Children.Add(visual.ValueText);
            }

            shell.Child = row;
            visual.Container.Children.Add(shell);

            if (isSlider)
            {
                var slider = new Slider
                {
                    Minimum = 0,
                    Maximum = 100,
                    StepFrequency = 1,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(34, 2, 8, 0)
                };
                slider.ValueChanged += async delegate(object s, RangeBaseValueChangedEventArgs ev)
                {
                    if (_syncingControls) return;
                    await PublishControl(visual, ev.NewValue.ToString(CultureInfo.InvariantCulture));
                };
                visual.Control = slider;
                visual.Container.Children.Add(slider);
            }

            return visual;
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
                _widgetIndex[state.Key] = state;
                InsertWidgetVisual(visual);
            }
            else
            {
                var newPage = NormalizePage(state.Page);
                if (!string.Equals(visual.PageName, newPage, StringComparison.Ordinal))
                    MoveWidgetVisual(visual, newPage);

                visual.State = state;
                visual.Title.Text = GetTitle(state);
                _widgetIndex[state.Key] = state;
            }

            _syncingControls = true;
            try { ApplyValueToVisual(visual, state.Value); }
            finally { _syncingControls = false; }
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
                if (existing == null) continue;

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
            InsertWidgetIntoPage(GetOrCreatePageVisual(newPageName), visual);
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
                Content = new StackPanel { Margin = new Thickness(0, 0, 0, 8) }
            };

            var item = new PivotItem
            {
                Header = pageName,
                Content = new ScrollViewer
                {
                    Content = page.Content,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                },
                Tag = pageName
            };
            page.Item = item;
            _pages[pageName] = page;
            WidgetPages.Items.Add(item);
            return page;
        }

        private void CleanupEmptyPages()
        {
            var empty = new List<string>();
            foreach (var pair in _pages)
                if (pair.Value.Content.Children.Count == 0) empty.Add(pair.Key);

            foreach (var name in empty)
            {
                var page = _pages[name];
                WidgetPages.Items.Remove(page.Item);
                _pages.Remove(name);
            }
        }

        private string GetTitle(WidgetState state)
        {
            if (!string.IsNullOrWhiteSpace(state.Description)) return state.Description;
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

        private void ApplyValueToVisual(WidgetVisual visual, string value)
        {
            if (visual.ValueText != null)
                visual.ValueText.Text = string.IsNullOrEmpty(value) ? "—" : value;

            var toggle = visual.Control as ToggleSwitch;
            if (toggle != null)
            {
                toggle.IsOn = ParseBoolean(value);
                return;
            }

            var slider = visual.Control as Slider;
            if (slider != null)
            {
                double number;
                if (double.TryParse((value ?? string.Empty).Replace(',', '.'),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    slider.Value = Math.Max(slider.Minimum, Math.Min(slider.Maximum, number));
            }
        }

        private static bool ParseBoolean(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            var s = value.Trim().ToLowerInvariant();
            return s == "1" || s == "true" || s == "on" || s == "yes" || s == "open";
        }

        private async void PublishSwitchValue(WidgetVisual visual, bool enabled)
        {
            if (_syncingControls) return;
            await PublishControl(visual, enabled ? "1" : "0");
        }

        private async Task PublishControl(WidgetVisual visual, string value)
        {
            try
            {
                if (_mqtt.State != MqttConnectionState.Connected)
                {
                    Log("MQTT не подключён: " + visual.State.Id);
                    return;
                }

                await _mqtt.PublishControlAsync(visual.State.DeviceId, visual.State.Id, value);
                Log("TX control " + visual.State.DeviceId + "/" + visual.State.Id + " = " + value);
            }
            catch (Exception ex)
            {
                Log("ERROR control: " + ex.Message);
            }
        }

        private void UpdateGraphWidget(WidgetState state)
        {
            if (state == null || !_widgetIndex.ContainsKey(state.Key)) return;
            if (!ContainsComboValue(GraphWidgetBox, state.Key))
            {
                GraphWidgetBox.Items.Add(state.Key);
                if (GraphWidgetBox.SelectedIndex < 0)
                    GraphWidgetBox.SelectedIndex = 0;
            }
        }

        private void RefreshGraphWidgets()
        {
            var selected = GraphWidgetBox.SelectedItem as string;
            GraphWidgetBox.Items.Clear();

            foreach (var state in _devices.Widgets.OrderBy(delegate(WidgetState s) { return s.Page; }).ThenBy(delegate(WidgetState s) { return s.Order; }))
            {
                _widgetIndex[state.Key] = state;
                GraphWidgetBox.Items.Add(state.Key);
            }

            if (!string.IsNullOrEmpty(selected) && ContainsComboValue(GraphWidgetBox, selected))
                GraphWidgetBox.SelectedItem = selected;
            else if (GraphWidgetBox.Items.Count > 0)
                GraphWidgetBox.SelectedIndex = 0;
        }

        private static bool ContainsComboValue(ComboBox box, string value)
        {
            for (var i = 0; i < box.Items.Count; i++)
                if (string.Equals(box.Items[i] as string, value, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private async void BuildGraphButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var key = GraphWidgetBox.SelectedItem as string;
                if (string.IsNullOrEmpty(key))
                {
                    GraphSummary.Text = "Нет выбранного датчика.";
                    return;
                }

                var state = _widgetIndex[key];
                var from = new DateTimeOffset(GraphFrom.Date.Date);
                var to = new DateTimeOffset(GraphTo.Date.Date.AddDays(1).AddTicks(-1));

                var points = await _measurementStore.ReadAsync(state.DeviceId, state.Id, from, to);
                _lastGraphPoints.Clear();
                foreach (var p in points) _lastGraphPoints.Add(p);

                DrawGraph(_lastGraphPoints);
                GraphSummary.Text = state.Description + " • " + points.Count.ToString(CultureInfo.InvariantCulture) +
                    " точек, " + from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                    " — " + to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                GraphSummary.Text = "Ошибка: " + ex.Message;
                Log("GRAPH ERROR: " + ex.Message);
            }
        }

        private void DrawGraph(IList<MeasurementPoint> points)
        {
            GraphCanvas.Children.Clear();
            if (points == null || points.Count < 2) return;

            var numeric = new List<Tuple<double, double>>();
            DateTimeOffset minTime = points[0].Timestamp;
            DateTimeOffset maxTime = points[points.Count - 1].Timestamp;
            double minValue = double.MaxValue;
            double maxValue = double.MinValue;

            foreach (var p in points)
            {
                double value;
                if (!double.TryParse((p.Value ?? string.Empty).Replace(',', '.'),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    continue;
                numeric.Add(Tuple.Create((p.Timestamp - minTime).TotalSeconds, value));
                minValue = Math.Min(minValue, value);
                maxValue = Math.Max(maxValue, value);
            }

            if (numeric.Count < 2) return;
            if (Math.Abs(maxValue - minValue) < 0.000001) maxValue = minValue + 1;

            var width = Math.Max(420.0, ActualWidth - 55.0);
            var height = 300.0;
            var padding = 18.0;
            var timeSpan = Math.Max(1.0, (maxTime - minTime).TotalSeconds);

            var axis = new Line
            {
                X1 = padding, Y1 = height - padding,
                X2 = width - padding, Y2 = height - padding,
                Stroke = new SolidColorBrush(Windows.UI.Colors.Gray),
                StrokeThickness = 1
            };
            GraphCanvas.Children.Add(axis);

            for (var i = 1; i < numeric.Count; i++)
            {
                var a = numeric[i - 1];
                var b = numeric[i];
                var x1 = padding + (a.Item1 / timeSpan) * (width - padding * 2);
                var y1 = padding + (1 - (a.Item2 - minValue) / (maxValue - minValue)) * (height - padding * 2);
                var x2 = padding + (b.Item1 / timeSpan) * (width - padding * 2);
                var y2 = padding + (1 - (b.Item2 - minValue) / (maxValue - minValue)) * (height - padding * 2);

                GraphCanvas.Children.Add(new Line
                {
                    X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                    Stroke = new SolidColorBrush(Windows.UI.Colors.DodgerBlue),
                    StrokeThickness = 2
                });
            }

            var high = new TextBlock { Text = maxValue.ToString("0.##", CultureInfo.InvariantCulture) };
            var low = new TextBlock { Text = minValue.ToString("0.##", CultureInfo.InvariantCulture) };
            Canvas.SetLeft(high, 2); Canvas.SetTop(high, 4);
            Canvas.SetLeft(low, 2); Canvas.SetTop(low, height - 30);
            GraphCanvas.Children.Add(high);
            GraphCanvas.Children.Add(low);
        }

        private async void ExportGraphButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var key = GraphWidgetBox.SelectedItem as string;
                if (string.IsNullOrEmpty(key)) return;

                var from = new DateTimeOffset(GraphFrom.Date.Date);
                var to = new DateTimeOffset(GraphTo.Date.Date.AddDays(1).AddTicks(-1));
                var points = await _measurementStore.ReadAsync(
                    _widgetIndex[key].DeviceId, _widgetIndex[key].Id, from, to);

                var name = "ESP32-" + SanitizeFilePart(key) + "-" +
                           from.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" +
                           to.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".doc";
                await _exporter.ExportAsync(points, from, to, name);
                GraphSummary.Text = "Экспортировано: " + name;
            }
            catch (Exception ex)
            {
                GraphSummary.Text = "Ошибка экспорта: " + ex.Message;
                Log("EXPORT ERROR: " + ex.Message);
            }
        }

        private async void ExportTodayButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var to = DateTimeOffset.Now;
                var from = to.Date;
                var points = await _measurementStore.ReadAllAsync(from, to);
                var name = "ESP32-history-" + from.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".doc";
                await _exporter.ExportAsync(points, from, to, name);
                SettingsStatus.Text = "Экспортировано: " + name;
            }
            catch (Exception ex)
            {
                SettingsStatus.Text = "Ошибка экспорта: " + ex.Message;
                Log("EXPORT ERROR: " + ex.Message);
            }
        }

        private async void AutoExportTimer_Tick(object sender, object e)
        {
            if (_autoExportRunning || _appSettings.AutoExportPeriod == "Never")
                return;

            _autoExportRunning = true;
            try
            {
                var now = DateTimeOffset.Now;
                var key = "LastAutoExportUtc";
                var lastText = Windows.Storage.ApplicationData.Current.LocalSettings.Values[key] as string;
                DateTimeOffset last;

                var shouldExport = !DateTimeOffset.TryParse(lastText, out last);
                var span = GetExportSpan(_appSettings.AutoExportPeriod);
                if (!shouldExport) shouldExport = now - last >= span;
                if (!shouldExport) return;

                var from = now.Subtract(span);
                var points = await _measurementStore.ReadAllAsync(from, now);
                var suffix = now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
                var name = "ESP32-auto-" + _appSettings.AutoExportPeriod + "-" + suffix + ".doc";

                await _exporter.ExportAsync(points, from, now, name);
                Windows.Storage.ApplicationData.Current.LocalSettings.Values[key] = now.ToString("o", CultureInfo.InvariantCulture);
                Log("AUTO EXPORT: " + name);
            }
            catch (Exception ex)
            {
                Log("AUTO EXPORT ERROR: " + ex.Message);
            }
            finally
            {
                _autoExportRunning = false;
            }
        }

        private static TimeSpan GetExportSpan(string period)
        {
            switch ((period ?? string.Empty).ToLowerInvariant())
            {
                case "day": return TimeSpan.FromDays(1);
                case "week": return TimeSpan.FromDays(7);
                case "month": return TimeSpan.FromDays(30);
                default: return TimeSpan.MaxValue;
            }
        }

        private async Task CleanupHistoryAsync()
        {
            var cutoff = DateTimeOffset.Now.AddDays(-Math.Max(1, _appSettings.RetentionDays));
            await _measurementStore.DeleteBeforeAsync(cutoff);
        }

        private async void AddScheduleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var command = ScheduleCommandBox.Text.Trim();
                ValidateCommand(command);
                var executeAt = CombineDateAndTime();
                if (executeAt <= DateTimeOffset.Now)
                    throw new InvalidOperationException("Время задачи должно быть в будущем.");

                await _scheduler.ScheduleAsync(command, executeAt);
                await RefreshScheduleAsync();
                Log("SCHEDULE ADD: " + command + " @ " + executeAt);
            }
            catch (Exception ex) { Log("SCHEDULE ERROR: " + ex.Message); }
        }

        private async void UpdateScheduleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selected = SelectedSchedule();
                if (selected == null) return;
                var command = ScheduleCommandBox.Text.Trim();
                ValidateCommand(command);
                var executeAt = CombineDateAndTime();
                if (executeAt <= DateTimeOffset.Now)
                    throw new InvalidOperationException("Время задачи должно быть в будущем.");

                await _scheduler.UpdateAsync(selected.Id, command, executeAt);
                await RefreshScheduleAsync();
                Log("SCHEDULE UPDATE: " + selected.Id);
            }
            catch (Exception ex) { Log("SCHEDULE ERROR: " + ex.Message); }
        }

        private async void CancelScheduleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selected = SelectedSchedule();
                if (selected == null) return;
                await _scheduler.CancelAsync(selected.Id);
                await RefreshScheduleAsync();
            }
            catch (Exception ex) { Log("SCHEDULE ERROR: " + ex.Message); }
        }

        private async void RefreshScheduleButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshScheduleAsync();
        }

        private async Task RefreshScheduleAsync()
        {
            var items = await _scheduler.GetAllAsync();
            _scheduleRows.Clear();
            ScheduleList.Items.Clear();

            foreach (var item in items)
            {
                _scheduleRows.Add(item);
                ScheduleList.Items.Add(
                    item.Status + " | " +
                    item.ExecuteAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") +
                    " | " + item.Command +
                    (string.IsNullOrEmpty(item.LastError) ? string.Empty : " | " + item.LastError));
            }
        }

        private async void Scheduler_TaskChanged(object sender, ScheduledCommand e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async delegate
            {
                await RefreshScheduleAsync();
            });
        }

        private void ScheduleList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var index = ScheduleList.SelectedIndex;
            if (index < 0 || index >= _scheduleRows.Count) return;

            var item = _scheduleRows[index];
            ScheduleCommandBox.Text = item.Command;
            ScheduleDate.Date = new DateTimeOffset(item.ExecuteAt.LocalDateTime.Date);
            ScheduleTime.Time = item.ExecuteAt.LocalDateTime.TimeOfDay;
        }

        private ScheduledCommand SelectedSchedule()
        {
            var index = ScheduleList.SelectedIndex;
            return index >= 0 && index < _scheduleRows.Count ? _scheduleRows[index] : null;
        }

        private DateTimeOffset CombineDateAndTime()
        {
            var date = ScheduleDate.Date.Date;
            var local = date.Add(ScheduleTime.Time);
            return new DateTimeOffset(local);
        }

        private static void ValidateCommand(string command)
        {
            var parts = (command ?? string.Empty).Split(new[] { '=' }, 2);
            if (parts.Length != 2 || parts[0].Trim().IndexOf('/') <= 0)
                throw new InvalidOperationException("Формат: device/widget=value");
        }

        private async void VoiceButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                NavMarfaButton.IsEnabled = false;
                VoiceResultText.Text = "Слушаю…";
                await _voice.StartListeningAsync();
            }
            catch (Exception ex)
            {
                VoiceResultText.Text = "Ошибка голоса: " + ex.Message;
                Log("VOICE ERROR: " + ex.Message);
            }
            finally
            {
                NavMarfaButton.IsEnabled = true;
                await _voice.StopListeningAsync();
            }
        }

        private async void Voice_CommandRecognized(object sender, string text)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async delegate
            {
                VoiceResultText.Text = "Распознано: " + text;
                try
                {
                    var parsed = _voiceParser.Parse(text);
                    if (parsed == null)
                    {
                        Log("VOICE: команда не распознана.");
                        return;
                    }

                    ValidateCommand(parsed.Command);

                    if (parsed.ExecuteAt.HasValue)
                    {
                        await _scheduler.ScheduleAsync(parsed.Command, parsed.ExecuteAt.Value);
                        await RefreshScheduleAsync();
                        Log("VOICE SCHEDULE: " + parsed.Command + " @ " + parsed.ExecuteAt.Value);
                    }
                    else
                    {
                        var plan = _commandExecutor.BuildPlan(parsed.Command);
                        for (var p = 0; p < plan.Actions.Count; p++)
                        {
                            var planned = plan.Actions[p];
                            Log("PLAN " + (planned.IsDependency ? "DEP " : "ACT ") + planned.Command +
                                (string.IsNullOrEmpty(planned.Reason) ? string.Empty : " | " + planned.Reason));
                        }

                        await _commandExecutor.ExecuteAsync(parsed.Command);
                        Log("VOICE EXECUTE: " + parsed.Command);
                    }
                }
                catch (Exception ex)
                {
                    Log("VOICE COMMAND ERROR: " + ex.Message);
                }
            });
        }

        private async void RefreshHistoryByRetention()
        {
            try { await CleanupHistoryAsync(); }
            catch (Exception ex) { Log("RETENTION ERROR: " + ex.Message); }
        }

        private void Log(string text)
        {
            var line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text;
            var old = LogText.Text ?? string.Empty;
            LogText.Text = line + Environment.NewLine + old;

            var lines = LogText.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 250)
            {
                var keep = lines.Take(250).ToArray();
                LogText.Text = string.Join(Environment.NewLine, keep);
            }
        }

        private static string SanitizeFilePart(string value)
        {
            var result = value ?? "widget";
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                result = result.Replace(c, '_');
            return result.Replace("/", "_").Replace("\\", "_");
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