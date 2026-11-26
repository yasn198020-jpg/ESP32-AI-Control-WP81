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
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Media.SpeechRecognition;
using Windows.Storage.Pickers;
using Windows.UI.Notifications;
using Windows.Data.Xml.Dom;
using Windows.Data.Json;
using Windows.Storage;
using Windows.Media.SpeechSynthesis;
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
        private readonly List<TrainedVoiceCommand> _trainedCommands = new List<TrainedVoiceCommand>();
        private Grid _trainedCommandsSection;
        private Grid _cleanupSection;
        private Grid _permissionsSection;
        private Grid _pendingMarfaSection;
        private Grid _deleteWidgetSection;
        private Grid _scenarioHubSection;
        private TextBox _scenarioProbeBox;
        private TextBlock _scenarioProbeResult;
        private StackPanel _pendingMarfaList;
        private StackPanel _deleteWidgetList;
        private DispatcherTimer _pendingMarfaTimer;
        private TextBlock _cleanupResultText;
        private StackPanel _trainedCommandsList;
        private Popup _trainingPopup;
        private Popup _variantPopup;
        private Popup _textSizePopup;
        private WidgetState _trainingTarget;
        private string _trainingValue = "1";
        private TextBox _trainingPhraseBox;
        private ComboBox _trainingModeBox;
        private ComboBox _trainingExistingPhraseBox;
        private ComboBox _trainingActionBox;
        private Button _trainingRecordButton;
        private TextBox _variantPhraseBox;
        private string _variantPhraseTarget;
        private MediaElement _speechPlayback;
        private SpeechSynthesizer _speechSynthesizer;
        private readonly List<string> _logLines = new List<string>();
        private bool _applyingTtsSettings;

        private readonly Dictionary<string, WidgetVisual> _visuals =
            new Dictionary<string, WidgetVisual>();
        private readonly Dictionary<string, PageVisual> _pages =
            new Dictionary<string, PageVisual>();
        // Keep the active device page stable while MQTT adds, moves or removes widgets.
        private string _selectedWidgetPageName;
        private bool _restoringWidgetPageSelection;
        private readonly Dictionary<string, WidgetState> _widgetIndex =
            new Dictionary<string, WidgetState>();
        private readonly List<ScheduledCommand> _scheduleRows =
            new List<ScheduledCommand>();
        private readonly List<string> _graphCompareKeys = new List<string>();
        private readonly List<MeasurementPoint> _lastGraphPoints =
            new List<MeasurementPoint>();
        private readonly List<MeasurementPoint> _graphNumericPoints =
            new List<MeasurementPoint>();
        private readonly List<double> _graphPointXPositions = new List<double>();
        private double _graphZoom = 1.0;
        private int _graphSelectedIndex = -1;
        private readonly Dictionary<FrameworkElement, double> _fontBaseSizes =
            new Dictionary<FrameworkElement, double>();
        private double _currentFontScale = 1.0;

        private DispatcherTimer _autoExportTimer;
        private DispatcherTimer _lastUpdatedTimer;
        private bool _showLastUpdated;
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
            WidgetPages.SelectionChanged += WidgetPages_SelectionChanged;
            GraphCanvas.Tapped += GraphCanvas_Tapped;
            _trainedCommands.AddRange(LoadTrainedCommands());
            _trainedCommandsSection = new Grid
            {
                Visibility = Visibility.Collapsed,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32))
            };
            MainSections.Children.Add(_trainedCommandsSection);
            BuildTrainedCommandsSection();
            _cleanupSection = new Grid
            {
                Visibility = Visibility.Collapsed,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32))
            };
            MainSections.Children.Add(_cleanupSection);
            BuildCleanupSection();
            _permissionsSection = new Grid
            {
                Visibility = Visibility.Collapsed,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32))
            };
            MainSections.Children.Add(_permissionsSection);
            BuildPermissionsSection();
            _pendingMarfaSection = new Grid
            {
                Visibility = Visibility.Collapsed,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32))
            };
            MainSections.Children.Add(_pendingMarfaSection);
            BuildPendingMarfaSection();
            _deleteWidgetSection = new Grid
            {
                Visibility = Visibility.Collapsed,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32))
            };
            MainSections.Children.Add(_deleteWidgetSection);
            BuildDeleteWidgetSection();
            _scenarioHubSection = new Grid
            {
                Visibility = Visibility.Collapsed,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32))
            };
            MainSections.Children.Add(_scenarioHubSection);
            BuildScenarioHubSection();
            _speechPlayback = new MediaElement
            {
                Width = 1, Height = 1, Opacity = 0,
                IsHitTestVisible = false,
                AutoPlay = true
            };
            MainSections.Children.Add(_speechPlayback);
            LocalScenarioEnabledCheckBox.IsChecked = true;
            LocalScenarioNotificationCheckBox.IsChecked = true;
            if (DashboardEmptyOverlay != null)
                DashboardEmptyOverlay.Visibility = WidgetPages.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateNavigationState();

            _settingsStore = new AppSettingsStore();
            _appSettings = new AppSettings();

            RefreshGraphSetPicker();
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
                delegate(WidgetState state)
                {
                    int configured;
                    if (_appSettings.WidgetMeasurementIntervals != null &&
                        _appSettings.WidgetMeasurementIntervals.TryGetValue(state.Key, out configured))
                        return Math.Max(1, configured);
                    return Math.Max(1, _appSettings.MeasurementIntervalSeconds);
                });

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
                if (_appSettings.GraphWidgetColors == null) _appSettings.GraphWidgetColors = new Dictionary<string, string>();
                RefreshGraphSetPicker();
                _logLines.Clear();
                _logLines.AddRange(_connectionJournal.Load().Take(250));
                RefreshLogView();
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

        private async void WebSocketFallback_WidgetUpdated(object sender, WebSocketWidgetUpdateEventArgs e)
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
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
            {
                RefreshLocalScenarioBuilderOptions();
            });
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
            RefreshLocalScenarioBuilderOptions();
            LocalScenariosList.Items.Clear();
            var scenarios = _localScenarioStore.Load();
            foreach (var scenario in scenarios)
            {
                var conditionText = string.Join("  ·  ", scenario.Conditions.Select((condition, index) =>
                    (index == 0 ? string.Empty : (condition.Connector == "OR" ? "ИЛИ " : "И ")) +
                    condition.WidgetId + " " + condition.Operator + " " +
                    condition.Threshold.ToString(CultureInfo.InvariantCulture)));
                var actionText = scenario.Actions == null || scenario.Actions.Count == 0
                    ? "Действия не заданы"
                    : string.Join("  ·  ", scenario.Actions.Select(action =>
                        action.DeviceId + "/" + action.WidgetId + " = " + action.Value));
                var details = new StackPanel { Margin = new Thickness(12, 9, 12, 9) };
                details.Children.Add(new TextBlock
                {
                    Text = (scenario.Enabled ? "●  " : "○  ") + scenario.Title,
                    FontSize = 16,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(scenario.Enabled
                        ? Windows.UI.Color.FromArgb(255, 208, 188, 255)
                        : Windows.UI.Color.FromArgb(255, 172, 173, 184))
                });
                details.Children.Add(new TextBlock
                {
                    Text = "ЕСЛИ: " + (string.IsNullOrWhiteSpace(conditionText) ? "условия не заданы" : conditionText),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 221, 228)),
                    Margin = new Thickness(0, 4, 0, 2)
                });
                details.Children.Add(new TextBlock
                {
                    Text = "ТО: " + actionText,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184))
                });
                details.Children.Add(new TextBlock
                {
                    Text = (scenario.NotificationEnabled ? "Уведомление включено" : "Без уведомления") +
                        (scenario.Enabled ? "  ·  Активно" : "  ·  Выключено"),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)),
                    Margin = new Thickness(0, 4, 0, 0)
                });
                var card = new Border
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 49, 58)),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 67, 77)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Child = details
                };
                var item = new ListViewItem
                {
                    Content = card,
                    Tag = scenario.Id,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 3, 0, 3)
                };
                LocalScenariosList.Items.Add(item);
            }
            LocalScenariosStatus.Text = "Сценариев: " + scenarios.Count;
        }

        private void RefreshLocalScenarioBuilderOptions()
        {
            if (_devices == null || LocalScenarioConditionWidgetBox == null ||
                LocalScenarioActionWidgetBox == null) return;

            var selectedCondition = (LocalScenarioConditionWidgetBox.SelectedItem as ComboBoxItem)?.Tag as string;
            var selectedAction = (LocalScenarioActionWidgetBox.SelectedItem as ComboBoxItem)?.Tag as string;
            var widgets = _devices.GetWidgetsSnapshot()
                .OrderBy(item => item.DeviceId)
                .ThenBy(item => item.Page)
                .ThenBy(item => item.Order)
                .ToList();

            LocalScenarioConditionWidgetBox.Items.Clear();
            foreach (var state in widgets.Where(IsValueWidget))
            {
                LocalScenarioConditionWidgetBox.Items.Add(new ComboBoxItem
                {
                    Content = (string.IsNullOrWhiteSpace(state.Description) ? state.Id : state.Description) +
                        "  ·  " + state.DeviceId + "/" + state.Id,
                    Tag = state.Key
                });
            }

            LocalScenarioActionWidgetBox.Items.Clear();
            foreach (var state in widgets)
            {
                var type = NormalizeType(state.WidgetType);
                var definition = NormalizeType(state.DefinitionName);
                if (type != "toggle" && type != "button" && type != "slider" &&
                    type != "select" && type != "dropdown" && definition != "button" &&
                    definition != "vbtn" && definition != "btn") continue;

                LocalScenarioActionWidgetBox.Items.Add(new ComboBoxItem
                {
                    Content = (string.IsNullOrWhiteSpace(state.Description) ? state.Id : state.Description) +
                        "  ·  " + state.DeviceId + "/" + state.Id,
                    Tag = state.Key
                });
            }

            if (LocalScenarioConditionWidgetBox.Items.Count > 0)
            {
                var match = LocalScenarioConditionWidgetBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag as string, selectedCondition, StringComparison.Ordinal));
                LocalScenarioConditionWidgetBox.SelectedItem = match ?? LocalScenarioConditionWidgetBox.Items[0];
            }
            if (LocalScenarioActionWidgetBox.Items.Count > 0)
            {
                var match = LocalScenarioActionWidgetBox.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag as string, selectedAction, StringComparison.Ordinal));
                LocalScenarioActionWidgetBox.SelectedItem = match ?? LocalScenarioActionWidgetBox.Items[0];
            }
        }

        private void LocalScenarioAddConditionButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = LocalScenarioConditionWidgetBox.SelectedItem as ComboBoxItem;
            var key = selected == null ? string.Empty : selected.Tag as string;
            var state = _devices == null ? null : _devices.GetWidgetsSnapshot()
                .FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
            if (state == null)
            {
                LocalScenariosStatus.Text = "Сначала подключите ESP32 и выберите датчик.";
                return;
            }

            double threshold;
            if (!double.TryParse((LocalScenarioThresholdBox.Text ?? string.Empty).Trim().Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out threshold) ||
                double.IsNaN(threshold) || double.IsInfinity(threshold))
            {
                LocalScenariosStatus.Text = "Введите числовой порог, например 25.";
                return;
            }

            var operatorItem = LocalScenarioOperatorBox.SelectedItem as ComboBoxItem;
            var connectorItem = LocalScenarioConnectorBox.SelectedItem as ComboBoxItem;
            var op = operatorItem == null ? ">" : operatorItem.Tag as string;
            var connector = connectorItem == null ? "AND" : connectorItem.Tag as string;
            var existing = (LocalScenarioConditionsBox.Text ?? string.Empty).Trim();
            var line = (existing.Length == 0 ? string.Empty : (connector ?? "AND") + " ") +
                state.Id + " " + (op ?? ">") + " " + threshold.ToString(CultureInfo.InvariantCulture);
            LocalScenarioConditionsBox.Text = existing.Length == 0 ? line :
                existing + Environment.NewLine + line;
            LocalScenarioThresholdBox.Text = string.Empty;
            LocalScenariosStatus.Text = "Условие добавлено. Можно добавить ещё одно или сохранить правило.";
        }

        private void LocalScenarioAddActionButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = LocalScenarioActionWidgetBox.SelectedItem as ComboBoxItem;
            var key = selected == null ? string.Empty : selected.Tag as string;
            var state = _devices == null ? null : _devices.GetWidgetsSnapshot()
                .FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
            if (state == null)
            {
                LocalScenariosStatus.Text = "Сначала подключите ESP32 и выберите исполнительный виджет.";
                return;
            }

            var value = (LocalScenarioActionValueBox.Text ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                LocalScenariosStatus.Text = "Укажите значение, которое нужно отправить устройству.";
                return;
            }

            var line = state.DeviceId + "/" + state.Id + "=" + value;
            var existing = (LocalScenarioActionsBox.Text ?? string.Empty).Trim();
            LocalScenarioActionsBox.Text = existing.Length == 0 ? line :
                existing + Environment.NewLine + line;
            LocalScenariosStatus.Text = "Действие добавлено. Можно добавить ещё одно или сохранить правило.";
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
            await ShowAppMessageAsync("Автосценарий", message);
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
            LocalScenarioThresholdBox.Text = string.Empty;
            LocalScenarioActionValueBox.Text = "1";
            LocalScenarioMessageBox.Text = "Условие выполнено: {value}";
            if (LocalScenarioConnectorBox.Items.Count > 0) LocalScenarioConnectorBox.SelectedIndex = 0;
            if (LocalScenarioOperatorBox.Items.Count > 0) LocalScenarioOperatorBox.SelectedIndex = 0;
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
            if (_pendingMarfaTimer != null)
                _pendingMarfaTimer.Stop();
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

        private void OpenMarfaDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            ShowSection(9);
        }

        private void ShowSection(int index)
        {
            _selectedSection = Math.Max(0, Math.Min(9, index));
            UpdateNavigationState();
            if (_selectedSection == 4)
                RefreshTrainedCommandsScreen();
            if (_selectedSection == 7)
            {
                RefreshPendingMarfaCommandsScreen();
                if (_pendingMarfaTimer == null)
                {
                    _pendingMarfaTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    _pendingMarfaTimer.Tick += delegate { RefreshPendingMarfaCommandsScreen(); };
                }
                _pendingMarfaTimer.Start();
            }
            else if (_pendingMarfaTimer != null)
                _pendingMarfaTimer.Stop();
            if (_selectedSection == 8)
                RefreshDeleteWidgetScreen();
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
            AddPopupMenuHeader(items, "КОМАНДЫ");
            AddPopupMenuItem(items, "Обученные команды", delegate { ShowSection(4); });
            AddPopupMenuItem(items, "Отложенные команды Марфы", delegate { ShowSection(7); });
            AddPopupMenuItem(items, "Удалить элемент", delegate { ShowSection(8); });
            AddPopupMenuItem(items, "Планировщик команд ESP32", delegate { ShowSection(2); });
            AddPopupMenuItem(items, "Центр сценариев Марфы", delegate { ShowSection(9); });
            AddPopupMenuSeparator(items);
            AddPopupMenuHeader(items, "ОБСЛУЖИВАНИЕ");
            AddPopupMenuItem(items, "Очистка приложения", delegate { ShowSection(5); });
            AddPopupMenuItem(items, "Выданные разрешения", delegate { ShowSection(6); });
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
            AddPopupMenuItem(items, "Размер текста", ShowTextSizeDialog);

            AddPopupMenuSeparator(items);
            AddPopupMenuHeader(items, "О ПРИЛОЖЕНИИ");
            AddPopupMenuItem(items, "Проверка Марфы", delegate { ShowSection(9); });
            AddPopupMenuItem(items, "О приложении", ShowAboutDialog);

            var availableHeight = Math.Max(260, ActualHeight - 76);
            var scroll = new ScrollViewer
            {
                Content = items,
                MaxHeight = availableHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var frame = new Border
            {
                Width = Math.Min(320, Math.Max(260, ActualWidth - 20)),
                MaxHeight = availableHeight,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 36, 37, 42)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 67, 77)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Child = scroll
            };

            // Keep the menu attached directly below the compact Android-style app bar.
            _navigationPopup = new Popup
            {
                Child = frame,
                HorizontalOffset = 4,
                VerticalOffset = 62,
                IsOpen = true
            };
        }

        private void ShowAboutDialog()
        {
            var body = new StackPanel();
            body.Children.Add(new TextBlock
            {
                Text = "О приложении",
                FontSize = 14,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                Margin = new Thickness(12, 12, 12, 6)
            });
            body.Children.Add(new TextBlock
            {
                Text = "ESP32 AI Control для Windows Phone 8.1",
                FontSize = 16,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 2, 12, 8)
            });
            body.Children.Add(new TextBlock
            {
                Text = "Управление ESP32 через MQTT, UDP и WebSocket. Голосовое управление, сценарии, история измерений и планирование команд.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)),
                Margin = new Thickness(12, 0, 12, 12)
            });
            var close = new Button
            {
                Content = "Закрыть",
                MinHeight = 42,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8, 4, 8, 8),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32)),
                FontWeight = Windows.UI.Text.FontWeights.SemiBold
            };
            body.Children.Add(close);
            var popup = CreateAppPopup(body, 330, 300);
            close.Click += delegate { popup.IsOpen = false; };
        }

        private void ShowTextSizeDialog()
        {
            var initial = _appSettings == null
                ? (float)UiFontScaleSlider.Value
                : (_appSettings.UiFontScale <= 0 ? (float)UiFontScaleSlider.Value : _appSettings.UiFontScale);
            var slider = new Slider
            {
                Minimum = 0.7,
                Maximum = 1.1,
                StepFrequency = 0.05,
                Value = initial,
                Margin = new Thickness(12, 6, 12, 6)
            };
            var valueLabel = new TextBlock
            {
                Text = "Масштаб: " + initial.ToString("0.00", CultureInfo.InvariantCulture),
                FontSize = 12,
                Margin = new Thickness(12, 2, 12, 8),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255))
            };
            slider.ValueChanged += delegate
            {
                valueLabel.Text = "Масштаб: " + slider.Value.ToString("0.00", CultureInfo.InvariantCulture);
                ApplyFontScale(slider.Value);
            };

            var body = new StackPanel();
            body.Children.Add(new TextBlock
            {
                Text = "Размер текста",
                FontSize = 14,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(12, 12, 12, 4),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255))
            });
            body.Children.Add(new TextBlock
            {
                Text = "Переместите ползунок, чтобы подобрать удобный размер. Изменение сразу применяется к вкладкам дашборда и настроек.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 0, 12, 4),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184))
            });
            body.Children.Add(slider);
            body.Children.Add(valueLabel);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8)
            };
            var cancel = new Button { Content = "Отмена", Margin = new Thickness(0, 0, 6, 0), MinHeight = 44 };
            cancel.Click += delegate
            {
                ApplyFontScale(initial);
                if (_textSizePopup != null) _textSizePopup.IsOpen = false;
            };
            var save = new Button
            {
                Content = "Сохранить",
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32)),
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                MinHeight = 44
            };
            save.Click += async delegate
            {
                try
                {
                    ApplyFontScale(slider.Value);
                    if (_appSettings != null)
                    {
                        _appSettings.UiFontScale = (float)slider.Value;
                        await _settingsStore.SaveAsync(_appSettings);
                    }
                    if (_textSizePopup != null) _textSizePopup.IsOpen = false;
                    Log("UI FONT SCALE: " + slider.Value.ToString("0.00", CultureInfo.InvariantCulture));
                }
                catch (Exception ex) { Log("FONT SCALE SAVE ERROR: " + ex.Message); }
            };
            actions.Children.Add(cancel);
            actions.Children.Add(save);
            body.Children.Add(actions);
            _textSizePopup = CreateAppPopup(body, 330, 360);
        }

        private void ApplyFontScale(double scale)
        {
            scale = Math.Max(0.7, Math.Min(1.1, scale));
            ApplyFontScaleToTree(this, scale);
            _currentFontScale = scale;
            if (UiFontScaleSlider != null && Math.Abs(UiFontScaleSlider.Value - scale) > 0.001)
                UiFontScaleSlider.Value = scale;
        }

        private void ApplyFontScaleToTree(DependencyObject root, double scale)
        {
            if (root == null) return;

            // Process children first. This captures inherited sizes before their
            // parent is rescaled, avoiding cumulative font growth/shrinkage.
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
                ApplyFontScaleToTree(VisualTreeHelper.GetChild(root, i), scale);

            var text = root as TextBlock;
            if (text != null)
            {
                if (!_fontBaseSizes.ContainsKey(text))
                {
                    var localValue = text.ReadLocalValue(TextBlock.FontSizeProperty);
                    _fontBaseSizes[text] = localValue == DependencyProperty.UnsetValue
                        ? text.FontSize / Math.Max(0.01, _currentFontScale)
                        : text.FontSize;
                }
                text.FontSize = _fontBaseSizes[text] * scale;
            }
            else
            {
                var control = root as Control;
                if (control != null)
                {
                    if (!_fontBaseSizes.ContainsKey(control))
                    {
                        var localValue = control.ReadLocalValue(Control.FontSizeProperty);
                        _fontBaseSizes[control] = localValue == DependencyProperty.UnsetValue
                            ? control.FontSize / Math.Max(0.01, _currentFontScale)
                            : control.FontSize;
                    }
                    control.FontSize = _fontBaseSizes[control] * scale;
                }
            }
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
            // Android-like dropdown row: compact leading icon, aligned title and full-width touch target.
            string glyph = "•";
            if (title.IndexOf("команд", StringComparison.OrdinalIgnoreCase) >= 0) glyph = "🎤";
            else if (title.IndexOf("очист", StringComparison.OrdinalIgnoreCase) >= 0) glyph = "🧹";
            else if (title.IndexOf("разреш", StringComparison.OrdinalIgnoreCase) >= 0) glyph = "🔐";
            else if (title.IndexOf("сценар", StringComparison.OrdinalIgnoreCase) >= 0) glyph = "⚙";
            else if (title.IndexOf("журнал", StringComparison.OrdinalIgnoreCase) >= 0) glyph = "☷";
            else if (title.IndexOf("UDP", StringComparison.OrdinalIgnoreCase) >= 0) glyph = "⌁";
            else if (title.IndexOf("MQTT", StringComparison.OrdinalIgnoreCase) >= 0) glyph = "☁";
            else if (title.IndexOf("хранен", StringComparison.OrdinalIgnoreCase) >= 0) glyph = "▤";
            else if (title.IndexOf("профил", StringComparison.OrdinalIgnoreCase) >= 0) glyph = "▣";
            else if (title.IndexOf("голос", StringComparison.OrdinalIgnoreCase) >= 0) glyph = "♫";
            else if (title.IndexOf("текст", StringComparison.OrdinalIgnoreCase) >= 0) glyph = "A";
            else if (title.IndexOf("Марф", StringComparison.OrdinalIgnoreCase) >= 0) glyph = "🎙";
            else if (title.IndexOf("прилож", StringComparison.OrdinalIgnoreCase) >= 0) glyph = "ⓘ";

            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            content.Children.Add(new TextBlock
            {
                Text = glyph,
                Width = 25,
                FontSize = 17,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            });
            content.Children.Add(new TextBlock
            {
                Text = title,
                Margin = new Thickness(14, 0, 0, 0),
                FontSize = 15,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 244, 244, 244)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var item = new Button
            {
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 244, 244, 244)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(16, 9, 14, 9),
                MinHeight = 48
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

        private void ShowUdpDevicesDialog()
        {
            var devices = _udpDiscovery.GetSnapshot();
            var body = new StackPanel();

            body.Children.Add(new TextBlock
            {
                Text = "UDP УСТРОЙСТВА",
                FontSize = 14,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                Margin = new Thickness(8, 6, 8, 10)
            });

            if (devices.Count == 0)
            {
                body.Children.Add(new TextBlock
                {
                    Text = "Пока не найдено ни одного UDP-устройства.",
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 220, 220, 225)),
                    Margin = new Thickness(8, 8, 8, 16)
                });
            }
            else
            {
                foreach (var device in devices)
                {
                    var card = new Border
                    {
                        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 36, 37, 43)),
                        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 67, 77)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(12),
                        Padding = new Thickness(10),
                        Margin = new Thickness(2, 3, 2, 5)
                    };
                    var details = new StackPanel();
                    details.Children.Add(new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(device.Name) ? device.Id : device.Name,
                        FontSize = 14,
                        FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Windows.UI.Colors.White)
                    });
                    details.Children.Add(new TextBlock
                    {
                        Text = device.Ip,
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                        Margin = new Thickness(0, 3, 0, 0)
                    });
                    details.Children.Add(new TextBlock
                    {
                        Text = "Последний сигнал: " + device.LastSeenAt.ToString("HH:mm:ss"),
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)),
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                    card.Child = details;
                    body.Children.Add(card);
                }
            }

            var close = new Button
            {
                Content = "Закрыть",
                MinHeight = 42,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8, 10, 8, 4)
            };
            body.Children.Add(close);
            var popup = CreateAppPopup(body, 330, 480);
            close.Click += delegate { popup.IsOpen = false; };
        }

        private void TopInfoButton_Click(object sender, RoutedEventArgs e)
        {
            _showLastUpdated = !_showLastUpdated;
            if (TopInfoButton != null)
            {
                TopInfoButton.Foreground = new SolidColorBrush(
                    _showLastUpdated
                        ? Windows.UI.Color.FromArgb(255, 208, 188, 255)
                        : Windows.UI.Color.FromArgb(255, 234, 241, 247));
            }

            if (_showLastUpdated)
            {
                if (_lastUpdatedTimer == null)
                {
                    _lastUpdatedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    _lastUpdatedTimer.Tick += delegate { RefreshLastUpdatedLabels(); };
                }
                _lastUpdatedTimer.Start();
            }
            else if (_lastUpdatedTimer != null)
            {
                _lastUpdatedTimer.Stop();
            }

            RefreshLastUpdatedLabels();
        }

        private void RefreshLastUpdatedLabels()
        {
            foreach (var visual in _visuals.Values)
                UpdateLastUpdatedLabel(visual);
        }

        private void UpdateLastUpdatedLabel(WidgetVisual visual)
        {
            if (visual == null || visual.UpdatedAtText == null)
                return;

            var updated = visual.State == null ? DateTimeOffset.MinValue : visual.State.UpdatedAt;
            var visible = _showLastUpdated && updated > DateTimeOffset.MinValue;
            visual.UpdatedAtText.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) return;

            var seconds = Math.Max(1, (long)(DateTimeOffset.Now - updated).TotalSeconds);
            string text;
            if (seconds < 60)
                text = "обновлено " + seconds.ToString(CultureInfo.InvariantCulture) + " сек назад";
            else if (seconds < 3600)
                text = "обновлено " + (seconds / 60).ToString(CultureInfo.InvariantCulture) + " мин назад";
            else if (seconds < 86400)
                text = "обновлено " + (seconds / 3600).ToString(CultureInfo.InvariantCulture) + " ч назад";
            else
                text = "обновлено " + (seconds / 86400).ToString(CultureInfo.InvariantCulture) + " дн назад";

            visual.UpdatedAtText.Text = text;
        }

        private void UpdateNavigationState()
        {
            if (PageSubtitle == null || MainSections == null) return;

            HomeSection.Visibility = _selectedSection == 0 ? Visibility.Visible : Visibility.Collapsed;
            GraphsSection.Visibility = _selectedSection == 1 ? Visibility.Visible : Visibility.Collapsed;
            TasksSection.Visibility = _selectedSection == 2 ? Visibility.Visible : Visibility.Collapsed;
            SettingsSection.Visibility = _selectedSection == 3 ? Visibility.Visible : Visibility.Collapsed;
            if (_trainedCommandsSection != null)
                _trainedCommandsSection.Visibility = _selectedSection == 4 ? Visibility.Visible : Visibility.Collapsed;
            if (_cleanupSection != null)
                _cleanupSection.Visibility = _selectedSection == 5 ? Visibility.Visible : Visibility.Collapsed;
            if (_permissionsSection != null)
                _permissionsSection.Visibility = _selectedSection == 6 ? Visibility.Visible : Visibility.Collapsed;
            if (_pendingMarfaSection != null)
                _pendingMarfaSection.Visibility = _selectedSection == 7 ? Visibility.Visible : Visibility.Collapsed;
            if (_deleteWidgetSection != null)
                _deleteWidgetSection.Visibility = _selectedSection == 8 ? Visibility.Visible : Visibility.Collapsed;
            if (_scenarioHubSection != null)
                _scenarioHubSection.Visibility = _selectedSection == 9 ? Visibility.Visible : Visibility.Collapsed;

            if (_selectedSection == 0)
                PageSubtitle.Text = "Главная";
            else if (_selectedSection == 1)
                PageSubtitle.Text = "История и графики";
            else if (_selectedSection == 2)
                PageSubtitle.Text = "Планировщик ESP32";
            else if (_selectedSection == 3)
                PageSubtitle.Text = "Настройки";
            else if (_selectedSection == 4)
                PageSubtitle.Text = "Обученные команды";
            else if (_selectedSection == 5)
                PageSubtitle.Text = "Очистка приложения";
            else if (_selectedSection == 6)
                PageSubtitle.Text = "Разрешения приложения";
            else if (_selectedSection == 7)
                PageSubtitle.Text = "Отложенные команды Марфы";
            else if (_selectedSection == 8)
                PageSubtitle.Text = "Удаление элемента";
            else
                PageSubtitle.Text = "Центр сценариев Марфы";

            SetNavigationButton(NavHomeButton, _selectedSection == 0);
            SetNavigationButton(NavMarfaButton, false);
            SetNavigationButton(NavCommandsButton, _selectedSection == 4);
            SetNavigationButton(NavGraphsButton, _selectedSection == 1);
            SetNavigationButton(NavTasksButton, _selectedSection == 7);
            // Settings stays available from the hamburger menu, not the bottom bar.
        }

        private void SetNavigationButton(Button button, bool isSelected)
        {
            if (button == null) return;

            // Android-style navigation: only the icon receives the selected pill.
            button.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            button.Foreground = new SolidColorBrush(isSelected
                ? Windows.UI.Color.FromArgb(255, 243, 244, 246)
                : Windows.UI.Color.FromArgb(255, 156, 163, 175));
            button.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

            var content = button.Content as StackPanel;
            if (content == null) return;

            for (var i = 0; i < content.Children.Count; i++)
            {
                var indicator = content.Children[i] as Border;
                if (indicator != null)
                {
                    indicator.Background = new SolidColorBrush(isSelected
                        ? Windows.UI.Color.FromArgb(255, 74, 68, 88)
                        : Windows.UI.Color.FromArgb(0, 0, 0, 0));
                    var icon = indicator.Child as TextBlock;
                    if (icon != null)
                        icon.Foreground = new SolidColorBrush(isSelected
                            ? Windows.UI.Color.FromArgb(255, 208, 188, 255)
                            : Windows.UI.Color.FromArgb(255, 156, 163, 175));
                    continue;
                }

                var label = content.Children[i] as TextBlock;
                if (label != null)
                    label.Foreground = new SolidColorBrush(isSelected
                        ? Windows.UI.Color.FromArgb(255, 243, 244, 246)
                        : Windows.UI.Color.FromArgb(255, 156, 163, 175));
            }
        }

        private void NavigateHome_Click(object sender, RoutedEventArgs e) { ShowSection(0); }
        private void NavigateMarfa_Click(object sender, RoutedEventArgs e) { VoiceButton_Click(sender, e); }
        private void NavigateCommands_Click(object sender, RoutedEventArgs e)
        {
            ShowSection(4);
            RefreshTrainedCommandsScreen();
        }
        private void NavigateGraphs_Click(object sender, RoutedEventArgs e) { ShowSection(1); }
        private void NavigateTasks_Click(object sender, RoutedEventArgs e) { ShowSection(7); }
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
                _appSettings.TtsVoiceId = SelectedSpeechVoiceId();
                ReadTtsSettingsFromUi();
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
                ApplyFontScale(UiFontScaleSlider.Value);
                _appSettings.MeasurementIntervalSeconds = Math.Max(1, interval);
                _appSettings.RetentionDays = Math.Max(1, retention);
                _appSettings.AutoExportPeriod = GetAutoExportTag();
                _appSettings.VoiceOpenCommand = VoiceOpenCommandBox.Text.Trim();
                _appSettings.VoiceCloseCommand = VoiceCloseCommandBox.Text.Trim();
                _appSettings.TtsVoiceId = SelectedSpeechVoiceId();
                ReadTtsSettingsFromUi();
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
            ApplyFontScale(UiFontScaleSlider.Value);
            PortBox.Text = _appSettings.Port;
            PrefixBox.Text = _appSettings.Prefix;
            UserNameBox.Text = _appSettings.UserName;
            PasswordBox.Password = _appSettings.Password;
            MeasurementIntervalBox.Text = _appSettings.MeasurementIntervalSeconds.ToString(CultureInfo.InvariantCulture);
            RetentionDaysBox.Text = _appSettings.RetentionDays.ToString(CultureInfo.InvariantCulture);
            VoiceOpenCommandBox.Text = _appSettings.VoiceOpenCommand;
            VoiceCloseCommandBox.Text = _appSettings.VoiceCloseCommand;
            LoadAvailableSpeechVoices();
            NormalizeTtsSettings();
            ApplyTtsSettingsToUi();
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
                _commandExecutor.SetScenarioScript(scenario);
                var parsedRules = _commandExecutor.GetScenarioRuleCount();
                if (parsedRules <= 0)
                    throw new InvalidOperationException("В файле не найдено ни одного распознанного правила scenario=>. Проверьте экспорт IoTManager.");

                ScenarioTextBox.Text = scenario;
                _appSettings.IoTManagerScenario = scenario;
                await _settingsStore.SaveAsync(_appSettings);
                ScenarioPlanList.Items.Clear();
                RefreshScenarioDiagnostics();
                ScenarioPlanStatus.Text = "Сценарий распознан: " +
                    parsedRules.ToString(CultureInfo.InvariantCulture) +
                    " правил. Для просмотра зависимостей заполните команды открытия/закрытия и нажмите «Проверить план».";

                SettingsStatus.Text = "Сценарий загружен и распознан. Правил: " +
                    parsedRules.ToString(CultureInfo.InvariantCulture) + ".";
                Log("SCENARIO IMPORT: " + file.Name + " · rules=" +
                    parsedRules.ToString(CultureInfo.InvariantCulture));
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
                RefreshScenarioPlanPreview();
                if (ScenarioPlanList.Items.Count == 0)
                    throw new InvalidOperationException("Нет плана для проверки. Заполните команды открытия/закрытия.");

                SettingsStatus.Text = "План проверен: " +
                    ScenarioPlanList.Items.Count.ToString(CultureInfo.InvariantCulture) +
                    " шаг(ов). Команды не отправлялись.";
            }
            catch (Exception ex)
            {
                ScenarioPlanList.Items.Clear();
                ScenarioPlanStatus.Text = "Проверка не пройдена: " + ex.Message;
                SettingsStatus.Text = "Ошибка проверки: " + ex.Message;
                Log("SCENARIO TEST ERROR: " + ex.Message);
            }
        }

        private void RefreshScenarioPlanPreview()
        {
            _commandExecutor.SetScenarioScript(ScenarioTextBox.Text ?? string.Empty);
            var ruleCount = _commandExecutor.GetScenarioRuleCount();
            RefreshScenarioDiagnostics();
            ScenarioPlanList.Items.Clear();

            if (ruleCount <= 0)
                throw new InvalidOperationException("Не найдено распознанных правил scenario=>.");

            var open = VoiceOpenCommandBox.Text == null ? string.Empty : VoiceOpenCommandBox.Text.Trim();
            var close = VoiceCloseCommandBox.Text == null ? string.Empty : VoiceCloseCommandBox.Text.Trim();
            if (string.IsNullOrEmpty(open) && string.IsNullOrEmpty(close))
                throw new InvalidOperationException("Заполните хотя бы одну команду открытия или закрытия.");

            if (!string.IsNullOrEmpty(open))
                AddScenarioPlanPreview("ОТКРЫТИЕ", open);
            if (!string.IsNullOrEmpty(close))
                AddScenarioPlanPreview("ЗАКРЫТИЕ", close);

            ScenarioPlanStatus.Text = "Распознано правил: " +
                ruleCount.ToString(CultureInfo.InvariantCulture) +
                ". Ниже показан порядок действий; отправки на ESP32 не было.";
        }

        private void RefreshScenarioDiagnostics()
        {
            ScenarioDiagnosticsList.Items.Clear();
            var diagnostics = _commandExecutor.GetScenarioDiagnostics();
            var unresolved = 0;
            var ambiguous = 0;

            for (var i = 0; i < diagnostics.Count; i++)
            {
                var item = diagnostics[i];
                if (item.Status != null && item.Status.IndexOf("Неоднознач", StringComparison.OrdinalIgnoreCase) >= 0)
                    ambiguous++;
                if (item.Status != null && item.Status.StartsWith("Не найден", StringComparison.OrdinalIgnoreCase))
                    unresolved++;

                var device = string.IsNullOrWhiteSpace(item.DeviceId)
                    ? string.Empty : Environment.NewLine + "ESP32: " + item.DeviceId;
                ScenarioDiagnosticsList.Items.Add(
                    item.Variable + " · " + item.Usage + Environment.NewLine +
                    item.Status + device + Environment.NewLine +
                    (item.Description ?? string.Empty) +
                    (item.AssignedValues.Count == 0 ? string.Empty :
                        Environment.NewLine + "Значения в правилах: " + string.Join(", ", item.AssignedValues)) +
                    " · встречается: " + item.Occurrences.ToString(CultureInfo.InvariantCulture));
            }

            if (diagnostics.Count == 0)
            {
                ScenarioDiagnosticsStatus.Text = "В разобранных правилах переменные не обнаружены.";
                return;
            }

            ScenarioDiagnosticsStatus.Text =
                "Переменных: " + diagnostics.Count.ToString(CultureInfo.InvariantCulture) +
                " · не найдено: " + unresolved.ToString(CultureInfo.InvariantCulture) +
                " · неоднозначных: " + ambiguous.ToString(CultureInfo.InvariantCulture) +
                ". Неизвестные имена могут быть служебными переменными IoTManager.";
        }

        private void AddScenarioPlanPreview(string title, string command)
        {
            var plan = _commandExecutor.BuildPlan(command);
            ScenarioPlanList.Items.Add("── " + title + " ──");
            for (var i = 0; i < plan.Actions.Count; i++)
            {
                var action = plan.Actions[i];
                var state = _devices.Get(action.DeviceId, action.WidgetId);
                var current = state == null || string.IsNullOrWhiteSpace(state.Value)
                    ? "текущее: неизвестно"
                    : "сейчас " + state.Value;
                var kind = action.IsDependency ? "ЗАВИСИМОСТЬ" : "ОСНОВНОЕ ДЕЙСТВИЕ";
                var reason = string.IsNullOrWhiteSpace(action.Reason)
                    ? string.Empty : Environment.NewLine + "Причина: " + action.Reason;
                var line = (i + 1).ToString(CultureInfo.InvariantCulture) + ". " + kind +
                    Environment.NewLine + action.Command + " · " + current + reason;
                ScenarioPlanList.Items.Add(line);
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
                RefreshLocalScenarioBuilderOptions();
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

        private FrameworkElement CreateWidgetIcon(WidgetState state, string title, out string visibleTitle)
        {
            string emoji;
            if (WidgetIconCatalog.TryGetLeadingEmoji(title, out emoji, out visibleTitle))
                return WidgetIconCatalog.CreateForEmoji(emoji);

            visibleTitle = title;
            var iconName = GetWidgetConfigString(state, "icon").Trim().ToLowerInvariant();
            string glyph;
            switch (iconName)
            {
                case "thermometer": glyph = "🌡"; break;
                case "water": glyph = "💧"; break;
                case "walk": glyph = "🚶"; break;
                case "body": glyph = "◉"; break;
                case "speedometer": glyph = "◌"; break;
                case "sunny": glyph = "☀"; break;
                default: glyph = "•"; break;
            }

            return new TextBlock
            {
                Text = glyph,
                FontSize = 21,
                Width = 34,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255))
            };
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
                Margin = new Thickness(0, 0, 0, 4),
                Tag = state.Key
            };

            var type = NormalizeType(state.WidgetType);
            var definition = NormalizeType(string.IsNullOrWhiteSpace(state.DefinitionName) ? state.WidgetType : state.DefinitionName);
            var isSwitch = definition == "toggle" || type == "switch" || type == "toggle" ||
                           type == "checkbox" || type == "vbutton" ||
                           type == "vbtn" || type == "buttonin";
            var isAction = definition == "button" || definition == "vbtn" || definition == "btn" || type == "button" || type == "action" || type == "buttonout";
            var isSlider = definition == "range" || definition == "rangeservo" || type == "slider" || type == "range";
            var isProgress = definition == "progressline";
            var isRoundProgress = definition == "progressround";
            var isFillGauge = definition == "fillgauge";
            var isChart = definition.StartsWith("chart") || IsWidgetConfigChart(state);
            var isSelect = definition == "select";
            var isTextInput = definition == "inputtxt" || definition == "inputdgt" || definition == "input";
            var isDateInput = definition == "inputdate";
            var isTimeInput = definition == "inputtm";

            var shell = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 43, 44, 52)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 67, 77)),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 10, 12, 10),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var shellContent = new StackPanel();

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
            string visibleTitle;
            var iconElement = CreateWidgetIcon(state, GetTitle(state), out visibleTitle);
            if (iconElement != null)
            {
                visual.EmojiIcon = iconElement;
                iconElement.VerticalAlignment = VerticalAlignment.Center;
                iconElement.HorizontalAlignment = HorizontalAlignment.Center;
                Grid.SetColumn(iconElement, 0);
                row.Children.Add(iconElement);
            }
            visual.Title = new TextBlock
            {
                Text = visibleTitle,
                FontSize = iconElement == null ? 15 : 16,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 243, 244, 246)),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(visual.Title, 1);
            row.Children.Add(visual.Title);

            visual.ValueText = new TextBlock
            {
                Text = (string.IsNullOrEmpty(state.Value) ? "—" : state.Value) + (string.IsNullOrWhiteSpace(state.Unit) ? string.Empty : " " + state.Unit),
                FontSize = 17,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
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
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32)),
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

            if (isSwitch || isAction ||
                definition.StartsWith("anydata") || definition.StartsWith("chart") ||
                definition == "progressline" || definition == "progressround" ||
                definition == "fillgauge" || type == "status" || type == "value")
            {
                shell.IsHoldingEnabled = true;
                shell.Holding += delegate(object sender, Windows.UI.Xaml.Input.HoldingRoutedEventArgs args)
                {
                    if (args.HoldingState == Windows.UI.Input.HoldingState.Started)
                    {
                        args.Handled = true;
                        OpenTrainingDialog(visual.State);
                    }
                };
            }

            shellContent.Children.Add(row);
            shell.Child = shellContent;
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
                    StepFrequency = Math.Max(0.01, GetWidgetConfigNumber(state, "step", 1)),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                    Margin = new Thickness(34, 2, 8, 0)
                };
                slider.ValueChanged += async delegate(object s, RangeBaseValueChangedEventArgs ev)
                {
                    if (_syncingControls) return;
                    var scale = GetWidgetConfigNumber(visual.State, "k", 1);
                    if (Math.Abs(scale) < 0.000001) scale = 1;
                    await PublishControl(visual, (ev.NewValue / scale).ToString(CultureInfo.InvariantCulture));
                };
                visual.Control = slider;
                shellContent.Children.Add(slider);
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
                shellContent.Children.Add(progress);
            }
            else if (isSelect)
            {
                visual.ValueText.Visibility = Visibility.Collapsed;
                var picker = new ComboBox
                {
                    MinWidth = 112,
                    MaxWidth = 150,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 37, 38, 45)),
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 234, 241, 247)),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 75, 76, 88))
                };
                foreach (var option in GetWidgetOptions(state))
                    picker.Items.Add(option);
                picker.SelectionChanged += async delegate
                {
                    if (_syncingControls || picker.SelectedIndex < 0) return;
                    await PublishControl(visual, picker.SelectedIndex.ToString(CultureInfo.InvariantCulture));
                };
                visual.Control = picker;
                Grid.SetColumn(picker, 2);
                row.Children.Add(picker);
            }
            else if (isTextInput || isDateInput || isTimeInput)
            {
                visual.ValueText.Visibility = Visibility.Collapsed;
                var inputPanel = new Grid { Margin = new Thickness(34, 4, 8, 4) };
                inputPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                inputPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                FrameworkElement editor;
                if (isDateInput)
                {
                    var datePicker = new DatePicker
                    {
                        Header = GetTitle(state),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 234, 241, 247))
                    };
                    DateTimeOffset dateValue;
                    if (DateTimeOffset.TryParse(state.Value, CultureInfo.CurrentCulture, DateTimeStyles.None, out dateValue))
                        datePicker.Date = dateValue;
                    editor = datePicker;
                }
                else if (isTimeInput)
                {
                    var timePicker = new TimePicker
                    {
                        Header = GetTitle(state),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        ClockIdentifier = "24HourClock",
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 234, 241, 247))
                    };
                    TimeSpan timeValue;
                    if (TimeSpan.TryParse(state.Value, CultureInfo.CurrentCulture, out timeValue))
                        timePicker.Time = timeValue;
                    editor = timePicker;
                }
                else
                {
                    var textBox = new TextBox
                    {
                        Text = state.Value ?? string.Empty,
                        Header = GetTitle(state),
                        MinWidth = 130,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    editor = textBox;
                }

                visual.Control = editor;
                Grid.SetColumn(editor, 0);
                inputPanel.Children.Add(editor);

                var sendButton = new Button
                {
                    Content = "Отправить",
                    MinWidth = 84,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32))
                };
                sendButton.Click += async delegate
                {
                    string outbound = string.Empty;
                    var textBox = editor as TextBox;
                    var datePicker = editor as DatePicker;
                    var timePicker = editor as TimePicker;
                    if (textBox != null) outbound = textBox.Text ?? string.Empty;
                    else if (datePicker != null) outbound = datePicker.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
                    else if (timePicker != null) outbound = timePicker.Time.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
                    await PublishControl(visual, outbound);
                };
                Grid.SetColumn(sendButton, 1);
                inputPanel.Children.Add(sendButton);
                shellContent.Children.Add(inputPanel);
            }
            else if (isRoundProgress || isFillGauge)
            {
                visual.ValueText.Visibility = Visibility.Collapsed;
                var gauge = CreateGaugeCanvas(isFillGauge);
                visual.Control = gauge;
                Grid.SetColumn(gauge, 2);
                row.Children.Add(gauge);
                UpdateGaugeCanvas(gauge, state, isFillGauge);
            }
            else if (isChart)
            {
                visual.ValueText.Visibility = Visibility.Collapsed;
                visual.ChartMeta = new TextBlock
                {
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)),
                    Margin = new Thickness(34, 3, 8, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                shellContent.Children.Add(visual.ChartMeta);
                visual.ChartCanvas = new Canvas
                {
                    Width = 300,
                    Height = 150,
                    MinHeight = 130,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(34, 4, 8, 4),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32))
                };
                shellContent.Children.Add(visual.ChartCanvas);
                visual.ChartFooter = new TextBlock
                {
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)),
                    Margin = new Thickness(34, 0, 8, 2)
                };
                shellContent.Children.Add(visual.ChartFooter);
                DrawWidgetChart(visual, state.Value);
            }

            visual.UpdatedAtText = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)),
                Margin = new Thickness(46, 2, 8, 0),
                TextWrapping = TextWrapping.NoWrap,
                Visibility = Visibility.Collapsed
            };
            shellContent.Children.Add(visual.UpdatedAtText);
            UpdateLastUpdatedLabel(visual);

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

        private static bool IsWidgetConfigChart(WidgetState state)
        {
            var widget = GetWidgetConfigString(state, "widget");
            return string.Equals(widget, "chart", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetWidgetConfigString(WidgetState state, string key)
        {
            try
            {
                if (state == null || string.IsNullOrWhiteSpace(state.ConfigJson)) return string.Empty;
                JsonObject config;
                IJsonValue value;
                if (!JsonObject.TryParse(state.ConfigJson, out config) ||
                    !config.TryGetValue(key, out value) || value.ValueType != JsonValueType.String)
                    return string.Empty;
                return value.GetString();
            }
            catch { return string.Empty; }
        }

        private static IList<string> GetWidgetOptions(WidgetState state)
        {
            var result = new List<string>();
            try
            {
                JsonObject config;
                IJsonValue raw;
                if (!string.IsNullOrWhiteSpace(state.ConfigJson) &&
                    JsonObject.TryParse(state.ConfigJson, out config) &&
                    config.TryGetValue("options", out raw) && raw.ValueType == JsonValueType.Array)
                {
                    var array = raw.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        var item = array[(int)i];
                        if (item.ValueType == JsonValueType.String)
                            result.Add(item.GetString());
                    }
                }
            }
            catch { }
            if (result.Count == 0) { result.Add("Выключен"); result.Add("Включен"); }
            return result;
        }

        private static Canvas CreateGaugeCanvas(bool fill)
        {
            return new Canvas { Width = fill ? 86 : 82, Height = fill ? 86 : 82 };
        }

        private static void UpdateGaugeCanvas(Canvas canvas, WidgetState state, bool fill)
        {
            if (canvas == null || state == null) return;
            canvas.Children.Clear();
            var size = fill ? 86.0 : 82.0;
            double value;
            if (!double.TryParse((state.Value ?? string.Empty).Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out value)) value = 0;
            var max = fill ? 100.0 : GetWidgetConfigNumber(state, "max", 100);
            if (max <= 0) max = 100;
            var fraction = Math.Max(0, Math.Min(1, value / max));
            var primary = ParseWidgetColor(GetWidgetConfigString(state, fill ? "circleColor" : "color"),
                Windows.UI.Color.FromArgb(255, 208, 188, 255));

            if (fill)
            {
                var circleRadius = size / 2.0 - 4;
                var circleCenter = size / 2.0;
                var waveBrush = new SolidColorBrush(ParseWidgetColor(GetWidgetConfigString(state, "waveColor"),
                    Windows.UI.Color.FromArgb(180, 0, 188, 212)));
                canvas.Children.Add(new Ellipse
                {
                    Width = size - 8, Height = size - 8,
                    Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 37, 38, 45)),
                    Margin = new Thickness(4)
                });
                // WP8.1 exposes FrameworkElement.Clip as RectangleGeometry.
                // Build the liquid boundary from horizontal chords instead of an ellipse clip.
                if (fraction > 0)
                {
                    const int waveSteps = 28;
                    var liquidTop = circleCenter + circleRadius - (circleRadius * 2.0 * fraction);
                    for (var step = 0; step < waveSteps; step++)
                    {
                        var y = liquidTop + (circleCenter + circleRadius - liquidTop) * (step + 0.5) / waveSteps;
                        var dy = y - circleCenter;
                        var halfWidth = Math.Sqrt(Math.Max(0, circleRadius * circleRadius - dy * dy));
                        var strip = new Rectangle
                        {
                            Width = halfWidth * 2.0 + 1.0,
                            Height = (circleCenter + circleRadius - liquidTop) / waveSteps + 1.0,
                            Fill = waveBrush
                        };
                        Canvas.SetLeft(strip, circleCenter - halfWidth);
                        Canvas.SetTop(strip, y - strip.Height / 2.0);
                        canvas.Children.Add(strip);
                    }
                }
                canvas.Children.Add(new Ellipse
                {
                    Width = size - 8, Height = size - 8,
                    Stroke = new SolidColorBrush(primary), StrokeThickness = 5,
                    Fill = new SolidColorBrush(Windows.UI.Colors.Transparent),
                    Margin = new Thickness(4)
                });
                var label = new TextBlock
                {
                    Text = Math.Round(fraction * 100).ToString(CultureInfo.InvariantCulture) + "%",
                    FontSize = 16,
                    FontWeight = Windows.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                    Width = size, TextAlignment = TextAlignment.Center
                };
                Canvas.SetTop(label, size / 2 - 12);
                canvas.Children.Add(label);
                return;
            }

            canvas.Children.Add(new Ellipse
            {
                Width = size - 12, Height = size - 12,
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 85, 85, 85)),
                StrokeThickness = Math.Max(4, GetWidgetConfigNumber(state, "stroke", 12)),
                Margin = new Thickness(6)
            });
            var arc = new Polyline
            {
                Stroke = new SolidColorBrush(primary),
                StrokeThickness = Math.Max(4, GetWidgetConfigNumber(state, "stroke", 12)),
                StrokeLineJoin = PenLineJoin.Round
            };
            var semi = string.Equals(GetWidgetConfigString(state, "semicircle"), "1", StringComparison.Ordinal);
            var start = semi ? 180.0 : -90.0;
            var sweep = (semi ? 180.0 : 360.0) * fraction;
            var radius = (size - 12) / 2.0;
            var center = size / 2.0;
            var samples = Math.Max(2, (int)(Math.Abs(sweep) / 5) + 1);
            for (var i = 0; i < samples; i++)
            {
                var angle = (start + sweep * i / (samples - 1)) * Math.PI / 180.0;
                arc.Points.Add(new Point(center + radius * Math.Cos(angle), center + radius * Math.Sin(angle)));
            }
            canvas.Children.Add(arc);
            var text = new TextBlock
            {
                Text = Math.Round(value).ToString(CultureInfo.InvariantCulture),
                FontSize = 16, FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                Width = size, TextAlignment = TextAlignment.Center
            };
            Canvas.SetTop(text, size / 2 - 11);
            canvas.Children.Add(text);
        }

        private static Windows.UI.Color ParseWidgetColor(string raw, Windows.UI.Color fallback)
        {
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            try
            {
                var text = raw.Trim();
                if (text.StartsWith("#")) text = text.Substring(1);
                if (text.Length == 6)
                    return Windows.UI.Color.FromArgb(255,
                        Convert.ToByte(text.Substring(0, 2), 16),
                        Convert.ToByte(text.Substring(2, 2), 16),
                        Convert.ToByte(text.Substring(4, 2), 16));
                if (text.Length == 8)
                    return Windows.UI.Color.FromArgb(
                        Convert.ToByte(text.Substring(0, 2), 16),
                        Convert.ToByte(text.Substring(2, 2), 16),
                        Convert.ToByte(text.Substring(4, 2), 16),
                        Convert.ToByte(text.Substring(6, 2), 16));
            }
            catch { }
            switch (raw.Trim().ToLowerInvariant())
            {
                case "red": return Windows.UI.Colors.Red;
                case "green": return Windows.UI.Colors.Green;
                case "blue": return Windows.UI.Colors.Blue;
                case "cyan": return Windows.UI.Colors.Cyan;
                case "yellow": return Windows.UI.Colors.Yellow;
                case "orange": return Windows.UI.Color.FromArgb(255, 255, 152, 0);
                case "white": return Windows.UI.Colors.White;
                case "black": return Windows.UI.Colors.Black;
                default: return fallback;
            }
        }

        private static Windows.UI.Color GetWidgetValueColor(WidgetState state, string value)
        {
            var fallback = Windows.UI.Color.FromArgb(255, 144, 202, 249);
            double numeric;
            if (!double.TryParse((value ?? string.Empty).Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out numeric))
                return fallback;
            try
            {
                JsonObject config;
                IJsonValue raw;
                if (!JsonObject.TryParse(state.ConfigJson ?? "{}", out config) ||
                    !config.TryGetValue("color", out raw)) return fallback;
                if (raw.ValueType == JsonValueType.String)
                    return ParseWidgetColor(raw.GetString(), fallback);
                if (raw.ValueType == JsonValueType.Array)
                {
                    var array = raw.GetArray();
                    var selectedLevel = double.NegativeInfinity;
                    var selected = fallback;
                    foreach (var entry in array)
                    {
                        if (entry.ValueType != JsonValueType.Object) continue;
                        var item = entry.GetObject();
                        IJsonValue levelRaw, colorRaw;
                        if (!item.TryGetValue("level", out levelRaw) ||
                            !item.TryGetValue("value", out colorRaw) ||
                            levelRaw.ValueType != JsonValueType.Number ||
                            colorRaw.ValueType != JsonValueType.String) continue;
                        var level = levelRaw.GetNumber();
                        if (level <= numeric && level >= selectedLevel)
                        {
                            selectedLevel = level;
                            selected = ParseWidgetColor(colorRaw.GetString(), fallback);
                        }
                    }
                    return selected;
                }
            }
            catch { }
            return fallback;
        }

        private static IList<string> GetWidgetConfigStringArray(WidgetState state, string key)
        {
            var result = new List<string>();
            try
            {
                JsonObject config;
                IJsonValue raw;
                if (state != null && JsonObject.TryParse(state.ConfigJson ?? "{}", out config) &&
                    config.TryGetValue(key, out raw) && raw.ValueType == JsonValueType.Array)
                {
                    foreach (var item in raw.GetArray())
                        if (item.ValueType == JsonValueType.String && !string.IsNullOrWhiteSpace(item.GetString()))
                            result.Add(item.GetString().Trim());
                }
            }
            catch { }
            return result;
        }

        private static IList<IList<double>> ParseWidgetChartValues(string payload)
        {
            var output = new List<IList<double>> { new List<double>(), new List<double>(), new List<double>() };
            try
            {
                JsonObject root;
                IJsonValue statusRaw;
                if (!JsonObject.TryParse(payload ?? string.Empty, out root) ||
                    !root.TryGetValue("status", out statusRaw) || statusRaw.ValueType != JsonValueType.Array)
                    return output;
                var status = statusRaw.GetArray();
                foreach (var item in status)
                {
                    if (item.ValueType != JsonValueType.Object) continue;
                    var point = item.GetObject();
                    for (var i = 0; i < 3; i++)
                    {
                        IJsonValue value;
                        var key = "y" + (i + 1).ToString(CultureInfo.InvariantCulture);
                        if (point.TryGetValue(key, out value) && value.ValueType == JsonValueType.Number)
                            output[i].Add(value.GetNumber());
                    }
                }
            }
            catch { }
            return output;
        }

        private static void DrawWidgetChart(WidgetVisual visual, string payload)
        {
            var canvas = visual == null ? null : visual.ChartCanvas;
            if (canvas == null) return;
            canvas.Children.Clear();
            var width = 300.0;
            var height = 142.0;
            var values = ParseWidgetChartValues(payload);
            var series = values.Where(v => v.Count > 0).ToList();
            var pointCount = series.Count == 0 ? 0 : series.Max(v => v.Count);
            var seriesNames = GetWidgetConfigStringArray(visual.State, "series");
            if (visual.ChartMeta != null)
            {
                var label = "Точек: " + pointCount.ToString(CultureInfo.InvariantCulture);
                if (seriesNames.Count > 0)
                    label += "  •  " + string.Join("  •  ", seriesNames);
                visual.ChartMeta.Text = label;
            }
            if (visual.ChartFooter != null)
            {
                var definition = NormalizeType(visual.State == null ? string.Empty : visual.State.DefinitionName);
                switch (definition)
                {
                    case "chart1": visual.ChartFooter.Text = "График без точек"; break;
                    case "chart2": visual.ChartFooter.Text = "График с точками"; break;
                    case "chart3": visual.ChartFooter.Text = "Дневной"; break;
                    case "chart4": visual.ChartFooter.Text = "Часовой"; break;
                    case "chart5": visual.ChartFooter.Text = "Двойной"; break;
                    case "chart6": visual.ChartFooter.Text = "Тройной"; break;
                    default: visual.ChartFooter.Text = "График"; break;
                }
            }
            canvas.Children.Add(new Rectangle
            {
                Width = width, Height = height,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32)),
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 67, 77)),
                StrokeThickness = 1
            });
            for (var grid = 1; grid <= 3; grid++)
            {
                var y = 12 + (height - 24) * grid / 4.0;
                canvas.Children.Add(new Line
                {
                    X1 = 8, X2 = width - 8, Y1 = y, Y2 = y,
                    Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 58, 60, 68)),
                    StrokeThickness = 1
                });
            }
            if (series.Count == 0 || series.All(v => v.Count < 2))
            {
                var empty = new TextBlock
                {
                    Text = "Нет данных графика",
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)),
                    FontSize = 12
                };
                Canvas.SetLeft(empty, 92);
                Canvas.SetTop(empty, 62);
                canvas.Children.Add(empty);
                return;
            }

            var colors = new[]
            {
                Windows.UI.Color.FromArgb(255, 66, 165, 245),
                Windows.UI.Color.FromArgb(255, 102, 187, 106),
                Windows.UI.Color.FromArgb(255, 255, 179, 0)
            };
            var def = NormalizeType(visual.State == null ? string.Empty : visual.State.DefinitionName);
            var isBar = def == "chart3" || def == "chart4" ||
                string.Equals(GetWidgetConfigString(visual.State, "type"), "bar", StringComparison.OrdinalIgnoreCase);
            var seriesIndex = 0;
            foreach (var data in series)
            {
                var min = data.Min();
                var max = data.Max();
                if (Math.Abs(max - min) < 0.000001) max = min + 1;
                var color = colors[Math.Min(seriesIndex, colors.Length - 1)];
                if (isBar)
                {
                    var visible = data.Skip(Math.Max(0, data.Count - 42)).ToList();
                    var barWidth = (width - 24) / Math.Max(1, visible.Count);
                    for (var i = 0; i < visible.Count; i++)
                    {
                        var normalized = (visible[i] - min) / (max - min);
                        var barHeight = Math.Max(2, (height - 28) * normalized);
                        var bar = new Rectangle
                        {
                            Width = Math.Max(1, barWidth - 2), Height = barHeight,
                            Fill = new SolidColorBrush(color)
                        };
                        Canvas.SetLeft(bar, 12 + i * barWidth);
                        Canvas.SetTop(bar, height - 14 - barHeight);
                        canvas.Children.Add(bar);
                    }
                }
                else
                {
                    var line = new Polyline
                    {
                        Stroke = new SolidColorBrush(color),
                        StrokeThickness = 2.5,
                        StrokeLineJoin = PenLineJoin.Round
                    };
                    for (var i = 0; i < data.Count; i++)
                    {
                        var x = data.Count == 1 ? width / 2 : 10 + (width - 20) * i / (data.Count - 1);
                        var y = height - 12 - (height - 24) * (data[i] - min) / (max - min);
                        line.Points.Add(new Point(x, y));
                    }
                    canvas.Children.Add(line);
                    var showPoints = GetWidgetConfigNumber(visual.State, "pointRadius", 2) > 0 && def != "chart1";
                    if (showPoints)
                    {
                        for (var i = 0; i < data.Count; i++)
                        {
                            var x = data.Count == 1 ? width / 2 : 10 + (width - 20) * i / (data.Count - 1);
                            var y = height - 12 - (height - 24) * (data[i] - min) / (max - min);
                            var point = new Ellipse { Width = 5, Height = 5, Fill = new SolidColorBrush(color) };
                            Canvas.SetLeft(point, x - 2.5);
                            Canvas.SetTop(point, y - 2.5);
                            canvas.Children.Add(point);
                        }
                    }
                }
                seriesIndex++;
            }
        }

        private void UpdateWidgetVisual(WidgetState state)
        {
            if (state == null || string.IsNullOrEmpty(state.Id))
                return;

            var isNil = string.Equals(NormalizeType(state.DefinitionName), "nil", StringComparison.Ordinal);
            WidgetVisual visual;
            if (isNil)
            {
                if (_visuals.TryGetValue(state.Key, out visual))
                {
                    PageVisual page;
                    if (_pages.TryGetValue(visual.PageName, out page))
                        page.Content.Children.Remove(visual.Container);
                    _visuals.Remove(state.Key);
                    _widgetIndex.Remove(state.Key);
                    CleanupEmptyPages();
                }
                return;
            }

            if (!_visuals.TryGetValue(state.Key, out visual))
            {
                visual = CreateWidgetVisual(state);
                ApplyFontScaleToTree(visual.Container, _currentFontScale);
                _visuals[state.Key] = visual;
                _widgetIndex[state.Key] = state;
                InsertWidgetVisual(visual);
            }
            else
            {
                var definitionChanged = !string.Equals(visual.State.DefinitionName, state.DefinitionName, StringComparison.OrdinalIgnoreCase);
                var configChanged = !string.Equals(visual.State.ConfigJson ?? "{}", state.ConfigJson ?? "{}", StringComparison.Ordinal);
                if (definitionChanged || configChanged)
                {
                    PageVisual oldPage;
                    if (_pages.TryGetValue(visual.PageName, out oldPage))
                        oldPage.Content.Children.Remove(visual.Container);
                    _visuals.Remove(state.Key);

                    visual = CreateWidgetVisual(state);
                    ApplyFontScaleToTree(visual.Container, _currentFontScale);
                    _visuals[state.Key] = visual;
                    _widgetIndex[state.Key] = state;
                    InsertWidgetVisual(visual);
                    CleanupEmptyPages();
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
            }

            _syncingControls = true;
            try { ApplyValueToVisual(visual, state.Value); }
            finally { _syncingControls = false; }
            UpdateLastUpdatedLabel(visual);
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

        private void WidgetPages_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_restoringWidgetPageSelection) return;
            var selected = WidgetPages.SelectedItem as PivotItem;
            if (selected == null)
            {
                _selectedWidgetPageName = null;
                return;
            }

            _selectedWidgetPageName = selected.Tag as string;
            if (string.IsNullOrEmpty(_selectedWidgetPageName))
                _selectedWidgetPageName = selected.Header as string;
        }

        private void RestoreWidgetPageSelection(string preferredPageName)
        {
            if (WidgetPages.Items.Count == 0)
            {
                _selectedWidgetPageName = null;
                return;
            }

            PageVisual target = null;
            if (!string.IsNullOrEmpty(preferredPageName))
                _pages.TryGetValue(preferredPageName, out target);

            // Match Android dashboard behavior: return to the default page when
            // the active device page disappears, otherwise use the first page.
            if (target == null)
                _pages.TryGetValue("Основная", out target);
            if (target == null)
            {
                var firstItem = WidgetPages.Items[0] as PivotItem;
                if (firstItem != null)
                {
                    var firstName = firstItem.Tag as string;
                    if (!string.IsNullOrEmpty(firstName))
                        _pages.TryGetValue(firstName, out target);
                }
            }

            if (target == null) return;
            _restoringWidgetPageSelection = true;
            try { WidgetPages.SelectedItem = target.Item; }
            finally { _restoringWidgetPageSelection = false; }
            _selectedWidgetPageName = target.Name;
        }

        private PageVisual GetOrCreatePageVisual(string pageName)
        {
            PageVisual page;
            if (_pages.TryGetValue(pageName, out page))
                return page;

            var previouslySelected = _selectedWidgetPageName;
            var hadPages = WidgetPages.Items.Count > 0;
            page = new PageVisual
            {
                Name = pageName,
                // Keep a consistent inset between the page tabs and the widget cards.
                Content = new StackPanel { Margin = new Thickness(8, 10, 8, 12) }
            };

            var item = new PivotItem
            {
                Header = pageName,
                Content = new ScrollViewer
                {
                    Content = page.Content,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                },
                Tag = pageName
            };
            page.Item = item;
            _pages[pageName] = page;
            WidgetPages.Items.Add(item);
            if (DashboardEmptyOverlay != null) DashboardEmptyOverlay.Visibility = Visibility.Collapsed;

            // Adding a new device must not pull the user away from the page
            // they are currently viewing. The first page remains auto-selected.
            if (hadPages)
                RestoreWidgetPageSelection(previouslySelected);
            else
                RestoreWidgetPageSelection(pageName);
            return page;
        }

        private void CleanupEmptyPages()
        {
            var preferredPageName = _selectedWidgetPageName;
            var selectedItem = WidgetPages.SelectedItem as PivotItem;
            if (selectedItem != null)
            {
                var selectedName = selectedItem.Tag as string;
                if (!string.IsNullOrEmpty(selectedName))
                    preferredPageName = selectedName;
            }

            var empty = new List<string>();
            foreach (var pair in _pages)
                if (pair.Value.Content.Children.Count == 0) empty.Add(pair.Key);

            _restoringWidgetPageSelection = true;
            try
            {
                foreach (var name in empty)
                {
                    var page = _pages[name];
                    WidgetPages.Items.Remove(page.Item);
                    _pages.Remove(name);
                }
            }
            finally
            {
                _restoringWidgetPageSelection = false;
            }

            if (DashboardEmptyOverlay != null)
                DashboardEmptyOverlay.Visibility = WidgetPages.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            RestoreWidgetPageSelection(preferredPageName);
        }

        private void ApplyWidgetTitle(WidgetVisual visual, WidgetState state)
        {
            var title = GetTitle(state);
            string visibleTitle;
            var icon = CreateWidgetIcon(state, title, out visibleTitle);
            visual.Title.Text = visibleTitle;

            if (visual.Row != null)
            {
                if (visual.EmojiIcon != null)
                    visual.Row.Children.Remove(visual.EmojiIcon);

                visual.EmojiIcon = icon;
                if (icon != null)
                {
                    icon.VerticalAlignment = VerticalAlignment.Center;
                    icon.HorizontalAlignment = HorizontalAlignment.Center;
                    Grid.SetColumn(icon, 0);
                    visual.Row.Children.Insert(0, icon);
                }
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
            {
                visual.ValueText.Text = (string.IsNullOrEmpty(value) ? "—" : value) + (visual.State == null || string.IsNullOrWhiteSpace(visual.State.Unit) ? string.Empty : " " + visual.State.Unit);
                visual.ValueText.Foreground = new SolidColorBrush(GetWidgetValueColor(visual.State, value));
            }

            // Android widget definitions can provide either a fixed color or
            // threshold entries in "color". Apply the same resolved color to
            // numeric controls as well as the value text.
            var valueColor = GetWidgetValueColor(visual.State, value);
            var sliderColorTarget = visual.Control as Slider;
            if (sliderColorTarget != null)
                sliderColorTarget.Foreground = new SolidColorBrush(valueColor);
            var progressColorTarget = visual.Control as ProgressBar;
            if (progressColorTarget != null)
                progressColorTarget.Foreground = new SolidColorBrush(valueColor);

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
                {
                    var scale = GetWidgetConfigNumber(visual.State, "k", 1);
                    if (Math.Abs(scale) < 0.000001) scale = 1;
                    var display = number * scale;
                    slider.Value = Math.Max(slider.Minimum, Math.Min(slider.Maximum, display));
                }
                return;
            }

            var combo = visual.Control as ComboBox;
            if (combo != null)
            {
                int selected;
                if (int.TryParse((value ?? string.Empty).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out selected))
                {
                    if (selected >= 0 && selected < combo.Items.Count)
                        combo.SelectedIndex = selected;
                }
                else
                {
                    var incoming = (value ?? string.Empty).Trim();
                    for (var i = 0; i < combo.Items.Count; i++)
                    {
                        var option = combo.Items[i] as string;
                        if (string.Equals(option, incoming, StringComparison.OrdinalIgnoreCase))
                        {
                            combo.SelectedIndex = i;
                            break;
                        }
                    }
                }
                return;
            }

            var editor = visual.Control as TextBox;
            if (editor != null) { editor.Text = value ?? string.Empty; return; }

            var datePicker = visual.Control as DatePicker;
            if (datePicker != null)
            {
                DateTimeOffset date;
                if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out date))
                    datePicker.Date = date;
                return;
            }

            var timePicker = visual.Control as TimePicker;
            if (timePicker != null)
            {
                TimeSpan time;
                if (TimeSpan.TryParse(value, CultureInfo.CurrentCulture, out time))
                    timePicker.Time = time;
                return;
            }

            var gauge = visual.Control as Canvas;
            if (gauge != null)
            {
                var definition = NormalizeType(visual.State == null ? string.Empty : visual.State.DefinitionName);
                if (definition == "progressround" || definition == "fillgauge")
                    UpdateGaugeCanvas(gauge, visual.State, definition == "fillgauge");
            }

            if (visual.ChartCanvas != null)
                DrawWidgetChart(visual, value);
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
                if (visual == null || visual.State == null)
                    return;

                var state = visual.State;
                var type = NormalizeType(state.WidgetType);
                var definition = NormalizeType(state.DefinitionName);
                var isControlCommand =
                    type == "toggle" || type == "switch" ||
                    type == "button" || type == "action" ||
                    type == "vbtn" || type == "btn" || type == "buttonout" ||
                    definition == "toggle" || definition == "button" ||
                    definition == "vbtn" || definition == "btn";

                if (isControlCommand)
                {
                    await _mqtt.PublishControlAsync(state.DeviceId, state.Id, value);
                    Log("TX control " + state.DeviceId + "/" + state.Id + " = " + value);
                }
                else if (!string.IsNullOrWhiteSpace(state.Topic))
                {
                    await _mqtt.PublishWidgetAsync(state.Topic, value);
                    Log("TX widget " + state.Topic + " = " + value);
                }
                else
                {
                    Log("MQTT CONFIG не содержит topic для " + state.DeviceId + "/" + state.Id);
                }
            }
            catch (Exception ex)
            {
                Log("ERROR control: " + ex.Message);
            }
        }

        private void HistoryWidgetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selected = HistoryWidgetList == null ? null : HistoryWidgetList.SelectedItem as ListViewItem;
            var key = selected == null ? null : selected.Tag as string;
            if (string.IsNullOrWhiteSpace(key) || !ContainsComboValue(GraphWidgetBox, key)) return;
            GraphWidgetBox.SelectedItem = key;
            BuildGraphButton_Click(this, null);
        }

        private void AddHistoryWidgetItem(WidgetState state)
        {
            if (HistoryWidgetList == null || state == null) return;
            for (var i = 0; i < HistoryWidgetList.Items.Count; i++)
            {
                var existing = HistoryWidgetList.Items[i] as ListViewItem;
                if (existing != null && string.Equals(existing.Tag as string, state.Key, StringComparison.Ordinal))
                    return;
            }

            var body = new StackPanel { Margin = new Thickness(2, 3, 2, 3) };
            body.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(state.Description) ? state.Id : state.Description,
                FontSize = 15,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Colors.White)
            });
            body.Children.Add(new TextBlock
            {
                Text = (state.Value ?? "—") + (state.Unit ?? string.Empty) + "   •   " + (state.Page ?? state.DeviceId),
                FontSize = 12,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184))
            });
            HistoryWidgetList.Items.Add(new ListViewItem { Content = body, Tag = state.Key });
        }

        private void ToggleGraphSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (GraphSettingsCard == null) return;
            GraphSettingsCard.Visibility = GraphSettingsCard.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private static bool IsNumericHistoryWidget(WidgetState state)
        {
            if (state == null) return false;
            var type = NormalizeType(state.WidgetType);
            var definition = NormalizeType(state.DefinitionName);
            if (type == "value" || type == "status" || type == "number" || type == "numeric")
                return true;
            return definition.StartsWith("chart", StringComparison.Ordinal) ||
                   definition.StartsWith("anydata", StringComparison.Ordinal) ||
                   definition == "value" || definition == "fillgauge" ||
                   definition == "progressline" || definition == "progressround";
        }

        private void UpdateGraphWidget(WidgetState state)
        {
            if (state == null || !_widgetIndex.ContainsKey(state.Key) || !IsNumericHistoryWidget(state)) return;
            if (!ContainsComboValue(GraphWidgetBox, state.Key))
            {
                GraphWidgetBox.Items.Add(state.Key);
                if (GraphWidgetBox.SelectedIndex < 0)
                    GraphWidgetBox.SelectedIndex = 0;
            }
            AddHistoryWidgetItem(state);
        }

        private void RefreshGraphWidgets()
        {
            var selected = GraphWidgetBox.SelectedItem as string;
            GraphWidgetBox.Items.Clear();
            if (HistoryWidgetList != null) HistoryWidgetList.Items.Clear();

            foreach (var state in _devices.Widgets.OrderBy(delegate(WidgetState s) { return s.Page; }).ThenBy(delegate(WidgetState s) { return s.Order; }))
            {
                _widgetIndex[state.Key] = state;
                if (IsNumericHistoryWidget(state))
                {
                    GraphWidgetBox.Items.Add(state.Key);
                    AddHistoryWidgetItem(state);
                }
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

        private async void SaveWidgetIntervalButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var key = GraphWidgetBox.SelectedItem as string;
                if (string.IsNullOrWhiteSpace(key)) { WidgetIntervalStatus.Text = "Сначала выберите датчик."; return; }
                int seconds;
                if (!int.TryParse((WidgetIntervalBox.Text ?? string.Empty).Trim(), out seconds) || seconds < 1)
                { WidgetIntervalStatus.Text = "Введите интервал не меньше 1 секунды."; return; }
                if (_appSettings.WidgetMeasurementIntervals == null)
                    _appSettings.WidgetMeasurementIntervals = new Dictionary<string, int>();
                _appSettings.WidgetMeasurementIntervals[key] = seconds;
                await _settingsStore.SaveAsync(_appSettings);
                WidgetIntervalStatus.Text = "Для " + key + " установлен интервал " + seconds + " сек.";
                Log("HISTORY INTERVAL: " + key + " = " + seconds + "s");
            }
            catch (Exception ex) { WidgetIntervalStatus.Text = "Ошибка сохранения: " + ex.Message; Log("HISTORY INTERVAL ERROR: " + ex.Message); }
        }

        private Tuple<DateTimeOffset, DateTimeOffset> GetSelectedGraphRange()
        {
            var now = DateTimeOffset.Now;
            var selected = GraphPeriodBox == null ? null : GraphPeriodBox.SelectedItem as ComboBoxItem;
            var tag = selected == null ? "today" : (selected.Tag as string ?? "today");
            DateTimeOffset from;
            switch (tag)
            {
                case "1h": from = now.AddHours(-1); break;
                case "6h": from = now.AddHours(-6); break;
                case "12h": from = now.AddHours(-12); break;
                case "3d": from = now.AddDays(-3); break;
                case "7d": from = now.AddDays(-7); break;
                case "30d": from = now.AddDays(-30); break;
                case "all": from = DateTimeOffset.MinValue; break;
                case "custom":
                    from = new DateTimeOffset(GraphFrom.Date.Date);
                    now = new DateTimeOffset(GraphTo.Date.Date.AddDays(1).AddTicks(-1));
                    if (now < from) now = from.AddDays(1).AddTicks(-1);
                    break;
                default: from = now.Date; break;
            }
            return Tuple.Create(from, now);
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
                var range = GetSelectedGraphRange();
                var from = range.Item1;
                var to = range.Item2;

                var points = await _measurementStore.ReadAsync(state.DeviceId, state.Id, from, to);
                _lastGraphPoints.Clear();
                foreach (var p in points) _lastGraphPoints.Add(p);

                DrawGraph(_lastGraphPoints);
                GraphSummary.Text = points.Count < 2
                    ? "Для построения графика нужно минимум два сохранённых числовых измерения. Сейчас: " + points.Count.ToString(CultureInfo.InvariantCulture) + ". Проверьте интервал записи и выберите более широкий период."
                    : (state.Description ?? key) + " • " + points.Count.ToString(CultureInfo.InvariantCulture) +
                      " точек, " + from.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) +
                      " — " + to.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                GraphSummary.Text = "Ошибка: " + ex.Message;
                Log("GRAPH ERROR: " + ex.Message);
            }
        }

        private Windows.UI.Color GetCompareWidgetColor(string key, int fallbackIndex)
        {
            string colorName;
            if (_appSettings.GraphWidgetColors != null &&
                _appSettings.GraphWidgetColors.TryGetValue(key, out colorName))
            {
                switch (colorName)
                {
                    case "Orange": return Windows.UI.Colors.Orange;
                    case "Green": return Windows.UI.Colors.LimeGreen;
                    case "Magenta": return Windows.UI.Colors.Magenta;
                    case "Cyan": return Windows.UI.Colors.Cyan;
                    case "Yellow": return Windows.UI.Colors.Yellow;
                    case "Red": return Windows.UI.Colors.Red;
                    case "Purple": return Windows.UI.Colors.MediumPurple;
                    default: return Windows.UI.Colors.DodgerBlue;
                }
            }
            var palette = new Windows.UI.Color[] {
                Windows.UI.Colors.DodgerBlue, Windows.UI.Colors.Orange,
                Windows.UI.Colors.LimeGreen, Windows.UI.Colors.Magenta,
                Windows.UI.Colors.Cyan, Windows.UI.Colors.Yellow
            };
            return palette[fallbackIndex % palette.Length];
        }

        private async void ApplyCompareColorButton_Click(object sender, RoutedEventArgs e)
        {
            var key = GraphCompareList.SelectedItem as string;
            var item = GraphColorBox.SelectedItem as ComboBoxItem;
            if (string.IsNullOrWhiteSpace(key))
            {
                GraphSummary.Text = "Сначала выберите датчик в списке сравнения.";
                return;
            }
            if (item == null || item.Tag == null) return;
            if (_appSettings.GraphWidgetColors == null)
                _appSettings.GraphWidgetColors = new Dictionary<string, string>();
            _appSettings.GraphWidgetColors[key] = item.Tag.ToString();
            await _settingsStore.SaveAsync(_appSettings);
            GraphSummary.Text = "Цвет сохранён для датчика: " + key;
            BuildCompareGraphButton_Click(null, null);
        }

        private void RemoveCompareWidgetButton_Click(object sender, RoutedEventArgs e)
        {
            var key = GraphCompareList.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(key)) { GraphSummary.Text = "Выберите датчик в списке."; return; }
            _graphCompareKeys.Remove(key);
            GraphCompareList.Items.Remove(key);
            GraphCanvas.Children.Clear();
            GraphSummary.Text = "Удалён из сравнения: " + key;
        }

        private async void SaveGraphSetButton_Click(object sender, RoutedEventArgs e)
        {
            var name = (GraphSetNameBox.Text ?? string.Empty).Trim();
            if (name.Length == 0 || _graphCompareKeys.Count == 0) { GraphSummary.Text = "Введите название и добавьте датчики."; return; }
            if (_appSettings.SavedGraphSets == null) _appSettings.SavedGraphSets = new List<SavedGraphSet>();
            var existing = _appSettings.SavedGraphSets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing == null) { existing = new SavedGraphSet { Name = name }; _appSettings.SavedGraphSets.Add(existing); }
            existing.WidgetKeys = _graphCompareKeys.ToList();
            await _settingsStore.SaveAsync(_appSettings);
            RefreshGraphSetPicker();
            GraphSavedSetsBox.SelectedItem = name;
            GraphSummary.Text = "Набор сохранён: " + name;
        }

        private void RefreshGraphSetPicker()
        {
            if (GraphSavedSetsBox == null) return;
            GraphSavedSetsBox.Items.Clear();
            foreach (var set in (_appSettings.SavedGraphSets ?? new List<SavedGraphSet>()).OrderBy(s => s.Name))
                GraphSavedSetsBox.Items.Add(set.Name);
        }

        private async void LoadGraphSetButton_Click(object sender, RoutedEventArgs e)
        {
            var name = GraphSavedSetsBox.SelectedItem as string;
            var set = (_appSettings.SavedGraphSets ?? new List<SavedGraphSet>()).FirstOrDefault(s => s.Name == name);
            if (set == null) { GraphSummary.Text = "Выберите сохранённый набор."; return; }
            _graphCompareKeys.Clear(); GraphCompareList.Items.Clear();
            foreach (var key in set.WidgetKeys ?? new List<string>())
                if (_widgetIndex.ContainsKey(key)) { _graphCompareKeys.Add(key); GraphCompareList.Items.Add(key); }
            GraphSetNameBox.Text = set.Name;
            GraphSummary.Text = "Загружен набор: " + set.Name;
        }

        private async void DeleteGraphSetButton_Click(object sender, RoutedEventArgs e)
        {
            var name = GraphSavedSetsBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(name)) { GraphSummary.Text = "Выберите набор для удаления."; return; }
            var set = (_appSettings.SavedGraphSets ?? new List<SavedGraphSet>()).FirstOrDefault(s => s.Name == name);
            if (set != null) _appSettings.SavedGraphSets.Remove(set);
            await _settingsStore.SaveAsync(_appSettings);
            RefreshGraphSetPicker();
            GraphSummary.Text = "Удалён набор: " + name;
        }

        private void SelectGraphPeriod(string tag)
        {
            if (GraphPeriodBox == null) return;
            for (var i = 0; i < GraphPeriodBox.Items.Count; i++)
            {
                var item = GraphPeriodBox.Items[i] as ComboBoxItem;
                if (item != null && string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
                {
                    GraphPeriodBox.SelectedItem = item;
                    return;
                }
            }
        }

        private void GraphPresetDayButton_Click(object sender, RoutedEventArgs e) { SelectGraphPeriod("today"); }
        private void GraphPresetWeekButton_Click(object sender, RoutedEventArgs e) { SelectGraphPeriod("7d"); }
        private void GraphPresetMonthButton_Click(object sender, RoutedEventArgs e) { SelectGraphPeriod("30d"); }

        private void AddCompareWidgetButton_Click(object sender, RoutedEventArgs e)
        {
            var key = GraphWidgetBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(key)) return;
            if (!_graphCompareKeys.Contains(key))
            {
                _graphCompareKeys.Add(key);
                GraphCompareList.Items.Add(key);
            }
        }

        private void ClearCompareWidgetsButton_Click(object sender, RoutedEventArgs e)
        {
            _graphCompareKeys.Clear();
            GraphCompareList.Items.Clear();
            GraphCanvas.Children.Clear();
            GraphSummary.Text = "Список сравнения очищен.";
        }

        private async void BuildCompareGraphButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_graphCompareKeys.Count == 0)
                {
                    GraphSummary.Text = "Добавьте датчики в список сравнения.";
                    return;
                }
                var range = GetSelectedGraphRange();
                var from = range.Item1;
                var to = range.Item2;
                var traces = new List<Tuple<string, IList<MeasurementPoint>>>();
                foreach (var key in _graphCompareKeys.ToList())
                {
                    WidgetState state;
                    if (!_widgetIndex.TryGetValue(key, out state)) continue;
                    var points = await _measurementStore.ReadAsync(state.DeviceId, state.Id, from, to);
                    traces.Add(Tuple.Create(key, (IList<MeasurementPoint>)points));
                }
                DrawCompareGraph(traces);
                GraphSummary.Text = "Сравнение: " + traces.Count.ToString(CultureInfo.InvariantCulture) +
                    " датчиков • " + from.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) +
                    " — " + to.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) +
                    ". Каждая линия нормирована по собственному диапазону.";
            }
            catch (Exception ex)
            {
                GraphSummary.Text = "Ошибка сравнения: " + ex.Message;
                Log("COMPARE GRAPH ERROR: " + ex.Message);
            }
        }

        // Each sensor receives its own compact chart panel and its own Y scale.
        // This avoids misleading comparisons between different units (for example °C and %).
        private void DrawCompareGraph(IList<Tuple<string, IList<MeasurementPoint>>> traces)
        {
            GraphCanvas.Children.Clear();
            _graphNumericPoints.Clear();
            _graphPointXPositions.Clear();


            var prepared = new List<Tuple<string, List<Tuple<DateTimeOffset, double>>>>();
            foreach (var trace in traces)
            {
                var values = trace.Item2
                    .Select(p =>
                    {
                        double value;
                        return double.TryParse((p.Value ?? string.Empty).Replace(',', '.'),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                            ? Tuple.Create(p.Timestamp, value) : null;
                    })
                    .Where(p => p != null)
                    .OrderBy(p => p.Item1)
                    .ToList();
                if (values.Count >= 2)
                    prepared.Add(Tuple.Create(trace.Item1, values));
            }

            if (prepared.Count == 0)
            {
                GraphSummary.Text = "Недостаточно числовых данных для сравнения.";
                GraphCanvas.Width = 280;
                GraphCanvas.Height = 120;
                return;
            }

            var allTimes = prepared.SelectMany(t => t.Item2).Select(p => p.Item1).ToList();
            var minTime = allTimes.Min();
            var maxTime = allTimes.Max();
            var timeSpan = Math.Max(1.0, (maxTime - minTime).TotalSeconds);
            var zoom = Math.Max(1.0, Math.Min(20.0, _graphZoom));
            var width = Math.Max(300.0, ActualWidth > 0 ? ActualWidth - 48.0 : 680.0) * zoom;
            const double panelHeight = 190.0;
            const double left = 54.0;
            const double right = 16.0;
            const double top = 28.0;
            const double bottom = 28.0;
            var height = panelHeight * prepared.Count + 8.0;
            GraphCanvas.Width = width;
            GraphCanvas.Height = height;
            GraphHorizontalScroll.Height = height + 12.0;

            for (var panel = 0; panel < prepared.Count; panel++)
            {
                var trace = prepared[panel];
                var values = trace.Item2;
                var panelTop = panel * panelHeight;
                var plotTop = panelTop + top;
                var plotBottom = panelTop + panelHeight - bottom;
                var plotHeight = plotBottom - plotTop;
                var low = values.Min(p => p.Item2);
                var high = values.Max(p => p.Item2);
                if (Math.Abs(high - low) < 0.000001) high = low + 1.0;
                var brush = new SolidColorBrush(GetCompareWidgetColor(trace.Item1, panel));

                var title = new TextBlock
                {
                    Text = trace.Item1 + "   " + low.ToString("0.##", CultureInfo.InvariantCulture) +
                           " … " + high.ToString("0.##", CultureInfo.InvariantCulture),
                    Foreground = brush,
                    FontSize = 13,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold
                };
                Canvas.SetLeft(title, left);
                Canvas.SetTop(title, panelTop + 3);
                GraphCanvas.Children.Add(title);

                for (var grid = 0; grid <= 2; grid++)
                {
                    var y = plotTop + plotHeight * grid / 2.0;
                    GraphCanvas.Children.Add(new Line
                    {
                        X1 = left, Y1 = y, X2 = width - right, Y2 = y,
                        Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 58, 60, 70)),
                        StrokeThickness = 1
                    });
                }

                var maxX = width - right;
                for (var i = 1; i < values.Count; i++)
                {
                    var x1 = left + ((values[i - 1].Item1 - minTime).TotalSeconds / timeSpan) * (maxX - left);
                    var x2 = left + ((values[i].Item1 - minTime).TotalSeconds / timeSpan) * (maxX - left);
                    var y1 = plotTop + (1.0 - (values[i - 1].Item2 - low) / (high - low)) * plotHeight;
                    var y2 = plotTop + (1.0 - (values[i].Item2 - low) / (high - low)) * plotHeight;
                    GraphCanvas.Children.Add(new Line
                    {
                        X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                        Stroke = brush, StrokeThickness = 2.5
                    });
                }

                var highLabel = new TextBlock
                {
                    Text = high.ToString("0.##", CultureInfo.InvariantCulture),
                    Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                    FontSize = 10
                };
                var lowLabel = new TextBlock
                {
                    Text = low.ToString("0.##", CultureInfo.InvariantCulture),
                    Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                    FontSize = 10
                };
                Canvas.SetLeft(highLabel, 3);
                Canvas.SetTop(highLabel, plotTop - 5);
                Canvas.SetLeft(lowLabel, 3);
                Canvas.SetTop(lowLabel, plotBottom - 7);
                GraphCanvas.Children.Add(highLabel);
                GraphCanvas.Children.Add(lowLabel);

                // Four readable time labels are shared by all panels.
                for (var tick = 0; tick <= 3; tick++)
                {
                    var fraction = tick / 3.0;
                    var x = left + fraction * (maxX - left);
                    var stamp = minTime.AddSeconds(timeSpan * fraction);
                    var format = timeSpan >= 86400.0 ? "dd.MM HH:mm" : "HH:mm";
                    var timeLabel = new TextBlock
                    {
                        Text = stamp.ToLocalTime().ToString(format, CultureInfo.InvariantCulture),
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)),
                        FontSize = 10
                    };
                    Canvas.SetLeft(timeLabel, Math.Max(left, Math.Min(maxX - 65, x - 24)));
                    Canvas.SetTop(timeLabel, plotBottom + 4);
                    GraphCanvas.Children.Add(timeLabel);
                }

                if (panel < prepared.Count - 1)
                {
                    GraphCanvas.Children.Add(new Line
                    {
                        X1 = 0, Y1 = panelTop + panelHeight - 1,
                        X2 = width, Y2 = panelTop + panelHeight - 1,
                        Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 75, 77, 88)),
                        StrokeThickness = 1
                    });
                }
            }

            if (GraphZoomLabel != null)
                GraphZoomLabel.Text = "×" + _graphZoom.ToString("0.0", new CultureInfo("ru-RU"));
        }

        private void DrawGraph(IList<MeasurementPoint> points)
        {
            GraphCanvas.Children.Clear();
            _graphNumericPoints.Clear();
            _graphPointXPositions.Clear();
            if (points == null || points.Count < 2) return;

            var numeric = new List<Tuple<double, double>>();
            DateTimeOffset minTime = DateTimeOffset.MaxValue;
            DateTimeOffset maxTime = DateTimeOffset.MinValue;
            double minValue = double.MaxValue;
            double maxValue = double.MinValue;

            foreach (var p in points)
            {
                double value;
                if (!double.TryParse((p.Value ?? string.Empty).Replace(',', '.'),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    continue;
                numeric.Add(Tuple.Create(0.0, value));
                _graphNumericPoints.Add(p);
                minTime = p.Timestamp < minTime ? p.Timestamp : minTime;
                maxTime = p.Timestamp > maxTime ? p.Timestamp : maxTime;
                minValue = Math.Min(minValue, value);
                maxValue = Math.Max(maxValue, value);
            }

            if (numeric.Count < 2)
            {
                GraphCanvas.Width = Math.Max(280.0, ActualWidth - 40.0);
                GraphCanvas.Height = 120;
                var empty = new TextBlock { Text = "Нужно минимум два числовых измерения для графика.", Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)), FontSize = 13, TextWrapping = TextWrapping.Wrap, Width = 260 };
                Canvas.SetLeft(empty, 10); Canvas.SetTop(empty, 42); GraphCanvas.Children.Add(empty);
                if (GraphSelectedPoint != null) GraphSelectedPoint.Text = "Нет достаточного количества точек";
                return;
            }
            if (Math.Abs(maxValue - minValue) < 0.000001) maxValue = minValue + 1;

            var timeSpan = Math.Max(1.0, (maxTime - minTime).TotalSeconds);
            for (var i = 0; i < _graphNumericPoints.Count; i++)
            {
                var seconds = (_graphNumericPoints[i].Timestamp - minTime).TotalSeconds;
                numeric[i] = Tuple.Create(seconds, numeric[i].Item2);
            }

            var baseWidth = Math.Max(280.0, ActualWidth > 0 ? ActualWidth - 40.0 : 680.0);
            var width = baseWidth * Math.Max(1.0, Math.Min(20.0, _graphZoom));
            GraphCanvas.Width = width;
            GraphCanvas.Height = 300;
            GraphHorizontalScroll.Height = 320;
            var height = 300.0;
            var padding = 24.0;

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

            for (var tick = 0; tick <= 4; tick++)
            {
                var fraction = tick / 4.0;
                var stamp = minTime.AddSeconds(timeSpan * fraction).ToLocalTime();
                var label = new TextBlock { Text = stamp.ToString(timeSpan >= 86400 ? "dd.MM HH:mm" : "HH:mm", CultureInfo.InvariantCulture), Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)), FontSize = 10 };
                Canvas.SetLeft(label, Math.Max(padding, Math.Min(width - padding - 58, padding + fraction * (width - padding * 2) - 24)));
                Canvas.SetTop(label, height - 18);
                GraphCanvas.Children.Add(label);
            }

            GraphCanvas.Children.Add(new Line
            {
                X1 = padding, Y1 = height - padding,
                X2 = width - padding, Y2 = height - padding,
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 130, 132, 145)),
                StrokeThickness = 1
            });

            for (var i = 0; i < numeric.Count; i++)
            {
                var x = padding + (numeric[i].Item1 / timeSpan) * (width - padding * 2);
                _graphPointXPositions.Add(x);
                if (i == 0) continue;

                var previous = numeric[i - 1];
                var current = numeric[i];
                var x1 = padding + (previous.Item1 / timeSpan) * (width - padding * 2);
                var y1 = padding + (1 - (previous.Item2 - minValue) / (maxValue - minValue)) * (height - padding * 2);
                var y2 = padding + (1 - (current.Item2 - minValue) / (maxValue - minValue)) * (height - padding * 2);
                GraphCanvas.Children.Add(new Line
                {
                    X1 = x1, Y1 = y1, X2 = x, Y2 = y1,
                    Stroke = new SolidColorBrush(Windows.UI.Colors.DodgerBlue), StrokeThickness = 2
                });
                GraphCanvas.Children.Add(new Line
                {
                    X1 = x, Y1 = y1, X2 = x, Y2 = y2,
                    Stroke = new SolidColorBrush(Windows.UI.Colors.DodgerBlue), StrokeThickness = 2
                });
            }

            var high = new TextBlock { Text = maxValue.ToString("0.##", CultureInfo.InvariantCulture) };
            var low = new TextBlock { Text = minValue.ToString("0.##", CultureInfo.InvariantCulture) };
            Canvas.SetLeft(high, 2); Canvas.SetTop(high, 4);
            Canvas.SetLeft(low, 2); Canvas.SetTop(low, height - 30);
            GraphCanvas.Children.Add(high);
            GraphCanvas.Children.Add(low);

            if (_graphSelectedIndex < 0 || _graphSelectedIndex >= _graphNumericPoints.Count)
                _graphSelectedIndex = _graphNumericPoints.Count - 1;
            DrawSelectedGraphPoint(numeric, minValue, maxValue, height, padding);
            if (GraphZoomLabel != null)
                GraphZoomLabel.Text = "×" + _graphZoom.ToString("0.0", new CultureInfo("ru-RU"));
        }

        private void DrawSelectedGraphPoint(IList<Tuple<double, double>> numeric, double minValue, double maxValue, double height, double padding)
        {
            if (_graphSelectedIndex < 0 || _graphSelectedIndex >= numeric.Count) return;
            var width = GraphCanvas.Width;
            var timeSpan = Math.Max(1.0, (numeric[numeric.Count - 1].Item1 - numeric[0].Item1));
            var point = numeric[_graphSelectedIndex];
            var x = padding + (point.Item1 / timeSpan) * (width - padding * 2);
            var y = padding + (1 - (point.Item2 - minValue) / (maxValue - minValue)) * (height - padding * 2);
            var marker = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                Stroke = new SolidColorBrush(Windows.UI.Colors.White),
                StrokeThickness = 1
            };
            Canvas.SetLeft(marker, x - 6);
            Canvas.SetTop(marker, y - 6);
            GraphCanvas.Children.Add(new Line
            {
                X1 = x, Y1 = padding, X2 = x, Y2 = height - padding,
                Stroke = new SolidColorBrush(Windows.UI.Colors.Red), StrokeThickness = 2
            });
            GraphCanvas.Children.Add(marker);
            if (GraphSelectedPoint != null && _graphSelectedIndex < _graphNumericPoints.Count)
            {
                var selected = _graphNumericPoints[_graphSelectedIndex];
                GraphSelectedPoint.Text = selected.Timestamp.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture) +
                    "   •   " + selected.Value + " " + (_widgetIndex.ContainsKey(selected.DeviceId + "/" + selected.WidgetId) ? (_widgetIndex[selected.DeviceId + "/" + selected.WidgetId].Unit ?? string.Empty) : string.Empty);
            }
        }

        private void GraphCanvas_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            var scale = e.Delta.Scale.X;
            if (scale > 0.01 && Math.Abs(scale - 1.0) > 0.005)
            {
                var oldZoom = _graphZoom;
                var newZoom = Math.Max(1.0, Math.Min(20.0, oldZoom * scale));
                if (Math.Abs(newZoom - oldZoom) > 0.001)
                {
                    var centerX = e.Position.X;
                    var oldOffset = GraphHorizontalScroll.HorizontalOffset;
                    var ratio = newZoom / oldZoom;
                    _graphZoom = newZoom;
                    DrawGraph(_lastGraphPoints);
                    var newOffset = (centerX + oldOffset) * ratio - centerX;
                    var maxOffset = Math.Max(0.0, GraphCanvas.Width - GraphHorizontalScroll.ViewportWidth);
                    GraphHorizontalScroll.ScrollToHorizontalOffset(Math.Max(0.0, Math.Min(maxOffset, newOffset)));
                }
            }

            if (Math.Abs(e.Delta.Translation.X) > 0.5 && _graphZoom > 1.0)
            {
                var maxOffset = Math.Max(0.0, GraphCanvas.Width - GraphHorizontalScroll.ViewportWidth);
                var offset = GraphHorizontalScroll.HorizontalOffset - e.Delta.Translation.X;
                GraphHorizontalScroll.ScrollToHorizontalOffset(Math.Max(0.0, Math.Min(maxOffset, offset)));
            }

            e.Handled = true;
        }

        private void GraphCanvas_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (_graphPointXPositions.Count == 0 || _graphNumericPoints.Count == 0) return;
            var x = e.GetPosition(GraphCanvas).X;
            var bestIndex = 0;
            var bestDistance = double.MaxValue;
            for (var i = 0; i < _graphPointXPositions.Count; i++)
            {
                var distance = Math.Abs(_graphPointXPositions[i] - x);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }
            _graphSelectedIndex = bestIndex;
            DrawGraph(_lastGraphPoints);
            var selected = _graphNumericPoints[bestIndex];
            GraphSummary.Text = "Выбрано: " +
                selected.Timestamp.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture) +
                " • " + selected.Value;
        }

        private void ZoomInGraphButton_Click(object sender, RoutedEventArgs e)
        {
            _graphZoom = Math.Min(20.0, _graphZoom * 1.5);
            DrawGraph(_lastGraphPoints);
        }

        private void ZoomOutGraphButton_Click(object sender, RoutedEventArgs e)
        {
            _graphZoom = Math.Max(1.0, _graphZoom / 1.5);
            DrawGraph(_lastGraphPoints);
        }

        private void ResetGraphZoomButton_Click(object sender, RoutedEventArgs e)
        {
            _graphZoom = 1.0;
            _graphSelectedIndex = -1;
            DrawGraph(_lastGraphPoints);
            if (GraphZoomLabel != null) GraphZoomLabel.Text = "×1,0";
        }

        private async void ExportCompareWordButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_graphCompareKeys.Count == 0)
                {
                    GraphSummary.Text = "Сначала добавьте датчики в список сравнения.";
                    return;
                }

                var range = GetSelectedGraphRange();
                var from = range.Item1;
                var to = range.Item2;
                var traces = new List<Tuple<string, IList<MeasurementPoint>>>();
                foreach (var key in _graphCompareKeys.ToList())
                {
                    WidgetState state;
                    if (!_widgetIndex.TryGetValue(key, out state)) continue;
                    var points = await _measurementStore.ReadAsync(state.DeviceId, state.Id, from, to);
                    traces.Add(Tuple.Create(key, (IList<MeasurementPoint>)points));
                }

                if (traces.Count == 0 || !traces.Any(t => t.Item2 != null && t.Item2.Count > 0))
                {
                    GraphSummary.Text = "Нет данных для экспорта.";
                    return;
                }

                var name = "ESP32-сравнение-" + from.ToString("yyyyMMdd", CultureInfo.InvariantCulture) +
                           "-" + to.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".doc";
                await _exporter.ExportComparisonAsync(traces, from, to, name);
                GraphSummary.Text = "Сравнение экспортировано в Word: " + name;
                Log("COMPARE WORD EXPORTED: " + name);
            }
            catch (Exception ex)
            {
                GraphSummary.Text = "Ошибка экспорта сравнения: " + ex.Message;
                Log("COMPARE WORD EXPORT ERROR: " + ex.Message);
            }
        }

        private async void ExportCompareCsvButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_graphCompareKeys.Count == 0)
                {
                    GraphSummary.Text = "Сначала добавьте датчики в список сравнения.";
                    return;
                }

                var range = GetSelectedGraphRange();
                var from = range.Item1;
                var to = range.Item2;
                var rows = new SortedDictionary<DateTimeOffset, Dictionary<string, string>>();
                var exportKeys = new List<string>();

                foreach (var key in _graphCompareKeys.ToList())
                {
                    WidgetState state;
                    if (!_widgetIndex.TryGetValue(key, out state)) continue;
                    exportKeys.Add(key);
                    var points = await _measurementStore.ReadAsync(state.DeviceId, state.Id, from, to);
                    foreach (var point in points)
                    {
                        Dictionary<string, string> values;
                        if (!rows.TryGetValue(point.Timestamp, out values))
                        {
                            values = new Dictionary<string, string>();
                            rows.Add(point.Timestamp, values);
                        }
                        values[key] = point.Value ?? string.Empty;
                    }
                }

                if (exportKeys.Count == 0 || rows.Count == 0)
                {
                    GraphSummary.Text = "Нет данных для экспорта.";
                    return;
                }

                var builder = new System.Text.StringBuilder();
                builder.Append(EscapeCsvField("Дата и время"));
                foreach (var key in exportKeys)
                {
                    builder.Append(';');
                    builder.Append(EscapeCsvField(key));
                }
                builder.AppendLine();

                foreach (var row in rows)
                {
                    builder.Append(EscapeCsvField(row.Key.ToLocalTime().ToString(
                        "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
                    foreach (var key in exportKeys)
                    {
                        string value;
                        builder.Append(';');
                        builder.Append(EscapeCsvField(row.Value.TryGetValue(key, out value) ? value : string.Empty));
                    }
                    builder.AppendLine();
                }

                var picker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = "ESP32-сравнение-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)
                };
                picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
                var file = await picker.PickSaveFileAsync();
                if (file == null)
                {
                    GraphSummary.Text = "Экспорт отменён.";
                    return;
                }

                await FileIO.WriteTextAsync(file, builder.ToString(), Windows.Storage.Streams.UnicodeEncoding.Utf8);
                GraphSummary.Text = "CSV сохранён: " + file.Name + " • датчиков: " +
                    exportKeys.Count.ToString(CultureInfo.InvariantCulture) +
                    " • строк: " + rows.Count.ToString(CultureInfo.InvariantCulture);
                Log("COMPARE CSV EXPORTED: " + file.Name);
            }
            catch (Exception ex)
            {
                GraphSummary.Text = "Ошибка CSV-экспорта: " + ex.Message;
                Log("COMPARE CSV EXPORT ERROR: " + ex.Message);
            }
        }

        private static string EscapeCsvField(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }

        private async void ExportGraphButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var key = GraphWidgetBox.SelectedItem as string;
                if (string.IsNullOrEmpty(key)) return;

                var range = GetSelectedGraphRange();
                var from = range.Item1;
                var to = range.Item2;
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
                    FontSize = 12,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                var sourceLabel = new TextBlock
                {
                    Text = string.Equals(item.Origin, "Voice", StringComparison.OrdinalIgnoreCase)
                        ? "МАРФА"
                        : "ПЛАНИРОВЩИК",
                    FontSize = 10,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)),
                    Margin = new Thickness(0, 0, 0, 4)
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
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)),
                    TextWrapping = TextWrapping.Wrap
                };
                var rowContent = new StackPanel
                {
                    Margin = new Thickness(10)
                };
                rowContent.Children.Add(sourceLabel);
                rowContent.Children.Add(statusLabel);
                rowContent.Children.Add(detailsLabel);

                var rowCard = new Border
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 43, 44, 52)),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 67, 77)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(14),
                    Margin = new Thickness(2, 3, 2, 6),
                    Child = rowContent
                };
                ScheduleList.Items.Add(new ListViewItem
                {
                    Content = rowCard,
                    Tag = item.Id
                });
            }
            ApplyFontScaleToTree(ScheduleList, _currentFontScale);
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
                    if (_trainingTarget != null)
                    {
                        if (_trainingPhraseBox != null) _trainingPhraseBox.Text = text;
                        VoiceResultText.Text = "Фраза для обучения: " + text;
                        return;
                    }
                    if (!string.IsNullOrWhiteSpace(_variantPhraseTarget))
                    {
                        if (_variantPhraseBox != null) _variantPhraseBox.Text = text;
                        VoiceResultText.Text = "Вариант фразы: " + text;
                        return;
                    }
                    if (await ExecuteTrainedCommandsAsync(text))
                        return;

                    var parsed = _voiceParser.Parse(text);
                    if (parsed == null)
                    {
                        Log("VOICE: команда не распознана.");
                        return;
                    }

                    ValidateCommand(parsed.Command);

                    if (parsed.ExecuteAt.HasValue)
                    {
                        await _scheduler.ScheduleVoiceAsync(parsed.Command, parsed.ExecuteAt.Value);
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

        private sealed class TrainedVoiceCommand
        {
            public string Phrase { get; set; }
            public string DeviceId { get; set; }
            public string WidgetId { get; set; }
            public string Value { get; set; }
            public List<string> Variants { get; set; }
        }

        private const string TrainedReadValue = "__READ_VALUE__";
        private const string TrainedCommandsStorageKey = "trained_voice_commands";

        private static List<TrainedVoiceCommand> LoadTrainedCommands()
        {
            var result = new List<TrainedVoiceCommand>();
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (!values.ContainsKey(TrainedCommandsStorageKey)) return result;
                var raw = values[TrainedCommandsStorageKey] as string;
                JsonArray array;
                if (string.IsNullOrWhiteSpace(raw) || !JsonArray.TryParse(raw, out array)) return result;
                foreach (var item in array)
                {
                    if (item.ValueType != JsonValueType.Object) continue;
                    var obj = item.GetObject();
                    var command = new TrainedVoiceCommand
                    {
                        Phrase = GetJsonString(obj, "phrase"),
                        DeviceId = GetJsonString(obj, "deviceId"),
                        WidgetId = GetJsonString(obj, "widgetId"),
                        Value = GetJsonString(obj, "value"),
                        Variants = new List<string>()
                    };
                    IJsonValue variantsRaw;
                    if (obj.TryGetValue("variants", out variantsRaw) && variantsRaw.ValueType == JsonValueType.Array)
                    {
                        foreach (var variant in variantsRaw.GetArray())
                            if (variant.ValueType == JsonValueType.String && !string.IsNullOrWhiteSpace(variant.GetString()))
                                command.Variants.Add(variant.GetString().Trim());
                    }
                    if (command.Variants.Count == 0 && !string.IsNullOrWhiteSpace(command.Phrase))
                        command.Variants.Add(command.Phrase);
                    if (!string.IsNullOrWhiteSpace(command.Phrase) &&
                        !string.IsNullOrWhiteSpace(command.DeviceId) &&
                        !string.IsNullOrWhiteSpace(command.WidgetId))
                        result.Add(command);
                }
            }
            catch { }
            return result;
        }

        private static string GetJsonString(JsonObject obj, string key)
        {
            IJsonValue value;
            if (obj == null || !obj.TryGetValue(key, out value)) return string.Empty;
            if (value.ValueType == JsonValueType.String) return value.GetString();
            return value.ToString();
        }

        private void SaveTrainedCommands()
        {
            try
            {
                var array = new JsonArray();
                foreach (var command in _trainedCommands)
                {
                    var item = new JsonObject();
                    item["phrase"] = JsonValue.CreateStringValue(command.Phrase ?? string.Empty);
                    item["deviceId"] = JsonValue.CreateStringValue(command.DeviceId ?? string.Empty);
                    item["widgetId"] = JsonValue.CreateStringValue(command.WidgetId ?? string.Empty);
                    item["value"] = JsonValue.CreateStringValue(command.Value ?? string.Empty);
                    var variants = new JsonArray();
                    foreach (var variant in (command.Variants ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
                        variants.Add(JsonValue.CreateStringValue(variant));
                    item["variants"] = variants;
                    array.Add(item);
                }
                ApplicationData.Current.LocalSettings.Values[TrainedCommandsStorageKey] = array.ToString();
            }
            catch (Exception ex) { Log("TRAINED SAVE ERROR: " + ex.Message); }
        }

        private void BuildPermissionsSection()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new StackPanel { Margin = new Thickness(14, 12, 14, 8) };
            header.Children.Add(new TextBlock
            {
                Text = "Возможности приложения",
                FontSize = 20,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold
            });
            header.Children.Add(new TextBlock
            {
                Text = "Проверка деклараций из Package.appxmanifest для Windows Phone 8.1.",
                FontSize = 13,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)),
                TextWrapping = TextWrapping.Wrap
            });
            root.Children.Add(header);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(10, 0, 10, 10)
            };
            var content = new StackPanel();
            content.Children.Add(CreateCapabilityCard(
                "Интернет",
                "internetClient",
                "Объявлено в манифесте. Используется для подключения к удалённому MQTT-брокеру и проверки обновлений."));
            content.Children.Add(CreateCapabilityCard(
                "Локальная сеть",
                "privateNetworkClientServer",
                "Объявлено в манифесте. Используется для обнаружения ESP32 по UDP и локального обмена данными."));
            content.Children.Add(CreateCapabilityCard(
                "Микрофон",
                "DeviceCapability: microphone",
                "Объявлено в манифесте. Требуется для голосового управления и записи команд."));
            content.Children.Add(CreateCapabilityCard(
                "Библиотека изображений",
                "picturesLibrary",
                "Объявлено в манифесте. Доступ используется системными сценариями выбора и сохранения файлов изображений."));

            var note = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 43, 44, 52)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 67, 77)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 2, 0, 8),
                Child = new TextBlock
                {
                    Text = "Важно: этот экран проверяет наличие возможностей в манифесте пакета. Windows Phone 8.1 не предоставляет тот же механизм динамической выдачи разрешений, что современные версии Android; фактическая доступность микрофона и сетевых служб дополнительно зависит от системных настроек и состояния устройства.",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 190, 192, 201))
                }
            };
            content.Children.Add(note);
            scroll.Content = content;
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);
            _permissionsSection.Children.Add(root);
        }

        private Border CreateCapabilityCard(string title, string capability, string description)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 43, 44, 52)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 67, 77)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var content = new StackPanel();
            var heading = new Grid();
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                VerticalAlignment = VerticalAlignment.Center
            });
            var state = new TextBlock
            {
                Text = "В МАНИФЕСТЕ",
                FontSize = 10,
                FontWeight = Windows.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(state, 1);
            heading.Children.Add(state);
            content.Children.Add(heading);
            content.Children.Add(new TextBlock
            {
                Text = capability,
                FontSize = 12,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                Margin = new Thickness(0, 3, 0, 5)
            });
            content.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 12,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)),
                TextWrapping = TextWrapping.Wrap
            });
            card.Child = content;
            return card;
        }

        private void BuildCleanupSection()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new StackPanel { Margin = new Thickness(14, 12, 14, 8) };
            header.Children.Add(new TextBlock
            {
                Text = "ХРАНЕНИЕ ДАННЫХ",
                FontSize = 11,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255))
            });
            header.Children.Add(new TextBlock
            {
                Text = "Очистка приложения",
                FontSize = 20,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold
            });
            header.Children.Add(new TextBlock
            {
                Text = "Можно удалить старые измерения и диагностические записи. Настройки подключения и управления не затрагиваются.",
                FontSize = 13,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)),
                TextWrapping = TextWrapping.Wrap
            });
            root.Children.Add(header);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(10, 0, 10, 10)
            };
            var content = new StackPanel();

            var historyCard = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 43, 44, 52)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 67, 77)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var historyBody = new StackPanel();
            historyBody.Children.Add(new TextBlock
            {
                Text = "Старые измерения",
                FontSize = 14,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255))
            });
            historyBody.Children.Add(new TextBlock
            {
                Text = "Будут удалены только точки истории старше установленного срока хранения. Текущие MQTT-виджеты и их значения не удаляются.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 8)
            });
            var historyButton = new Button
            {
                Content = "Очистить старые измерения",
                MinHeight = 44,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32)),
                FontWeight = Windows.UI.Text.FontWeights.SemiBold
            };
            historyButton.Click += async delegate
            {
                var days = _appSettings == null ? 7 : Math.Max(1, _appSettings.RetentionDays);
                var confirmed = await ShowAppConfirmationAsync(
                    "Очистка истории",
                    "Удалить записи истории старше " + days.ToString(CultureInfo.InvariantCulture) +
                    " дн.? Более новые измерения сохранятся.",
                    "Удалить");
                if (!confirmed) return;

                try
                {
                    await CleanupHistoryAsync();
                    _cleanupResultText.Text = "История очищена. Срок хранения: " +
                        days.ToString(CultureInfo.InvariantCulture) + " дн.";
                    Log("CLEANUP: удалены измерения старше " + days + " дн.");
                }
                catch (Exception ex)
                {
                    _cleanupResultText.Text = "Ошибка очистки истории: " + ex.Message;
                    Log("CLEANUP ERROR: " + ex.Message);
                }
            };
            historyBody.Children.Add(historyButton);
            historyCard.Child = historyBody;
            content.Children.Add(historyCard);

            var logCard = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 43, 44, 52)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 67, 77)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 0, 10)
            };
            var logBody = new StackPanel();
            logBody.Children.Add(new TextBlock
            {
                Text = "Диагностический журнал",
                FontSize = 18,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold
            });
            logBody.Children.Add(new TextBlock
            {
                Text = "Удаляются сохранённые записи подключения, UDP и сетевых ошибок, а также текущий список журнала на экране. История измерений и обученные голосовые команды останутся.",
                FontSize = 13,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 190, 192, 201)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 8)
            });
            var logButton = new Button
            {
                Content = "Очистить журнал",
                MinHeight = 46
            };
            logButton.Click += async delegate
            {
                var confirmed = await ShowAppConfirmationAsync(
                    "Очистка журнала",
                    "Удалить сохранённые записи подключения, UDP и сетевых ошибок? История измерений и обученные команды останутся.",
                    "Очистить");
                if (!confirmed) return;

                _connectionJournal.Clear();
                _logLines.Clear();
                RefreshLogView();
                if (LogActionStatus != null) LogActionStatus.Text = "Журнал очищен.";
                _cleanupResultText.Text = "Диагностический журнал очищен.";
            };
            logBody.Children.Add(logButton);
            logCard.Child = logBody;
            content.Children.Add(logCard);

            var note = new TextBlock
            {
                Text = "Примечание: Windows Phone 8.1 не использует Android cacheDir и систему runtime-разрешений Android. Поэтому очистка не затрагивает системные данные, APK и настройки приложения.",
                FontSize = 12,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 2, 4, 8)
            };
            content.Children.Add(note);
            _cleanupResultText = new TextBlock
            {
                Text = "Действия очистки не выполняются автоматически.",
                FontSize = 13,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 4, 4, 8)
            };
            content.Children.Add(_cleanupResultText);

            scroll.Content = content;
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);
            _cleanupSection.Children.Add(root);
        }



        private void BuildScenarioHubSection()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new StackPanel { Margin = new Thickness(14, 12, 14, 8) };
            header.Children.Add(new TextBlock
            {
                Text = "Сценарии Марфы",
                FontSize = 20,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold
            });
            header.Children.Add(new TextBlock
            {
                Text = "Управление сценариями ESP32 / IoTManager и локальными правилами. Проверка команды ниже только строит план — ничего на устройство не отправляет.",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 185, 187, 198))
            });
            root.Children.Add(header);

            var content = new StackPanel { Margin = new Thickness(10, 0, 10, 10) };
            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            content.Children.Add(CreateScenarioHubCard(
                "Сценарии ESP32 / IoTManager",
                "Импорт export.json, проверка зависимостей и плана действий.",
                "Открыть сценарий",
                delegate
                {
                    ShowSection(3);
                    SettingsPivot.SelectedIndex = 4;
                }));
            content.Children.Add(CreateScenarioHubCard(
                "Мои локальные правила",
                "Условия AND / OR, действия MQTT, уведомления, включение и отключение правил.",
                "Открыть правила",
                delegate
                {
                    ShowSection(3);
                    SettingsPivot.SelectedIndex = 5;
                }));

            var preview = new StackPanel { Margin = new Thickness(0) };
            preview.Children.Add(new TextBlock
            {
                Text = "Проверка команды",
                FontSize = 14,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255))
            });
            preview.Children.Add(new TextBlock
            {
                Text = "Например: «открой теплицу через 10 минут» или esp32/relay=1",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 185, 187, 198)),
                Margin = new Thickness(0, 3, 0, 7)
            });
            _scenarioProbeBox = new TextBox
            {
                Header = "Команда для проверки",
                PlaceholderText = "Введите команду Марфе",
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = false,
                MinHeight = 48
            };
            preview.Children.Add(_scenarioProbeBox);
            var check = new Button
            {
                Content = "Проверить команду Марфы",
                MinHeight = 44,
                Margin = new Thickness(0, 8, 0, 6),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32)),
                FontWeight = Windows.UI.Text.FontWeights.SemiBold
            };
            check.Click += ScenarioProbeButton_Click;
            preview.Children.Add(check);
            var diagnosticActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            var clearResult = new Button
            {
                Content = "Очистить результат",
                MinHeight = 40,
                Margin = new Thickness(0, 0, 6, 0),
                FontSize = 12
            };
            clearResult.Click += ClearScenarioProbeResult_Click;
            diagnosticActions.Children.Add(clearResult);
            var copyResult = new Button
            {
                Content = "Копировать результат",
                MinHeight = 40,
                FontSize = 12
            };
            copyResult.Click += CopyScenarioProbeResult_Click;
            diagnosticActions.Children.Add(copyResult);
            preview.Children.Add(diagnosticActions);
            _scenarioProbeResult = new TextBlock
            {
                Text = "Результат проверки появится здесь.",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 190, 192, 201)),
                Margin = new Thickness(0, 4, 0, 2)
            };
            preview.Children.Add(new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 36, 37, 42)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 67, 77)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10),
                Child = _scenarioProbeResult
            });
            content.Children.Add(new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 43, 44, 52)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 70, 72, 82)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 8),
                Child = preview
            });

            _scenarioHubSection.Children.Add(root);
        }

        private Border CreateScenarioHubCard(string title, string description, string actionText, Action action)
        {
            var panel = new StackPanel { Margin = new Thickness(0) };
            panel.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 185, 187, 198)),
                Margin = new Thickness(0, 4, 0, 8)
            });
            var button = new Button
            {
                Content = actionText,
                MinHeight = 42,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            button.Click += delegate { action(); };
            panel.Children.Add(button);
            return new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 43, 44, 52)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 70, 72, 82)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 8),
                Child = panel
            };
        }

        private void ClearScenarioProbeResult_Click(object sender, RoutedEventArgs e)
        {
            if (_scenarioProbeResult != null) _scenarioProbeResult.Text = "Результат очищен.";
        }

        private string _pendingShareText;

        private void ShareText_DataRequested(Windows.ApplicationModel.DataTransfer.DataTransferManager sender,
            Windows.ApplicationModel.DataTransfer.DataRequestedEventArgs args)
        {
            args.Request.Data.Properties.Title = "ESP32 AI Control";
            args.Request.Data.SetText(_pendingShareText ?? string.Empty);
            sender.DataRequested -= ShareText_DataRequested;
        }

        private void CopyScenarioProbeResult_Click(object sender, RoutedEventArgs e)
        {
            var text = _scenarioProbeResult == null ? string.Empty : _scenarioProbeResult.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                if (_scenarioProbeResult != null) _scenarioProbeResult.Text = "Нет результата для копирования.";
                return;
            }

            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.DataTransferManager.GetForCurrentView().DataRequested += ShareText_DataRequested;
            _pendingShareText = text;
            Windows.ApplicationModel.DataTransfer.DataTransferManager.ShowShareUI();
            
            if (_scenarioProbeResult != null)
                _scenarioProbeResult.Text = text + Environment.NewLine + "Результат скопирован.";
        }

        private void ScenarioProbeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var raw = (_scenarioProbeBox == null ? string.Empty : _scenarioProbeBox.Text).Trim();
                if (raw.Length == 0)
                    throw new InvalidOperationException("Введите команду для проверки.");

                var parsed = _voiceParser.Parse(raw);
                if (parsed == null)
                {
                    ValidateCommand(raw);
                    parsed = new ParsedVoiceCommand { Command = raw };
                }

                if (parsed.ExecuteAt.HasValue)
                {
                    _scenarioProbeResult.Text =
                        "Марфа распознаёт отложенную команду:\n" + parsed.Command +
                        "\nВремя выполнения: " + parsed.ExecuteAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) +
                        "\nКоманда не поставлена в очередь и не отправлена.";
                    return;
                }

                var plan = _commandExecutor.BuildPlan(parsed.Command);
                if (plan == null || plan.Actions == null || plan.Actions.Count == 0)
                {
                    _scenarioProbeResult.Text = "Для команды не найдено действий. Проверьте ID устройства, виджета и сценарий IoTManager.";
                    return;
                }

                var lines = new List<string>();
                lines.Add("Команда: " + parsed.Command);
                lines.Add("План действий:");
                for (var i = 0; i < plan.Actions.Count; i++)
                {
                    var item = plan.Actions[i];
                    lines.Add((item.IsDependency ? "Зависимость: " : "Действие: ") + item.Command +
                        (string.IsNullOrWhiteSpace(item.Reason) ? string.Empty : " — " + item.Reason));
                }
                lines.Add("Проверка завершена. На ESP32 ничего не отправлено.");
                _scenarioProbeResult.Text = string.Join(Environment.NewLine, lines);
            }
            catch (Exception ex)
            {
                _scenarioProbeResult.Text = "Ошибка проверки: " + ex.Message;
            }
        }

        private void BuildPendingMarfaSection()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new StackPanel { Margin = new Thickness(14, 12, 14, 8) };
            header.Children.Add(new TextBlock
            {
                Text = "Отложенные команды Марфы",
                FontSize = 18,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255))
            });
            header.Children.Add(new TextBlock
            {
                Text = "Команды, которые Марфа приняла и ожидает к выполнению. Команды из ручного планировщика ESP32 находятся отдельно.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184))
            });
            root.Children.Add(header);

            var scroll = new ScrollViewer
            {
                Margin = new Thickness(12, 0, 12, 10),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            _pendingMarfaList = new StackPanel();
            scroll.Content = _pendingMarfaList;
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);
            _pendingMarfaSection.Children.Add(root);
        }

        private async void RefreshPendingMarfaCommandsScreen()
        {
            if (_pendingMarfaList == null || _scheduler == null) return;
            try
            {
                var all = await _scheduler.GetAllAsync();
                var pending = all.Where(item =>
                    string.Equals(item.Origin, "Voice", StringComparison.OrdinalIgnoreCase) &&
                    (item.Status == "Scheduled" || item.Status == "Executing"))
                    .OrderBy(item => item.ExecuteAt).ToList();

                _pendingMarfaList.Children.Clear();
                if (pending.Count == 0)
                {
                    _pendingMarfaList.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 43, 44, 52)),
                        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 67, 77)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(14),
                        Padding = new Thickness(14),
                        Margin = new Thickness(2, 4, 2, 8),
                        Child = new TextBlock
                        {
                            Text = "Сейчас отложенных команд Марфы нет.",
                            FontSize = 13,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184))
                        }
                    });
                    ApplyFontScaleToTree(_pendingMarfaList, _currentFontScale);
                    return;
                }

                foreach (var item in pending)
                {
                    var remaining = item.ExecuteAt - DateTimeOffset.Now;
                    var seconds = Math.Max(0, (long)remaining.TotalSeconds);
                    var hours = seconds / 3600;
                    var minutes = (seconds % 3600) / 60;
                    var rest = seconds % 60;
                    var remainingText = hours > 0
                        ? "Через " + hours + " ч " + minutes + " мин"
                        : minutes > 0
                            ? "Через " + minutes + " мин " + rest + " сек"
                            : "Через " + rest + " сек";

                    var panel = new StackPanel { Margin = new Thickness(12) };
                    panel.Children.Add(new TextBlock
                    {
                        Text = item.Command,
                        FontSize = 14,
                        FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                        TextWrapping = TextWrapping.Wrap
                    });
                    panel.Children.Add(new TextBlock
                    {
                        Text = "Выполнение: " + item.ExecuteAt.ToLocalTime().ToString("dd.MM.yyyy  HH:mm", CultureInfo.InvariantCulture),
                        FontSize = 12,
                        Margin = new Thickness(0, 5, 0, 0),
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184))
                    });
                    panel.Children.Add(new TextBlock
                    {
                        Text = remainingText,
                        FontSize = 12,
                        FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                        Margin = new Thickness(0, 3, 0, 7),
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255))
                    });
                    var cancel = new Button
                    {
                        Content = "Удалить из очереди",
                        MinHeight = 38,
                        FontSize = 12,
                        Padding = new Thickness(10, 4, 10, 4),
                        HorizontalAlignment = HorizontalAlignment.Left
                    };
                    var commandId = item.Id;
                    cancel.Click += async delegate
                    {
                        try
                        {
                            await _scheduler.CancelAsync(commandId);
                            RefreshPendingMarfaCommandsScreen();
                            await RefreshScheduleAsync();
                        }
                        catch (Exception ex) { Log("MARFA QUEUE CANCEL ERROR: " + ex.Message); }
                    };
                    panel.Children.Add(cancel);
                    _pendingMarfaList.Children.Add(new Border
                    {
                        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 43, 44, 52)),
                        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 67, 77)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(14),
                        Margin = new Thickness(2, 3, 2, 7),
                        Child = panel
                    });
                }
                ApplyFontScaleToTree(_pendingMarfaList, _currentFontScale);
            }
            catch (Exception ex) { Log("MARFA QUEUE ERROR: " + ex.Message); }
        }

        private void BuildDeleteWidgetSection()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new StackPanel { Margin = new Thickness(14, 12, 14, 8) };
            header.Children.Add(new TextBlock
            {
                Text = "Удаление элемента",
                FontSize = 20,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold
            });
            header.Children.Add(new TextBlock
            {
                Text = "Удаляется только локальная запись приложения. ESP32 не получает команд. Если элемент снова пришлёт CONFIG или STATE, он появится автоматически. История измерений при этом сохраняется.",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 185, 187, 198))
            });
            root.Children.Add(header);

            var scroll = new ScrollViewer
            {
                Margin = new Thickness(10, 0, 10, 10),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            _deleteWidgetList = new StackPanel();
            scroll.Content = _deleteWidgetList;
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);
            _deleteWidgetSection.Children.Add(root);
        }

        private void RefreshDeleteWidgetScreen()
        {
            if (_deleteWidgetList == null || _devices == null) return;
            _deleteWidgetList.Children.Clear();
            var widgets = _devices.GetWidgetsSnapshot()
                .OrderBy(item => item.DeviceId)
                .ThenBy(item => item.Page)
                .ThenBy(item => item.Order)
                .ToList();

            if (widgets.Count == 0)
            {
                _deleteWidgetList.Children.Add(new TextBlock
                {
                    Text = "Нет известных элементов для удаления.",
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)),
                    Margin = new Thickness(12, 18, 12, 18)
                });
                return;
            }

            foreach (var state in widgets)
            {
                var panel = new StackPanel { Margin = new Thickness(0) };
                panel.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(state.Description) ? state.Id : state.Description,
                    FontSize = 14,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                    TextWrapping = TextWrapping.Wrap
                });
                panel.Children.Add(new TextBlock
                {
                    Text = "ESP: " + state.DeviceId + "   •   ID: " + state.Id + "   •   Страница: " + NormalizePage(state.Page),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 185, 187, 198)),
                    Margin = new Thickness(0, 4, 0, 7)
                });
                var remove = new Button
                {
                    Content = "Удалить локально",
                    MinHeight = 44,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                var deviceId = state.DeviceId;
                var widgetId = state.Id;
                remove.Click += async delegate
                {
                    var confirmed = await ShowAppConfirmationAsync(
                        "Удалить элемент?",
                        "Будет удалена только локальная запись " + deviceId + "/" + widgetId +
                        ". Команда на ESP32 не отправляется. При следующем сообщении элемент появится снова.",
                        "Удалить");
                    if (!confirmed) return;

                    var key = deviceId + "/" + widgetId;
                    if (_devices.RemoveLocal(deviceId, widgetId))
                    {
                        WidgetVisual visual;
                        if (_visuals.TryGetValue(key, out visual))
                        {
                            PageVisual page;
                            if (_pages.TryGetValue(visual.PageName, out page))
                                page.Content.Children.Remove(visual.Container);
                            _visuals.Remove(key);
                        }
                        _widgetIndex.Remove(key);
                        CleanupEmptyPages();
                        RefreshGraphWidgets();
                        RefreshDeleteWidgetScreen();
                        Log("WIDGET REMOVED LOCALLY: " + key);
                    }
                };
                panel.Children.Add(remove);
                _deleteWidgetList.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 43, 44, 52)),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 67, 77)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(12),
                    Margin = new Thickness(2, 3, 2, 7),
                    Child = panel
                });
            }
        }

        private void BuildTrainedCommandsSection()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var header = new StackPanel { Margin = new Thickness(14, 12, 14, 8) };
            header.Children.Add(new TextBlock
            {
                Text = "Обученные команды",
                FontSize = 18,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255))
            });
            header.Children.Add(new TextBlock
            {
                Text = "Фразы, связанные с датчиками и действиями ESP32.",
                FontSize = 12, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184))
            });
            root.Children.Add(header);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12, 0, 12, 10) };
            _trainedCommandsList = new StackPanel();
            scroll.Content = _trainedCommandsList;
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);
            _trainedCommandsSection.Children.Add(root);
        }

        private void RefreshTrainedCommandsScreen()
        {
            if (_trainedCommandsList == null) return;
            _trainedCommandsList.Children.Clear();

            var top = new Grid { Margin = new Thickness(2, 2, 2, 8) };
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var count = new TextBlock
            {
                Text = "Записанные команды: " + _trainedCommands.Count.ToString(CultureInfo.InvariantCulture),
                FontSize = 13, FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)),
                VerticalAlignment = VerticalAlignment.Center
            };
            top.Children.Add(count);
            var clear = new Button
            {
                Content = "Очистить всё",
                MinHeight = 38,
                FontSize = 12,
                Padding = new Thickness(9, 4, 9, 4)
            };
            clear.Click += async delegate
            {
                var confirmed = await ShowAppConfirmationAsync(
                    "Очистить обученные команды?",
                    "Будут удалены все сохранённые фразы и их варианты. Настройки MQTT и другие данные не изменятся.",
                    "Удалить всё");
                if (confirmed)
                {
                    _trainedCommands.Clear();
                    SaveTrainedCommands();
                    RefreshTrainedCommandsScreen();
                }
            };
            Grid.SetColumn(clear, 1);
            top.Children.Add(clear);
            _trainedCommandsList.Children.Add(top);

            if (_trainedCommands.Count == 0)
            {
                _trainedCommandsList.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 43, 44, 52)),
                    CornerRadius = new CornerRadius(14),
                    Padding = new Thickness(18),
                    Child = new TextBlock
                    {
                        Text = "Пока нет обученных команд.\n\nЗажмите виджет на главном экране, чтобы связать с ним голосовую фразу.",
                        FontSize = 13, TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 190, 192, 201))
                    }
                });
                ApplyFontScaleToTree(_trainedCommandsList, _currentFontScale);
                return;
            }

            foreach (var command in _trainedCommands.ToList())
            {
                var card = new Border
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 43, 44, 52)),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 67, 77)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(14),
                    Padding = new Thickness(12),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                var content = new StackPanel();
                content.Children.Add(new TextBlock
                {
                    Text = "«" + command.Phrase + "»",
                    FontSize = 15, FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 5)
                });
                var state = _devices == null ? null : _devices.Get(command.DeviceId, command.WidgetId);
                var widgetName = state == null ? command.WidgetId : GetTitle(state);
                content.Children.Add(new TextBlock
                {
                    Text = command.DeviceId + "  •  " + widgetName,
                    FontSize = 12, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 5)
                });
                var action = command.Value == TrainedReadValue ? "ПРОИЗНЕСТИ ЗНАЧЕНИЕ" : command.Value == "1" ? "ВКЛ / ОТКРЫТЬ" : "ВЫКЛ / ЗАКРЫТЬ";
                content.Children.Add(new TextBlock
                {
                    Text = action,
                    FontSize = 11, FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                    Margin = new Thickness(0, 0, 0, 7)
                });
                if (command.Variants != null && command.Variants.Count > 1)
                    content.Children.Add(new TextBlock
                    {
                        Text = "Варианты: " + string.Join(", ", command.Variants),
                        FontSize = 12, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 172, 173, 184)),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 7)
                    });
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                var edit = new Button { Content = "Изменить", MinHeight = 36, FontSize = 12, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 5, 0) };
                edit.Click += delegate
                {
                    if (state == null)
                    {
                        VoiceResultText.Text = "Виджет больше не существует. Удалите старую команду или восстановите виджет.";
                        return;
                    }
                    OpenTrainingDialog(state);
                    if (_trainingPhraseBox != null)
                        _trainingPhraseBox.Text = command.Phrase;
                    if (_trainingModeBox != null)
                        _trainingModeBox.SelectedIndex = 0;
                    if (_trainingActionBox != null)
                        _trainingActionBox.SelectedIndex = command.Value == "0" ? 1 : 0;
                };
                var addVariant = new Button { Content = "＋ Вариант", MinHeight = 36, FontSize = 12, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 5, 0) };
                addVariant.Click += delegate { OpenVariantDialog(command.Phrase); };
                var remove = new Button { Content = "Удалить", MinHeight = 36, FontSize = 12, Padding = new Thickness(8, 4, 8, 4) };
                remove.Click += delegate
                {
                    _trainedCommands.RemoveAll(c =>
                        c.Phrase == command.Phrase && c.DeviceId == command.DeviceId &&
                        c.WidgetId == command.WidgetId && c.Value == command.Value);
                    SaveTrainedCommands();
                    RefreshTrainedCommandsScreen();
                };
                buttons.Children.Add(edit);
                buttons.Children.Add(addVariant);
                buttons.Children.Add(remove);
                content.Children.Add(buttons);
                card.Child = content;
                _trainedCommandsList.Children.Add(card);
            }
            ApplyFontScaleToTree(_trainedCommandsList, _currentFontScale);
        }

        private static bool IsValueWidget(WidgetState state)
        {
            if (state == null) return false;
            var def = NormalizeType(state.DefinitionName);
            var type = NormalizeType(state.WidgetType);
            return type == "status" || type == "value" ||
                def.StartsWith("anydata") || def.StartsWith("chart") ||
                def == "fillgauge" || def == "progressline" || def == "progressround";
        }

        private void OpenTrainingDialog(WidgetState state)
        {
            if (state == null) return;
            var definition = NormalizeType(state.DefinitionName);
            var type = NormalizeType(state.WidgetType);
            var canTrain = IsValueWidget(state) || type == "toggle" ||
                type == "button" || definition == "button" || definition == "vbtn" || definition == "btn";
            if (!canTrain) return;

            _trainingTarget = state.Clone();
            _trainingValue = IsValueWidget(state) ? TrainedReadValue : "1";
            _trainingPhraseBox = new TextBox
            {
                Header = "Фраза команды",
                PlaceholderText = "Например: открой форточку",
                MinHeight = 52,
                Text = string.Empty
            };
            _trainingModeBox = new ComboBox { MinHeight = 48, Header = "Добавить как" };
            _trainingModeBox.Items.Add("Новая команда");
            _trainingModeBox.Items.Add("К существующей");
            _trainingModeBox.SelectedIndex = 0;
            _trainingExistingPhraseBox = new ComboBox
            {
                MinHeight = 48,
                Header = "Существующая фраза",
                Visibility = Visibility.Collapsed
            };
            foreach (var phrase in _trainedCommands.Select(c => c.Phrase).Distinct(StringComparer.OrdinalIgnoreCase))
                _trainingExistingPhraseBox.Items.Add(phrase);

            _trainingActionBox = new ComboBox
            {
                MinHeight = 48,
                Header = "Действие",
                Visibility = IsValueWidget(state) ? Visibility.Collapsed : Visibility.Visible
            };
            _trainingActionBox.Items.Add("Открыть / включить");
            _trainingActionBox.Items.Add("Закрыть / выключить");
            _trainingActionBox.SelectedIndex = 0;

            _trainingModeBox.SelectionChanged += delegate
            {
                var attach = _trainingModeBox.SelectedIndex == 1;
                _trainingExistingPhraseBox.Visibility = attach ? Visibility.Visible : Visibility.Collapsed;
                _trainingPhraseBox.Visibility = attach ? Visibility.Collapsed : Visibility.Visible;
                if (_trainingRecordButton != null)
                    _trainingRecordButton.Visibility = attach ? Visibility.Collapsed : Visibility.Visible;
            };

            var body = new StackPanel { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 43, 44, 52)) };
            body.Children.Add(new TextBlock
            {
                Text = "Обучить голосовую команду",
                FontSize = 20, FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                Margin = new Thickness(14, 14, 14, 0)
            });
            body.Children.Add(new TextBlock
            {
                Text = "Виджет: " + GetTitle(state),
                FontSize = 14, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 190, 192, 201)),
                Margin = new Thickness(14, 0, 14, 0)
            });
            body.Children.Add(_trainingModeBox);
            body.Children.Add(_trainingExistingPhraseBox);
            if (IsValueWidget(state))
            {
                body.Children.Add(new TextBlock
                {
                    Text = "Эта фраза будет произносить текущее значение датчика или виджета.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 190, 192, 201)),
                    Margin = new Thickness(14, 0, 14, 0)
                });
            }
            else body.Children.Add(_trainingActionBox);
            body.Children.Add(_trainingPhraseBox);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8, 4, 8, 8)
            };
            var cancel = new Button { Content = "Отмена", Margin = new Thickness(0, 0, 6, 0) };
            cancel.Click += delegate
            {
                if (_trainingPopup != null) _trainingPopup.IsOpen = false;
                _trainingTarget = null;
                _trainingPhraseBox = null;
                _trainingModeBox = null;
                _trainingExistingPhraseBox = null;
                _trainingActionBox = null;
                _trainingRecordButton = null;
            };
            var record = new Button { Content = "🎤 Записать", Margin = new Thickness(0, 0, 6, 0) };
            _trainingRecordButton = record;
            record.Click += async delegate
            {
                try
                {
                    VoiceResultText.Text = "Слушаю фразу для обучения…";
                    await _voice.StartListeningAsync();
                }
                catch (Exception ex) { VoiceResultText.Text = "Ошибка микрофона: " + ex.Message; }
            };
            var save = new Button
            {
                Content = "Сохранить",
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                Foreground = new SolidColorBrush(Windows.UI.Colors.White)
            };
            save.Click += delegate
            {
                if (_trainingModeBox.SelectedIndex != 1 &&
                    _trainingActionBox != null && _trainingActionBox.Visibility == Visibility.Visible)
                    _trainingValue = _trainingActionBox.SelectedIndex == 1 ? "0" : "1";
                var phrase = _trainingModeBox.SelectedIndex == 1
                    ? (_trainingExistingPhraseBox.SelectedItem as string)
                    : _trainingPhraseBox.Text;
                SaveTrainedCommand(phrase);
            };
            actions.Children.Add(cancel);
            actions.Children.Add(record);
            actions.Children.Add(save);
            body.Children.Add(actions);

            _trainingPopup = CreateAppPopup(body, 330, 520);
        }

        private Task ShowAppMessageAsync(string title, string message)
        {
            var body = new StackPanel();
            body.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 18,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                Margin = new Thickness(12, 12, 12, 6)
            });
            body.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 190, 192, 201)),
                Margin = new Thickness(12, 0, 12, 12)
            });
            var close = new Button
            {
                Content = "Понятно",
                MinHeight = 44,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8, 4, 8, 8),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32)),
                FontWeight = Windows.UI.Text.FontWeights.SemiBold
            };
            Popup popup = null;
            var completion = new TaskCompletionSource<bool>();
            close.Click += delegate
            {
                if (popup != null) popup.IsOpen = false;
                completion.TrySetResult(true);
            };
            body.Children.Add(close);
            popup = CreateAppPopup(body, 330, 280);
            return completion.Task;
        }

        private async Task<bool> ShowAppConfirmationAsync(string title, string message, string confirmText)
        {
            var body = new StackPanel();
            body.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 18,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                Margin = new Thickness(12, 12, 12, 6)
            });
            body.Children.Add(new TextBlock
            {
                Text = message,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 190, 192, 201)),
                Margin = new Thickness(12, 0, 12, 12)
            });

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8, 4, 8, 8)
            };
            Popup popup = null;
            var completion = new TaskCompletionSource<bool>();
            var cancel = new Button
            {
                Content = "Отмена",
                MinHeight = 44,
                Margin = new Thickness(0, 0, 6, 0)
            };
            cancel.Click += delegate
            {
                if (popup != null) popup.IsOpen = false;
                completion.TrySetResult(false);
            };
            var confirm = new Button
            {
                Content = confirmText,
                MinHeight = 44,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255)),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 32, 32)),
                FontWeight = Windows.UI.Text.FontWeights.SemiBold
            };
            confirm.Click += delegate
            {
                if (popup != null) popup.IsOpen = false;
                completion.TrySetResult(true);
            };
            actions.Children.Add(cancel);
            actions.Children.Add(confirm);
            body.Children.Add(actions);
            popup = CreateAppPopup(body, 330, 280);
            return await completion.Task;
        }

        private Popup CreateAppPopup(Panel content, double width, double maxHeight)
        {
            ApplyFontScaleToTree(content, _currentFontScale);
            var scroll = new ScrollViewer
            {
                Content = content,
                MaxHeight = Math.Min(maxHeight, Math.Max(260, ActualHeight - 140)),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            var frame = new Border
            {
                Width = Math.Min(width, Math.Max(270, ActualWidth - 24)),
                MaxHeight = Math.Min(maxHeight, Math.Max(280, ActualHeight - 110)),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 43, 44, 52)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 65, 67, 77)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12),
                Child = scroll
            };
            // Keep custom dialogs centered on both 720x1280 and smaller WP8.1 layouts.
            var popup = new Popup
            {
                Child = frame,
                HorizontalOffset = Math.Max(8, (ActualWidth - frame.Width) / 2),
                VerticalOffset = Math.Max(76, (ActualHeight - Math.Min(maxHeight, ActualHeight - 110)) / 2),
                IsOpen = true
            };
            return popup;
        }

        private void SaveTrainedCommand(string phrase)
        {
            if (_trainingTarget == null) return;
            var targetPhrase = (phrase ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(targetPhrase))
            {
                VoiceResultText.Text = "Введите или запишите фразу.";
                return;
            }

            var previous = _trainedCommands.FirstOrDefault(c =>
                string.Equals(c.Phrase, targetPhrase, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.DeviceId, _trainingTarget.DeviceId, StringComparison.Ordinal) &&
                string.Equals(c.WidgetId, _trainingTarget.Id, StringComparison.Ordinal));
            var variants = previous == null || previous.Variants == null
                ? new List<string>()
                : new List<string>(previous.Variants);
            if (!variants.Contains(targetPhrase, StringComparer.OrdinalIgnoreCase))
                variants.Add(targetPhrase);

            // Android replaces an existing phrase/device/widget binding rather
            // than keeping contradictory ON and OFF actions for one spoken phrase.
            _trainedCommands.RemoveAll(c =>
                string.Equals(c.Phrase, targetPhrase, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.DeviceId, _trainingTarget.DeviceId, StringComparison.Ordinal) &&
                string.Equals(c.WidgetId, _trainingTarget.Id, StringComparison.Ordinal));
            _trainedCommands.Add(new TrainedVoiceCommand
            {
                Phrase = targetPhrase,
                DeviceId = _trainingTarget.DeviceId,
                WidgetId = _trainingTarget.Id,
                Value = _trainingValue,
                Variants = variants
            });

            SaveTrainedCommands();
            if (_trainingPopup != null) _trainingPopup.IsOpen = false;
            _trainingTarget = null;
            _trainingPhraseBox = null;
            _trainingModeBox = null;
            _trainingExistingPhraseBox = null;
            _trainingActionBox = null;
            _trainingRecordButton = null;
            VoiceResultText.Text = "Действие добавлено к команде: " + targetPhrase;
            RefreshTrainedCommandsScreen();
        }

        private void OpenVariantDialog(string phrase)
        {
            _variantPhraseTarget = phrase;
            _variantPhraseBox = new TextBox
            {
                Header = "Новый вариант фразы",
                PlaceholderText = "Введите или запишите вариант",
                MinHeight = 52
            };
            var body = new StackPanel();
            body.Children.Add(new TextBlock
            {
                Text = "Добавить вариант",
                FontSize = 20, FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(14, 14, 14, 0)
            });
            body.Children.Add(new TextBlock
            {
                Text = "Основная команда: «" + phrase + "»",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(14, 0, 14, 0),
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 190, 192, 201))
            });
            body.Children.Add(_variantPhraseBox);
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(8)
            };
            var cancel = new Button { Content = "Отмена", Margin = new Thickness(0, 0, 6, 0) };
            cancel.Click += delegate
            {
                if (_variantPopup != null) _variantPopup.IsOpen = false;
                _variantPhraseTarget = null;
                _variantPhraseBox = null;
            };
            var record = new Button { Content = "🎤 Записать", Margin = new Thickness(0, 0, 6, 0) };
            record.Click += async delegate
            {
                try { await _voice.StartListeningAsync(); }
                catch (Exception ex) { VoiceResultText.Text = "Ошибка микрофона: " + ex.Message; }
            };
            var add = new Button
            {
                Content = "Добавить",
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 208, 188, 255))
            };
            add.Click += delegate
            {
                var variant = (_variantPhraseBox.Text ?? string.Empty).Trim();
                if (variant.Length == 0) return;
                foreach (var item in _trainedCommands.Where(c => string.Equals(c.Phrase, _variantPhraseTarget, StringComparison.OrdinalIgnoreCase)))
                    if (!item.Variants.Contains(variant, StringComparer.OrdinalIgnoreCase))
                        item.Variants.Add(variant);
                SaveTrainedCommands();
                if (_variantPopup != null) _variantPopup.IsOpen = false;
                _variantPhraseTarget = null;
                _variantPhraseBox = null;
                VoiceResultText.Text = "Вариант фразы добавлен.";
                RefreshTrainedCommandsScreen();
            };
            actions.Children.Add(cancel);
            actions.Children.Add(record);
            actions.Children.Add(add);
            body.Children.Add(actions);
            _variantPopup = CreateAppPopup(body, 330, 420);
        }

        private async Task<bool> ExecuteTrainedCommandsAsync(string spokenText)
        {
            var matches = MatchTrainedCommands(spokenText);
            if (matches.Count == 0) return false;
            var replies = new List<string>();
            foreach (var command in matches)
            {
                var state = _devices.Get(command.DeviceId, command.WidgetId);
                if (command.Value == TrainedReadValue)
                {
                    if (state == null || string.IsNullOrWhiteSpace(state.Value))
                        replies.Add("Нет актуального значения " + command.WidgetId);
                    else
                        replies.Add(GetTitle(state) + ": " + state.Value + (string.IsNullOrWhiteSpace(state.Unit) ? string.Empty : " " + state.Unit));
                    continue;
                }

                if (state == null)
                {
                    replies.Add("Виджет не найден: " + command.WidgetId);
                    continue;
                }
                try
                {
                    var type = NormalizeType(state.WidgetType);
                    if (type == "toggle" || type == "button" || type == "vbtn" || type == "btn")
                        await _mqtt.PublishControlAsync(command.DeviceId, command.WidgetId, command.Value);
                    else if (!string.IsNullOrWhiteSpace(state.Topic))
                        await _mqtt.PublishWidgetAsync(state.Topic, command.Value);
                    else
                    {
                        replies.Add("Нет MQTT topic для " + command.WidgetId);
                        continue;
                    }
                    replies.Add("Команда отправлена: " + GetTitle(state));
                }
                catch (Exception ex) { replies.Add("Ошибка " + command.WidgetId + ": " + ex.Message); }
            }

            var reply = string.Join(". ", replies);
            VoiceResultText.Text = reply;
            if (reply.Length > 0) await SpeakTextAsync(reply);
            Log("TRAINED COMMAND: " + spokenText + " => " + reply);
            return true;
        }

        private List<TrainedVoiceCommand> MatchTrainedCommands(string spokenText)
        {
            var text = NormalizeTrainedPhrase(spokenText);
            if (text.Length == 0) return new List<TrainedVoiceCommand>();

            var exactPhrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var command in _trainedCommands)
            {
                var phrases = (command.Variants ?? new List<string>()).Concat(new[] { command.Phrase });
                if (phrases.Any(p => NormalizeTrainedPhrase(p) == text))
                    exactPhrases.Add(NormalizeTrainedPhrase(command.Phrase));
            }
            if (exactPhrases.Count > 0)
                return _trainedCommands.Where(c => exactPhrases.Contains(NormalizeTrainedPhrase(c.Phrase))).ToList();

            var containedPhrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var command in _trainedCommands)
            {
                var phrases = (command.Variants ?? new List<string>()).Concat(new[] { command.Phrase });
                if (phrases.Any(p =>
                {
                    var candidate = NormalizeTrainedPhrase(p);
                    return candidate.Length > 0 && (" " + text + " ").Contains(" " + candidate + " ");
                }))
                    containedPhrases.Add(NormalizeTrainedPhrase(command.Phrase));
            }
            if (containedPhrases.Count > 0)
                return _trainedCommands.Where(c => containedPhrases.Contains(NormalizeTrainedPhrase(c.Phrase))).ToList();

            var spokenWords = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (spokenWords.Length > 0 && spokenWords.All(w => w.Length >= 3))
            {
                var partialScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in _trainedCommands.GroupBy(c => NormalizeTrainedPhrase(c.Phrase)))
                {
                    var hasPartial = group.Any(command =>
                        (command.Variants ?? new List<string>()).Concat(new[] { command.Phrase })
                            .Any(phrase => PhraseWordsContainedInText(text, NormalizeTrainedPhrase(phrase))));
                    if (!hasPartial) continue;

                    var score = group.SelectMany(command =>
                            (command.Variants ?? new List<string>()).Concat(new[] { command.Phrase }))
                        .Select(phrase => SimilarityAgainstTrainedText(text, NormalizeTrainedPhrase(phrase)))
                        .DefaultIfEmpty(0.0)
                        .Max();
                    partialScores[group.Key] = score;
                }

                if (partialScores.Count > 0)
                {
                    var bestPartial = partialScores.OrderByDescending(pair => pair.Value).First().Key;
                    return _trainedCommands.Where(c =>
                        string.Equals(NormalizeTrainedPhrase(c.Phrase), bestPartial, StringComparison.OrdinalIgnoreCase)).ToList();
                }
            }

            var candidates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in _trainedCommands.GroupBy(c => NormalizeTrainedPhrase(c.Phrase)))
            {
                if (string.IsNullOrWhiteSpace(group.Key)) continue;
                var best = 0.0;
                foreach (var command in group)
                {
                    var phrases = (command.Variants ?? new List<string>()).Concat(new[] { command.Phrase });
                    foreach (var phrase in phrases)
                    {
                        var candidate = NormalizeTrainedPhrase(phrase);
                        if (candidate.Length == 0) continue;
                        best = Math.Max(best, SimilarityAgainstTrainedText(text, candidate));
                    }
                }
                if (best >= 0.78)
                    candidates[group.Key] = best;
            }

            if (candidates.Count == 0) return new List<TrainedVoiceCommand>();
            var bestScore = candidates.Values.Max();
            var bestGroups = new HashSet<string>(
                candidates.Where(pair => pair.Value >= bestScore - 0.04).Select(pair => pair.Key),
                StringComparer.OrdinalIgnoreCase);
            return _trainedCommands.Where(c => bestGroups.Contains(NormalizeTrainedPhrase(c.Phrase))).ToList();
        }

        private static bool PhraseWordsContainedInText(string text, string phrase)
        {
            var textWords = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(StemTrainedWord).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var phraseWords = phrase.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(StemTrainedWord).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (textWords.Count == 0 || phraseWords.Count == 0) return false;
            if (textWords.Any(word => word.Length < 3)) return false;
            return textWords.All(spoken => phraseWords.Contains(spoken, StringComparer.OrdinalIgnoreCase));
        }

        private static string StemTrainedWord(string word)
        {
            var value = (word ?? string.Empty).ToLowerInvariant();
            var endings = new[]
            {
                "иями", "ами", "ого", "ему", "ому", "ыми", "ими",
                "ать", "ить", "еть", "ять", "уй", "ой", "ый", "ий",
                "ая", "яя", "ое", "ее", "ые", "ие", "ы", "и", "а", "я", "о", "е"
            };
            foreach (var ending in endings)
            {
                if (value.Length > ending.Length + 2 && value.EndsWith(ending, StringComparison.Ordinal))
                    return value.Substring(0, value.Length - ending.Length);
            }
            return value;
        }

        private static double SimilarityAgainstTrainedText(string text, string phrase)
        {
            if (text == phrase) return 1.0;
            if (text.Contains(phrase)) return 0.96;

            var textWords = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var phraseWords = phrase.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (phraseWords.Length == 0 || textWords.Length == 0) return 0.0;

            var minSize = Math.Max(1, phraseWords.Length - 1);
            var maxSize = Math.Min(textWords.Length, phraseWords.Length + 1);
            var best = 0.0;
            for (var size = minSize; size <= maxSize; size++)
            {
                for (var i = 0; i <= textWords.Length - size; i++)
                {
                    var window = string.Join(" ", textWords.Skip(i).Take(size));
                    best = Math.Max(best, Math.Max(
                        NormalizedTrainedEditSimilarity(window, phrase),
                        TrainedTokenSimilarity(window, phrase)));
                }
            }
            return best;
        }

        private static double TrainedTokenSimilarity(string first, string second)
        {
            var a = new HashSet<string>(first.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(StemTrainedWord), StringComparer.OrdinalIgnoreCase);
            var b = new HashSet<string>(second.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(StemTrainedWord), StringComparer.OrdinalIgnoreCase);
            if (a.Count == 0 || b.Count == 0) return 0.0;
            var common = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
            return (2.0 * common) / (a.Count + b.Count);
        }

        private static double NormalizedTrainedEditSimilarity(string first, string second)
        {
            var maxLength = Math.Max(first.Length, second.Length);
            if (maxLength == 0) return 1.0;
            return 1.0 - (double)TrainedLevenshteinDistance(first, second) / maxLength;
        }

        private static int TrainedLevenshteinDistance(string first, string second)
        {
            if (first == second) return 0;
            if (first.Length == 0) return second.Length;
            if (second.Length == 0) return first.Length;

            var previous = new int[second.Length + 1];
            for (var j = 0; j <= second.Length; j++) previous[j] = j;

            for (var i = 0; i < first.Length; i++)
            {
                var current = new int[second.Length + 1];
                current[0] = i + 1;
                for (var j = 0; j < second.Length; j++)
                {
                    var cost = first[i] == second[j] ? 0 : 1;
                    current[j + 1] = Math.Min(
                        Math.Min(current[j] + 1, previous[j + 1] + 1),
                        previous[j] + cost);
                }
                previous = current;
            }
            return previous[second.Length];
        }

        private static string NormalizeTrainedPhrase(string phrase)
        {
            var result = new System.Text.StringBuilder();
            var previousSpace = true;
            foreach (var ch in (phrase ?? string.Empty).ToLowerInvariant().Replace('ё', 'е'))
            {
                if (char.IsLetterOrDigit(ch))
                {
                    result.Append(ch);
                    previousSpace = false;
                }
                else if (!previousSpace)
                {
                    result.Append(' ');
                    previousSpace = true;
                }
            }
            var normalized = result.ToString().Trim();
            var prefixes = new[] { "марфой", "марфа", "марфу", "марфе" };
            foreach (var prefix in prefixes)
            {
                if (normalized == prefix)
                    return string.Empty;
                if (normalized.StartsWith(prefix + " ", StringComparison.Ordinal))
                {
                    normalized = normalized.Substring(prefix.Length).Trim();
                    break;
                }
            }
            return normalized;
        }

        private void LoadAvailableSpeechVoices()
        {
            if (TtsVoiceBox == null) return;
            TtsVoiceBox.Items.Clear();

            try
            {
                var voices = SpeechSynthesizer.AllVoices.ToList();
                foreach (var voice in voices)
                {
                    var label = voice.DisplayName + "  (" + voice.Language + ")";
                    TtsVoiceBox.Items.Add(new ComboBoxItem
                    {
                        Content = label,
                        Tag = voice.Id
                    });
                }

                ComboBoxItem selected = null;
                foreach (var item in TtsVoiceBox.Items)
                {
                    var candidate = item as ComboBoxItem;
                    if (candidate != null &&
                        string.Equals(candidate.Tag as string, _appSettings == null ? string.Empty : _appSettings.TtsVoiceId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        selected = candidate;
                        break;
                    }
                }

                if (selected == null)
                {
                    selected = TtsVoiceBox.Items
                        .OfType<ComboBoxItem>()
                        .FirstOrDefault(item =>
                        {
                            var voice = voices.FirstOrDefault(v => string.Equals(v.Id, item.Tag as string, StringComparison.OrdinalIgnoreCase));
                            return voice != null && (voice.Language ?? string.Empty).StartsWith("ru", StringComparison.OrdinalIgnoreCase);
                        });
                }

                if (selected == null)
                    selected = TtsVoiceBox.Items.OfType<ComboBoxItem>().FirstOrDefault();

                TtsVoiceBox.SelectedItem = selected;
                TtsVoiceStatus.Text = selected == null
                    ? "В системе не найден установленный голос синтеза речи."
                    : "Доступно голосов: " + voices.Count.ToString(CultureInfo.InvariantCulture) +
                      ". Для ответа Марфы будет использован выбранный голос.";
            }
            catch (Exception ex)
            {
                TtsVoiceStatus.Text = "Не удалось получить список голосов: " + ex.Message;
            }
        }

        private string SelectedSpeechVoiceId()
        {
            var item = TtsVoiceBox == null ? null : TtsVoiceBox.SelectedItem as ComboBoxItem;
            return item == null ? string.Empty : (item.Tag as string ?? string.Empty);
        }

        private void NormalizeTtsSettings()
        {
            if (_appSettings == null) return;
            if (_appSettings.TtsVoiceRate < 0.75f || _appSettings.TtsVoiceRate > 1.15f)
                _appSettings.TtsVoiceRate = 0.92f;
            if (_appSettings.TtsVoicePitch < 0.85f || _appSettings.TtsVoicePitch > 1.25f)
                _appSettings.TtsVoicePitch = 1.05f;
            if (string.IsNullOrWhiteSpace(_appSettings.TtsVoicePreset))
                _appSettings.TtsVoicePreset = "friendly";
        }

        private void ApplyTtsSettingsToUi()
        {
            if (_appSettings == null || TtsRateSlider == null || TtsPitchSlider == null) return;
            NormalizeTtsSettings();
            _applyingTtsSettings = true;
            TtsRateSlider.Value = Math.Max(0.75, Math.Min(1.15, _appSettings.TtsVoiceRate));
            TtsPitchSlider.Value = Math.Max(0.85, Math.Min(1.25, _appSettings.TtsVoicePitch));
            var preset = TtsPresetBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                string.Equals(item.Tag as string, _appSettings.TtsVoicePreset, StringComparison.OrdinalIgnoreCase));
            if (preset == null)
                preset = TtsPresetBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                    string.Equals(item.Tag as string, "friendly", StringComparison.OrdinalIgnoreCase));
            TtsPresetBox.SelectedItem = preset;
            _applyingTtsSettings = false;
            UpdateTtsValueLabels();
        }

        private void ReadTtsSettingsFromUi()
        {
            if (_appSettings == null) return;
            _appSettings.TtsVoicePreset = SelectedTtsPresetId();
            _appSettings.TtsVoiceRate = (float)TtsRateSlider.Value;
            _appSettings.TtsVoicePitch = (float)TtsPitchSlider.Value;
        }

        private string SelectedTtsPresetId()
        {
            var item = TtsPresetBox == null ? null : TtsPresetBox.SelectedItem as ComboBoxItem;
            return item == null ? "friendly" : (item.Tag as string ?? "friendly");
        }

        private void UpdateTtsValueLabels()
        {
            if (TtsRateValueText != null)
                TtsRateValueText.Text = TtsRateSlider.Value.ToString("0.00", CultureInfo.CurrentCulture);
            if (TtsPitchValueText != null)
                TtsPitchValueText.Text = TtsPitchSlider.Value.ToString("0.00", CultureInfo.CurrentCulture);
        }

        private void TtsRateSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (TtsRateValueText != null)
                TtsRateValueText.Text = e.NewValue.ToString("0.00", CultureInfo.CurrentCulture);
        }

        private void TtsPitchSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (TtsPitchValueText != null)
                TtsPitchValueText.Text = e.NewValue.ToString("0.00", CultureInfo.CurrentCulture);
        }

        private void TtsPresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_applyingTtsSettings || TtsPresetBox == null) return;
            var preset = SelectedTtsPresetId();
            _applyingTtsSettings = true;
            switch (preset)
            {
                case "soft":
                    TtsRateSlider.Value = 0.88;
                    TtsPitchSlider.Value = 1.12;
                    break;
                case "natural":
                    TtsRateSlider.Value = 0.98;
                    TtsPitchSlider.Value = 1.00;
                    break;
                case "assistant":
                    TtsRateSlider.Value = 0.94;
                    TtsPitchSlider.Value = 0.96;
                    break;
                default:
                    TtsRateSlider.Value = 0.92;
                    TtsPitchSlider.Value = 1.05;
                    break;
            }
            _applyingTtsSettings = false;
            UpdateTtsValueLabels();
        }

        private async void TtsTestButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(SelectedSpeechVoiceId()))
            {
                TtsVoiceStatus.Text = "Нет выбранного системного голоса.";
                return;
            }

            TtsVoiceStatus.Text = "Проверка голоса…";
            var completed = await SpeakTextAsync("Марфа слушает. Проверка синтеза речи.", true);
            if (completed)
                TtsVoiceStatus.Text = "Тестовая фраза синтезирована и передана на воспроизведение.";
            // On failure SpeakTextAsync leaves the detailed error in this status field.
        }

        private async Task<bool> SpeakTextAsync(string text, bool useCurrentSettings = false)
        {
            try
            {
                if (_speechSynthesizer == null)
                    _speechSynthesizer = new SpeechSynthesizer();

                var selectedId = useCurrentSettings
                    ? SelectedSpeechVoiceId()
                    : (_appSettings == null ? string.Empty : _appSettings.TtsVoiceId);
                var selectedVoice = SpeechSynthesizer.AllVoices.FirstOrDefault(v =>
                    !string.IsNullOrWhiteSpace(selectedId) &&
                    string.Equals(v.Id, selectedId, StringComparison.OrdinalIgnoreCase));
                if (selectedVoice == null && !string.IsNullOrWhiteSpace(_appSettings == null ? string.Empty : _appSettings.TtsVoiceId))
                    selectedVoice = SpeechSynthesizer.AllVoices.FirstOrDefault(v =>
                        string.Equals(v.Id, _appSettings.TtsVoiceId, StringComparison.OrdinalIgnoreCase));
                if (selectedVoice == null)
                    selectedVoice = SpeechSynthesizer.AllVoices.FirstOrDefault(v =>
                        (v.Language ?? string.Empty).StartsWith("ru", StringComparison.OrdinalIgnoreCase));
                if (selectedVoice == null)
                    selectedVoice = SpeechSynthesizer.DefaultVoice;

                if (selectedVoice != null)
                    _speechSynthesizer.Voice = selectedVoice;

                var rate = useCurrentSettings
                    ? (float)TtsRateSlider.Value
                    : (_appSettings == null ? 0.92f : _appSettings.TtsVoiceRate);
                var pitch = useCurrentSettings
                    ? (float)TtsPitchSlider.Value
                    : (_appSettings == null ? 1.05f : _appSettings.TtsVoicePitch);
                var ratePercent = (int)Math.Round((rate - 1.0f) * 100.0f);
                var pitchPercent = (int)Math.Round((pitch - 1.0f) * 100.0f);
                var rateText = (ratePercent >= 0 ? "+" : string.Empty) + ratePercent.ToString(CultureInfo.InvariantCulture) + "%";
                var pitchText = (pitchPercent >= 0 ? "+" : string.Empty) + pitchPercent.ToString(CultureInfo.InvariantCulture) + "%";
                var safeText = (text ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;")
                    .Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
                var language = selectedVoice == null || string.IsNullOrWhiteSpace(selectedVoice.Language)
                    ? "ru-RU" : selectedVoice.Language.Replace("&", "&amp;").Replace("\"", "&quot;");
                var ssml = "<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"" +
                    language + "\"><prosody rate=\"" + rateText + "\" pitch=\"" + pitchText + "\">" +
                    safeText + "</prosody></speak>";

                var stream = await _speechSynthesizer.SynthesizeSsmlToStreamAsync(ssml);
                _speechPlayback.SetSource(stream, stream.ContentType);
                _speechPlayback.Play();
                return true;
            }
            catch (Exception ex)
            {
                if (TtsVoiceStatus != null)
                    TtsVoiceStatus.Text = "Ошибка синтеза речи: " + ex.Message;
                Log("TTS ERROR: " + ex.Message);
                return false;
            }
        }

        private void LogModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshLogView();
        }

        private void LogFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshLogView();
        }

        private async void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            var confirmed = await ShowAppConfirmationAsync(
                "Очистить журнал?",
                "Будут удалены текущие записи и сохранённая сетевая диагностика. История измерений и настройки останутся.",
                "Очистить");
            if (!confirmed) return;

            _connectionJournal.Clear();
            _logLines.Clear();
            RefreshLogView();
            if (LogActionStatus != null) LogActionStatus.Text = "Журнал очищен.";
        }

        private void CopyLogButton_Click(object sender, RoutedEventArgs e)
        {
            var text = LogText == null ? string.Empty : LogText.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                if (LogActionStatus != null) LogActionStatus.Text = "Нет записей для копирования.";
                return;
            }

            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(text);
            Windows.ApplicationModel.DataTransfer.DataTransferManager.GetForCurrentView().DataRequested += ShareText_DataRequested;
            _pendingShareText = text;
            Windows.ApplicationModel.DataTransfer.DataTransferManager.ShowShareUI();
            
            if (LogActionStatus != null) LogActionStatus.Text = "Отображаемые записи журнала скопированы.";
        }

        private void RefreshLogView()
        {
            if (LogText == null) return;
            var modeItem = LogModeBox == null ? null : LogModeBox.SelectedItem as ComboBoxItem;
            var mode = modeItem == null ? "SESSION" : (modeItem.Tag as string ?? "SESSION");
            var filterItem = LogFilterBox == null ? null : LogFilterBox.SelectedItem as ComboBoxItem;
            var filter = filterItem == null ? "ALL" : (filterItem.Tag as string ?? "ALL");
            IEnumerable<string> visible = mode == "SAVED"
                ? _connectionJournal.Load().Take(250)
                : _logLines;
            if (filter == "MQTT")
                visible = visible.Where(line => line.IndexOf("MQTT", StringComparison.OrdinalIgnoreCase) >= 0);
            else if (filter == "UDP")
                visible = visible.Where(line => line.IndexOf("UDP", StringComparison.OrdinalIgnoreCase) >= 0);
            else if (filter == "WS")
                visible = visible.Where(line => line.IndexOf("WS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("WebSocket", StringComparison.OrdinalIgnoreCase) >= 0);
            else if (filter == "ACTION")
                visible = visible.Where(line => line.IndexOf("ACTION", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("ДЕЙСТВ", StringComparison.OrdinalIgnoreCase) >= 0);
            else if (filter == "VERIFY")
                visible = visible.Where(line => line.IndexOf("VERIFY", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("ПРОВЕР", StringComparison.OrdinalIgnoreCase) >= 0);
            else if (filter == "ERROR")
                visible = visible.Where(line => line.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("ошибка", StringComparison.OrdinalIgnoreCase) >= 0);
            else if (filter == "SCENARIO")
                visible = visible.Where(line => line.IndexOf("СЦЕНАРИЙ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    line.IndexOf("SCENARIO", StringComparison.OrdinalIgnoreCase) >= 0);
            LogText.Text = string.Join(Environment.NewLine, visible);
        }

        private void Log(string text)
        {
            var line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text;
            _logLines.Insert(0, line);
            if (_logLines.Count > 250)
                _logLines.RemoveRange(250, _logLines.Count - 250);
            RefreshLogView();

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
            public Canvas ChartCanvas { get; set; }
            public TextBlock ChartMeta { get; set; }
            public TextBlock ChartFooter { get; set; }
            public TextBlock UpdatedAtText { get; set; }
        }

        private sealed class PageVisual
        {
            public string Name { get; set; }
            public StackPanel Content { get; set; }
            public PivotItem Item { get; set; }
        }
    }
}