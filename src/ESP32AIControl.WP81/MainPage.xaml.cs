using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
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
        private readonly ScenarioProfileStore _scenarioStore;
        private readonly ScenarioDeviationMonitor _scenarioMonitor;
        private readonly List<ScenarioProfile> _scenarioProfiles = new List<ScenarioProfile>();
        private readonly Wp81VoiceCommandService _voice;
        private VoiceCommandParser _voiceParser;
        private string _selectedScenarioProfileId;
        private bool _refreshingScenarioList;

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
            _scenarioStore = new ScenarioProfileStore();
            _scenarioMonitor = new ScenarioDeviationMonitor(_devices);
            _scenarioMonitor.DeviationsChanged += ScenarioMonitor_DeviationsChanged;
            _voice = new Wp81VoiceCommandService();
            _voiceParser = new VoiceCommandParser(string.Empty, string.Empty, _devices);

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
                ApplySettingsToUi();
                await LoadScenarioProfilesAsync();
                await _scheduler.InitializeAsync();

                GraphFrom.Date = DateTimeOffset.Now.AddDays(-1);
                GraphTo.Date = DateTimeOffset.Now;

                _autoExportTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
                _autoExportTimer.Tick += AutoExportTimer_Tick;
                _autoExportTimer.Start();

                await CleanupHistoryAsync();
                await RefreshScheduleAsync();
                RefreshGraphWidgets();
                UpdateNavigationChrome();
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
            if (index < 0 || index >= MainPivot.Items.Count)
                return;

            NavigationPopup.IsOpen = false;
            MainPivot.SelectedIndex = index;
            UpdateNavigationChrome();
        }

        private void MainPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateNavigationChrome();
        }

        private void UpdateNavigationChrome()
        {
            if (NavHomeButton == null || NavMarfaButton == null ||
                NavCommandsButton == null || NavHistoryButton == null ||
                NavPendingButton == null || PageTitleText == null)
                return;

            switch (MainPivot.SelectedIndex)
            {
                case 0: PageTitleText.Text = "Главная"; break;
                case 1: PageTitleText.Text = "Марфа"; break;
                case 2: PageTitleText.Text = "Команды"; break;
                case 3: PageTitleText.Text = "История и графики"; break;
                case 4: PageTitleText.Text = "Отложенные команды"; break;
                case 5: PageTitleText.Text = "Настройки"; break;
                case 6: PageTitleText.Text = "Сценарии"; break;
                case 7: PageTitleText.Text = "Журнал"; break;
                default: PageTitleText.Text = "ESP32 AI Control"; break;
            }

            SetNavigationButtonState(NavHomeButton, MainPivot.SelectedIndex == 0);
            SetNavigationButtonState(NavMarfaButton, MainPivot.SelectedIndex == 1);
            SetNavigationButtonState(NavCommandsButton, MainPivot.SelectedIndex == 2);
            SetNavigationButtonState(NavHistoryButton, MainPivot.SelectedIndex == 3);
            SetNavigationButtonState(NavPendingButton, MainPivot.SelectedIndex == 4);
        }

        private void SetNavigationButtonState(Button button, bool selected)
        {
            button.Background = new SolidColorBrush(
                selected
                    ? Windows.UI.Color.FromArgb(255, 55, 58, 66)
                    : Windows.UI.Color.FromArgb(0, 0, 0, 0));
            button.Foreground = new SolidColorBrush(
                selected
                    ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
                    : Windows.UI.Color.FromArgb(255, 189, 189, 189));
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            NavigationPopup.IsOpen = !NavigationPopup.IsOpen;
        }

        private void MenuScenarios_Click(object sender, RoutedEventArgs e) { ShowSection(6); }
        private void MenuJournal_Click(object sender, RoutedEventArgs e) { ShowSection(7); }
        private void MenuSettings_Click(object sender, RoutedEventArgs e) { ShowSection(5); }
        private void MenuHistory_Click(object sender, RoutedEventArgs e) { ShowSection(3); }
        private void MenuPending_Click(object sender, RoutedEventArgs e) { ShowSection(4); }

        private void CommandsNavigation_Click(object sender, RoutedEventArgs e) { ShowSection(2); }
        private void HistoryNavigation_Click(object sender, RoutedEventArgs e) { ShowSection(3); }
        private void PendingNavigation_Click(object sender, RoutedEventArgs e) { ShowSection(4); }
        private void MarfaNavigation_Click(object sender, RoutedEventArgs e)
        {
            ShowSection(1);
            VoiceButton_Click(sender, e);
        }
        private void MarfaListenButton_Click(object sender, RoutedEventArgs e) { VoiceButton_Click(sender, e); }

        private void NavigateHome_Click(object sender, RoutedEventArgs e) { ShowSection(0); }
        private void NavigateGraphs_Click(object sender, RoutedEventArgs e) { ShowSection(3); }
        private void NavigateTasks_Click(object sender, RoutedEventArgs e) { ShowSection(4); }
        private void NavigateSettings_Click(object sender, RoutedEventArgs e) { ShowSection(5); }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            LogText.Text = string.Empty;
        }

        private void CopyLogButton_Click(object sender, RoutedEventArgs e)
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(LogText.Text ?? string.Empty);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            JournalStatusText.Text = "Журнал скопирован в буфер обмена.";
        }

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

                _voiceParser = new VoiceCommandParser(
                    _appSettings.VoiceOpenCommand,
                    _appSettings.VoiceCloseCommand,
                    _devices);
                ApplyScenarioProfilesToEngine();

                await _settingsStore.SaveAsync(_appSettings);
                await CleanupHistoryAsync();

                OpenCommandSummary.Text = string.IsNullOrWhiteSpace(_appSettings.VoiceOpenCommand)
                    ? "Команда не настроена. Укажите MQTT-цель в настройках."
                    : _appSettings.VoiceOpenCommand;
                CloseCommandSummary.Text = string.IsNullOrWhiteSpace(_appSettings.VoiceCloseCommand)
                    ? "Команда не настроена. Укажите MQTT-цель в настройках."
                    : _appSettings.VoiceCloseCommand;
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
                _appSettings.VoiceCloseCommand,
                _devices);
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
            OpenCommandSummary.Text = string.IsNullOrWhiteSpace(_appSettings.VoiceOpenCommand)
                ? "Команда не настроена. Укажите MQTT-цель в настройках."
                : _appSettings.VoiceOpenCommand;
            CloseCommandSummary.Text = string.IsNullOrWhiteSpace(_appSettings.VoiceCloseCommand)
                ? "Команда не настроена. Укажите MQTT-цель в настройках."
                : _appSettings.VoiceCloseCommand;
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
            var host = (HostBox.Text ?? string.Empty).Trim();
            var portText = (PortBox.Text ?? string.Empty).Trim();
            var prefix = (PrefixBox.Text ?? string.Empty).Trim().Trim('/');
            int port;

            if (string.IsNullOrWhiteSpace(host))
                throw new InvalidOperationException("Укажите адрес MQTT-сервера.");
            if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) ||
                port < 1 || port > 65535)
                throw new InvalidOperationException("Порт MQTT должен быть числом от 1 до 65535.");
            if (string.IsNullOrWhiteSpace(prefix))
                throw new InvalidOperationException("Укажите MQTT prefix.");

            _settings.Host = host;
            _settings.Port = port.ToString(CultureInfo.InvariantCulture);
            _settings.Prefix = prefix;
            _settings.UserName = (UserNameBox.Text ?? string.Empty).Trim();
            _settings.Password = PasswordBox.Password ?? string.Empty;
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
                if (string.IsNullOrWhiteSpace(ScenarioTitleBox.Text))
                    ScenarioTitleBox.Text = System.IO.Path.GetFileNameWithoutExtension(file.Name);

                ScenarioPlanText.Text = "Файл загружен в редактор. Проверьте содержимое и нажмите «Сохранить сценарий».";
                ScenarioStatus.Text = "Сценарий загружен в редактор; сохранение ещё не выполнено.";
                Log("SCENARIO IMPORT: " + file.Name);
            }
            catch (Exception ex)
            {
                ScenarioStatus.Text = "Ошибка импорта сценария: " + ex.Message;
                Log("SCENARIO IMPORT ERROR: " + ex.Message);
            }
        }

        private void TestScenarioButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var source = ScenarioTextBox.Text ?? string.Empty;
                var compiled = ScenarioLanguageParser.Compile(source);
                var report = new List<string>();
                report.Add("Полный разбор IoT-сценария: " +
                    compiled.Rules.Count.ToString(CultureInfo.InvariantCulture) + " правил.");
                report.Add("Идентификаторов: " +
                    compiled.Identifiers.Count.ToString(CultureInfo.InvariantCulture) + ".");

                if (compiled.Errors.Count > 0)
                {
                    report.Add("Ошибки синтаксиса:");
                    for (var i = 0; i < compiled.Errors.Count; i++)
                        report.Add("• " + compiled.Errors[i]);
                    ScenarioPlanText.Text = string.Join(Environment.NewLine, report);
                    ScenarioStatus.Text = "Сценарий не прошёл проверку. Сохранение заблокировано.";
                    Log("SCENARIO TEST INVALID: " + string.Join(" | ", compiled.Errors.ToArray()));
                    return;
                }

                if (compiled.Rules.Count == 0)
                {
                    report.Add("Не найдены правила IF / THEN с действиями.");
                    ScenarioPlanText.Text = string.Join(Environment.NewLine, report);
                    ScenarioStatus.Text = "В сценарии нет исполняемых правил.";
                    return;
                }

                _commandExecutor.SetScenarioScript(source);
                var open = (VoiceOpenCommandBox.Text ?? string.Empty).Trim();
                var close = (VoiceCloseCommandBox.Text ?? string.Empty).Trim();

                if (!string.IsNullOrEmpty(open))
                    report.Add(LogScenarioPlan("OPEN", open));

                if (!string.IsNullOrEmpty(close))
                    report.Add(LogScenarioPlan("CLOSE", close));

                if (string.IsNullOrEmpty(open) && string.IsNullOrEmpty(close))
                    report.Add("Команды открытия и закрытия не настроены. Проверка обратного плана пропущена.");

                ScenarioPlanText.Text = string.Join(Environment.NewLine + Environment.NewLine, report);
                ScenarioStatus.Text = "Проверка завершена. Правил: " +
                    compiled.Rules.Count.ToString(CultureInfo.InvariantCulture) + ".";
                Log("SCENARIO TEST COMPLETE: rules=" +
                    compiled.Rules.Count.ToString(CultureInfo.InvariantCulture));
                ApplyScenarioProfilesToEngine();
            }
            catch (Exception ex)
            {
                ScenarioPlanText.Text = "Проверка не выполнена: " + ex.Message;
                ScenarioStatus.Text = "Ошибка проверки: " + ex.Message;
                Log("SCENARIO TEST ERROR: " + ex.Message);
                ApplyScenarioProfilesToEngine();
            }
        }

        private async void SaveScenarioButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var source = ScenarioTextBox.Text ?? string.Empty;
                if (string.IsNullOrWhiteSpace(source))
                    throw new InvalidOperationException("Содержимое сценария пустое.");

                var validation = ScenarioLanguageParser.Compile(source);
                if (validation.Errors.Count > 0)
                    throw new InvalidOperationException(
                        "Ошибки синтаксиса: " + string.Join("; ", validation.Errors.ToArray()));
                if (validation.Rules.Count == 0)
                    throw new InvalidOperationException("В сценарии нет правил IF / THEN с действиями.");

                var title = (ScenarioTitleBox.Text ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(title))
                    title = "Сценарий " + (_scenarioProfiles.Count + 1).ToString(CultureInfo.InvariantCulture);

                ScenarioProfile profile = null;
                if (!string.IsNullOrEmpty(_selectedScenarioProfileId))
                    profile = _scenarioProfiles.FirstOrDefault(delegate(ScenarioProfile item)
                    {
                        return string.Equals(item.Id, _selectedScenarioProfileId, StringComparison.Ordinal);
                    });

                var isNew = profile == null;
                if (isNew)
                {
                    profile = new ScenarioProfile
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        Enabled = true
                    };
                    _scenarioProfiles.Add(profile);
                }

                profile.Title = title;
                profile.Source = source;
                profile.Enabled = ScenarioEnabledCheckBox.IsChecked == true;
                _selectedScenarioProfileId = profile.Id;

                await _scenarioStore.SaveAsync(_scenarioProfiles);
                ApplyScenarioProfilesToEngine();
                await _settingsStore.SaveAsync(_appSettings);
                RefreshScenarioProfilesUi();

                ScenarioStatus.Text = (isNew ? "Сценарий добавлен: " : "Сценарий обновлён: ") + title +
                    (profile.Enabled ? " (активен)." : " (выключен).");
                Log("SCENARIO " + (isNew ? "ADDED: " : "UPDATED: ") + title +
                    "; active rules=" + _commandExecutor.GetScenarioRuleCount().ToString(CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                ScenarioStatus.Text = "Ошибка сохранения: " + ex.Message;
                Log("SCENARIO SAVE ERROR: " + ex.Message);
            }
        }

        private async void DeleteScenarioButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrEmpty(_selectedScenarioProfileId))
                    throw new InvalidOperationException("Сначала выберите сохранённый сценарий.");

                var profile = _scenarioProfiles.FirstOrDefault(delegate(ScenarioProfile item)
                {
                    return string.Equals(item.Id, _selectedScenarioProfileId, StringComparison.Ordinal);
                });
                if (profile == null)
                    throw new InvalidOperationException("Сценарий уже удалён.");

                var dialog = new Windows.UI.Popups.MessageDialog(
                    "Удалить сценарий «" + profile.Title + "»?");
                dialog.Commands.Add(new Windows.UI.Popups.UICommand("Удалить"));
                dialog.Commands.Add(new Windows.UI.Popups.UICommand("Отмена"));
                dialog.DefaultCommandIndex = 1;
                dialog.CancelCommandIndex = 1;
                var answer = await dialog.ShowAsync();
                if (answer == null || answer.Label != "Удалить")
                    return;

                _scenarioProfiles.Remove(profile);
                _selectedScenarioProfileId = null;
                await _scenarioStore.SaveAsync(_scenarioProfiles);
                ApplyScenarioProfilesToEngine();
                await _settingsStore.SaveAsync(_appSettings);
                RefreshScenarioProfilesUi();
                ClearScenarioEditor();
                ScenarioStatus.Text = "Сценарий удалён.";
                Log("SCENARIO DELETED: " + profile.Title);
            }
            catch (Exception ex)
            {
                ScenarioStatus.Text = "Не удалось удалить сценарий: " + ex.Message;
                Log("SCENARIO DELETE ERROR: " + ex.Message);
            }
        }

        private void NewScenarioButton_Click(object sender, RoutedEventArgs e)
        {
            _selectedScenarioProfileId = null;
            _refreshingScenarioList = true;
            ScenarioList.SelectedIndex = -1;
            _refreshingScenarioList = false;
            ClearScenarioEditor();
            ScenarioStatus.Text = "Новый сценарий. Введите название и код, затем сохраните.";
        }

        private void ClearScenarioButton_Click(object sender, RoutedEventArgs e)
        {
            NewScenarioButton_Click(sender, e);
        }

        private void ClearScenarioEditor()
        {
            ScenarioTitleBox.Text = string.Empty;
            ScenarioTextBox.Text = string.Empty;
            ScenarioEnabledCheckBox.IsChecked = true;
            ScenarioPlanText.Text = "Новый сценарий ещё не сохранён.";
        }

        private void ScenarioList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_refreshingScenarioList)
                return;

            var index = ScenarioList.SelectedIndex;
            if (index < 0 || index >= _scenarioProfiles.Count)
                return;

            var profile = _scenarioProfiles[index];
            _selectedScenarioProfileId = profile.Id;
            LoadScenarioProfileIntoEditor(profile);
        }

        private void LoadScenarioProfileIntoEditor(ScenarioProfile profile)
        {
            if (profile == null)
                return;

            ScenarioTitleBox.Text = profile.Title ?? string.Empty;
            ScenarioTextBox.Text = profile.Source ?? string.Empty;
            ScenarioEnabledCheckBox.IsChecked = profile.Enabled;
            ScenarioPlanText.Text = "Выбран сценарий «" + profile.Title + "». Нажмите «Проверить план» для анализа.";
            ScenarioStatus.Text = profile.Enabled ? "Сценарий активен." : "Сценарий выключен.";
        }

        private void RefreshScenarioProfilesUi()
        {
            _refreshingScenarioList = true;
            ScenarioList.Items.Clear();

            var selectedIndex = -1;
            for (var i = 0; i < _scenarioProfiles.Count; i++)
            {
                var profile = _scenarioProfiles[i];
                ScenarioList.Items.Add((profile.Enabled ? "● " : "○ ") +
                    (string.IsNullOrWhiteSpace(profile.Title) ? "Без названия" : profile.Title));
                if (!string.IsNullOrEmpty(_selectedScenarioProfileId) &&
                    string.Equals(profile.Id, _selectedScenarioProfileId, StringComparison.Ordinal))
                    selectedIndex = i;
            }

            ScenarioList.SelectedIndex = selectedIndex;
            _refreshingScenarioList = false;

            if (selectedIndex >= 0)
                LoadScenarioProfileIntoEditor(_scenarioProfiles[selectedIndex]);
        }

        private async Task LoadScenarioProfilesAsync()
        {
            IList<ScenarioProfile> loaded;
            try
            {
                loaded = await _scenarioStore.LoadAsync();
            }
            catch (Exception ex)
            {
                // A damaged profile file must not prevent MQTT, history or the rest
                // of the application from starting. Keep the legacy setting as fallback.
                loaded = new List<ScenarioProfile>();
                ScenarioStatus.Text = "Не удалось прочитать список сценариев: " + ex.Message;
                Log("SCENARIO LOAD ERROR: " + ex.Message);
            }

            _scenarioProfiles.Clear();
            foreach (var profile in loaded)
            {
                if (profile == null || string.IsNullOrWhiteSpace(profile.Id) ||
                    string.IsNullOrWhiteSpace(profile.Source))
                    continue;
                _scenarioProfiles.Add(profile.Clone());
            }

            // Migrate the former single-script setting without losing the user's existing scenario.
            if (_scenarioProfiles.Count == 0 && !string.IsNullOrWhiteSpace(_appSettings.IoTManagerScenario))
            {
                _scenarioProfiles.Add(new ScenarioProfile
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Title = "Основной сценарий",
                    Source = _appSettings.IoTManagerScenario,
                    Enabled = true
                });
                await _scenarioStore.SaveAsync(_scenarioProfiles);
                ScenarioStatus.Text = "Старый сценарий перенесён в новый список.";
            }

            _selectedScenarioProfileId = _scenarioProfiles.Count > 0
                ? _scenarioProfiles[0].Id
                : null;
            ApplyScenarioProfilesToEngine();
            RefreshScenarioProfilesUi();
        }

        private void ApplyScenarioProfilesToEngine()
        {
            var activeScripts = new List<string>();
            foreach (var profile in _scenarioProfiles)
                if (profile.Enabled && !string.IsNullOrWhiteSpace(profile.Source))
                    activeScripts.Add(profile.Source.Trim());

            _appSettings.IoTManagerScenario = string.Join(
                Environment.NewLine + Environment.NewLine, activeScripts);
            _commandExecutor.SetScenarioScript(_appSettings.IoTManagerScenario);
            _scenarioMonitor.SetProfiles(_scenarioProfiles);
            _scenarioMonitor.Observe();
        }

        private async void ScenarioMonitor_DeviationsChanged(
            object sender,
            ScenarioDeviationChangedEventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
            {
                var lines = new List<string>();
                var diagnostics = _scenarioMonitor.Diagnostics;
                if (diagnostics.Count > 0)
                {
                    lines.Add("Ошибки сценариев:");
                    for (var i = 0; i < diagnostics.Count; i++)
                        lines.Add("• " + diagnostics[i]);
                }

                if (e == null || e.Deviations.Count == 0)
                {
                    lines.Add(diagnostics.Count == 0
                        ? "Отклонений от активных сценариев нет. Контроль выполняется по MQTT-обновлениям."
                        : "Проверка отклонений включена только для сценариев без ошибок.");
                }
                else
                {
                    lines.Add("Активные отклонения: " +
                        e.Deviations.Count.ToString(CultureInfo.InvariantCulture));
                    foreach (var deviation in e.Deviations.Take(12))
                        lines.Add("• " + deviation.Message);
                    if (e.Deviations.Count > 12)
                        lines.Add("… и ещё " + (e.Deviations.Count - 12).ToString(CultureInfo.InvariantCulture));
                }

                ScenarioDeviationText.Text = string.Join(Environment.NewLine, lines);
            });
        }

        private string LogScenarioPlan(string name, string command)
        {
            Log("SCENARIO TEST " + name + ": " + command);

            var plan = _commandExecutor.BuildPlan(command);
            var lines = new List<string>();
            lines.Add(name + ": " + command);

            if (plan.Actions.Count == 0)
                lines.Add("  Нет дополнительных действий в плане. Команда будет проверена исполнителем напрямую.");

            for (var i = 0; i < plan.Actions.Count; i++)
            {
                var action = plan.Actions[i];
                var line = (action.IsDependency ? "  Зависимость: " : "  Действие: ") + action.Command;
                if (!string.IsNullOrEmpty(action.Reason))
                    line += " — " + action.Reason;
                lines.Add(line);
                Log("PLAN " + (action.IsDependency ? "DEP " : "ACT ") + action.Command +
                    (string.IsNullOrEmpty(action.Reason) ? string.Empty : " | " + action.Reason));
            }

            return string.Join(Environment.NewLine, lines);
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

            try
            {
                _scenarioMonitor.Observe();
            }
            catch (Exception ex)
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
                {
                    Log("SCENARIO MONITOR ERROR: " + ex.Message);
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
                var isConnected = e.State == MqttConnectionState.Connected;
                TopConnectionText.Text = isConnected ? "ON" : "OFF";
                ConnectionIndicator.Fill = new SolidColorBrush(
                    isConnected ? Windows.UI.Colors.LimeGreen : Windows.UI.Colors.IndianRed);
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

            visual.Container = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 8),
                Tag = state.Key
            };

            var type = NormalizeType(state.WidgetType);
            var definition = NormalizeType(state.DefinitionName);
            var isSwitch = type == "switch" || type == "toggle" || type == "checkbox" ||
                           type == "inputbool" || definition == "toggle";
            var isAction = type == "button" || type == "action" || type == "buttonout" ||
                           type == "buttonin" || type == "pushbutton" ||
                           definition == "button" || definition == "vbtn" || definition == "btn";
            var isRange = definition == "range" || definition == "rangeservo" || type == "range" || type == "rangeservo";
            var isSlider = isRange || type == "slider";
            var isNumericInput = definition == "inputdgt" || type == "inputdgt" || type == "inputint" || type == "inputfloat" || type == "number";
            var isDateInput = definition == "inputdate" || type == "inputdate";
            var isTimeInput = definition == "inputtm" || type == "inputtm";
            var isSelect = definition == "select" || type == "select";
            var isCircularProgress = definition == "progressround" || type == "progressround";
            var isFillGauge = definition == "fillgauge" || type == "fillgauge";
            var isProgressLine = definition == "progressline" || type == "progress";
            var isProgress = isCircularProgress || isFillGauge || isProgressLine;
            var isChart = definition.StartsWith("chart", StringComparison.Ordinal) || type == "chart";
            var isInput = !isSwitch && !isAction && !isSlider && !isNumericInput && !isDateInput && !isTimeInput && !isSelect && !isProgress && !isChart &&
                          (type == "input" || type == "inputtext" || type == "text" || type == "inputtxt" || type == "inputstr" || type == "inputstring" || definition == "inputtxt");

            var shell = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 43, 43, 43)),
                Padding = new Thickness(12, 10, 12, 10),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 57, 59, 68)),
                CornerRadius = new CornerRadius(12)
            };

            visual.Shell = shell;
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });

            var icon = new TextBlock
            {
                Text = isSwitch ? "◉" : (isAction ? "●" : (isRange || isSlider ? "◌" : (isProgress ? "◍" : (isChart ? "▥" : "◆")))),
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
                Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };

            if (isSwitch)
            {
                var check = new CheckBox
                {
                    Content = "Вкл.",
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 209, 213, 219)),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    MinWidth = 82
                };
                check.Checked += delegate { PublishSwitchValue(visual, true); };
                check.Unchecked += delegate { PublishSwitchValue(visual, false); };
                visual.Control = check;
                visual.SwitchControl = check;
                Grid.SetColumn(check, 2);
                row.Children.Add(check);
                visual.ValueText.Visibility = Visibility.Collapsed;
            }
            else
            {
                var valueBadge = new Border
                {
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 54, 64)),
                    Child = visual.ValueText,
                    Padding = new Thickness(5, 5, 5, 5),
                    MinWidth = 76,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                visual.ValueBadge = valueBadge;
                Grid.SetColumn(valueBadge, 2);
                row.Children.Add(valueBadge);
            }

            shell.Child = row;
            visual.Container.Children.Add(shell);

            if (isAction)
            {
                var button = new Button
                {
                    Content = "Выполнить",
                    MinWidth = 130,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 5, 0, 0)
                };
                button.Click += async delegate { await PublishControl(visual, "1"); };
                visual.Control = button;
                visual.Container.Children.Add(button);
            }
            else if (isSlider)
            {
                var minimum = ReadWidgetNumber(state.Minimum, 0);
                var maximum = ReadWidgetNumber(state.Maximum, definition == "rangeservo" ? 180 : 100);
                if (maximum <= minimum) maximum = minimum + 100;
                var step = ReadWidgetNumber(state.Step, 1);
                var scale = ReadWidgetNumber(state.ScaleFactor, 1);
                if (step <= 0) step = 1;
                if (scale == 0) scale = 1;
                var slider = new Slider
                {
                    Minimum = minimum, Maximum = maximum, StepFrequency = step,
                    Width = 280, HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(34, 2, 0, 0)
                };
                slider.PointerReleased += async delegate
                {
                    if (_syncingControls) return;
                    await PublishControl(visual, (slider.Value / scale).ToString("0.####", CultureInfo.InvariantCulture));
                };
                visual.Control = slider;
                visual.SliderControl = slider;
                visual.Container.Children.Add(slider);
                visual.UnitText = new TextBlock
                {
                    Text = "Диапазон " + FormatWidgetNumber(minimum) + " … " + FormatWidgetNumber(maximum) +
                           (string.IsNullOrWhiteSpace(state.Unit) ? string.Empty : " " + state.Unit),
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 156, 163, 175)),
                    Margin = new Thickness(34, 0, 0, 0)
                };
                visual.Container.Children.Add(visual.UnitText);
            }
            else if (isNumericInput || isInput)
            {
                var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(34, 4, 0, 0) };
                var input = new TextBox { Text = state.Value ?? string.Empty, Width = 210, MinWidth = 100, FontSize = 16 };
                var send = new Button { Content = "Отправить", Margin = new Thickness(8, 0, 0, 0), MinWidth = 100 };
                send.Click += async delegate
                {
                    var value = input.Text == null ? string.Empty : input.Text.Trim();
                    if (!string.IsNullOrEmpty(value)) await PublishControl(visual, value);
                };
                visual.InputControl = input;
                visual.Control = input;
                inputRow.Children.Add(input);
                inputRow.Children.Add(send);
                visual.Container.Children.Add(inputRow);
            }
            else if (isDateInput)
            {
                var dateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(34, 4, 0, 0) };
                var date = new DatePicker { HorizontalAlignment = HorizontalAlignment.Left, Width = 210 };
                DateTimeOffset parsedDate;
                if (DateTimeOffset.TryParse(state.Value, CultureInfo.CurrentCulture, DateTimeStyles.None, out parsedDate)) date.Date = parsedDate;
                var sendDate = new Button { Content = "Отправить", Margin = new Thickness(8, 0, 0, 0) };
                sendDate.Click += async delegate { await PublishControl(visual, date.Date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)); };
                visual.DateControl = date;
                dateRow.Children.Add(date); dateRow.Children.Add(sendDate);
                visual.Container.Children.Add(dateRow);
            }
            else if (isTimeInput)
            {
                var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(34, 4, 0, 0) };
                var time = new TimePicker { HorizontalAlignment = HorizontalAlignment.Left, Width = 210 };
                TimeSpan parsedTime;
                if (TimeSpan.TryParse(state.Value, out parsedTime)) time.Time = parsedTime;
                var sendTime = new Button { Content = "Отправить", Margin = new Thickness(8, 0, 0, 0) };
                sendTime.Click += async delegate { await PublishControl(visual, time.Time.ToString(@"hh\:mm", CultureInfo.InvariantCulture)); };
                visual.TimeControl = time;
                timeRow.Children.Add(time); timeRow.Children.Add(sendTime);
                visual.Container.Children.Add(timeRow);
            }
            else if (isSelect)
            {
                var select = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(34, 4, 0, 0) };
                var options = state.Options ?? new string[0];
                if (options.Length == 0) options = new[] { "Выключен", "Включен" };
                foreach (var option in options) select.Items.Add(option);
                int selected;
                if (!int.TryParse(state.Value, out selected)) selected = 0;
                select.SelectedIndex = Math.Max(0, Math.Min(options.Length - 1, selected));
                select.SelectionChanged += async delegate
                {
                    if (_syncingControls || select.SelectedIndex < 0) return;
                    await PublishControl(visual, select.SelectedIndex.ToString(CultureInfo.InvariantCulture));
                };
                visual.SelectControl = select;
                visual.Control = select;
                visual.Container.Children.Add(select);
            }
            else if (isProgress)
            {
                var minimum = ReadWidgetNumber(state.Minimum, 0);
                var maximum = ReadWidgetNumber(state.Maximum, 100);
                if (maximum <= minimum) maximum = minimum + 100;

                if (isCircularProgress || isFillGauge)
                {
                    visual.IsFillGauge = isFillGauge;
                    visual.GaugeCanvas = new Canvas
                    {
                        Width = 112,
                        Height = 112,
                        Margin = new Thickness(34, 5, 0, 0)
                    };
                    visual.Container.Children.Add(visual.GaugeCanvas);
                    visual.UnitText = new TextBlock
                    {
                        Text = "Максимум " + FormatWidgetNumber(maximum) +
                               (string.IsNullOrWhiteSpace(state.Unit) ? string.Empty : " " + state.Unit),
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 156, 163, 175)),
                        Margin = new Thickness(34, 0, 0, 0)
                    };
                    visual.Container.Children.Add(visual.UnitText);
                }
                else
                {
                    var progress = new ProgressBar
                    {
                        Minimum = minimum,
                        Maximum = maximum,
                        Value = Math.Max(minimum, Math.Min(maximum, ReadWidgetNumber(state.Value, minimum))),
                        Height = 12,
                        Margin = new Thickness(34, 8, 8, 4)
                    };
                    visual.ProgressControl = progress;
                    visual.Container.Children.Add(progress);
                    visual.UnitText = new TextBlock
                    {
                        Text = (state.Value ?? "0") + " / " + FormatWidgetNumber(maximum) +
                               (string.IsNullOrWhiteSpace(state.Unit) ? string.Empty : " " + state.Unit),
                        FontSize = 11,
                        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 156, 163, 175)),
                        Margin = new Thickness(34, 0, 0, 0)
                    };
                    visual.Container.Children.Add(visual.UnitText);
                }
            }
            else if (isChart)
            {
                visual.InlineChartCanvas = new Canvas
                {
                    Height = 92,
                    Width = 300,
                    MinWidth = 240,
                    Margin = new Thickness(34, 5, 0, 0)
                };
                visual.InlineChartCanvas.SizeChanged += delegate { DrawInlineChart(visual); };
                visual.Container.Children.Add(visual.InlineChartCanvas);

                var openChart = new Button
                {
                    Content = "Открыть историю",
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(34, 2, 0, 0)
                };
                openChart.Click += delegate { OpenWidgetHistory(visual.State); };
                visual.Container.Children.Add(openChart);
            }
            else if (!isSwitch)
            {
                var info = new TextBlock
                {
                    Text = "Только отображение",
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 156, 163, 175)),
                    FontSize = 11,
                    Margin = new Thickness(34, 2, 0, 0)
                };
                visual.Container.Children.Add(info);
                if (!isAction) visual.Control = info;
            }

            return visual;
        }

        private void UpdateWidgetVisual(WidgetState state)
        {
            if (state == null || string.IsNullOrEmpty(state.Id) ||
                string.Equals(state.DefinitionName, "nil", StringComparison.OrdinalIgnoreCase))
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
                var configurationChanged = visual.State != null &&
                    (!string.Equals(visual.State.WidgetType, state.WidgetType, StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(visual.State.DefinitionName, state.DefinitionName, StringComparison.OrdinalIgnoreCase) ||
                     !string.Equals(visual.State.Minimum, state.Minimum, StringComparison.Ordinal) ||
                     !string.Equals(visual.State.Maximum, state.Maximum, StringComparison.Ordinal) ||
                     !string.Equals(visual.State.Step, state.Step, StringComparison.Ordinal) ||
                     !string.Equals(visual.State.Unit, state.Unit, StringComparison.Ordinal) ||
                     !string.Equals(visual.State.ScaleFactor, state.ScaleFactor, StringComparison.Ordinal) ||
                     !string.Equals(visual.State.Options == null ? string.Empty : string.Join("|", visual.State.Options), state.Options == null ? string.Empty : string.Join("|", state.Options), StringComparison.Ordinal) ||
                     !string.Equals(visual.State.ColorConfig, state.ColorConfig, StringComparison.Ordinal) ||
                     !string.Equals(visual.State.WarningMinimum, state.WarningMinimum, StringComparison.Ordinal) ||
                     !string.Equals(visual.State.WarningMaximum, state.WarningMaximum, StringComparison.Ordinal));
                if (configurationChanged)
                {
                    PageVisual oldPage;
                    if (_pages.TryGetValue(visual.PageName, out oldPage))
                        oldPage.Content.Children.Remove(visual.Container);
                    visual = CreateWidgetVisual(state);
                    _visuals[state.Key] = visual;
                    InsertWidgetVisual(visual);
                    CleanupEmptyPages();
                }
                else
                {
                    if (!string.Equals(visual.PageName, newPage, StringComparison.Ordinal))
                        MoveWidgetVisual(visual, newPage);
                    visual.State = state;
                    visual.Title.Text = GetTitle(state);
                }
                _widgetIndex[state.Key] = state;
            }

            _syncingControls = true;
            try
            {
                ApplyValueToVisual(visual, state.Value);
                ApplyWidgetThresholdStyle(visual, state);
            }
            finally { _syncingControls = false; }

            if (visual.InlineChartCanvas != null)
                RefreshInlineChart(visual);
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
                Content = page.Content,
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
                visual.ValueText.Text = string.IsNullOrEmpty(value) ? "—" :
                    value + (string.IsNullOrWhiteSpace(visual.State.Unit) ? string.Empty : " " + visual.State.Unit);

            var check = visual.SwitchControl;
            if (check != null)
            {
                check.IsChecked = ParseBoolean(value);
                return;
            }

            var slider = visual.SliderControl;
            if (slider != null)
            {
                double number;
                var scale = ReadWidgetNumber(visual.State.ScaleFactor, 1);
                if (scale == 0) scale = 1;
                if (double.TryParse((value ?? string.Empty).Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    slider.Value = Math.Max(slider.Minimum, Math.Min(slider.Maximum, number * scale));
            }

            if (visual.InputControl != null && visual.InputControl.FocusState == FocusState.Unfocused)
                visual.InputControl.Text = value ?? string.Empty;
            if (visual.SelectControl != null)
            {
                int selected;
                if (int.TryParse(value, out selected) && selected >= 0 && selected < visual.SelectControl.Items.Count)
                    visual.SelectControl.SelectedIndex = selected;
            }
            if (visual.ProgressControl != null)
            {
                var progressValue = ReadWidgetNumber(value, visual.ProgressControl.Minimum);
                visual.ProgressControl.Value = Math.Max(visual.ProgressControl.Minimum,
                    Math.Min(visual.ProgressControl.Maximum, progressValue));
                if (visual.UnitText != null)
                    visual.UnitText.Text = FormatWidgetNumber(progressValue) + " / " +
                        FormatWidgetNumber(visual.ProgressControl.Maximum) +
                        (string.IsNullOrWhiteSpace(visual.State.Unit) ? string.Empty : " " + visual.State.Unit);
            }
            if (visual.DateControl != null)
            {
                DateTimeOffset date;
                if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.None, out date)) visual.DateControl.Date = date;
            }
            if (visual.TimeControl != null)
            {
                TimeSpan time;
                if (TimeSpan.TryParse(value, out time)) visual.TimeControl.Time = time;
            }
        }

        private static Windows.UI.Color ResolveWidgetColor(string raw, double value)
        {
            var fallback = Windows.UI.Color.FromArgb(255, 144, 202, 249);
            if (double.IsNaN(value) || string.IsNullOrWhiteSpace(raw)) return fallback;
            var text = raw.Trim();
            if (text.StartsWith("[", StringComparison.Ordinal))
            {
                var objects = System.Text.RegularExpressions.Regex.Matches(text, "\\{[^}]*\\}");
                var selectedLevel = double.NegativeInfinity;
                var selectedColor = string.Empty;
                foreach (System.Text.RegularExpressions.Match item in objects)
                {
                    var levelMatch = System.Text.RegularExpressions.Regex.Match(item.Value, "\\\"level\\\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)");
                    var colorMatch = System.Text.RegularExpressions.Regex.Match(item.Value, "\\\"value\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"");
                    double level;
                    if (levelMatch.Success && colorMatch.Success && double.TryParse(levelMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out level) && level <= value && level >= selectedLevel)
                    { selectedLevel = level; selectedColor = colorMatch.Groups[1].Value; }
                }
                text = selectedColor;
            }
            if ((text.Length == 7 || text.Length == 9) && text[0] == '#')
            {
                byte a = 255, r, g, b;
                var offset = text.Length == 9 ? 3 : 1;
                if (text.Length == 9 && !byte.TryParse(text.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a)) a = 255;
                if (byte.TryParse(text.Substring(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
                    byte.TryParse(text.Substring(offset + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
                    byte.TryParse(text.Substring(offset + 4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
                    return Windows.UI.Color.FromArgb(a, r, g, b);
            }
            switch (text.ToLowerInvariant())
            {
                case "red": return Windows.UI.Colors.Red;
                case "green": return Windows.UI.Colors.Green;
                case "blue": return Windows.UI.Colors.Blue;
                case "orange": return Windows.UI.Colors.Orange;
                case "yellow": return Windows.UI.Colors.Yellow;
                case "white": return Windows.UI.Colors.White;
                case "gray": case "grey": return Windows.UI.Colors.Gray;
                case "lime": return Windows.UI.Colors.Lime;
                case "cyan": return Windows.UI.Colors.Cyan;
                case "magenta": return Windows.UI.Colors.Magenta;
            }
            return fallback;
        }

        private static string FormatWidgetNumber(double value)
        {
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private void DrawCircularGauge(WidgetVisual visual, Windows.UI.Color accent)
        {
            if (visual == null || visual.GaugeCanvas == null || visual.State == null)
                return;

            var canvas = visual.GaugeCanvas;
            canvas.Children.Clear();

            var state = visual.State;
            var minimum = ReadWidgetNumber(state.Minimum, 0);
            var maximum = ReadWidgetNumber(state.Maximum, 100);
            if (maximum <= minimum) maximum = minimum + 100;
            var number = ReadWidgetNumber(state.Value, minimum);
            var fraction = Math.Max(0.0, Math.Min(1.0, (number - minimum) / (maximum - minimum)));
            var config = state.ConfigJson ?? string.Empty;
            var stroke = ReadWidgetNumber(ReadJsonNumber(config, "stroke").HasValue
                ? ReadJsonNumber(config, "stroke").Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty, visual.IsFillGauge ? 7.0 : 20.0);
            stroke = Math.Max(4.0, Math.Min(24.0, stroke));

            var ringColor = accent;
            var configuredRing = ReadJsonString(config, visual.IsFillGauge ? "circleColor" : "color");
            if (!string.IsNullOrWhiteSpace(configuredRing))
                ringColor = ResolveWidgetColor(configuredRing, number);

            if (visual.IsFillGauge)
            {
                var waveColor = ringColor;
                var configuredWave = ReadJsonString(config, "waveColor");
                if (!string.IsNullOrWhiteSpace(configuredWave))
                    waveColor = ResolveWidgetColor(configuredWave, number);

                var fill = new Ellipse
                {
                    Width = 64,
                    Height = 64,
                    Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(
                        (byte)(24 + fraction * 82), waveColor.R, waveColor.G, waveColor.B))
                };
                Canvas.SetLeft(fill, 24);
                Canvas.SetTop(fill, 24);
                canvas.Children.Add(fill);

                var gaugeRing = new Ellipse
                {
                    Width = 86,
                    Height = 86,
                    Stroke = new SolidColorBrush(ringColor),
                    StrokeThickness = stroke,
                    Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
                };
                Canvas.SetLeft(gaugeRing, 13);
                Canvas.SetTop(gaugeRing, 13);
                canvas.Children.Add(gaugeRing);
            }
            else
            {
                var semicircle = Regex.IsMatch(config,
                    "\"semicircle\"\\s*:\\s*(?:\"1\"|1|true)",
                    RegexOptions.IgnoreCase);
                if (semicircle)
                {
                    var track = new Polyline
                    {
                        Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 77, 80, 88)),
                        StrokeThickness = stroke
                    };
                    var trackPoints = new PointCollection();
                    for (var i = 0; i <= 32; i++)
                    {
                        var angle = (180.0 + 180.0 * i / 32.0) * Math.PI / 180.0;
                        trackPoints.Add(new Windows.Foundation.Point(
                            56 + 39 * Math.Cos(angle),
                            56 + 39 * Math.Sin(angle)));
                    }
                    track.Points = trackPoints;
                    canvas.Children.Add(track);
                }
                else
                {
                    var track = new Ellipse
                    {
                        Width = 86,
                        Height = 86,
                        Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 77, 80, 88)),
                        StrokeThickness = stroke,
                        Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
                    };
                    Canvas.SetLeft(track, 13);
                    Canvas.SetTop(track, 13);
                    canvas.Children.Add(track);
                }

                if (fraction > 0.001)
                {
                    var arc = new Polyline
                    {
                        Stroke = new SolidColorBrush(ringColor),
                        StrokeThickness = stroke
                    };
                    var points = new PointCollection();
                    var segments = Math.Max(2, (int)Math.Ceiling(64 * fraction));
                    var startAngle = semicircle ? 180.0 : -90.0;
                    var sweepAngle = (semicircle ? 180.0 : 360.0) * fraction;
                    for (var i = 0; i <= segments; i++)
                    {
                        var angle = (startAngle + sweepAngle * i / segments) * Math.PI / 180.0;
                        points.Add(new Windows.Foundation.Point(
                            56 + 39 * Math.Cos(angle),
                            56 + 39 * Math.Sin(angle)));
                    }
                    arc.Points = points;
                    canvas.Children.Add(arc);
                }
            }

            var displayValue = visual.IsFillGauge
                ? Math.Round(fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%"
                : FormatWidgetNumber(number);
            var valueText = new TextBlock
            {
                Text = displayValue,
                Width = 86,
                Height = 28,
                FontSize = 18,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Canvas.SetLeft(valueText, 13);
            Canvas.SetTop(valueText, 42);
            canvas.Children.Add(valueText);
        }

        private static string ReadJsonString(string json, string name)
        {
            var pattern = "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"([^\"]*)\"";
            var match = Regex.Match(json ?? string.Empty, pattern, RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        private void RefreshInlineChart(WidgetVisual visual)
        {
            if (visual == null || visual.InlineChartCanvas == null || visual.State == null ||
                visual.InlineChartLoading)
                return;

            var now = DateTimeOffset.Now;
            if (visual.LastInlineChartRefresh != DateTimeOffset.MinValue &&
                (now - visual.LastInlineChartRefresh).TotalSeconds < 30)
            {
                DrawInlineChart(visual);
                return;
            }

            visual.LastInlineChartRefresh = now;
            visual.InlineChartLoading = true;
            RefreshInlineChartAsync(visual, visual.State.Clone());
        }

        private async void RefreshInlineChartAsync(WidgetVisual visual, WidgetState state)
        {
            try
            {
                var chartSamples = ParseInlineChartPayload(state.StatusPayload);
                if (chartSamples.Count == 0)
                {
                    var points = await _measurementStore.ReadAsync(
                        state.DeviceId, state.Id, DateTimeOffset.Now.AddDays(-7), DateTimeOffset.Now);
                    chartSamples = new List<Tuple<double?, double?, double?>>();
                    foreach (var point in points)
                    {
                        double number;
                        if (double.TryParse((point.Value ?? string.Empty).Replace(',', '.'),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
                            !double.IsNaN(number) && !double.IsInfinity(number))
                            chartSamples.Add(Tuple.Create((double?)number, (double?)null, (double?)null));
                    }
                }

                if (chartSamples.Count > 48)
                    chartSamples = chartSamples.Skip(chartSamples.Count - 48).ToList();

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
                {
                    WidgetVisual current;
                    if (_visuals.TryGetValue(state.Key, out current) && object.ReferenceEquals(current, visual))
                    {
                        visual.InlineChartSamples = chartSamples;
                        DrawInlineChart(visual);
                    }
                    visual.InlineChartLoading = false;
                });
            }
            catch (Exception ex)
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, delegate
                {
                    visual.InlineChartLoading = false;
                    Log("INLINE CHART ERROR: " + ex.Message);
                });
            }
        }

        private static IList<Tuple<double?, double?, double?>> ParseInlineChartPayload(string payload)
        {
            var result = new List<Tuple<double?, double?, double?>>();
            if (string.IsNullOrWhiteSpace(payload))
                return result;

            var status = System.Text.RegularExpressions.Regex.Match(
                payload, "\\\"status\\\"\\s*:\\s*\\[(.*?)\\]",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!status.Success)
                return result;

            var objects = System.Text.RegularExpressions.Regex.Matches(status.Groups[1].Value, "\\{[^{}]*\\}");
            foreach (System.Text.RegularExpressions.Match item in objects)
            {
                var first = ReadJsonNumber(item.Value, "y1");
                var second = ReadJsonNumber(item.Value, "y2");
                var third = ReadJsonNumber(item.Value, "y3");

                if (!first.HasValue && !second.HasValue && !third.HasValue)
                {
                    first = ReadJsonNumber(item.Value, "y");
                    if (!first.HasValue) first = ReadJsonNumber(item.Value, "value");
                }

                if (first.HasValue || second.HasValue || third.HasValue)
                    result.Add(Tuple.Create(first, second, third));
            }

            return result;
        }

        private static double? ReadJsonNumber(string json, string name)
        {
            var pattern = "\\\"" + System.Text.RegularExpressions.Regex.Escape(name) +
                "\\\"\\s*:\\s*(-?(?:[0-9]+(?:\\.[0-9]*)?|\\.[0-9]+)(?:[eE][+-]?[0-9]+)?)";
            var match = System.Text.RegularExpressions.Regex.Match(
                json ?? string.Empty, pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
                return null;

            double value;
            if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out value) ||
                double.IsNaN(value) || double.IsInfinity(value))
                return null;
            return value;
        }

        private void DrawInlineChart(WidgetVisual visual)
        {
            if (visual == null || visual.InlineChartCanvas == null)
                return;

            var canvas = visual.InlineChartCanvas;
            canvas.Children.Clear();
            var width = canvas.ActualWidth >= 120 ? canvas.ActualWidth : 300.0;
            var height = canvas.ActualHeight >= 40 ? canvas.ActualHeight : 92.0;
            const double left = 30.0;
            const double rightPadding = 8.0;
            const double top = 10.0;
            const double bottomPadding = 12.0;
            var plotWidth = Math.Max(1.0, width - left - rightPadding);
            var plotHeight = Math.Max(1.0, height - top - bottomPadding);
            var samples = visual.InlineChartSamples ?? new List<Tuple<double?, double?, double?>>();

            if (samples.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = visual.InlineChartLoading ? "Загрузка истории…" : "Нет данных графика",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 156, 163, 175))
                };
                Canvas.SetLeft(empty, left);
                Canvas.SetTop(empty, 34);
                canvas.Children.Add(empty);
                return;
            }

            var definition = NormalizeType(visual.State.DefinitionName);
            var isBar = definition == "chart3" || definition == "chart4" ||
                System.Text.RegularExpressions.Regex.IsMatch(
                    visual.State.ConfigJson ?? string.Empty,
                    "\\\"type\\\"\\s*:\\s*\\\"bar\\\"",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var showPoints = definition == "chart2" ||
                (definition != "chart1" && !System.Text.RegularExpressions.Regex.IsMatch(
                    visual.State.ConfigJson ?? string.Empty,
                    "\\\"pointRadius\\\"\\s*:\\s*0(?:\\D|$)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase));

            var seriesValues = new List<List<double>>();
            var seriesIndexes = new List<int>();
            for (var seriesIndex = 0; seriesIndex < 3; seriesIndex++)
            {
                var values = new List<double>();
                foreach (var sample in samples)
                {
                    var value = seriesIndex == 0 ? sample.Item1 :
                        (seriesIndex == 1 ? sample.Item2 : sample.Item3);
                    if (value.HasValue)
                        values.Add(value.Value);
                }
                if (values.Count > 0)
                {
                    seriesValues.Add(values);
                    seriesIndexes.Add(seriesIndex);
                }
            }

            if (seriesValues.Count == 0)
                return;

            var allValues = seriesValues.SelectMany(delegate(List<double> values) { return values; }).ToList();
            var min = allValues.Min();
            var max = allValues.Max();
            if (Math.Abs(max - min) < 0.000001) max = min + 1.0;

            for (var grid = 0; grid < 3; grid++)
            {
                var y = top + plotHeight * grid / 2.0;
                canvas.Children.Add(new Line
                {
                    X1 = left, Y1 = y, X2 = width - rightPadding, Y2 = y,
                    Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 63, 66, 74)),
                    StrokeThickness = 1
                });
            }

            var colors = new[]
            {
                Windows.UI.Color.FromArgb(255, 105, 145, 245),
                Windows.UI.Color.FromArgb(255, 102, 187, 106),
                Windows.UI.Color.FromArgb(255, 255, 179, 0)
            };

            for (var seriesIndex = 0; seriesIndex < seriesValues.Count; seriesIndex++)
            {
                var values = seriesValues[seriesIndex];
                var sourceIndex = seriesIndexes[seriesIndex];
                var color = colors[seriesIndex % colors.Length];
                if (isBar)
                {
                    var groupWidth = plotWidth / Math.Max(1, samples.Count);
                    var barWidth = Math.Max(1.0, groupWidth / seriesValues.Count - 1.0);
                    for (var sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
                    {
                        var sample = samples[sampleIndex];
                        var value = sourceIndex == 0 ? sample.Item1 :
                            (sourceIndex == 1 ? sample.Item2 : sample.Item3);
                        if (!value.HasValue) continue;
                        var x = left + sampleIndex * groupWidth + seriesIndex * barWidth;
                        var normalized = (value.Value - min) / (max - min);
                        var barHeight = Math.Max(1.0, normalized * plotHeight);
                        var bar = new Rectangle
                        {
                            Width = barWidth,
                            Height = barHeight,
                            Fill = new SolidColorBrush(color)
                        };
                        Canvas.SetLeft(bar, x);
                        Canvas.SetTop(bar, top + plotHeight - barHeight);
                        canvas.Children.Add(bar);
                    }
                }
                else
                {
                    var line = new Polyline
                    {
                        Stroke = new SolidColorBrush(color),
                        StrokeThickness = 2
                    };
                    var points = new PointCollection();
                    var pointIndex = 0;
                    foreach (var sample in samples)
                    {
                        var value = sourceIndex == 0 ? sample.Item1 :
                            (sourceIndex == 1 ? sample.Item2 : sample.Item3);
                        if (!value.HasValue) continue;
                        var x = left + (samples.Count == 1 ? plotWidth / 2.0 :
                            plotWidth * pointIndex / Math.Max(1, values.Count - 1));
                        var y = top + (1.0 - (value.Value - min) / (max - min)) * plotHeight;
                        points.Add(new Windows.Foundation.Point(x, y));
                        pointIndex++;
                    }
                    line.Points = points;
                    canvas.Children.Add(line);

                    if (showPoints)
                    {
                        foreach (var point in points)
                        {
                            var dot = new Ellipse
                            {
                                Width = 5,
                                Height = 5,
                                Fill = new SolidColorBrush(color)
                            };
                            Canvas.SetLeft(dot, point.X - 2.5);
                            Canvas.SetTop(dot, point.Y - 2.5);
                            canvas.Children.Add(dot);
                        }
                    }
                }
            }

            var high = new TextBlock
            {
                Text = FormatWidgetNumber(allValues.Max()),
                FontSize = 9,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 156, 163, 175))
            };
            var low = new TextBlock
            {
                Text = FormatWidgetNumber(allValues.Min()),
                FontSize = 9,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 156, 163, 175))
            };
            Canvas.SetLeft(high, 0);
            Canvas.SetTop(high, top - 3);
            Canvas.SetLeft(low, 0);
            Canvas.SetTop(low, top + plotHeight - 8);
            canvas.Children.Add(high);
            canvas.Children.Add(low);
        }

        private void OpenWidgetHistory(WidgetState state)
        {
            if (state == null) return;
            if (ContainsComboValue(GraphWidgetBox, state.Key)) GraphWidgetBox.SelectedItem = state.Key;
            ShowSection(3);
            BuildGraphButton_Click(this, new RoutedEventArgs());
        }

        private static Windows.UI.Color ResolveWidgetColor(string raw, double value)
        {
            var fallback = Windows.UI.Color.FromArgb(255, 144, 202, 249);
            if (double.IsNaN(value) || string.IsNullOrWhiteSpace(raw)) return fallback;
            var text = raw.Trim();
            if (text.StartsWith("[", StringComparison.Ordinal))
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(text,
                    "\\{[^}]*\\\"level\\\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)[^}]*\\\"value\\\"\\s*:\\s*\\\"([^\\\"]+)\\\"[^}]*\\}");
                var selectedLevel = double.NegativeInfinity;
                var selectedColor = string.Empty;
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    double level;
                    if (double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out level) && level <= value && level >= selectedLevel)
                    { selectedLevel = level; selectedColor = match.Groups[2].Value; }
                }
                text = selectedColor;
            }
            if (text.Length == 7 && text[0] == '#')
            {
                byte r, g, b;
                if (byte.TryParse(text.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r) &&
                    byte.TryParse(text.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g) &&
                    byte.TryParse(text.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b))
                    return Windows.UI.Color.FromArgb(255, r, g, b);
            }
            switch (text.ToLowerInvariant())
            {
                case "red": return Windows.UI.Colors.Red;
                case "green": return Windows.UI.Colors.Green;
                case "blue": return Windows.UI.Colors.Blue;
                case "orange": return Windows.UI.Colors.Orange;
                case "yellow": return Windows.UI.Colors.Yellow;
                case "white": return Windows.UI.Colors.White;
                case "gray": case "grey": return Windows.UI.Colors.Gray;
                case "lime": return Windows.UI.Colors.Lime;
                case "cyan": return Windows.UI.Colors.Cyan;
                case "magenta": return Windows.UI.Colors.Magenta;
            }
            return fallback;
        }

        private static double ReadWidgetNumber(string value, double fallback)
        {
            double result;
            if (double.TryParse((value ?? string.Empty).Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out result)) return result;
            return fallback;
        }

        private void ApplyWidgetThresholdStyle(WidgetVisual visual, WidgetState state)
        {
            if (visual == null || visual.Shell == null) return;
            var number = ReadWidgetNumber(state.Value, double.NaN);
            var low = ReadWidgetNumber(state.WarningMinimum, double.NaN);
            var high = ReadWidgetNumber(state.WarningMaximum, double.NaN);
            var warning = !double.IsNaN(number) &&
                ((!double.IsNaN(low) && number < low) || (!double.IsNaN(high) && number > high));
            visual.Shell.Background = new SolidColorBrush(warning
                ? Windows.UI.Color.FromArgb(255, 65, 43, 39)
                : Windows.UI.Color.FromArgb(255, 43, 43, 43));
            visual.Shell.BorderBrush = new SolidColorBrush(warning
                ? Windows.UI.Color.FromArgb(255, 190, 95, 65)
                : Windows.UI.Color.FromArgb(255, 57, 59, 68));
            var accent = warning
                ? Windows.UI.Color.FromArgb(255, 255, 190, 150)
                : ResolveWidgetColor(state.ColorConfig, number);
            if (visual.ValueText != null)
                visual.ValueText.Foreground = new SolidColorBrush(accent);
            if (visual.ValueBadge != null)
                visual.ValueBadge.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(42, accent.R, accent.G, accent.B));
            if (visual.UnitText != null && visual.SliderControl != null)
                visual.UnitText.Text =
                    FormatWidgetNumber(visual.SliderControl.Minimum) + "–" +
                    FormatWidgetNumber(visual.SliderControl.Maximum) +
                    (string.IsNullOrWhiteSpace(state.Unit) ? string.Empty : " " + state.Unit);

            if (visual.GaugeCanvas != null)
                DrawCircularGauge(visual, accent);
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
                ScheduleStatusText.Text = "Задача добавлена: " + executeAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss");
                Log("SCHEDULE ADD: " + command + " @ " + executeAt);
            }
            catch (Exception ex)
            {
                ScheduleStatusText.Text = "Не удалось добавить задачу: " + ex.Message;
                Log("SCHEDULE ERROR: " + ex.Message);
            }
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
                ScheduleStatusText.Text = "Задача обновлена.";
                Log("SCHEDULE UPDATE: " + selected.Id);
            }
            catch (Exception ex)
            {
                ScheduleStatusText.Text = "Не удалось изменить задачу: " + ex.Message;
                Log("SCHEDULE ERROR: " + ex.Message);
            }
        }

        private async void CancelScheduleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selected = SelectedSchedule();
                if (selected == null) return;
                await _scheduler.CancelAsync(selected.Id);
                await RefreshScheduleAsync();
                ScheduleStatusText.Text = "Задача отменена.";
            }
            catch (Exception ex)
            {
                ScheduleStatusText.Text = "Не удалось отменить задачу: " + ex.Message;
                Log("SCHEDULE ERROR: " + ex.Message);
            }
        }

        private async void RetryScheduleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selected = SelectedSchedule();
                if (selected == null)
                    throw new InvalidOperationException("Сначала выберите задачу.");
                if (!string.Equals(selected.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Повторить можно только задачу со статусом «Ошибка».");

                await _scheduler.UpdateAsync(selected.Id, selected.Command, DateTimeOffset.Now.AddMinutes(1));
                await RefreshScheduleAsync();
                ScheduleStatusText.Text = "Повтор запланирован примерно через минуту.";
                Log("SCHEDULE RETRY: " + selected.Id);
            }
            catch (Exception ex)
            {
                ScheduleStatusText.Text = "Повтор не выполнен: " + ex.Message;
                Log("SCHEDULE RETRY ERROR: " + ex.Message);
            }
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
                    FormatScheduleStatus(item.Status) + " | " +
                    item.ExecuteAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") +
                    " | " + item.Command +
                    (string.IsNullOrEmpty(item.LastError) ? string.Empty : " | " + item.LastError));
            }

            if (items.Count == 0)
                ScheduleStatusText.Text = "Список пока пуст. Добавьте команду и время выполнения.";
        }

        private static string FormatScheduleStatus(string status)
        {
            switch ((status ?? string.Empty).ToLowerInvariant())
            {
                case "scheduled": return "Ожидает";
                case "executing": return "Выполняется";
                case "completed": return "Выполнено";
                case "cancelled": return "Отменено";
                case "failed": return "Ошибка";
                default: return string.IsNullOrEmpty(status) ? "Неизвестно" : status;
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
            ScheduleStatusText.Text = FormatScheduleStatus(item.Status) + " • " +
                item.ExecuteAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") +
                (string.IsNullOrWhiteSpace(item.LastError) ? string.Empty : Environment.NewLine + item.LastError);
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
            if (parts.Length != 2)
                throw new InvalidOperationException("Формат: device/widget=value");

            var target = parts[0].Trim();
            var value = parts[1].Trim();
            var separator = target.IndexOf('/');
            if (separator <= 0 || separator >= target.Length - 1 ||
                target.IndexOf('/', separator + 1) >= 0 ||
                target.Any(char.IsWhiteSpace) || string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("Укажите ID устройства, ID виджета и непустое значение: device/widget=value");
        }

        private async void VoiceButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                VoiceButton.IsEnabled = false;
                CommandVoiceButton.IsEnabled = false;
                MarfaListenButton.IsEnabled = false;
                VoiceResultText.Text = "Слушаю…";
                VoiceStatusText.Text = "Марфа слушает…";
                MarfaVoiceResultText.Text = "Марфа слушает…";
                await _voice.StartListeningAsync();
            }
            catch (Exception ex)
            {
                VoiceResultText.Text = "Ошибка голоса: " + ex.Message;
                VoiceStatusText.Text = VoiceResultText.Text;
                MarfaVoiceResultText.Text = VoiceResultText.Text;
                Log("VOICE ERROR: " + ex.Message);
            }
            finally
            {
                VoiceButton.IsEnabled = true;
                CommandVoiceButton.IsEnabled = true;
                MarfaListenButton.IsEnabled = true;
                await _voice.StopListeningAsync();
            }
        }

        private async Task<string> ShowVoiceTargetChoiceAsync(IList<VoiceTargetOption> options)
        {
            if (options == null || options.Count == 0)
                return null;

            var completion = new TaskCompletionSource<string>();
            var popup = new Popup();
            popup.HorizontalOffset = 8;
            popup.VerticalOffset = 58;

            var panel = new StackPanel();
            panel.Width = 328;

            panel.Children.Add(new TextBlock
            {
                Text = "Марфа нашла несколько устройств. Выберите нужное:",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 17,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 245, 245, 245)),
                Margin = new Thickness(0, 0, 0, 10)
            });

            var choices = new ListView
            {
                Height = 244,
                SelectionMode = ListViewSelectionMode.Single,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 37, 38, 44))
            };
            foreach (var option in options)
                choices.Items.Add(option.DisplayText);
            panel.Children.Add(choices);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            var chooseButton = new Button
            {
                Content = "Выбрать",
                IsEnabled = false,
                Margin = new Thickness(0, 0, 8, 0)
            };
            var cancelButton = new Button { Content = "Отмена" };

            choices.SelectionChanged += delegate
            {
                chooseButton.IsEnabled = choices.SelectedIndex >= 0;
            };
            chooseButton.Click += delegate
            {
                var index = choices.SelectedIndex;
                if (index < 0 || index >= options.Count)
                    return;
                completion.TrySetResult(options[index].Command);
                popup.IsOpen = false;
            };
            cancelButton.Click += delegate
            {
                completion.TrySetResult(null);
                popup.IsOpen = false;
            };

            buttons.Children.Add(chooseButton);
            buttons.Children.Add(cancelButton);
            panel.Children.Add(buttons);

            popup.Child = new Border
            {
                Width = 352,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 43, 44, 51)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 91, 94, 106)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12),
                Child = panel
            };
            popup.Closed += delegate
            {
                completion.TrySetResult(null);
            };
            popup.IsOpen = true;
            return await completion.Task;
        }

        private async void Voice_CommandRecognized(object sender, string text)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async delegate
            {
                VoiceResultText.Text = "Распознано: " + text;
                VoiceStatusText.Text = VoiceResultText.Text;
                MarfaVoiceResultText.Text = VoiceResultText.Text;
                try
                {
                    var selectedAmbiguousCommands = new List<string>();
                    ParsedVoiceCommand parsed;
                    while (true)
                    {
                        parsed = _voiceParser.Parse(text, selectedAmbiguousCommands);
                        if (parsed != null)
                            break;

                        var options = _voiceParser.LastAmbiguousOptions;
                        if (options == null || options.Count == 0)
                        {
                            var reason = string.IsNullOrWhiteSpace(_voiceParser.LastReason)
                                ? "Команда не распознана или элемент не найден."
                                : _voiceParser.LastReason;
                            VoiceStatusText.Text = reason;
                            MarfaVoiceResultText.Text = reason;
                            VoiceResultText.Text = reason;
                            Log("VOICE: " + reason);
                            return;
                        }

                        VoiceStatusText.Text = "Марфа уточняет устройство…";
                        MarfaVoiceResultText.Text = VoiceStatusText.Text;
                        var selectedCommand = await ShowVoiceTargetChoiceAsync(options);
                        if (string.IsNullOrWhiteSpace(selectedCommand))
                        {
                            VoiceStatusText.Text = "Уточнение отменено. Ничего не отправлено.";
                            MarfaVoiceResultText.Text = VoiceStatusText.Text;
                            VoiceResultText.Text = VoiceStatusText.Text;
                            Log("VOICE: пользователь отменил уточнение устройства; команды не отправлены.");
                            return;
                        }

                        var replacePreviousChoice = !string.IsNullOrWhiteSpace(_voiceParser.LastReason) &&
                            _voiceParser.LastReason.IndexOf("больше не доступен", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (replacePreviousChoice && selectedAmbiguousCommands.Count > 0)
                            selectedAmbiguousCommands[selectedAmbiguousCommands.Count - 1] = selectedCommand;
                        else
                            selectedAmbiguousCommands.Add(selectedCommand);

                        Log("VOICE CLARIFICATION: selected " + selectedCommand);
                        VoiceStatusText.Text = "Проверяю выбранные устройства…";
                        MarfaVoiceResultText.Text = VoiceStatusText.Text;
                        if (selectedAmbiguousCommands.Count > 5)
                        {
                            VoiceStatusText.Text = "Слишком много неоднозначных действий. Ничего не отправлено.";
                            MarfaVoiceResultText.Text = VoiceStatusText.Text;
                            VoiceResultText.Text = VoiceStatusText.Text;
                            Log("VOICE: превышен лимит уточнений; команды не отправлены.");
                            return;
                        }
                    }

                    var commands = parsed.Commands != null && parsed.Commands.Count > 0
                        ? parsed.Commands
                        : new List<string> { parsed.Command };

                    for (var i = 0; i < commands.Count; i++)
                        ValidateCommand(commands[i]);

                    if (parsed.RequiresConfirmation)
                    {
                        var confirmationText = parsed.ConfirmationText;
                        if (string.IsNullOrWhiteSpace(confirmationText))
                            confirmationText = "Выполнить команды:\n" + string.Join("\n", commands.ToArray());
                        var dialog = new Windows.UI.Popups.MessageDialog(
                            confirmationText,
                            "Подтверждение Марфы");
                        dialog.Commands.Add(new Windows.UI.Popups.UICommand("Выполнить"));
                        dialog.Commands.Add(new Windows.UI.Popups.UICommand("Отмена"));
                        dialog.DefaultCommandIndex = 0;
                        dialog.CancelCommandIndex = 1;
                        var selection = await dialog.ShowAsync();
                        if (selection == null || !string.Equals(selection.Label, "Выполнить", StringComparison.Ordinal))
                        {
                            VoiceStatusText.Text = "Команда отменена.";
                            MarfaVoiceResultText.Text = "Команда отменена.";
                            Log("VOICE: пользователь отменил выполнение.");
                            return;
                        }
                    }

                    if (parsed.ExecuteAt.HasValue)
                    {
                        // Multi-command delayed execution is rejected by the parser;
                        // keep this path strictly single-command.
                        await _scheduler.ScheduleAsync(commands[0], parsed.ExecuteAt.Value);
                        await RefreshScheduleAsync();
                        Log("VOICE SCHEDULE: " + commands[0] + " @ " + parsed.ExecuteAt.Value);
                    }
                    else
                    {
                        // Preflight every command before sending the first MQTT
                        // packet. A malformed or unplannable later command must
                        // never leave the first action executed by itself.
                        for (var i = 0; i < commands.Count; i++)
                        {
                            var plan = _commandExecutor.BuildPlan(commands[i]);
                            if (plan == null || plan.Actions == null || plan.Actions.Count == 0)
                                throw new InvalidOperationException("Не удалось построить план для команды: " + commands[i]);

                            for (var p = 0; p < plan.Actions.Count; p++)
                            {
                                var planned = plan.Actions[p];
                                Log("PLAN " + (planned.IsDependency ? "DEP " : "ACT ") + planned.Command +
                                    (string.IsNullOrEmpty(planned.Reason) ? string.Empty : " | " + planned.Reason));
                            }
                        }

                        var executed = 0;
                        try
                        {
                            for (var i = 0; i < commands.Count; i++)
                            {
                                await _commandExecutor.ExecuteAsync(commands[i]);
                                executed++;
                                Log("VOICE EXECUTE " + executed + "/" + commands.Count + ": " + commands[i]);
                            }
                            VoiceStatusText.Text = "Выполнено действий: " + executed;
                            MarfaVoiceResultText.Text = VoiceStatusText.Text;
                        }
                        catch
                        {
                            if (executed > 0)
                                Log("VOICE PARTIAL: выполнено " + executed + " из " + commands.Count +
                                    ". Остальные команды остановлены после ошибки.");
                            throw;
                        }
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
            public Border Shell { get; set; }
            public Border ValueBadge { get; set; }
            public Slider SliderControl { get; set; }
            public CheckBox SwitchControl { get; set; }
            public TextBox InputControl { get; set; }
            public TextBlock UnitText { get; set; }
            public ComboBox SelectControl { get; set; }
            public ProgressBar ProgressControl { get; set; }
            public Canvas GaugeCanvas { get; set; }
            public bool IsFillGauge { get; set; }
            public Canvas InlineChartCanvas { get; set; }
            public IList<Tuple<double?, double?, double?>> InlineChartSamples { get; set; }
            public bool InlineChartLoading { get; set; }
            public DateTimeOffset LastInlineChartRefresh { get; set; }
            public DatePicker DateControl { get; set; }
            public TimePicker TimeControl { get; set; }
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