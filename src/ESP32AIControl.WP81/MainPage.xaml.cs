using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.Networking.Connectivity;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Shapes;
using Windows.Media.SpeechRecognition;
using Windows.Storage.Pickers;
using Windows.UI.Notifications;
using Windows.Data.Xml.Dom;
using Windows.Data.Json;
using ESP32AIControl.WP81.Core;
using ESP32AIControl.WP81.Core.Export;
using ESP32AIControl.WP81.Core.History;
using ESP32AIControl.WP81.Core.Mqtt;
using ESP32AIControl.WP81.Core.Network;
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
        private readonly UdpDiscoveryService _udpDiscovery;
        private readonly ConnectionJournal _connectionJournal;
        private readonly DeviceWebSocketFallbackService _webSocketFallback;
        private readonly LocalScenarioStore _localScenarioStore;
        private readonly LocalScenarioEngine _localScenarioEngine;
        private string _editingLocalScenarioId;
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
        private DispatcherTimer _mqttSupervisorTimer;
        private bool _syncingControls;
        private bool _initializing;
        private bool _autoExportRunning;
        private bool _mqttReconnectEnabled;
        private bool _mqttReconnectInProgress;
        private bool _networkStatusSubscribed;
        private int _selectedSection = 0;

        public MainPage()
        {
            InitializeComponent();
            LocalScenarioEnabledCheckBox.IsChecked = true;
            LocalScenarioNotificationCheckBox.IsChecked = true;
            if (DashboardEmptyState != null)
                DashboardEmptyState.Visibility = WidgetPages.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateNavigationState();

            _settingsStore = new AppSettingsStore();
            _appSettings = new AppSettings();

            _settings = new MqttSettings();
            _devices = new DeviceRepository();
            _mqtt = new Esp32MqttService(_settings, _devices);
            _localScenarioStore = new LocalScenarioStore();
            _localScenarioEngine = new LocalScenarioEngine(_localScenarioStore, LocalScenario_Triggered);
            _connectionJournal = new ConnectionJournal();
            _udpDiscovery = new UdpDiscoveryService();
            _webSocketFallback = new DeviceWebSocketFallbackService();
            _webSocketFallback.Diagnostic += WebSocketFallback_Diagnostic;
            _webSocketFallback.WidgetUpdated += WebSocketFallback_WidgetUpdated;
            _mqtt.SetWebSocketFallbackService(_webSocketFallback);
            _udpDiscovery.DeviceDiscovered += UdpDiscovery_DeviceDiscovered;
            _udpDiscovery.Diagnostic += UdpDiscovery_Diagnostic;
            _udpDiscovery.DeviceExpired += UdpDiscovery_DeviceExpired;

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
            _voice.RecognitionFailed += Voice_RecognitionFailed;

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
                LogText.Text = string.Join(Environment.NewLine, _connectionJournal.Load());
                if (_appSettings.SavedMqttProfiles == null)
                    _appSettings.SavedMqttProfiles = new List<SavedMqttProfile>();
                ApplyDefaultMqttProfileToSettings();
                ApplySettingsToMqtt();
                _localScenarioEngine.Reload();
                RefreshLocalScenariosList();
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
                Log("Настройки: источник " + (_settingsStore.LastLoadSource ?? "неизвестен") +
                    "; резерв MQTT: " + (_settingsStore.LastRoamingSaveStatus ?? "не проверено"));

                // Android starts its MQTT foreground service automatically.
                // On WP8.1 keep reconnecting while the app is active and reconnect on resume.
                _mqttReconnectEnabled = true;
                SubscribeNetworkStatusChanged();
                try { await _udpDiscovery.StartAsync(); }
                catch (Exception udpError) { Log("UDP discovery start: " + udpError.Message); }
                _webSocketFallback.SetMqttOnline(_mqtt.State != MqttConnectionState.Disconnected);
                foreach (var discovered in _udpDiscovery.GetSnapshot())
                    _webSocketFallback.UpdateDevice(discovered);
                StartMqttSupervisor();
            }
            catch (Exception ex)
            {
                Log("INIT ERROR: " + ex.Message);
            }
            finally
            {
                _initializing = false;
            }

            // Connect only after settings and scheduled tasks have finished loading.
            await EnsureMqttConnectedAsync("запуск приложения");
        }

        private async void UdpDiscovery_DeviceDiscovered(object sender, UdpDiscoveredDevice device)
        {
            if (device == null) return;
            _webSocketFallback.UpdateDevice(device);
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
            {
                Log("UDP ESP32: " + (string.IsNullOrWhiteSpace(device.Name) ? device.Id : device.Name) +
                    " · " + device.Ip + " · " + device.Status +
                    (string.IsNullOrWhiteSpace(device.FirmwareVersion) ? string.Empty : " · FW " + device.FirmwareVersion));
            });
        }

        private async void WebSocketFallback_Diagnostic(object sender, string message)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate { Log(message); });
        }

        private void WebSocketFallback_WidgetUpdated(object sender, WebSocketWidgetUpdateEventArgs e)
        {
            if (e == null) return;
            var message = new Esp32Message
            {
                Kind = Esp32MessageKind.Status,
                DeviceId = e.DeviceId,
                WidgetId = e.WidgetId,
                Value = e.Value,
                Retained = e.IsSnapshot
            };
            _devices.Apply(message);
            _localScenarioEngine.OnMessage(message, !e.IsSnapshot);
        }

        private async void LocalScenario_Triggered(LocalScenarioDefinition scenario, string rawValue)
        {
            if (scenario == null) return;

            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
            {
                Log("АВТОСЦЕНАРИЙ: " + scenario.Title + " · значение " + rawValue);
            });

            foreach (var action in scenario.Actions)
            {
                try
                {
                    await _commandExecutor.ExecuteLocalActionAsync(
                        action.DeviceId, action.WidgetId, action.Value);
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
                    {
                        Log("СЦЕНАРИЙ CONFIRMED " + action.DeviceId + "/" + action.WidgetId + "=" + action.Value);
                    });
                }
                catch (Exception ex)
                {
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
                    {
                        Log("СЦЕНАРИЙ TX ERROR: " + ex.Message);
                    });
                }
            }

            if (scenario.NotificationEnabled)
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
                {
                    try
                    {
                        var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                        var texts = xml.GetElementsByTagName("text");
                        texts.Item(0).AppendChild(xml.CreateTextNode(scenario.Title ?? "Сценарий"));
                        texts.Item(1).AppendChild(xml.CreateTextNode((scenario.Message ?? "Условие выполнено")
                            .Replace("{value}", rawValue ?? string.Empty)));
                        ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(xml));
                    }
                    catch (Exception ex)
                    {
                        Log("Уведомление сценария: " + ex.Message);
                    }
                });
            }
        }

        private void RefreshLocalScenariosList()
        {
            if (LocalScenariosList == null) return;
            LocalScenariosList.Items.Clear();
            var scenarios = _localScenarioStore.Load();
            foreach (var scenario in scenarios)
            {
                var conditionText = string.Join(" · ", scenario.Conditions.Select((condition, index) =>
                    (index == 0 ? string.Empty : condition.Connector + " ") +
                    condition.WidgetId + " " + condition.Operator + " " +
                    condition.Threshold.ToString(CultureInfo.InvariantCulture)));
                var item = new ListViewItem
                {
                    Content = (scenario.Enabled ? "● " : "○ ") + scenario.Title +
                        Environment.NewLine + conditionText,
                    Tag = scenario.Id
                };
                LocalScenariosList.Items.Add(item);
            }
            LocalScenariosStatus.Text = "Сценариев: " + scenarios.Count;
        }

        private async void LocalScenarioSaveButton_Click(object sender, RoutedEventArgs e)
        {
            var title = (LocalScenarioTitleBox.Text ?? string.Empty).Trim();
            if (title.Length == 0) title = "Сценарий";

            var scenario = new LocalScenarioDefinition
            {
                Id = string.IsNullOrWhiteSpace(_editingLocalScenarioId)
                    ? Guid.NewGuid().ToString("N")
                    : _editingLocalScenarioId,
                Title = title,
                Message = string.IsNullOrWhiteSpace(LocalScenarioMessageBox.Text)
                    ? "Условие выполнено"
                    : LocalScenarioMessageBox.Text.Trim(),
                Enabled = LocalScenarioEnabledCheckBox.IsChecked == true,
                NotificationEnabled = LocalScenarioNotificationCheckBox.IsChecked == true
            };

            var conditionLines = (LocalScenarioConditionsBox.Text ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < conditionLines.Length; i++)
            {
                var tokens = conditionLines[i].Trim().Split(
                    new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) continue;

                var connector = "AND";
                if (string.Equals(tokens[0], "AND", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tokens[0], "OR", StringComparison.OrdinalIgnoreCase))
                {
                    connector = tokens[0].ToUpperInvariant();
                    tokens = tokens.Skip(1).ToArray();
                }

                if (tokens.Length != 3)
                {
                    await ShowLocalScenarioErrorAsync("Строка условия " + (i + 1) +
                        ": ожидается widgetId оператор значение.");
                    return;
                }

                double threshold;
                if (!double.TryParse(tokens[2].Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out threshold) ||
                    double.IsNaN(threshold) || double.IsInfinity(threshold))
                {
                    await ShowLocalScenarioErrorAsync("Неверный порог в строке " + (i + 1) + ".");
                    return;
                }

                var op = tokens[1];
                if (op != ">" && op != ">=" && op != "<" && op != "<=" &&
                    op != "=" && op != "==" && op != "!=")
                {
                    await ShowLocalScenarioErrorAsync("Неизвестный оператор в строке " + (i + 1) + ".");
                    return;
                }

                scenario.Conditions.Add(new LocalScenarioCondition
                {
                    WidgetId = tokens[0],
                    Operator = op,
                    Threshold = threshold,
                    Connector = scenario.Conditions.Count == 0 ? "AND" : connector
                });
            }

            if (scenario.Conditions.Count == 0)
            {
                await ShowLocalScenarioErrorAsync("Добавьте хотя бы одно условие.");
                return;
            }

            var actionLines = (LocalScenarioActionsBox.Text ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < actionLines.Length; i++)
            {
                var parts = actionLines[i].Trim().Split(new[] { '=' }, 2);
                if (parts.Length != 2)
                {
                    await ShowLocalScenarioErrorAsync("Строка действия " + (i + 1) +
                        ": ожидается deviceId/widgetId=value.");
                    return;
                }
                var target = parts[0].Trim().Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (target.Length != 2 || target[0].Trim().Length == 0 || target[1].Trim().Length == 0)
                {
                    await ShowLocalScenarioErrorAsync("Неверная цель в строке действия " + (i + 1) + ".");
                    return;
                }
                scenario.Actions.Add(new LocalScenarioAction
                {
                    DeviceId = target[0].Trim(),
                    WidgetId = target[1].Trim(),
                    Value = parts[1].Trim()
                });
            }

            var items = _localScenarioStore.Load().ToList();
            var existingIndex = items.FindIndex(x => x.Id == scenario.Id);
            if (existingIndex >= 0) items[existingIndex] = scenario;
            else items.Add(scenario);

            _localScenarioStore.Save(items);
            _localScenarioEngine.ResetScenario(scenario.Id);
            _localScenarioEngine.Reload();
            RefreshLocalScenariosList();
            ClearLocalScenarioEditor();
            LocalScenariosStatus.Text = "Сценарий сохранён. Срабатывание будет после нового перехода условий в TRUE.";
        }

        private async System.Threading.Tasks.Task ShowLocalScenarioErrorAsync(string message)
        {
            await new Windows.UI.Popups.MessageDialog(message, "Автосценарий").ShowAsync();
        }

        private void LocalScenariosList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = LocalScenariosList.SelectedItem as ListViewItem;
            if (selected == null)
            {
                _editingLocalScenarioId = null;
                return;
            }

            var id = selected.Tag as string;
            var scenario = _localScenarioStore.Load().FirstOrDefault(x => x.Id == id);
            if (scenario == null) return;

            _editingLocalScenarioId = scenario.Id;
            LocalScenarioTitleBox.Text = scenario.Title;
            LocalScenarioMessageBox.Text = scenario.Message;
            LocalScenarioEnabledCheckBox.IsChecked = scenario.Enabled;
            LocalScenarioNotificationCheckBox.IsChecked = scenario.NotificationEnabled;
            LocalScenarioConditionsBox.Text = string.Join(Environment.NewLine,
                scenario.Conditions.Select((condition, index) =>
                    (index == 0 ? string.Empty : condition.Connector + " ") +
                    condition.WidgetId + " " + condition.Operator + " " +
                    condition.Threshold.ToString(CultureInfo.InvariantCulture)));
            LocalScenarioActionsBox.Text = string.Join(Environment.NewLine,
                scenario.Actions.Select(action =>
                    action.DeviceId + "/" + action.WidgetId + "=" + action.Value));
            LocalScenarioSaveButton.Content = "Сохранить изменения";
        }

        private void LocalScenarioClearButton_Click(object sender, RoutedEventArgs e)
        {
            ClearLocalScenarioEditor();
        }

        private void ClearLocalScenarioEditor()
        {
            _editingLocalScenarioId = null;
            LocalScenarioTitleBox.Text = string.Empty;
            LocalScenarioConditionsBox.Text = string.Empty;
            LocalScenarioActionsBox.Text = string.Empty;
            LocalScenarioMessageBox.Text = "Условие выполнено: {value}";
            LocalScenarioEnabledCheckBox.IsChecked = true;
            LocalScenarioNotificationCheckBox.IsChecked = true;
            LocalScenarioSaveButton.Content = "Добавить сценарий";
            LocalScenariosList.SelectedIndex = -1;
        }

        private void LocalScenarioToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = LocalScenariosList.SelectedItem as ListViewItem;
            if (selected == null) return;
            var id = selected.Tag as string;
            var items = _localScenarioStore.Load().ToList();
            var scenario = items.FirstOrDefault(x => x.Id == id);
            if (scenario == null) return;
            scenario.Enabled = !scenario.Enabled;
            _localScenarioStore.Save(items);
            _localScenarioEngine.ResetScenario(id);
            _localScenarioEngine.Reload();
            RefreshLocalScenariosList();
            LocalScenariosStatus.Text = scenario.Title + (scenario.Enabled ? ": включён" : ": выключен");
        }

        private void LocalScenarioDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = LocalScenariosList.SelectedItem as ListViewItem;
            if (selected == null) return;
            var id = selected.Tag as string;
            var items = _localScenarioStore.Load().Where(x => x.Id != id).ToList();
            _localScenarioStore.Save(items);
            _localScenarioStore.DeleteRuntimeState(id);
            _localScenarioEngine.ResetScenario(id);
            _localScenarioEngine.Reload();
            RefreshLocalScenariosList();
            ClearLocalScenarioEditor();
            LocalScenariosStatus.Text = "Сценарий удалён.";
        }

        private async void UdpDiscovery_DeviceExpired(object sender, UdpDiscoveredDevice device)
        {
            if (device == null) return;
            _webSocketFallback.RemoveDevice(device.Id, device.Ip);
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
            {
                Log("UDP ESP32: объявление устарело, устройство удалено · " + device.Ip);
            });
        }

        private async void UdpDiscovery_Diagnostic(object sender, string message)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate { Log(message); });
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_autoExportTimer != null)
                _autoExportTimer.Stop();
            UnsubscribeNetworkStatusChanged();
            _udpDiscovery.Stop();
            _webSocketFallback.SetMqttOnline(true);
        }

        private void SubscribeNetworkStatusChanged()
        {
            if (_networkStatusSubscribed)
                return;

            NetworkInformation.NetworkStatusChanged += NetworkInformation_NetworkStatusChanged;
            _networkStatusSubscribed = true;
        }

        private void UnsubscribeNetworkStatusChanged()
        {
            if (!_networkStatusSubscribed)
                return;

            NetworkInformation.NetworkStatusChanged -= NetworkInformation_NetworkStatusChanged;
            _networkStatusSubscribed = false;
        }

        private async void NetworkInformation_NetworkStatusChanged(object sender)
        {
            try
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async delegate
                {
                    if (_initializing || !_mqttReconnectEnabled)
                        return;

                    var profile = NetworkInformation.GetInternetConnectionProfile();
                    if (profile == null)
                    {
                        // No Internet profile does not necessarily mean that
                        // the local Wi-Fi/LAN is down. Keep UDP discovery and
                        // WebSocket fallback alive for ESP32 devices on the LAN.
                        _udpDiscovery.Stop();
                        try { await _udpDiscovery.StartAsync(); }
                        catch (Exception udpError) { Log("UDP local-network restart: " + udpError.Message); }
                        await _mqtt.DisconnectAsync();
                        Log("Интернет недоступен. MQTT отключён; локальный UDP/WebSocket поиск продолжает работу.");
                        return;
                    }

                    // A socket may remain half-open after Wi-Fi changes. Force
                    // a clean session so the regular supervisor can reconnect
                    // using the current network route.
                    _udpDiscovery.Stop();
                    try { await _udpDiscovery.StartAsync(); }
                    catch (Exception udpError) { Log("UDP discovery restart: " + udpError.Message); }
                    await _mqtt.DisconnectAsync();
                    await EnsureMqttConnectedAsync("изменение сетевого подключения");
                });
            }
            catch (Exception ex)
            {
                try
                {
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
                    {
                        Log("Network change: " + ex.Message);
                    });
                }
                catch { }
            }
        }

        private void StartMqttSupervisor()
        {
            if (_mqttSupervisorTimer != null)
                return;

            _mqttSupervisorTimer = new DispatcherTimer();
            _mqttSupervisorTimer.Interval = TimeSpan.FromSeconds(15);
            _mqttSupervisorTimer.Tick += MqttSupervisorTimer_Tick;
            _mqttSupervisorTimer.Start();
        }

        private async void MqttSupervisorTimer_Tick(object sender, object e)
        {
            _udpDiscovery.PruneExpired();
            await EnsureMqttConnectedAsync("проверка соединения");
        }

        public async void OnApplicationResuming()
        {
            if (_appSettings == null)
                return;

            _mqttReconnectEnabled = true;
            _udpDiscovery.Stop();
            try { await _udpDiscovery.StartAsync(); }
            catch (Exception ex) { Log("UDP resume: " + ex.Message); }

            // A suspended WP8.1 socket can look connected while its route is
            // already dead. Recreate MQTT instead of trusting the old state.
            if (_mqtt.State != MqttConnectionState.Disconnected)
                await _mqtt.DisconnectAsync();
            await EnsureMqttConnectedAsync("возврат приложения из фона");
        }

        private async Task EnsureMqttConnectedAsync(string reason)
        {
            if (!_mqttReconnectEnabled || _initializing || _mqttReconnectInProgress)
                return;

            if (_mqtt.State != MqttConnectionState.Disconnected)
                return;

            _mqttReconnectInProgress = true;
            try
            {
                Log("MQTT: восстанавливаю соединение (" + reason + ")…");
                await _mqtt.ConnectAsync();
                Log("MQTT: соединение восстановлено, подписка активна.");
            }
            catch (Exception ex)
            {
                Log("MQTT reconnect: " + ex.Message);
            }
            finally
            {
                _mqttReconnectInProgress = false;
            }
        }

        private void ShowSection(int index)
        {
            _selectedSection = Math.Max(0, Math.Min(3, index));
            UpdateNavigationState();
        }

        private Popup _navigationPopup;

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (_navigationPopup != null && _navigationPopup.IsOpen)
            {
                _navigationPopup.IsOpen = false;
                return;
            }

            var items = new StackPanel { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 36, 37, 42)) };
            AddPopupMenuHeader(items, "ОБСЛУЖИВАНИЕ");
            AddPopupSettingsItem(items, "Сценарии ESP32 / IoTManager", 4);
            AddPopupSettingsItem(items, "Автосценарии", 5);
            AddPopupSettingsItem(items, "Журнал", 6);
            AddPopupMenuItem(items, "UDP устройства", ShowUdpDevicesDialog);

            AddPopupMenuSeparator(items);
            AddPopupMenuHeader(items, "НАСТРОЙКИ");
            AddPopupSettingsItem(items, "MQTT подключение", 0);
            AddPopupSettingsItem(items, "Хранение истории", 1);
            AddPopupSettingsItem(items, "Сохранённые профили", 2);
            AddPopupSettingsItem(items, "Голос и команды", 3);

            var scroll = new ScrollViewer
            {
                Content = items,
                MaxHeight = Math.Max(260, ActualHeight - 105),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var frame = new Border
            {
                Width = Math.Min(340, Math.Max(260, ActualWidth - 24)),
                MaxHeight = Math.Max(270, ActualHeight - 95),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 36, 37, 42)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 67, 77)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Child = scroll
            };

            _navigationPopup = new Popup
            {
                Child = frame,
                HorizontalOffset = 4,
                VerticalOffset = 78,
                IsOpen = true
            };
        }

        private void AddPopupMenuHeader(Panel panel, string title)
        {
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 11,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                Margin = new Thickness(14, 10, 10, 4)
            });
        }

        private void AddPopupMenuSeparator(Panel panel)
        {
            panel.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 70, 70, 70)),
                Margin = new Thickness(8, 5, 8, 5)
            });
        }

        private void AddPopupMenuItem(Panel panel, string title, Action action)
        {
            var item = new Button
            {
                Content = title,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 244, 244, 244)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(14, 10, 12, 10),
                MinHeight = 46
            };
            item.Click += delegate
            {
                if (_navigationPopup != null) _navigationPopup.IsOpen = false;
                action();
            };
            panel.Children.Add(item);
        }

        private void AddPopupSettingsItem(Panel panel, string title, int pivotIndex)
        {
            AddPopupMenuItem(panel, title, delegate
            {
                ShowSection(3);
                if (SettingsPivot != null)
                    SettingsPivot.SelectedIndex = pivotIndex;
            });
        }

        private async void ShowUdpDevicesDialog()
        {
            var devices = _udpDiscovery.GetSnapshot();
            var message = devices.Count == 0
                ? "Пока не найдено ни одного UDP-устройства."
                : string.Join(Environment.NewLine, devices.Select(device =>
                    (string.IsNullOrWhiteSpace(device.Name) ? device.Id : device.Name) +
                    " — " + device.Ip + " (обновлено " + device.LastSeenAt.ToString("HH:mm:ss") + ")"));
            await new Windows.UI.Popups.MessageDialog(message, "UDP устройства").ShowAsync();
        }

        private async void TopInfoButton_Click(object sender, RoutedEventArgs e)
        {
            var state = ConnectionText == null ? "Состояние MQTT неизвестно." : ConnectionText.Text;
            var dialog = new Windows.UI.Popups.MessageDialog(
                state + Environment.NewLine +
                "Дополнительные разделы, сценарии, журнал и сетевые настройки доступны через меню ☰.",
                "ESP32 AI Control");
            await dialog.ShowAsync();
        }

        private void UpdateNavigationState()
        {
            if (PageSubtitle == null || MainSections == null) return;

            HomeSection.Visibility = _selectedSection == 0 ? Visibility.Visible : Visibility.Collapsed;
            GraphsSection.Visibility = _selectedSection == 1 ? Visibility.Visible : Visibility.Collapsed;
            TasksSection.Visibility = _selectedSection == 2 ? Visibility.Visible : Visibility.Collapsed;
            SettingsSection.Visibility = _selectedSection == 3 ? Visibility.Visible : Visibility.Collapsed;

            if (_selectedSection == 0)
                PageSubtitle.Text = "Главная";
            else if (_selectedSection == 1)
                PageSubtitle.Text = "История и графики";
            else if (_selectedSection == 2)
                PageSubtitle.Text = "Отложенные";
            else
                PageSubtitle.Text = "Настройки";

            SetNavigationButton(NavHomeButton, _selectedSection == 0);
            SetNavigationButton(NavMarfaButton, false);
            SetNavigationButton(NavCommandsButton,
                _selectedSection == 3 && SettingsPivot != null && SettingsPivot.SelectedIndex == 3);
            SetNavigationButton(NavGraphsButton, _selectedSection == 1);
            SetNavigationButton(NavTasksButton, _selectedSection == 2);
            // Settings stays available from the hamburger menu, not the bottom bar.
        }

        private void SetNavigationButton(Button button, bool isSelected)
        {
            if (button == null) return;

            // Keep the bottom bar visually quiet like Android navigation:
            // the selected destination gets a soft surface and an accent icon,
            // rather than turning the entire navigation tile bright purple.
            button.Background = new SolidColorBrush(isSelected
                ? Windows.UI.Color.FromArgb(255, 74, 68, 88)
                : Windows.UI.Color.FromArgb(255, 36, 37, 45));
            button.Foreground = new SolidColorBrush(isSelected
                ? Windows.UI.Color.FromArgb(255, 244, 244, 244)
                : Windows.UI.Color.FromArgb(255, 176, 176, 176));
            button.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

            var content = button.Content as StackPanel;
            if (content == null) return;
            for (var i = 0; i < content.Children.Count; i++)
            {
                var label = content.Children[i] as TextBlock;
                if (label == null) continue;
                label.Foreground = new SolidColorBrush(isSelected && i == 0
                    ? Windows.UI.Color.FromArgb(255, 208, 188, 255)
                    : (isSelected
                        ? Windows.UI.Color.FromArgb(255, 243, 244, 246)
                        : Windows.UI.Color.FromArgb(255, 156, 163, 175)));
            }
        }

        private void NavigateHome_Click(object sender, RoutedEventArgs e) { ShowSection(0); }
        private void NavigateMarfa_Click(object sender, RoutedEventArgs e) { VoiceButton_Click(sender, e); }
        private void NavigateCommands_Click(object sender, RoutedEventArgs e)
        {
            ShowSection(3);
            if (SettingsPivot != null)
                SettingsPivot.SelectedIndex = 3;
            UpdateNavigationState();
        }
        private void NavigateGraphs_Click(object sender, RoutedEventArgs e) { ShowSection(1); }
        private void NavigateTasks_Click(object sender, RoutedEventArgs e) { ShowSection(2); }
        private void NavigateSettings_Click(object sender, RoutedEventArgs e) { ShowSection(3); }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ReadMqttSettingsFromUi();
                _mqttReconnectEnabled = true;

                // Match Android behavior: persist the connection settings before connecting.
                _appSettings.Host = _settings.Host;
                _appSettings.Port = _settings.Port;
                _appSettings.Prefix = _settings.Prefix;
                _appSettings.UserName = _settings.UserName;
                _appSettings.Password = _settings.Password;
                _appSettings.ClientId = _settings.ClientId;
                _appSettings.UseTls = _settings.UseTls;
                await _settingsStore.SaveAsync(_appSettings);

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
            _mqttReconnectEnabled = false;
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

        private async void ExportSettingsBackupButton_Click(object sender, RoutedEventArgs e)
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
                _appSettings.UseTls = _settings.UseTls;
                _appSettings.UiFontScale = (float)UiFontScaleSlider.Value;
                _appSettings.MeasurementIntervalSeconds = Math.Max(1, interval);
                _appSettings.RetentionDays = Math.Max(1, retention);
                _appSettings.AutoExportPeriod = GetAutoExportTag();
                _appSettings.VoiceOpenCommand = VoiceOpenCommandBox.Text.Trim();
                _appSettings.VoiceCloseCommand = VoiceCloseCommandBox.Text.Trim();
                _appSettings.IoTManagerScenario = ScenarioTextBox.Text ?? string.Empty;

                // FileSavePicker is not supported reliably on Windows Phone 8.1.
                // Save to the user's Pictures library, which is outside app-local storage.
                var folder = Windows.Storage.KnownFolders.SavedPictures;
                var file = await folder.CreateFileAsync(
                    "ESP32-AI-Control-settings-backup.json",
                    Windows.Storage.CreationCollisionOption.ReplaceExisting);

                await _settingsStore.ExportToFileAsync(file, _appSettings);
                await _settingsStore.SaveAsync(_appSettings);
                SettingsStatus.Text = "Резервная копия сохранена в Изображения\\ESP32-AI-Control-settings-backup.json";
                Log("SETTINGS BACKUP EXPORTED: " + file.Name);
            }
            catch (Exception ex)
            {
                SettingsStatus.Text = "Ошибка резервного копирования: " + ex.Message;
                Log("SETTINGS BACKUP EXPORT ERROR: " + ex.Message);
            }
        }

        private async void ImportSettingsBackupButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Restore from the persistent Pictures library backup created by this app.
                var file = await Windows.Storage.KnownFolders.SavedPictures.GetFileAsync(
                    "ESP32-AI-Control-settings-backup.json");

                var imported = await _settingsStore.ImportFromFileAsync(file);
                _appSettings = imported;
                await _settingsStore.SaveAsync(_appSettings);

                ApplyDefaultMqttProfileToSettings();
                ApplySettingsToMqtt();
                _commandExecutor.SetScenarioScript(_appSettings.IoTManagerScenario ?? string.Empty);
                _voiceParser = new VoiceCommandParser(
                    _appSettings.VoiceOpenCommand ?? string.Empty,
                    _appSettings.VoiceCloseCommand ?? string.Empty);
                ApplySettingsToUi();
                RefreshSavedMqttProfiles();

                SettingsStatus.Text = "Настройки восстановлены из резервной копии: " + file.Name;
                Log("SETTINGS BACKUP IMPORTED: " + file.Name);
            }
            catch (Exception ex)
            {
                SettingsStatus.Text = "Ошибка восстановления: " + ex.Message;
                Log("SETTINGS BACKUP IMPORT ERROR: " + ex.Message);
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
                _appSettings.UseTls = _settings.UseTls;
                _appSettings.UiFontScale = (float)UiFontScaleSlider.Value;
                WidgetPages.FontSize = 14 * (double)_appSettings.UiFontScale;
                SettingsPivot.FontSize = 14 * (double)_appSettings.UiFontScale;
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
                Log("Настройки сохранены. Резерв MQTT: " +
                    (_settingsStore.LastRoamingSaveStatus ?? "не проверено"));
            }
            catch (Exception ex)
            {
                SettingsStatus.Text = "Ошибка: " + ex.Message;
                Log("SETTINGS ERROR: " + ex.Message);
            }
        }

        private void RefreshSavedMqttProfiles()
        {
            if (_appSettings.SavedMqttProfiles == null)
                _appSettings.SavedMqttProfiles = new List<SavedMqttProfile>();

            var selectedName = SelectedSavedMqttProfileName();
            SavedProfilesList.Items.Clear();
            foreach (var profile in _appSettings.SavedMqttProfiles)
            {
                if (profile == null || string.IsNullOrWhiteSpace(profile.Name)) continue;
                var label = string.Equals(profile.Name, _appSettings.DefaultMqttProfileName, StringComparison.OrdinalIgnoreCase)
                    ? "★ " + profile.Name + " (по умолчанию)" : profile.Name;
                SavedProfilesList.Items.Add(label);
            }

            if (!string.IsNullOrEmpty(selectedName))
            {
                for (var i = 0; i < SavedProfilesList.Items.Count; i++)
                {
                    var label = SavedProfilesList.Items[i] as string;
                    if (string.Equals((label ?? string.Empty).Replace("★ ", string.Empty).Replace(" (по умолчанию)", string.Empty), selectedName, StringComparison.OrdinalIgnoreCase))
                    {
                        SavedProfilesList.SelectedIndex = i;
                        break;
                    }
                }
            }
            if (SavedProfilesList.SelectedIndex < 0 && SavedProfilesList.Items.Count > 0)
                SavedProfilesList.SelectedIndex = 0;
        }

        private string SelectedSavedMqttProfileName()
        {
            var label = SavedProfilesList == null ? null : SavedProfilesList.SelectedItem as string;
            if (string.IsNullOrEmpty(label)) return null;
            return label.Replace("★ ", string.Empty).Replace(" (по умолчанию)", string.Empty);
        }

        private SavedMqttProfile SelectedSavedMqttProfile()
        {
            var name = SelectedSavedMqttProfileName();
            if (string.IsNullOrEmpty(name) || _appSettings.SavedMqttProfiles == null) return null;
            return _appSettings.SavedMqttProfiles.FirstOrDefault(p => p != null &&
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private void SavedProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var profile = SelectedSavedMqttProfile();
            if (profile != null) SavedProfileNameBox.Text = profile.Name;
        }

        private async void SaveMqttProfileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var name = (SavedProfileNameBox.Text ?? string.Empty).Trim();
                if (name.Length == 0)
                    throw new InvalidOperationException("Введите название профиля.");

                ReadMqttSettingsFromUi();
                if (_appSettings.SavedMqttProfiles == null)
                    _appSettings.SavedMqttProfiles = new List<SavedMqttProfile>();

                var profile = _appSettings.SavedMqttProfiles.FirstOrDefault(p => p != null &&
                    string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (profile == null)
                {
                    profile = new SavedMqttProfile();
                    _appSettings.SavedMqttProfiles.Add(profile);
                }

                profile.Name = name;
                profile.Host = _settings.Host;
                profile.Port = _settings.Port;
                profile.Prefix = _settings.Prefix;
                profile.UserName = _settings.UserName;
                profile.Password = _settings.Password;
                profile.ClientId = _settings.ClientId;
                profile.UseTls = _settings.UseTls;
                _appSettings.Host = _settings.Host;
                _appSettings.Port = _settings.Port;
                _appSettings.Prefix = _settings.Prefix;
                _appSettings.UserName = _settings.UserName;
                _appSettings.Password = _settings.Password;
                _appSettings.ClientId = _settings.ClientId;
                _appSettings.UseTls = _settings.UseTls;

                await _settingsStore.SaveAsync(_appSettings);
                RefreshSavedMqttProfiles();
                SavedProfilesStatus.Text = "Профиль «" + name + "» сохранён.";
                Log("MQTT профиль сохранён: " + name);
            }
            catch (Exception ex)
            {
                SavedProfilesStatus.Text = "Ошибка: " + ex.Message;
                Log("MQTT PROFILE SAVE ERROR: " + ex.Message);
            }
        }

        private async void LoadMqttProfileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var profile = SelectedSavedMqttProfile();
                if (profile == null) throw new InvalidOperationException("Сначала выберите профиль.");

                _appSettings.Host = profile.Host ?? string.Empty;
                _appSettings.Port = profile.Port ?? "1883";
                _appSettings.Prefix = profile.Prefix ?? string.Empty;
                _appSettings.UserName = profile.UserName ?? string.Empty;
                _appSettings.Password = profile.Password ?? string.Empty;
                _appSettings.ClientId = string.IsNullOrEmpty(profile.ClientId) ? "ESP32-AI-Control-WP81" : profile.ClientId;
                _appSettings.UseTls = profile.UseTls;
                ApplySettingsToMqtt();
                ApplySettingsToUi();
                await _settingsStore.SaveAsync(_appSettings);
                SavedProfilesStatus.Text = "Профиль «" + profile.Name + "» загружен. Нажмите «Подключить», чтобы применить соединение.";
                Log("MQTT профиль загружен: " + profile.Name);
            }
            catch (Exception ex)
            {
                SavedProfilesStatus.Text = "Ошибка: " + ex.Message;
                Log("MQTT PROFILE LOAD ERROR: " + ex.Message);
            }
        }

        private async void SetDefaultMqttProfileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var profile = SelectedSavedMqttProfile();
                if (profile == null) throw new InvalidOperationException("Сначала выберите профиль.");
                _appSettings.DefaultMqttProfileName = profile.Name;
                await _settingsStore.SaveAsync(_appSettings);
                RefreshSavedMqttProfiles();
                SavedProfilesStatus.Text = "«" + profile.Name + "» будет загружаться при запуске приложения.";
                Log("MQTT профиль по умолчанию: " + profile.Name);
            }
            catch (Exception ex)
            {
                SavedProfilesStatus.Text = "Ошибка: " + ex.Message;
                Log("MQTT PROFILE DEFAULT ERROR: " + ex.Message);
            }
        }

        private async void DeleteMqttProfileButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var profile = SelectedSavedMqttProfile();
                if (profile == null) throw new InvalidOperationException("Сначала выберите профиль.");
                var name = profile.Name;
                _appSettings.SavedMqttProfiles.Remove(profile);
                if (string.Equals(_appSettings.DefaultMqttProfileName, name, StringComparison.OrdinalIgnoreCase))
                    _appSettings.DefaultMqttProfileName = string.Empty;
                await _settingsStore.SaveAsync(_appSettings);
                SavedProfileNameBox.Text = string.Empty;
                RefreshSavedMqttProfiles();
                SavedProfilesStatus.Text = "Профиль «" + name + "» удалён.";
                Log("MQTT профиль удалён: " + name);
            }
            catch (Exception ex)
            {
                SavedProfilesStatus.Text = "Ошибка: " + ex.Message;
                Log("MQTT PROFILE DELETE ERROR: " + ex.Message);
            }
        }

        private void ApplyDefaultMqttProfileToSettings()
        {
            if (_appSettings == null || _appSettings.SavedMqttProfiles == null ||
                string.IsNullOrEmpty(_appSettings.DefaultMqttProfileName)) return;

            var profile = _appSettings.SavedMqttProfiles.FirstOrDefault(p => p != null &&
                string.Equals(p.Name, _appSettings.DefaultMqttProfileName, StringComparison.OrdinalIgnoreCase));
            if (profile == null) return;

            _appSettings.Host = profile.Host ?? string.Empty;
            _appSettings.Port = profile.Port ?? "1883";
            _appSettings.Prefix = profile.Prefix ?? string.Empty;
            _appSettings.UserName = profile.UserName ?? string.Empty;
            _appSettings.Password = profile.Password ?? string.Empty;
            _appSettings.ClientId = string.IsNullOrEmpty(profile.ClientId) ? "ESP32-AI-Control-WP81" : profile.ClientId;
            _appSettings.UseTls = profile.UseTls;
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
            _settings.UseTls = _appSettings.UseTls;

            _voiceParser = new VoiceCommandParser(
                _appSettings.VoiceOpenCommand,
                _appSettings.VoiceCloseCommand);
        }

        private void ApplySettingsToUi()
        {
            HostBox.Text = _appSettings.Host;
            ClientIdBox.Text = string.IsNullOrEmpty(_appSettings.ClientId) ? "ESP32-AI-Control-WP81" : _appSettings.ClientId;
            MqttTlsCheckBox.IsChecked = _appSettings.UseTls;
            UiFontScaleSlider.Value = Math.Max(0.7, Math.Min(1.1, _appSettings.UiFontScale <= 0 ? 0.85 : _appSettings.UiFontScale));
            WidgetPages.FontSize = 14 * (double)UiFontScaleSlider.Value;
            SettingsPivot.FontSize = 14 * (double)UiFontScaleSlider.Value;
            PortBox.Text = _appSettings.Port;
            PrefixBox.Text = _appSettings.Prefix;
            UserNameBox.Text = _appSettings.UserName;
            PasswordBox.Password = _appSettings.Password;
            MeasurementIntervalBox.Text = _appSettings.MeasurementIntervalSeconds.ToString(CultureInfo.InvariantCulture);
            RetentionDaysBox.Text = _appSettings.RetentionDays.ToString(CultureInfo.InvariantCulture);
            VoiceOpenCommandBox.Text = _appSettings.VoiceOpenCommand;
            VoiceCloseCommandBox.Text = _appSettings.VoiceCloseCommand;
            ScenarioTextBox.Text = _appSettings.IoTManagerScenario;
            RefreshSavedMqttProfiles();

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

        private async void MqttSetting_LostFocus(object sender, RoutedEventArgs e)
        {
            await SaveMqttSettingsQuietlyAsync();
        }

        private async void MqttTlsCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            await SaveMqttSettingsQuietlyAsync();
        }

        private async Task SaveMqttSettingsQuietlyAsync()
        {
            if (_initializing || _appSettings == null || _settingsStore == null)
                return;

            try
            {
                ReadMqttSettingsFromUi();
                _appSettings.Host = _settings.Host;
                _appSettings.Port = _settings.Port;
                _appSettings.Prefix = _settings.Prefix;
                _appSettings.UserName = _settings.UserName;
                _appSettings.Password = _settings.Password;
                _appSettings.ClientId = _settings.ClientId;
                _appSettings.UseTls = _settings.UseTls;
                await _settingsStore.SaveAsync(_appSettings);
            }
            catch (Exception ex)
            {
                Log("Не удалось сохранить настройки MQTT: " + ex.Message);
            }
        }

        private void ReadMqttSettingsFromUi()
        {
            _settings.Host = HostBox.Text.Trim();
            _settings.Port = PortBox.Text.Trim();
            _settings.Prefix = PrefixBox.Text.Trim().Trim('/');
            _settings.UserName = UserNameBox.Text.Trim();
            _settings.Password = PasswordBox.Password;
            _settings.ClientId = string.IsNullOrWhiteSpace(ClientIdBox.Text) ? "ESP32-AI-Control-WP81" : ClientIdBox.Text.Trim();
            _settings.UseTls = MqttTlsCheckBox.IsChecked == true;
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
            _localScenarioEngine.OnMessage(e, !e.Retained);
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
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
                {
                    Log("HISTORY ERROR: " + ex.Message);
                });
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
            var definition = NormalizeType(string.IsNullOrWhiteSpace(state.DefinitionName) ? state.WidgetType : state.DefinitionName);
            var isSwitch = definition == "toggle" || type == "switch" || type == "toggle" ||
                           type == "checkbox" || type == "vbutton" ||
                           type == "vbtn" || type == "buttonin";
            var isAction = definition == "button" || definition == "vbtn" || definition == "btn" || type == "button" || type == "action" || type == "buttonout";
            var isSlider = definition == "range" || definition == "rangeservo" || type == "slider" || type == "range" || type == "inputdgt";
            var isProgress = definition == "progressline";

            var shell = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 43, 44, 52)),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(16, 6, 16, 6),
                BorderThickness = new Thickness(0)
            };

            var row = new Grid();
            visual.Row = row;
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            // Keep the value/control column content-sized instead of reserving a
            // narrow fixed 96 px slot. Long sensor values must remain readable
            // without forcing the title outside the available Lumia screen width.
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // WP8.1 may not contain glyphs for modern emoji. Detect supported emoji
            // by their exact Unicode sequence and draw an app-owned vector equivalent.
            // The underlying MQTT state is never modified.
            string receivedEmoji;
            string visibleTitle;
            if (WidgetIconCatalog.TryGetLeadingEmoji(GetTitle(state), out receivedEmoji, out visibleTitle))
            {
                var emojiIcon = WidgetIconCatalog.CreateForEmoji(receivedEmoji);
                visual.EmojiIcon = emojiIcon;
                emojiIcon.VerticalAlignment = VerticalAlignment.Center;
                emojiIcon.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetColumn(emojiIcon, 0);
                row.Children.Add(emojiIcon);
                visual.Title = new TextBlock
                {
                    Text = visibleTitle,
                    FontSize = 17,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 243, 244, 246)),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
            }
            else
            {
                // Unsupported emoji are not guessed or removed; preserve the MQTT title.
                visual.Title = new TextBlock
                {
                    Text = GetTitle(state),
                    FontSize = 16,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 243, 244, 246)),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
            }
            Grid.SetColumn(visual.Title, 1);
            row.Children.Add(visual.Title);

            visual.ValueText = new TextBlock
            {
                Text = (string.IsNullOrEmpty(state.Value) ? "—" : state.Value) + (string.IsNullOrWhiteSpace(state.Unit) ? string.Empty : " " + state.Unit),
                FontSize = 18,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 98, 168, 255)),
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MaxWidth = 148,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            if (isSwitch)
            {
                var toggle = new ToggleSwitch
                {
                    OnContent = "",
                    OffContent = "",
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 203, 213, 225)),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    MinWidth = 58
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
                    MaxWidth = 128,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                    Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                    Padding = new Thickness(12, 7, 12, 7),
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
                double sliderMin = GetWidgetConfigNumber(state, "min", 0);
                double sliderMax = GetWidgetConfigNumber(state, "max", definition == "rangeservo" ? 180 : 100);
                if (sliderMax <= sliderMin) sliderMax = sliderMin + 1;
                var slider = new Slider
                {
                    Minimum = sliderMin,
                    Maximum = sliderMax,
                    StepFrequency = 1,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
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
            else if (isProgress)
            {
                var progress = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = Math.Max(0.0001, GetWidgetConfigNumber(state, "max", 100)),
                    Height = Math.Max(4, GetWidgetConfigNumber(state, "stroke", 10)),
                    Margin = new Thickness(34, 4, 8, 2),
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 67, 77))
                };
                visual.Control = progress;
                visual.Container.Children.Add(progress);
            }

            return visual;
        }

        private static double GetWidgetConfigNumber(WidgetState state, string key, double fallback)
        {
            try
            {
                if (state == null || string.IsNullOrWhiteSpace(state.ConfigJson)) return fallback;
                JsonObject config;
                if (!JsonObject.TryParse(state.ConfigJson, out config)) return fallback;
                IJsonValue value;
                if (!config.TryGetValue(key, out value) || value.ValueType != JsonValueType.Number) return fallback;
                return value.GetNumber();
            }
            catch { return fallback; }
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
                ApplyWidgetTitle(visual, state);
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
            if (DashboardEmptyState != null) DashboardEmptyState.Visibility = Visibility.Collapsed;
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

            if (DashboardEmptyState != null)
                DashboardEmptyState.Visibility = WidgetPages.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplyWidgetTitle(WidgetVisual visual, WidgetState state)
        {
            var title = GetTitle(state);
            string emoji;
            string visibleTitle;
            bool hasEmoji = WidgetIconCatalog.TryGetLeadingEmoji(title, out emoji, out visibleTitle);

            if (hasEmoji)
            {
                visual.Title.Text = visibleTitle;
                if (visual.Row != null)
                {
                    if (visual.EmojiIcon != null)
                        visual.Row.Children.Remove(visual.EmojiIcon);
                    visual.EmojiIcon = WidgetIconCatalog.CreateForEmoji(emoji);
                    visual.EmojiIcon.VerticalAlignment = VerticalAlignment.Center;
                    visual.EmojiIcon.HorizontalAlignment = HorizontalAlignment.Center;
                    Grid.SetColumn(visual.EmojiIcon, 0);
                    visual.Row.Children.Insert(0, visual.EmojiIcon);
                }
            }
            else
            {
                visual.Title.Text = title;
                if (visual.Row != null && visual.EmojiIcon != null)
                    visual.Row.Children.Remove(visual.EmojiIcon);
                visual.EmojiIcon = null;
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
                visual.ValueText.Text = (string.IsNullOrEmpty(value) ? "—" : value) + (visual.State == null || string.IsNullOrWhiteSpace(visual.State.Unit) ? string.Empty : " " + visual.State.Unit);

            var toggle = visual.Control as ToggleSwitch;
            if (toggle != null)
            {
                toggle.IsOn = ParseBoolean(value);
                return;
            }

            var progress = visual.Control as ProgressBar;
            if (progress != null)
            {
                double progressValue;
                if (double.TryParse((value ?? string.Empty).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out progressValue))
                    progress.Value = Math.Max(progress.Minimum, Math.Min(progress.Maximum, progressValue));
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

            var width = Math.Max(280.0, GraphCanvas.ActualWidth > 0 ? GraphCanvas.ActualWidth : ActualWidth - 40.0);
            GraphCanvas.Width = width;
            var height = 300.0;
            var padding = 18.0;
            var timeSpan = Math.Max(1.0, (maxTime - minTime).TotalSeconds);

            // Subtle horizontal guides make values easier to compare without
            // competing with the sensor series.
            for (var gridIndex = 0; gridIndex <= 4; gridIndex++)
            {
                var y = padding + (height - padding * 2) * gridIndex / 4.0;
                GraphCanvas.Children.Add(new Line
                {
                    X1 = padding,
                    Y1 = y,
                    X2 = width - padding,
                    Y2 = y,
                    Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 58, 60, 70)),
                    StrokeThickness = 1
                });
            }

            var axis = new Line
            {
                X1 = padding, Y1 = height - padding,
                X2 = width - padding, Y2 = height - padding,
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 130, 132, 145)),
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
                var statusLabel = new TextBlock
                {
                    Text = item.Status,
                    FontSize = 14,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 174, 151, 255)),
                    Margin = new Thickness(0, 0, 0, 3)
                };
                var detailsText = item.ExecuteAt.ToLocalTime().ToString(
                    "dd.MM.yyyy  HH:mm", CultureInfo.InvariantCulture) +
                    Environment.NewLine + item.Command;
                if (!string.IsNullOrEmpty(item.LastError))
                    detailsText += Environment.NewLine + "Ошибка: " + item.LastError;

                var detailsLabel = new TextBlock
                {
                    Text = detailsText,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 185, 187, 198)),
                    TextWrapping = TextWrapping.Wrap
                };
                var rowContent = new StackPanel
                {
                    Margin = new Thickness(2, 1, 2, 1)
                };
                rowContent.Children.Add(statusLabel);
                rowContent.Children.Add(detailsLabel);

                ScheduleList.Items.Add(new ListViewItem
                {
                    Content = rowContent,
                    Tag = item.Id
                });
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

        private async void Voice_RecognitionFailed(object sender, string message)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
            {
                VoiceResultText.Text = message;
                Log("VOICE: " + message);
            });
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

            var udpDiagnostic =
                text.IndexOf("UDP", StringComparison.OrdinalIgnoreCase) >= 0 &&
                text.IndexOf("UDP ESP32:", StringComparison.OrdinalIgnoreCase) < 0;
            if (text.IndexOf("MQTT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                udpDiagnostic ||
                text.IndexOf("WS ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("СЦЕНАРИЙ", StringComparison.OrdinalIgnoreCase) >= 0)
                _connectionJournal.Append(line);
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
            public Grid Row { get; set; }
            public FrameworkElement EmojiIcon { get; set; }
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