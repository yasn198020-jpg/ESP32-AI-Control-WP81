using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Text;
using System.Globalization;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.Storage;
using Windows.UI.Core;
using Windows.Media.SpeechRecognition;
using Windows.Media.SpeechSynthesis;
using ESP32AIControl.UWP.Core.Marfa;
using Windows.UI.Xaml.Shapes;
using Windows.UI.Notifications;

namespace ESP32AIControl.UWP
{
    public sealed partial class MainPage : Page
    {
        private bool _menuOpen;
        private readonly DashboardSnapshot _dashboardSnapshot = new DashboardSnapshot();
        private DashboardRenderer _dashboardRenderer;
        private UpdateManifest _pendingUpdateManifest;
        private readonly MqttDeviceService _mqtt = new MqttDeviceService();
        private readonly UdpDiscoveryService _udpDiscovery = new UdpDiscoveryService();
        private readonly AppDiagnosticsStore _diagnostics = new AppDiagnosticsStore();
        private readonly DispatcherTimer _udpPruneTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        private string _udpDiagnostic = "UDP-приёмник ещё не запускался.";
        private readonly HistoryStore _historyStore = new HistoryStore();
        private readonly DelayedCommandStore _delayedCommandStore = new DelayedCommandStore();
        private readonly ScenarioAutomationStore _scenarioStore = new ScenarioAutomationStore();
        private readonly StoredDeviceScenarioStore _marfaScenarioStore = new StoredDeviceScenarioStore();
        private readonly MarfaCommandEngine _marfaCommandEngine;
        private SpeechRecognizer _marfaRecognizer;
        private SpeechSynthesizer _marfaSynthesizer;
        private MediaElement _marfaAudio;
        private DateTimeOffset _marfaWakeUntil = DateTimeOffset.MinValue;
        private bool _marfaProcessing;
        private readonly DispatcherTimer _delayedCommandTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private bool _delayedCommandTickRunning;
        private bool _isSuspended;

        public event EventHandler<DeviceCommandRequestedEventArgs> DeviceCommandRequested;

        public MainPage()
        {
            InitializeComponent();
            _marfaCommandEngine = new MarfaCommandEngine(new StoredScenarioAdvisor(_marfaScenarioStore));
            _dashboardRenderer = new DashboardRenderer(DashboardHost, _dashboardSnapshot,
                page => { StatusText.Text = "Страница: " + page; },
                (device, widget, value) => DeviceCommandRequested?.Invoke(this,
                    new DeviceCommandRequestedEventArgs(device, widget, value)));
            object showUpdated;
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue("show_last_updated", out showUpdated))
                _dashboardRenderer.ShowLastUpdated = Convert.ToBoolean(showUpdated);
            _diagnostics.Add("App", "Приложение запущено.");
            MarfaCoreDiagnostics.Sink = message => _diagnostics.Add("Marfa", message);
            _mqtt.ConnectionChanged += connected =>
            {
                var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    ConnectionIndicator.Foreground = new SolidColorBrush(connected
                        ? Color.FromArgb(255, 76, 175, 80) : Color.FromArgb(255, 240, 90, 90));
                    StatusText.Text = connected ? "MQTT подключён. Ожидание данных ESP32…" : "MQTT не подключён.";
                });
            };
            _mqtt.Log += message =>
            {
                _diagnostics.Add("MQTT", message);
                var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => StatusText.Text = message);
            };
            _mqtt.DevicesChanged += devices =>
            {
                var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => SetDeviceSnapshot(devices));
            };
            _udpDiscovery.DevicesChanged += devices =>
            {
                var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (PageTitle.Text == "UDP") RenderUdpDeviceList(devices);
                });
            };
            _udpDiscovery.Diagnostic += message =>
            {
                _diagnostics.Add("UDP", message);
                var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    _udpDiagnostic = message;
                    if (PageTitle.Text == "UDP") StatusText.Text = message;
                });
            };
            DeviceCommandRequested += async (sender, args) =>
            {
                bool sent = await _mqtt.SendControlAsync(args.Device.Id, args.Widget.Id, args.Value);
                if (!sent)
                {
                    var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                        () => StatusText.Text = "Команда не отправлена: нет соединения MQTT.");
                }
            };
            ShowDashboard();
            UpdateNavigation(HomeNav);
            TryRestoreMqttConnection();
            _delayedCommandTimer.Tick += DelayedCommandTimer_Tick;
            _delayedCommandTimer.Start();
            _udpPruneTimer.Tick += (sender, args) => _udpDiscovery.PruneExpired();
            _udpPruneTimer.Start();
            var udpStart = _udpDiscovery.StartAsync();
            udpStart.ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    var error = task.Exception == null ? "неизвестная ошибка" : task.Exception.GetBaseException().Message;
                    var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => StatusText.Text = "UDP: " + error);
                }
            });
            SizeChanged += MainPage_SizeChanged;
        }

        public void OnAppSuspending()
        {
            if (_isSuspended) return;
            _isSuspended = true;
            var stopMarfa = StopMarfaContinuousAsync();
            _delayedCommandTimer.Stop();
            _udpPruneTimer.Stop();
            _udpDiscovery.Stop();
            _mqtt.Stop();
            _diagnostics.Add("Lifecycle", "Приложение приостановлено; сетевые службы остановлены.");
        }

        public void OnAppResuming()
        {
            if (!_isSuspended) return;
            _isSuspended = false;
            _diagnostics.Add("Lifecycle", "Приложение возобновлено; восстановление сетевых служб.");
            _delayedCommandTimer.Start();
            _udpPruneTimer.Start();
            TryRestoreMqttConnection();

            var udpStart = _udpDiscovery.StartAsync();
            udpStart.ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    var error = task.Exception == null ? "неизвестная ошибка" : task.Exception.GetBaseException().Message;
                    _diagnostics.Add("UDP", "Не удалось восстановить UDP после возобновления: " + error);
                    var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal,
                        () => StatusText.Text = "UDP: " + error);
                }
            });
        }

        private void MainPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Keep the phone layout fluid; the wider navigation rail is enabled at tablet/desktop widths.
            bool wide = e.NewSize.Width >= 720;
            if (wide)
            {
                MenuColumn.Width = new GridLength(220);
                SideMenu.Visibility = Visibility.Visible;
                BottomNavigation.Visibility = Visibility.Collapsed;
                _menuOpen = true;
            }
            else
            {
                MenuColumn.Width = new GridLength(_menuOpen ? 250 : 0);
                SideMenu.Visibility = _menuOpen ? Visibility.Visible : Visibility.Collapsed;
                BottomNavigation.Visibility = Visibility.Visible;
            }
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            _menuOpen = !_menuOpen;
            if (ActualWidth >= 720)
                return;

            MenuColumn.Width = new GridLength(_menuOpen ? 250 : 0);
            SideMenu.Visibility = _menuOpen ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetSection(string title, string heading, string description)
        {
            PageTitle.Text = title;
            SectionHeading.Text = heading;
            SectionDescription.Text = description;
            StatusText.Text = "Раздел приложения";
            DashboardHost.Visibility = Visibility.Collapsed;
            _menuOpen = false;
            if (ActualWidth < 720)
            {
                MenuColumn.Width = new GridLength(0);
                SideMenu.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateNavigation(Button selected)
        {
            Button[] buttons = { HomeNav, MarfaNav, CommandsNav, HistoryNav, PendingNav };
            foreach (var button in buttons)
                button.Foreground = new SolidColorBrush(Color.FromArgb(255, 172, 173, 184));
            selected.Foreground = new SolidColorBrush(Color.FromArgb(255, 208, 188, 255));
        }

        private void ShowDashboard()
        {
            PageTitle.Text = "Главная";
            SectionHeading.Text = "Устройства";
            SectionDescription.Text = "Устройства ESP32 и элементы управления";
            StatusText.Text = "Нет конфигурации. Подключитесь к MQTT и нажмите HELLO.";
            DashboardHost.Visibility = Visibility.Visible;
            _dashboardRenderer.Render();
            _menuOpen = false;
            if (ActualWidth < 720)
            {
                MenuColumn.Width = new GridLength(0);
                SideMenu.Visibility = Visibility.Collapsed;
            }
        }

        // Receives the live device snapshot from the shared MQTT runtime.
        public void SetDeviceSnapshot(IEnumerable<DeviceModel> devices)
        {
            _dashboardSnapshot.Devices.Clear();
            if (devices != null)
                foreach (var device in devices)
                {
                    _dashboardSnapshot.Devices.Add(device);
                    foreach (var widget in device.Widgets)
                        if (widget.Type == "VALUE" || widget.Type == "STATUS")
                            _historyStore.UpdateLatest(device.Id, widget.Id, widget.Value, widget.LastUpdatedUnixMs);
                }
            var triggered = _scenarioStore.Evaluate(_dashboardSnapshot.Devices);
            if (triggered.Count > 0) _ = ExecuteScenariosAsync(triggered);
            if (PageTitle.Text == "Главная") _dashboardRenderer.Render();
        }

        private async Task ExecuteScenariosAsync(IList<AutomationScenario> scenarios)
        {
            foreach (var scenario in scenarios)
            {
                if (scenario.NotificationEnabled)
                {
                    var first = scenario.Conditions.FirstOrDefault();
                    var sensor = first == null ? null : _dashboardSnapshot.Devices.SelectMany(x => x.Widgets).FirstOrDefault(x => x.Id == first.WidgetId);
                    string message = (scenario.Message ?? "Условие выполнено").Replace("{value}", sensor == null ? "" : sensor.Value);
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        StatusText.Text = "Сценарий «" + scenario.Title + "»: " + message;
                        try
                        {
                            var toastXml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                            var textNodes = toastXml.GetElementsByTagName("text");
                            textNodes.Item(0).AppendChild(toastXml.CreateTextNode(scenario.Title));
                            textNodes.Item(1).AppendChild(toastXml.CreateTextNode(message));
                            ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(toastXml));
                        }
                        catch { }
                    });
                }
                if (scenario.ActionType == "MQTT_CONTROL")
                {
                    foreach (var action in scenario.Actions)
                    {
                        bool sent = await _mqtt.SendControlAsync(action.DeviceId, action.WidgetId, action.Value);
                        if (!sent) break;
                    }
                }
            }
        }

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            SetSection("Обновление", "Обновление приложения", "Проверка канала Windows 10 Mobile…");
            UpdateDownloadButton.Visibility = Visibility.Collapsed;
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            UpdateProgressText.Visibility = Visibility.Collapsed;
            UpdateProgressBar.Value = 0;
            try
            {
                var result = await UpdateService.CheckAsync();
                if (!result.Manifest.Enabled)
                {
                    StatusText.Text = "Канал обновлений пока не опубликован. Текущая версия: " + result.CurrentVersion;
                    return;
                }
                if (!result.IsUpdateAvailable)
                {
                    StatusText.Text = "Установлена актуальная версия: " + result.CurrentVersion;
                    return;
                }
                _pendingUpdateManifest = result.Manifest;
                SectionDescription.Text = "Доступна версия " + result.AvailableVersion + ". " +
                    (result.Manifest.ReleaseNotes ?? "");
                StatusText.Text = "Текущая версия: " + result.CurrentVersion +
                    ". Перед загрузкой будет проверена SHA-256.";
                UpdateDownloadButton.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Не удалось проверить обновление: " + ex.Message;
            }
        }

        private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
        {
            UpdateDownloadButton.IsEnabled = false;
            try
            {
                UpdateProgressBar.Visibility = Visibility.Visible;
                UpdateProgressText.Visibility = Visibility.Visible;
                UpdateProgressBar.IsIndeterminate = true;
                UpdateProgressBar.Value = 0;
                UpdateProgressText.Text = "Подготовка загрузки…";
                var downloadClock = Stopwatch.StartNew();
                StorageFile file = await UpdateService.DownloadAndVerifyAsync(
                    _pendingUpdateManifest,
                    (received, total) =>
                    {
                        double seconds = Math.Max(downloadClock.Elapsed.TotalSeconds, 0.1);
                        double bytesPerSecond = received / seconds;
                        string speed = bytesPerSecond >= 1024 * 1024
                            ? (bytesPerSecond / 1024 / 1024).ToString("0.00") + " МБ/с"
                            : (bytesPerSecond / 1024).ToString("0") + " КБ/с";
                        if (total > 0)
                        {
                            UpdateProgressBar.IsIndeterminate = false;
                            UpdateProgressBar.Value = Math.Min(100.0, received * 100.0 / total);
                            UpdateProgressText.Text = (received / 1024.0 / 1024.0).ToString("0.00") +
                                " из " + (total / 1024.0 / 1024.0).ToString("0.00") +
                                " МБ  •  " + UpdateProgressBar.Value.ToString("0") + "%  •  " + speed;
                        }
                        else
                        {
                            UpdateProgressBar.IsIndeterminate = true;
                            UpdateProgressText.Text = (received / 1024.0 / 1024.0).ToString("0.00") +
                                " МБ  •  " + speed;
                        }
                    });
                downloadClock.Stop();
                UpdateProgressBar.IsIndeterminate = false;
                UpdateProgressBar.Value = 100;
                UpdateProgressText.Text = "Загрузка завершена. Проверка SHA-256…";

                UpdateProgressText.Text = "SHA-256 совпадает. Подготовка установки через Device Portal…";
                var portal = await RequestDevicePortalCredentialsAsync();
                if (portal == null)
                {
                    StatusText.Text = "Установка отменена. Загруженный пакет сохранён для следующей попытки.";
                    return;
                }

                StatusText.Text = "Отправка пакета установщику Lumia…";
                UpdateProgressBar.IsIndeterminate = true;
                UpdateProgressText.Text = "Передача пакета Device Portal по локальной сети. Не закрывайте приложение…";
                string installMessage = await UpdateService.InstallViaDevicePortalAsync(
                    file, portal.Address);

                UpdateProgressBar.IsIndeterminate = false;
                UpdateProgressBar.Value = 100;
                UpdateProgressText.Text = "Запрос установки принят устройством.";
                StatusText.Text = installMessage;
            }
            catch (Exception ex)
            {
                var details = ex.GetType().FullName +
                    " (HRESULT 0x" + ex.HResult.ToString("X8") + "): " + ex.Message;
                if (ex.InnerException != null)
                    details += "\nВнутренняя ошибка: " + ex.InnerException.GetType().FullName +
                        " (HRESULT 0x" + ex.InnerException.HResult.ToString("X8") + "): " +
                        ex.InnerException.Message;
                StatusText.Text = "Не удалось установить обновление. " + details;
                UpdateProgressText.Text = details;
                UpdateProgressText.Visibility = Visibility.Visible;
            }
            finally
            {
                UpdateDownloadButton.IsEnabled = true;
            }
        }

        private TaskCompletionSource<DevicePortalCredentials> _portalCredentialsCompletion;

        private Task<DevicePortalCredentials> RequestDevicePortalCredentialsAsync()
        {
            PortalAddressBox.Text = UpdateService.GetDevicePortalAddress();
            _portalCredentialsCompletion = new TaskCompletionSource<DevicePortalCredentials>();
            DevicePortalOverlay.Visibility = Visibility.Visible;
            return _portalCredentialsCompletion.Task;
        }

        private void PortalCancel_Click(object sender, RoutedEventArgs e)
        {
            DevicePortalOverlay.Visibility = Visibility.Collapsed;
            if (_portalCredentialsCompletion != null)
                _portalCredentialsCompletion.TrySetResult(null);
        }

        private void PortalContinue_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(PortalAddressBox.Text))
            {
                StatusText.Text = "Укажите IP-адрес Lumia в локальной сети.";
                return;
            }

            try
            {
                UpdateService.SaveDevicePortalAddress(PortalAddressBox.Text);
                var result = new DevicePortalCredentials
                {
                    Address = PortalAddressBox.Text.Trim(),
                };
                DevicePortalOverlay.Visibility = Visibility.Collapsed;
                if (_portalCredentialsCompletion != null)
                    _portalCredentialsCompletion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Не удалось сохранить адрес Device Portal: " + ex.Message;
            }
        }

        private void Home_Click(object sender, RoutedEventArgs e)
        {
            ShowDashboard();
            UpdateNavigation(HomeNav);
        }

        private void Marfa_Click(object sender, RoutedEventArgs e)
        {
            ShowMarfa();
            UpdateNavigation(MarfaNav);
        }

        private void ShowMarfa()
        {
            SetSection("Марфа", "Голосовое управление", "Непрерывное распознавание русской речи с активацией по слову «Марфа».");
            DashboardHost.Visibility = Visibility.Visible;
            DashboardHost.Children.Clear();

            var root = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            root.Children.Add(new TextBlock { Text = "Марфа", FontSize = 24, FontWeight = Windows.UI.Text.FontWeights.SemiBold });
            root.Children.Add(new TextBlock {
                Text = "Запустите прослушивание. Скажите «Марфа», дождитесь ответа и произнесите команду. Для работы нужен установленный русский речевой пакет Windows. Прослушивание действует, пока приложение открыто.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12)
            });
            var recognized = new TextBlock { Text = "Прослушивание выключено.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
            var listen = new Button { Content = "🎙 Запустить Марфу", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 48 };
            _marfaAudio = new MediaElement { AutoPlay = false, Volume = 1.0 };
            listen.Click += async (s, a) =>
            {
                listen.IsEnabled = false;
                try
                {
                    if (_marfaRecognizer == null)
                    {
                        await StartMarfaContinuousAsync(recognized);
                        listen.Content = "Остановить Марфу";
                    }
                    else
                    {
                        await StopMarfaContinuousAsync();
                        listen.Content = "🎙 Запустить Марфу";
                        recognized.Text = "Прослушивание выключено.";
                    }
                }
                catch (Exception ex)
                {
                    recognized.Text = "Ошибка голосового движка: " + ex.Message;
                    await StopMarfaContinuousAsync();
                }
                finally { listen.IsEnabled = true; }
            };
            root.Children.Add(listen);
            root.Children.Add(recognized);
            root.Children.Add(_marfaAudio);
            root.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 10, 0, 10), Background = new SolidColorBrush(Color.FromArgb(255, 70, 70, 78)) });
            root.Children.Add(new TextBlock { Text = "Доступные обученные команды", FontSize = 18, FontWeight = Windows.UI.Text.FontWeights.SemiBold });
            var commands = _trainedCommandStore.Load();
            if (commands.Count == 0)
                root.Children.Add(new TextBlock { Text = "Сначала создайте команды в разделе «Команды».", TextWrapping = TextWrapping.Wrap });
            foreach (var command in commands)
            {
                var device = _dashboardSnapshot.Devices.FirstOrDefault(x => x.Id == command.DeviceId);
                var widget = device == null ? null : device.Widgets.FirstOrDefault(x => x.Id == command.WidgetId);
                root.Children.Add(new TextBlock {
                    Text = "• " + command.Phrase + " — " + (device == null ? command.DeviceId : device.Name) +
                        " / " + (widget == null ? command.WidgetId : widget.Title),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 5, 0, 5)
                });
            }
            DashboardHost.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        private async Task StartMarfaContinuousAsync(TextBlock output)
        {
            var recognizer = new SpeechRecognizer(new Windows.Globalization.Language("ru-RU"));
            recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "marfa_dictation"));
            var compile = await recognizer.CompileConstraintsAsync();
            if (compile.Status != SpeechRecognitionResultStatus.Success)
            {
                recognizer.Dispose();
                throw new InvalidOperationException("Не удалось подготовить русский речевой пакет: " + compile.Status);
            }

            recognizer.ContinuousRecognitionSession.AutoStopSilenceTimeout = TimeSpan.FromSeconds(5);
            recognizer.ContinuousRecognitionSession.ResultGenerated += async (sender, args) =>
            {
                string phrase = args.Result == null ? "" : args.Result.Text;
                if (String.IsNullOrWhiteSpace(phrase)) return;
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                {
                    await HandleMarfaRecognizedTextAsync(phrase, output);
                });
            };
            recognizer.ContinuousRecognitionSession.Completed += (sender, args) =>
            {
                if (args.Status != SpeechRecognitionResultStatus.Success)
                    MarfaCoreDiagnostics.Log("MARFA speech session completed: " + args.Status);
            };

            _marfaRecognizer = recognizer;
            _marfaWakeUntil = DateTimeOffset.MinValue;
            await recognizer.ContinuousRecognitionSession.StartAsync();
            output.Text = "Марфа слушает. Скажите «Марфа».";
            MarfaCoreDiagnostics.Log("MARFA continuous speech recognition started (ru-RU).");
        }

        private async Task StopMarfaContinuousAsync()
        {
            var recognizer = _marfaRecognizer;
            _marfaRecognizer = null;
            _marfaWakeUntil = DateTimeOffset.MinValue;
            if (recognizer != null)
            {
                try { await recognizer.ContinuousRecognitionSession.StopAsync(); } catch { }
                recognizer.Dispose();
            }
        }

        private async Task HandleMarfaRecognizedTextAsync(string phrase, TextBlock output)
        {
            if (_isSuspended || _marfaProcessing) return;
            string normalized = NormalizeVoicePhrase(phrase);
            var wake = System.Text.RegularExpressions.Regex.Match(normalized, @"^\s*марфа\b\s*(.*)$");
            string command = "";
            if (wake.Success)
            {
                command = wake.Groups[1].Value.Trim();
                if (command.Length == 0)
                {
                    _marfaWakeUntil = DateTimeOffset.UtcNow.AddSeconds(10);
                    output.Text = "Распознано: «" + phrase + "». Марфа слушает команду.";
                    await SpeakMarfaAsync("Слушаю");
                    return;
                }
            }
            else if (DateTimeOffset.UtcNow <= _marfaWakeUntil)
            {
                command = normalized;
            }
            else
            {
                MarfaCoreDiagnostics.Log("MARFA ignored speech without wake word: " + phrase);
                return;
            }

            _marfaWakeUntil = DateTimeOffset.MinValue;
            if (String.IsNullOrWhiteSpace(command)) return;
            _marfaProcessing = true;
            try
            {
                output.Text = "Распознано: «" + phrase + "»";
                await ExecuteTrainedVoiceCommandAsync(command, output);
            }
            finally { _marfaProcessing = false; }
        }

        private async Task SpeakMarfaAsync(string text)
        {
            try
            {
                if (_marfaSynthesizer == null)
                {
                    _marfaSynthesizer = new SpeechSynthesizer();
                    var russianVoice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase));
                    if (russianVoice != null) _marfaSynthesizer.Voice = russianVoice;
                }
                if (_marfaAudio == null) return;
                var stream = await _marfaSynthesizer.SynthesizeTextToStreamAsync(text);
                _marfaAudio.SetSource(stream, stream.ContentType);
                _marfaAudio.Play();
            }
            catch (Exception ex) { MarfaCoreDiagnostics.Log("MARFA TTS error: " + ex.Message); }
        }

        private IList<MarfaDevice> BuildMarfaDeviceSnapshot()
        {
            var result = new List<MarfaDevice>();
            foreach (var source in _dashboardSnapshot.Devices)
            {
                var device = new MarfaDevice { Id = source.Id, Name = source.Name, Online = source.Online };
                foreach (var item in source.Widgets)
                {
                    MarfaWidgetType type;
                    if (!Enum.TryParse(item.Type ?? "", true, out type)) type = MarfaWidgetType.VALUE;
                    device.Widgets.Add(new MarfaWidgetState
                    {
                        Id = item.Id, Title = item.Title, Type = type, Value = item.Value,
                        Unit = item.Unit, Page = item.Page, Order = item.Order,
                        DefinitionName = item.DefinitionName, ConfigJson = item.ConfigJson,
                        LastUpdated = item.LastUpdatedUnixMs
                    });
                }
                result.Add(device);
            }
            return result;
        }

        private async Task ExecuteNaturalMarfaCommandAsync(string recognizedText, TextBlock output)
        {
            var parsed = _marfaCommandEngine.Parse(recognizedText, BuildMarfaDeviceSnapshot());
            output.Text += "\n" + parsed.Reply;

            if (parsed.Action == LocalCommandAction.SMART_RULE)
            {
                var confirmRule = new ContentDialog
                {
                    Title = "Сохранить сценарий Марфы?",
                    Content = parsed.Reply,
                    PrimaryButtonText = "Сохранить",
                    CloseButtonText = "Отмена"
                };
                if (await confirmRule.ShowAsync() != ContentDialogResult.Primary)
                {
                    output.Text += "\nСоздание сценария отменено.";
                    return;
                }
                _scenarioStore.Save(new AutomationScenario
                {
                    Title = parsed.Reply,
                    Message = "Условие выполнено: {value}",
                    Enabled = true,
                    NotificationEnabled = true,
                    ActionType = "MQTT_CONTROL",
                    Conditions = new List<AutomationCondition>
                    {
                        new AutomationCondition
                        {
                            WidgetId = parsed.ConditionWidgetId,
                            Operator = parsed.ConditionOperator,
                            Threshold = parsed.ConditionThreshold,
                            Connector = "AND"
                        }
                    },
                    Actions = new List<AutomationAction>
                    {
                        new AutomationAction
                        {
                            DeviceId = parsed.ActionDeviceId,
                            WidgetId = parsed.ActionWidgetId,
                            Value = parsed.ActionValue
                        }
                    }
                });
                output.Text += "\nСценарий сохранён. Марфа будет отслеживать условие по данным ESP32.";
                return;
            }

            if (parsed.Action == LocalCommandAction.READ_VALUE)
            {
                var device = _dashboardSnapshot.Devices.FirstOrDefault(x => x.Id == parsed.DeviceId);
                var widget = device == null ? null : device.Widgets.FirstOrDefault(x => x.Id == parsed.WidgetId);
                if (widget != null)
                    output.Text += "\nТекущее значение: " + widget.Value + (String.IsNullOrWhiteSpace(widget.Unit) ? "" : " " + widget.Unit);
                return;
            }
            if (parsed.Action != LocalCommandAction.CONTROL || parsed.ActionItems == null || parsed.ActionItems.Count == 0)
                return;

            var confirmation = new ContentDialog
            {
                Title = "Подтвердить команду Марфы",
                Content = parsed.Reply + "\n\nБудут отправлены команды: " + parsed.ActionItems.Count,
                PrimaryButtonText = "Выполнить",
                CloseButtonText = "Отмена"
            };
            var choice = await confirmation.ShowAsync();
            if (choice != ContentDialogResult.Primary)
            {
                _marfaCommandEngine.ClearDialogContext();
                output.Text += "\nКоманда отменена.";
                return;
            }

            if (parsed.DelayMs > 0)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                int queued = 0;
                foreach (var action in parsed.ActionItems)
                {
                    var target = _dashboardSnapshot.Devices.FirstOrDefault(x => x.Id == action.DeviceId);
                    var targetWidget = target == null ? null : target.Widgets.FirstOrDefault(x => x.Id == action.WidgetId);
                    if (target == null || targetWidget == null)
                    {
                        output.Text += "\nНе найдено устройство или элемент " + action.WidgetId + ". Остальные команды не поставлены в очередь.";
                        break;
                    }
                    _delayedCommandStore.Add(new DelayedDeviceCommand
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        DeviceId = action.DeviceId,
                        DeviceName = target.Name ?? "",
                        WidgetId = action.WidgetId,
                        WidgetName = targetWidget.Title ?? action.WidgetId,
                        Value = action.Value,
                        CreatedAtUnixMs = now,
                        ExecuteAtUnixMs = now + parsed.DelayMs
                    });
                    queued++;
                }
                if (queued > 0)
                    output.Text += "\nВ очередь поставлено: " + queued + " из " + parsed.ActionItems.Count +
                        ". Выполнение: " + DateTimeOffset.FromUnixTimeMilliseconds(now + parsed.DelayMs).ToLocalTime().ToString("g") + ".";
                return;
            }

            int sentCount = 0;
            foreach (var action in parsed.ActionItems)
            {
                var target = _dashboardSnapshot.Devices.FirstOrDefault(x => x.Id == action.DeviceId);
                var targetWidget = target == null ? null : target.Widgets.FirstOrDefault(x => x.Id == action.WidgetId);
                if (target == null || targetWidget == null)
                {
                    output.Text += "\nНе найдено устройство или элемент " + action.WidgetId + ". Остальные команды остановлены.";
                    break;
                }
                bool sent = await _mqtt.SendControlAsync(action.DeviceId, action.WidgetId, action.Value);
                if (!sent)
                {
                    output.Text += "\nMQTT не отправил команду. Проверьте соединение.";
                    break;
                }
                sentCount++;
            }
            if (sentCount > 0) output.Text += "\nОтправлено команд: " + sentCount + " из " + parsed.ActionItems.Count + ". Ожидаю подтверждения ESP32.";
        }

        private static string NormalizeVoicePhrase(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            var chars = value.Trim().ToLowerInvariant().Select(ch =>
                Char.IsLetterOrDigit(ch) || Char.IsWhiteSpace(ch) ? ch : ' ').ToArray();
            return String.Join(" ", new string(chars).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private sealed class TrainedCommandRepositoryAdapter : ITrainedCommandRepository
        {
            private readonly TrainedVoiceCommandStore _store;
            public TrainedCommandRepositoryAdapter(TrainedVoiceCommandStore store) { _store = store; }
            public IList<ESP32AIControl.UWP.Core.Marfa.TrainedVoiceCommand> Load()
            {
                return _store.Load().Select(x => new ESP32AIControl.UWP.Core.Marfa.TrainedVoiceCommand
                {
                    Phrase = x.Phrase, DeviceId = x.DeviceId, WidgetId = x.WidgetId, Value = x.Value,
                    Variants = (x.Variants ?? new List<string>()).ToList()
                }).ToList();
            }
        }

        private async Task ExecuteTrainedVoiceCommandAsync(string recognizedText, TextBlock output)
        {
            var matcher = new TrainedCommandMatcher(new TrainedCommandRepositoryAdapter(_trainedCommandStore));
            var matches = matcher.MatchAll(recognizedText);
            if (matches.Count == 0)
            {
                await ExecuteNaturalMarfaCommandAsync(recognizedText, output);
                return;
            }

            var resolved = new List<Tuple<ESP32AIControl.UWP.Core.Marfa.TrainedVoiceCommand, DeviceModel, WidgetModel>>();
            foreach (var match in matches)
            {
                var device = _dashboardSnapshot.Devices.FirstOrDefault(x => x.Id == match.DeviceId);
                var widget = device == null ? null : device.Widgets.FirstOrDefault(x => x.Id == match.WidgetId);
                if (device == null || widget == null)
                {
                    output.Text += "\nУстройство или виджет команды «" + match.Phrase + "» сейчас не найден.";
                    return;
                }
                resolved.Add(Tuple.Create(match, device, widget));
            }

            bool reads = resolved.All(x => x.Item1.Value == TrainedCommandConstants.ReadValue);
            bool controls = resolved.All(x => x.Item1.Value != TrainedCommandConstants.ReadValue);
            if (!reads && !controls)
            {
                output.Text += "\nВ одной обученной фразе смешаны чтение датчика и управление. Разделите её на две команды.";
                return;
            }
            if (reads)
            {
                foreach (var item in resolved)
                    output.Text += "\n" + item.Item3.Title + ": " +
                        MarfaSpeechTextFormatter.FormatTemperatureForSpeech(item.Item3.Value, item.Item3.Unit, item.Item3.Title);
                return;
            }

            if (resolved.Count > 1)
            {
                var confirm = new ContentDialog
                {
                    Title = "Подтвердить обученную команду",
                    Content = "Одна фраза управляет элементами: " +
                        String.Join(", ", resolved.Select(x => x.Item3.Title ?? x.Item2.Name)),
                    PrimaryButtonText = "Выполнить",
                    CloseButtonText = "Отмена"
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                {
                    output.Text += "\nОбученная команда отменена.";
                    return;
                }
            }

            int sent = 0;
            foreach (var item in resolved)
            {
                bool ok = await _mqtt.SendControlAsync(item.Item1.DeviceId, item.Item1.WidgetId, item.Item1.Value);
                if (!ok)
                {
                    output.Text += "\nНе удалось отправить команду. Остальные действия остановлены.";
                    break;
                }
                sent++;
            }
            output.Text += "\nОтправлено обученных действий: " + sent + " из " + resolved.Count + ".";
        }

        private readonly TrainedVoiceCommandStore _trainedCommandStore = new TrainedVoiceCommandStore();

        private void Commands_Click(object sender, RoutedEventArgs e)
        {
            ShowCommands();
            UpdateNavigation(CommandsNav);
        }

        private void ShowCommands()
        {
            SetSection("Команды", "Обученные команды", "Фразы, варианты произношения и действия для устройств ESP32.");
            DashboardHost.Visibility = Visibility.Visible;
            DashboardHost.Children.Clear();

            var root = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var phrase = new TextBox { PlaceholderText = "Например: включи свет" };
            var variant = new TextBox { PlaceholderText = "Дополнительный вариант фразы (необязательно)" };
            var devicePicker = new ComboBox { PlaceholderText = "Выберите устройство", HorizontalAlignment = HorizontalAlignment.Stretch };
            var widgetPicker = new ComboBox { PlaceholderText = "Выберите элемент управления", HorizontalAlignment = HorizontalAlignment.Stretch };
            var valuePicker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            valuePicker.Items.Add(new ComboBoxItem { Content = "Включить / открыть", Tag = "1" });
            valuePicker.Items.Add(new ComboBoxItem { Content = "Выключить / закрыть", Tag = "0" });
            valuePicker.Items.Add(new ComboBoxItem { Content = "Произнести текущее значение", Tag = "__READ_VALUE__" });
            valuePicker.SelectedIndex = 0;

            var devices = _dashboardSnapshot.Devices.ToList();
            foreach (var device in devices)
                devicePicker.Items.Add(new ComboBoxItem { Content = String.IsNullOrWhiteSpace(device.Name) ? device.Id : device.Name, Tag = device.Id });
            Action refreshWidgets = () =>
            {
                widgetPicker.Items.Clear();
                var selected = devicePicker.SelectedItem as ComboBoxItem;
                var device = selected == null ? null : devices.FirstOrDefault(x => x.Id == (string)selected.Tag);
                if (device == null) return;
                foreach (var widget in device.Widgets)
                    widgetPicker.Items.Add(new ComboBoxItem { Content = String.IsNullOrWhiteSpace(widget.Title) ? widget.Id : widget.Title, Tag = widget.Id });
                if (widgetPicker.Items.Count > 0) widgetPicker.SelectedIndex = 0;
            };
            devicePicker.SelectionChanged += (s, a) => refreshWidgets();
            if (devicePicker.Items.Count > 0) devicePicker.SelectedIndex = 0;

            root.Children.Add(new TextBlock { Text = "Создать обученную команду", FontSize = 20, FontWeight = Windows.UI.Text.FontWeights.SemiBold });
            root.Children.Add(new TextBlock { Text = "Фраза команды" });
            root.Children.Add(phrase);
            root.Children.Add(new TextBlock { Text = "Устройство" });
            root.Children.Add(devicePicker);
            root.Children.Add(new TextBlock { Text = "Виджет" });
            root.Children.Add(widgetPicker);
            root.Children.Add(new TextBlock { Text = "Действие" });
            root.Children.Add(valuePicker);
            root.Children.Add(new TextBlock { Text = "Вариант произношения" });
            root.Children.Add(variant);

            var add = new Button { Content = "Сохранить команду", HorizontalAlignment = HorizontalAlignment.Stretch };
            add.Click += (s, a) =>
            {
                var selectedDevice = devicePicker.SelectedItem as ComboBoxItem;
                var selectedWidget = widgetPicker.SelectedItem as ComboBoxItem;
                var selectedValue = valuePicker.SelectedItem as ComboBoxItem;
                if (String.IsNullOrWhiteSpace(phrase.Text) || selectedDevice == null || selectedWidget == null || selectedValue == null)
                {
                    StatusText.Text = "Укажите фразу, устройство и виджет.";
                    return;
                }
                var command = new TrainedVoiceCommand {
                    Phrase = phrase.Text.Trim(), DeviceId = (string)selectedDevice.Tag,
                    WidgetId = (string)selectedWidget.Tag, Value = (string)selectedValue.Tag,
                    Variants = String.IsNullOrWhiteSpace(variant.Text) ? new List<string>() : new List<string> { variant.Text.Trim() }
                };
                _trainedCommandStore.Add(command);
                phrase.Text = ""; variant.Text = "";
                RenderTrainedCommands(root);
                StatusText.Text = "Команда сохранена на этом устройстве.";
            };
            root.Children.Add(add);
            root.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8), Background = new SolidColorBrush(Color.FromArgb(255, 70, 70, 78)) });
            RenderTrainedCommands(root);
            DashboardHost.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        private void RenderTrainedCommands(StackPanel root)
        {
            while (root.Children.Count > 13) root.Children.RemoveAt(root.Children.Count - 1);
            var commands = _trainedCommandStore.Load();
            root.Children.Add(new TextBlock { Text = "Сохранённые команды: " + commands.Count, FontSize = 18, FontWeight = Windows.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
            if (commands.Count == 0)
                root.Children.Add(new TextBlock { Text = "Пока нет обученных команд. Создайте первую команду выше.", TextWrapping = TextWrapping.Wrap });
            foreach (var command in commands)
            {
                var device = _dashboardSnapshot.Devices.FirstOrDefault(x => x.Id == command.DeviceId);
                var widget = device == null ? null : device.Widgets.FirstOrDefault(x => x.Id == command.WidgetId);
                var cardContent = new StackPanel { Margin = new Thickness(12) };
                var card = new Border { Margin = new Thickness(0, 4, 0, 8), Background = new SolidColorBrush(Color.FromArgb(255, 43, 43, 49)), Child = cardContent };
                cardContent.Children.Add(new TextBlock { Text = "«" + command.Phrase + "»", FontSize = 18, FontWeight = Windows.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                cardContent.Children.Add(new TextBlock { Text = (device == null ? command.DeviceId : device.Name) + " • " + (widget == null ? command.WidgetId : widget.Title), TextWrapping = TextWrapping.Wrap });
                cardContent.Children.Add(new TextBlock { Text = command.Value == "__READ_VALUE__" ? "ПРОИЗНЕСТИ ЗНАЧЕНИЕ" : command.Value == "1" ? "ВКЛ / ОТКРЫТЬ" : "ВЫКЛ / ЗАКРЫТЬ" });
                if (command.Variants != null && command.Variants.Count > 1)
                    cardContent.Children.Add(new TextBlock { Text = "Варианты: " + String.Join(", ", command.Variants), TextWrapping = TextWrapping.Wrap });
                var actions = new StackPanel { Orientation = Orientation.Horizontal };
                var addVariant = new Button { Content = "Добавить вариант" };
                addVariant.Click += (s, a) =>
                {
                    var dialog = new ContentDialog { Title = "Вариант фразы", PrimaryButtonText = "Добавить", CloseButtonText = "Отмена" };
                    var input = new TextBox { PlaceholderText = "Напишите другой вариант команды" };
                    dialog.Content = input;
                    dialog.PrimaryButtonClick += (sender, args) =>
                    {
                        if (!String.IsNullOrWhiteSpace(input.Text)) _trainedCommandStore.AddVariant(command.Phrase, input.Text);
                        ShowCommands();
                    };
                    var ignored = dialog.ShowAsync();
                };
                var remove = new Button { Content = "Удалить" };
                remove.Click += (s, a) => { _trainedCommandStore.Remove(command); ShowCommands(); };
                actions.Children.Add(addVariant); actions.Children.Add(remove); cardContent.Children.Add(actions);
                root.Children.Add(card);
            }
            if (commands.Count > 0)
            {
                var clear = new Button { Content = "Очистить все команды" };
                clear.Click += async (s, a) =>
                {
                    var dialog = new ContentDialog { Title = "Очистить обученные команды?", Content = "Все сохранённые фразы и варианты будут удалены.", PrimaryButtonText = "Удалить всё", CloseButtonText = "Отмена" };
                    if (await dialog.ShowAsync() == ContentDialogResult.Primary) { _trainedCommandStore.Clear(); ShowCommands(); }
                };
                root.Children.Add(clear);
            }
        }

        private void History_Click(object sender, RoutedEventArgs e)
        {
            ShowHistory();
            UpdateNavigation(HistoryNav);
        }

        private void ShowHistory()
        {
            SetSection("История", "История измерений", "Запись телеметрии MQTT, периоды хранения и графики значений.");
            DashboardHost.Visibility = Visibility.Visible;
            DashboardHost.Children.Clear();

            var root = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            root.Children.Add(new TextBlock { Text = "Выберите датчик", FontSize = 18, FontWeight = Windows.UI.Text.FontWeights.SemiBold });
            var devicePicker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Устройство" };
            var widgetPicker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Датчик / значение" };
            var devices = _dashboardSnapshot.Devices.ToList();
            foreach (var device in devices)
                devicePicker.Items.Add(new ComboBoxItem { Content = String.IsNullOrWhiteSpace(device.Name) ? device.Id : device.Name, Tag = device.Id });

            var samplePicker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            AddChoice(samplePicker, "1 сек", 1000); AddChoice(samplePicker, "5 сек", 5000);
            AddChoice(samplePicker, "10 сек", 10000); AddChoice(samplePicker, "30 сек", 30000);
            AddChoice(samplePicker, "1 мин", 60000); AddChoice(samplePicker, "5 мин", 300000);
            AddChoice(samplePicker, "10 мин", 600000); AddChoice(samplePicker, "30 мин", 1800000);
            AddChoice(samplePicker, "1 час", 3600000);
            SelectChoice(samplePicker, _historyStore.SamplePeriodMs);
            samplePicker.SelectionChanged += (s, a) => {
                var item = samplePicker.SelectedItem as ComboBoxItem;
                if (item != null) _historyStore.SetSamplePeriod(Convert.ToInt32(item.Tag));
            };

            var periodPicker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            AddChoice(periodPicker, "1 час", 3600000L); AddChoice(periodPicker, "6 часов", 21600000L);
            AddChoice(periodPicker, "12 часов", 43200000L); AddChoice(periodPicker, "Сегодня", 86400000L);
            AddChoice(periodPicker, "3 дня", 259200000L); AddChoice(periodPicker, "7 дней", 604800000L);
            AddChoice(periodPicker, "30 дней", 2592000000L); AddChoice(periodPicker, "Весь срок", 0L);
            SelectChoice(periodPicker, _historyStore.DisplayPeriodMs);
            periodPicker.SelectionChanged += (s, a) => {
                var item = periodPicker.SelectedItem as ComboBoxItem;
                if (item != null) _historyStore.SetDisplayPeriod(Convert.ToInt64(item.Tag));
            };

            var modePicker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            AddChoice(modePicker, "При изменении значения", "change");
            AddChoice(modePicker, "По интервалу", "interval");
            AddChoice(modePicker, "Изменение + интервал", "change_interval");
            SelectChoice(modePicker, "change_interval");
            var graphIntervalPicker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            AddChoice(graphIntervalPicker, "1 минута", 60000L); AddChoice(graphIntervalPicker, "5 минут", 300000L);
            AddChoice(graphIntervalPicker, "10 минут", 600000L); AddChoice(graphIntervalPicker, "30 минут", 1800000L);
            AddChoice(graphIntervalPicker, "1 час", 3600000L); AddChoice(graphIntervalPicker, "2 часа", 7200000L);
            SelectChoice(graphIntervalPicker, 1800000L);
            var graphConfigsPanel = new StackPanel();
            var addGraphConfig = new Button { Content = "＋ Добавить график для датчика", HorizontalAlignment = HorizontalAlignment.Stretch };

            var retentionPicker = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            AddChoice(retentionPicker, "1 день", 1); AddChoice(retentionPicker, "3 дня", 3);
            AddChoice(retentionPicker, "7 дней", 7); AddChoice(retentionPicker, "14 дней", 14);
            AddChoice(retentionPicker, "30 дней", 30); AddChoice(retentionPicker, "90 дней", 90);
            AddChoice(retentionPicker, "180 дней", 180); AddChoice(retentionPicker, "365 дней", 365);
            AddChoice(retentionPicker, "Всегда", 0);
            SelectChoice(retentionPicker, _historyStore.RetentionDays);
            retentionPicker.SelectionChanged += (s, a) => {
                var item = retentionPicker.SelectedItem as ComboBoxItem;
                if (item != null) _historyStore.SetRetentionDays(Convert.ToInt32(item.Tag));
            };

            var info = new TextBlock { Text = "История загружается из локального архива.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
            IList<HistoryPoint> lastPoints = new List<HistoryPoint>();
            string selectedUnit = "";
            var chart = new Canvas { Width = Math.Max(320, ActualWidth - 70), Height = 220, Background = new SolidColorBrush(Color.FromArgb(255, 35, 36, 42)) };
            var chartScroll = new ScrollViewer { Content = chart, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Height = 230 };
            var refresh = new Button { Content = "Обновить график", HorizontalAlignment = HorizontalAlignment.Stretch };
            var export = new Button { Content = "Экспортировать CSV", HorizontalAlignment = HorizontalAlignment.Stretch };
            var clear = new Button { Content = "Очистить историю", HorizontalAlignment = HorizontalAlignment.Stretch };
            var selectedDeviceId = "";
            var selectedWidgetId = "";
            Action refreshGraphConfigs = null;
            refreshGraphConfigs = () =>
            {
                graphConfigsPanel.Children.Clear();
                var configs = _historyStore.GraphConfigs();
                if (configs.Count == 0)
                    graphConfigsPanel.Children.Add(new TextBlock { Text = "Дополнительные графики не настроены.", TextWrapping = TextWrapping.Wrap });
                foreach (var config in configs)
                {
                    var device = devices.FirstOrDefault(x => x.Id == config.DeviceId);
                    var widget = device == null ? null : device.Widgets.FirstOrDefault(x => x.Id == config.WidgetId);
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
                    row.Children.Add(new TextBlock {
                        Text = (widget == null ? config.WidgetId : widget.Title) + " • " +
                            (config.Mode == "change" ? "изменение" : config.Mode == "interval" ? "интервал" : "изменение + интервал") +
                            " • " + TimeSpan.FromMilliseconds(config.IntervalMs).ToString(@"h\:mm"),
                        TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
                    });
                    var removeConfig = new Button { Content = "Удалить", Margin = new Thickness(8, 0, 0, 0) };
                    removeConfig.Click += (s, a) => {
                        _historyStore.RemoveGraphConfig(config.DeviceId, config.WidgetId);
                        refreshGraphConfigs();
                    };
                    row.Children.Add(removeConfig);
                    graphConfigsPanel.Children.Add(row);
                }
            };

            Action refreshWidgets = () =>
            {
                widgetPicker.Items.Clear();
                var selected = devicePicker.SelectedItem as ComboBoxItem;
                selectedDeviceId = selected == null ? "" : Convert.ToString(selected.Tag);
                var device = devices.FirstOrDefault(x => x.Id == selectedDeviceId);
                if (device == null) return;
                foreach (var widget in device.Widgets.Where(w => w.Type == "VALUE" || w.Type == "STATUS"))
                {
                    double number;
                    if (Double.TryParse((widget.Value ?? "").Trim().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out number))
                        widgetPicker.Items.Add(new ComboBoxItem { Content = String.IsNullOrWhiteSpace(widget.Title) ? widget.Id : widget.Title, Tag = widget.Id });
                }
                if (widgetPicker.Items.Count > 0) widgetPicker.SelectedIndex = 0;
            };

            Func<Task> loadGraph = async () =>
            {
                var d = devicePicker.SelectedItem as ComboBoxItem;
                var w = widgetPicker.SelectedItem as ComboBoxItem;
                if (d == null || w == null)
                {
                    info.Text = "Нет числовых датчиков. Подключитесь к MQTT и дождитесь числовых значений.";
                    chart.Children.Clear();
                    return;
                }
                selectedDeviceId = Convert.ToString(d.Tag);
                selectedWidgetId = Convert.ToString(w.Tag);
                long since = _historyStore.DisplaySince();
                var points = await _historyStore.LoadAsync(selectedDeviceId, selectedWidgetId, since);
                lastPoints = points;
                chart.Children.Clear();
                if (points.Count == 0)
                {
                    info.Text = "Для выбранного датчика пока нет записей. Дождитесь следующего периода измерения.";
                    return;
                }
                double min = points.Min(p => p.Value), max = points.Max(p => p.Value);
                if (Math.Abs(max - min) < 0.000001) { max += 1; min -= 1; }
                double left = 42, right = chart.Width - 12, top = 14, bottom = chart.Height - 28;
                for (int i = 0; i < 5; i++)
                {
                    double y = top + (bottom - top) * i / 4.0;
                    chart.Children.Add(new Line { X1 = left, X2 = right, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(Color.FromArgb(70, 190, 190, 200)), StrokeThickness = 1 });
                    var label = new TextBlock { Text = (max - (max - min) * i / 4.0).ToString("0.##", CultureInfo.InvariantCulture), FontSize = 10, Foreground = new SolidColorBrush(Color.FromArgb(255, 190, 190, 200)) };
                    Canvas.SetLeft(label, 2); Canvas.SetTop(label, y - 8); chart.Children.Add(label);
                }
                var seriesBrush = new SolidColorBrush(Color.FromArgb(255, 117, 190, 255));
                if (points.Count == 1)
                {
                    var dot = new Ellipse { Width = 8, Height = 8, Fill = seriesBrush };
                    double onlyY = bottom - (points[0].Value - min) * (bottom - top) / (max - min);
                    Canvas.SetLeft(dot, left - 4); Canvas.SetTop(dot, onlyY - 4); chart.Children.Add(dot);
                }
                long firstTimestamp = points[0].Timestamp;
                long timestampSpan = Math.Max(1L, points[points.Count - 1].Timestamp - firstTimestamp);
                for (int i = 1; i < points.Count; i++)
                {
                    double x1 = left + (right - left) * (points[i - 1].Timestamp - firstTimestamp) / (double)timestampSpan;
                    double x2 = left + (right - left) * (points[i].Timestamp - firstTimestamp) / (double)timestampSpan;
                    double y1 = bottom - (points[i - 1].Value - min) * (bottom - top) / (max - min);
                    double y2 = bottom - (points[i].Value - min) * (bottom - top) / (max - min);
                    chart.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = seriesBrush, StrokeThickness = 2 });
                }
                var firstTime = DateTimeOffset.FromUnixTimeMilliseconds(points[0].Timestamp).ToLocalTime().ToString("dd.MM HH:mm");
                var lastTime = DateTimeOffset.FromUnixTimeMilliseconds(points[points.Count - 1].Timestamp).ToLocalTime().ToString("dd.MM HH:mm");
                var startLabel = new TextBlock { Text = firstTime, FontSize = 10, Foreground = new SolidColorBrush(Color.FromArgb(255, 190, 190, 200)) };
                var endLabel = new TextBlock { Text = lastTime, FontSize = 10, Foreground = new SolidColorBrush(Color.FromArgb(255, 190, 190, 200)) };
                Canvas.SetLeft(startLabel, left); Canvas.SetTop(startLabel, bottom + 5);
                Canvas.SetLeft(endLabel, Math.Max(left, right - 90)); Canvas.SetTop(endLabel, bottom + 5);
                chart.Children.Add(startLabel); chart.Children.Add(endLabel);
                info.Text = "Точек: " + points.Count + " • минимум: " + min.ToString("0.###", CultureInfo.InvariantCulture) +
                    " • максимум: " + max.ToString("0.###", CultureInfo.InvariantCulture) +
                    " • последнее: " + points[points.Count - 1].RawValue;
                var device = devices.FirstOrDefault(x => x.Id == selectedDeviceId);
                var widget = device == null ? null : device.Widgets.FirstOrDefault(x => x.Id == selectedWidgetId);
                selectedUnit = widget == null ? "" : widget.Unit;
                if (!String.IsNullOrWhiteSpace(selectedUnit)) info.Text += " " + selectedUnit;
            };

            periodPicker.SelectionChanged += async (s, a) =>
            {
                var item = periodPicker.SelectedItem as ComboBoxItem;
                if (item != null) _historyStore.SetDisplayPeriod(Convert.ToInt64(item.Tag));
                await loadGraph();
            };
            devicePicker.SelectionChanged += (s, a) => { refreshWidgets(); };
            widgetPicker.SelectionChanged += async (s, a) => { await loadGraph(); };
            addGraphConfig.Click += (s, a) =>
            {
                var d = devicePicker.SelectedItem as ComboBoxItem;
                var w = widgetPicker.SelectedItem as ComboBoxItem;
                var mode = modePicker.SelectedItem as ComboBoxItem;
                var interval = graphIntervalPicker.SelectedItem as ComboBoxItem;
                if (d == null || w == null || mode == null || interval == null)
                {
                    info.Text = "Сначала выберите устройство и числовой датчик.";
                    return;
                }
                _historyStore.AddGraphConfig(new HistoryGraphConfig {
                    DeviceId = Convert.ToString(d.Tag), WidgetId = Convert.ToString(w.Tag),
                    Mode = Convert.ToString(mode.Tag), IntervalMs = Convert.ToInt64(interval.Tag)
                });
                refreshGraphConfigs();
                info.Text = "Настройки дополнительного графика сохранены.";
            };
            refresh.Click += async (s, a) => await loadGraph();
            export.Click += async (s, a) =>
            {
                if (lastPoints == null || lastPoints.Count == 0) { info.Text = "Нет точек для экспорта."; return; }
                try
                {
                    var picker = new Windows.Storage.Pickers.FileSavePicker();
                    picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
                    picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
                    picker.SuggestedFileName = "esp32-history-" + DateTime.Now.ToString("yyyyMMdd-HHmm");
                    var file = await picker.PickSaveFileAsync();
                    if (file == null) return;
                    var csv = new StringBuilder();
                    csv.AppendLine("timestamp_utc,device_id,widget_id,value,raw_value,unit");
                    foreach (var point in lastPoints)
                    {
                        var time = DateTimeOffset.FromUnixTimeMilliseconds(point.Timestamp).UtcDateTime.ToString("o");
                        csv.Append(EscapeCsv(time)).Append(',')
                            .Append(EscapeCsv(point.DeviceId)).Append(',')
                            .Append(EscapeCsv(point.WidgetId)).Append(',')
                            .Append(point.Value.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                            .Append(EscapeCsv(point.RawValue)).Append(',')
                            .Append(EscapeCsv(selectedUnit)).AppendLine();
                    }
                    await Windows.Storage.FileIO.WriteTextAsync(file, csv.ToString());
                    info.Text = "Экспортировано точек: " + lastPoints.Count + " • " + file.Name;
                }
                catch (Exception ex) { info.Text = "Ошибка экспорта: " + ex.Message; }
            };
            clear.Click += async (s, a) =>
            {
                var dialog = new ContentDialog { Title = "Очистить историю?", Content = "Все сохранённые измерения будут удалены.", PrimaryButtonText = "Очистить", CloseButtonText = "Отмена" };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    await _historyStore.ClearAsync();
                    await loadGraph();
                }
            };
            if (devicePicker.Items.Count > 0) devicePicker.SelectedIndex = 0;
            refreshWidgets();

            root.Children.Add(new TextBlock { Text = "Устройство" }); root.Children.Add(devicePicker);
            root.Children.Add(new TextBlock { Text = "Показатель" }); root.Children.Add(widgetPicker);
            root.Children.Add(new TextBlock { Text = "Период графика" }); root.Children.Add(periodPicker);
            root.Children.Add(new TextBlock { Text = "Интервал общей записи" }); root.Children.Add(samplePicker);
            root.Children.Add(new TextBlock { Text = "Настройка отдельного графика" });
            root.Children.Add(modePicker); root.Children.Add(graphIntervalPicker); root.Children.Add(addGraphConfig);
            root.Children.Add(graphConfigsPanel); refreshGraphConfigs();
            root.Children.Add(new TextBlock { Text = "Срок хранения" }); root.Children.Add(retentionPicker);
            root.Children.Add(new TextBlock { Text = "Запись выполняется независимо от частоты MQTT; по умолчанию каждые 10 секунд.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) });
            root.Children.Add(refresh); root.Children.Add(export); root.Children.Add(info); root.Children.Add(chartScroll); root.Children.Add(clear);
            DashboardHost.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var ignored = loadGraph();
        }

        private static string EscapeCsv(string value)
        {
            string text = value ?? "";
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        private static void AddChoice(ComboBox picker, string label, object value)
        {
            picker.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }

        private static void SelectChoice(ComboBox picker, object value)
        {
            foreach (var entry in picker.Items.OfType<ComboBoxItem>())
                if (Convert.ToString(entry.Tag) == Convert.ToString(value)) { picker.SelectedItem = entry; return; }
        }

        private void Pending_Click(object sender, RoutedEventArgs e)
        {
            SetSection("Отложенные", "Отложенные команды", "Запланируйте действие устройства на более позднее время.");
            UpdateNavigation(PendingNav);
            ShowPendingCommands();
        }

        private void ShowPendingCommands()
        {
            DashboardHost.Visibility = Visibility.Visible;
            DashboardHost.Children.Clear();
            var root = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var devicePicker = new ComboBox { Header = "Устройство", MinWidth = 240 };
            var widgetPicker = new ComboBox { Header = "Элемент управления", MinWidth = 240 };
            var valueBox = new TextBox { Header = "Значение команды", PlaceholderText = "Например: 1, 0 или 23.5" };
            var delayBox = new TextBox { Header = "Выполнить через (минут)", Text = "5" };
            var info = new TextBlock { Text = "Важно: команда отправляется в назначенное время, только если приложение активно и MQTT подключён. Если Windows приостановит приложение, отправка произойдёт после возвращения в приложение и восстановления MQTT.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromArgb(255, 170, 175, 190)), Margin = new Thickness(0, 8, 0, 0) };
            var listPanel = new StackPanel();
            var addButton = new Button { Content = "Запланировать команду", HorizontalAlignment = HorizontalAlignment.Stretch };
            var devices = _dashboardSnapshot.Devices.ToList();
            foreach (var device in devices)
                devicePicker.Items.Add(new ComboBoxItem { Content = String.IsNullOrWhiteSpace(device.Name) ? device.Id : device.Name, Tag = device.Id });
            Action refreshWidgets = () =>
            {
                widgetPicker.Items.Clear();
                var selectedDevice = devicePicker.SelectedItem as ComboBoxItem;
                var device = selectedDevice == null ? null : devices.FirstOrDefault(x => x.Id == Convert.ToString(selectedDevice.Tag));
                if (device == null) return;
                foreach (var widget in device.Widgets.Where(x => x.Type == "TOGGLE" || x.Type == "BUTTON" || x.Type == "INPUT"))
                    widgetPicker.Items.Add(new ComboBoxItem { Content = String.IsNullOrWhiteSpace(widget.Title) ? widget.Id : widget.Title, Tag = widget.Id });
                if (widgetPicker.Items.Count > 0) widgetPicker.SelectedIndex = 0;
            };
            devicePicker.SelectionChanged += (s, e) => refreshWidgets();
            if (devicePicker.Items.Count > 0) devicePicker.SelectedIndex = 0;
            refreshWidgets();
            addButton.Click += (s, e) =>
            {
                var selectedDevice = devicePicker.SelectedItem as ComboBoxItem;
                var selectedWidget = widgetPicker.SelectedItem as ComboBoxItem;
                int minutes;
                if (selectedDevice == null || selectedWidget == null)
                {
                    info.Text = "Сначала выберите устройство и элемент управления.";
                    return;
                }
                if (!Int32.TryParse(delayBox.Text, out minutes) || minutes < 1 || minutes > 525600)
                {
                    info.Text = "Укажите задержку от 1 минуты до 365 дней.";
                    return;
                }
                if (String.IsNullOrWhiteSpace(valueBox.Text))
                {
                    info.Text = "Введите значение, которое нужно отправить устройству.";
                    return;
                }
                string deviceId = Convert.ToString(selectedDevice.Tag);
                string widgetId = Convert.ToString(selectedWidget.Tag);
                var device = devices.First(x => x.Id == deviceId);
                var widget = device.Widgets.First(x => x.Id == widgetId);
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _delayedCommandStore.Add(new DelayedDeviceCommand {
                    Id = Guid.NewGuid().ToString("N"), DeviceId = deviceId,
                    DeviceName = device.Name, WidgetId = widgetId, WidgetName = widget.Title,
                    Value = valueBox.Text.Trim(), CreatedAtUnixMs = now,
                    ExecuteAtUnixMs = now + (long)minutes * 60L * 1000L
                });
                valueBox.Text = "";
                info.Text = "Команда сохранена. Она будет отправлена в назначенное время, когда приложение активно и MQTT подключён.";
                RefreshPendingCommandList(listPanel);
            };
            root.Children.Add(new TextBlock { Text = "Создание отложенной команды", FontSize = 20, FontWeight = Windows.UI.Text.FontWeights.SemiBold });
            root.Children.Add(devicePicker);
            root.Children.Add(widgetPicker);
            root.Children.Add(valueBox);
            root.Children.Add(delayBox);
            root.Children.Add(addButton);
            root.Children.Add(info);
            root.Children.Add(new TextBlock { Text = "Очередь команд", FontSize = 20, Margin = new Thickness(0, 18, 0, 4), FontWeight = Windows.UI.Text.FontWeights.SemiBold });
            root.Children.Add(listPanel);
            DashboardHost.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            RefreshPendingCommandList(listPanel);
        }

        private void RefreshPendingCommandList(Panel panel)
        {
            panel.Children.Clear();
            var commands = _delayedCommandStore.GetAll();
            if (commands.Count == 0)
            {
                panel.Children.Add(new TextBlock { Text = "Отложенных команд пока нет.", Margin = new Thickness(0, 8, 0, 8) });
                return;
            }
            foreach (var command in commands)
            {
                var cardContent = new StackPanel();
                var card = new Border { Margin = new Thickness(0, 6, 0, 10), Padding = new Thickness(12), Background = new SolidColorBrush(Color.FromArgb(255, 42, 44, 54)), Child = cardContent };
                long remaining = Math.Max(0, command.ExecuteAtUnixMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                string when = DateTimeOffset.FromUnixTimeMilliseconds(command.ExecuteAtUnixMs).ToLocalTime().ToString("dd.MM.yyyy HH:mm");
                string countdown = remaining <= 0 ? "Ожидает отправки" : "Осталось: " + TimeSpan.FromMilliseconds(remaining).ToString(@"d\.hh\:mm\:ss");
                cardContent.Children.Add(new TextBlock { Text = command.DeviceName + " • " + command.WidgetName + " = " + command.Value, FontSize = 17, TextWrapping = TextWrapping.Wrap });
                cardContent.Children.Add(new TextBlock { Text = "Выполнение: " + when });
                cardContent.Children.Add(new TextBlock { Text = countdown, Foreground = new SolidColorBrush(Color.FromArgb(255, 190, 190, 205)) });
                var cancel = new Button { Content = "Отменить", Tag = command.Id, HorizontalAlignment = HorizontalAlignment.Left };
                cancel.Click += (s, e) =>
                {
                    _delayedCommandStore.Remove(Convert.ToString(((Button)s).Tag));
                    RefreshPendingCommandList(panel);
                };
                cardContent.Children.Add(cancel);
                panel.Children.Add(card);
            }
        }

        private async void DelayedCommandTimer_Tick(object sender, object e)
        {
            if (_delayedCommandTickRunning) return;
            _delayedCommandTickRunning = true;
            try
            {
                foreach (var command in _delayedCommandStore.GetAll().Where(x => x.ExecuteAtUnixMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToList())
                {
                    if (!_mqtt.IsConnected) break;
                    bool sent = await _mqtt.SendControlAsync(command.DeviceId, command.WidgetId, command.Value);
                    if (sent) _delayedCommandStore.Remove(command.Id);
                    else break;
                }
                if (PageTitle.Text == "Отложенные")
                {
                    var scroll = DashboardHost.Children.OfType<ScrollViewer>().FirstOrDefault();
                    var root = scroll == null ? null : scroll.Content as StackPanel;
                    var list = root == null ? null : root.Children.OfType<StackPanel>().LastOrDefault();
                    if (list != null) RefreshPendingCommandList(list);
                }
            }
            catch (Exception ex) { StatusText.Text = "Ошибка очереди отложенных команд: " + ex.Message; }
            finally { _delayedCommandTickRunning = false; }
        }

        private void Scenarios_Click(object sender, RoutedEventArgs e)
        {
            SetSection("Сценарии", "Автоматизация", "Условия по датчикам и действия для устройств.");
            ShowScenarios();
        }

        private void ShowScenarios()
        {
            DashboardHost.Visibility = Visibility.Visible;
            DashboardHost.Children.Clear();
            var root = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var add = new Button { Content = "+ Добавить сценарий" };
            add.Click += (s, e) => ShowScenarioEditor(null);
            root.Children.Add(add);
            var scenarios = _scenarioStore.GetAll();
            if (scenarios.Count == 0) root.Children.Add(new TextBlock { Text = "Сценариев пока нет. Добавьте условия датчиков и действия устройств.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) });
            foreach (var scenario in scenarios)
            {
                var body = new StackPanel();
                var card = new Border { Margin = new Thickness(0, 8, 0, 8), Padding = new Thickness(12), Background = new SolidColorBrush(Color.FromArgb(255, 42, 44, 54)), Child = body };
                body.Children.Add(new TextBlock { Text = scenario.Title, FontSize = 19, FontWeight = Windows.UI.Text.FontWeights.SemiBold });
                foreach (var condition in scenario.Conditions)
                {
                    var widget = _dashboardSnapshot.Devices.SelectMany(x => x.Widgets).FirstOrDefault(x => x.Id == condition.WidgetId);
                    body.Children.Add(new TextBlock { Text = (condition.Connector == "OR" ? "ИЛИ " : "") + (widget == null ? condition.WidgetId : widget.Title) + " " + condition.Operator + " " + condition.Threshold.ToString(CultureInfo.InvariantCulture), TextWrapping = TextWrapping.Wrap });
                }
                body.Children.Add(new TextBlock { Text = scenario.ActionType == "MQTT_CONTROL" ? "MQTT-действий: " + scenario.Actions.Count : "Только уведомление" });
                body.Children.Add(new TextBlock { Text = scenario.Enabled ? "Включён" : "Выключен" });
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                var toggle = new Button { Content = scenario.Enabled ? "Выключить" : "Включить", Tag = scenario.Id };
                toggle.Click += (s, e) => { _scenarioStore.SetEnabled(Convert.ToString(((Button)s).Tag), !scenario.Enabled); ShowScenarios(); };
                var edit = new Button { Content = "Изменить", Tag = scenario.Id, Margin = new Thickness(6, 0, 0, 0) };
                edit.Click += (s, e) => ShowScenarioEditor(_scenarioStore.GetAll().FirstOrDefault(x => x.Id == Convert.ToString(((Button)s).Tag)));
                var remove = new Button { Content = "Удалить", Tag = scenario.Id, Margin = new Thickness(6, 0, 0, 0) };
                remove.Click += async (s, e) =>
                {
                    var dialog = new ContentDialog { Title = "Удалить сценарий?", Content = scenario.Title, PrimaryButtonText = "Удалить", CloseButtonText = "Отмена" };
                    if (await dialog.ShowAsync() == ContentDialogResult.Primary) { _scenarioStore.Delete(Convert.ToString(((Button)s).Tag)); ShowScenarios(); }
                };
                buttons.Children.Add(toggle); buttons.Children.Add(edit); buttons.Children.Add(remove);
                body.Children.Add(buttons); root.Children.Add(card);
            }
            DashboardHost.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        private void ShowScenarioEditor(AutomationScenario existing)
        {
            var sensors = _dashboardSnapshot.Devices.SelectMany(d => d.Widgets.Where(w => w.Type == "VALUE" || w.Type == "STATUS").Select(w => new { Device = d, Widget = w })).ToList();
            var controls = _dashboardSnapshot.Devices.SelectMany(d => d.Widgets.Where(w => w.Type == "TOGGLE" || w.Type == "BUTTON" || w.Type == "INPUT").Select(w => new { Device = d, Widget = w })).ToList();
            DashboardHost.Children.Clear();
            var root = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var title = new TextBox { Header = "Название", Text = existing == null ? "Температура высокая" : existing.Title };
            var message = new TextBox { Header = "Текст уведомления", Text = existing == null ? "Условие выполнено: {value}" : existing.Message };
            var sensorPicker = new ComboBox { Header = "Датчик", MinWidth = 220 };
            foreach (var x in sensors) sensorPicker.Items.Add(new ComboBoxItem { Content = x.Widget.Title + " (" + x.Device.Name + ")", Tag = x.Widget.Id });
            if (sensorPicker.Items.Count > 0) sensorPicker.SelectedIndex = 0;
            var operatorPicker = new ComboBox { Header = "Оператор" };
            foreach (var op in new[] { ">", ">=", "<", "<=", "=" }) operatorPicker.Items.Add(new ComboBoxItem { Content = op, Tag = op });
            operatorPicker.SelectedIndex = 0;
            var threshold = new TextBox { Header = "Порог", Text = "25" };
            var connector = new ComboBox { Header = "Связь со следующим условием" };
            connector.Items.Add(new ComboBoxItem { Content = "И", Tag = "AND" });
            connector.Items.Add(new ComboBoxItem { Content = "ИЛИ", Tag = "OR" });
            connector.SelectedIndex = 0;
            var conditionsPanel = new StackPanel();
            var conditions = new List<AutomationCondition>();
            Action renderConditions = () =>
            {
                conditionsPanel.Children.Clear();
                foreach (var c in conditions)
                {
                    var found = sensors.FirstOrDefault(x => x.Widget.Id == c.WidgetId);
                    conditionsPanel.Children.Add(new TextBlock { Text = (c.Connector == "OR" ? "ИЛИ " : "") + (found == null ? c.WidgetId : found.Widget.Title) + " " + c.Operator + " " + c.Threshold.ToString(CultureInfo.InvariantCulture) });
                }
            };
            var addCondition = new Button { Content = "Добавить условие" };
            addCondition.Click += (s, e) =>
            {
                var selected = sensorPicker.SelectedItem as ComboBoxItem;
                double value;
                if (selected == null || !Double.TryParse(threshold.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out value)) { StatusText.Text = "Выберите датчик и числовой порог."; return; }
                var op = operatorPicker.SelectedItem as ComboBoxItem;
                var join = connector.SelectedItem as ComboBoxItem;
                conditions.Add(new AutomationCondition { WidgetId = Convert.ToString(selected.Tag), Operator = op == null ? ">" : Convert.ToString(op.Tag), Threshold = value, Connector = conditions.Count == 0 ? "AND" : (join == null ? "AND" : Convert.ToString(join.Tag)) });
                renderConditions();
            };
            var actionType = new ComboBox { Header = "Тип сценария" };
            actionType.Items.Add(new ComboBoxItem { Content = "Только уведомление", Tag = "NOTIFICATION" });
            actionType.Items.Add(new ComboBoxItem { Content = "Управление MQTT", Tag = "MQTT_CONTROL" });
            SelectChoice(actionType, existing == null ? "NOTIFICATION" : existing.ActionType);
            var controlPicker = new ComboBox { Header = "Элемент управления", MinWidth = 220 };
            foreach (var x in controls) controlPicker.Items.Add(new ComboBoxItem { Content = x.Widget.Title + " (" + x.Device.Name + ")", Tag = x.Device.Id + "|" + x.Widget.Id });
            if (controlPicker.Items.Count > 0) controlPicker.SelectedIndex = 0;
            var actionValue = new TextBox { Header = "Значение действия", Text = "1" };
            var actionsPanel = new StackPanel();
            var actions = new List<AutomationAction>();
            Action renderActions = () =>
            {
                actionsPanel.Children.Clear();
                foreach (var a in actions)
                {
                    var found = controls.FirstOrDefault(x => x.Device.Id == a.DeviceId && x.Widget.Id == a.WidgetId);
                    actionsPanel.Children.Add(new TextBlock { Text = (found == null ? a.DeviceId + "/" + a.WidgetId : found.Widget.Title) + " → " + a.Value });
                }
            };
            var addAction = new Button { Content = "Добавить действие" };
            addAction.Click += (s, e) =>
            {
                var selected = controlPicker.SelectedItem as ComboBoxItem;
                if (selected == null || String.IsNullOrWhiteSpace(actionValue.Text)) { StatusText.Text = "Выберите элемент управления и значение."; return; }
                var ids = Convert.ToString(selected.Tag).Split('|');
                if (ids.Length == 2) actions.Add(new AutomationAction { DeviceId = ids[0], WidgetId = ids[1], Value = actionValue.Text.Trim() });
                renderActions();
            };
            if (existing != null)
            {
                conditions.AddRange(existing.Conditions.Select(x => new AutomationCondition { WidgetId = x.WidgetId, Operator = x.Operator, Threshold = x.Threshold, Connector = x.Connector }));
                actions.AddRange(existing.Actions.Select(x => new AutomationAction { DeviceId = x.DeviceId, WidgetId = x.WidgetId, Value = x.Value }));
                renderConditions(); renderActions();
            }
            root.Children.Add(new TextBlock { Text = existing == null ? "Новый сценарий" : "Редактирование сценария", FontSize = 21 });
            root.Children.Add(title); root.Children.Add(sensorPicker); root.Children.Add(operatorPicker); root.Children.Add(threshold); root.Children.Add(connector); root.Children.Add(addCondition); root.Children.Add(conditionsPanel);
            root.Children.Add(actionType); root.Children.Add(message); root.Children.Add(controlPicker); root.Children.Add(actionValue); root.Children.Add(addAction); root.Children.Add(actionsPanel);
            var enabled = new CheckBox { Content = "Сценарий включён", IsChecked = existing == null ? true : existing.Enabled };
            var notifications = new CheckBox { Content = "Показывать уведомление", IsChecked = existing == null ? true : existing.NotificationEnabled };
            root.Children.Add(enabled); root.Children.Add(notifications);
            var save = new Button { Content = "Сохранить сценарий", Margin = new Thickness(0, 10, 0, 0) };
            save.Click += (s, e) =>
            {
                if (String.IsNullOrWhiteSpace(title.Text) || conditions.Count == 0) { StatusText.Text = "Укажите название и добавьте условие."; return; }
                var kindItem = actionType.SelectedItem as ComboBoxItem;
                string kind = kindItem == null ? "NOTIFICATION" : Convert.ToString(kindItem.Tag);
                if (kind == "MQTT_CONTROL" && actions.Count == 0) { StatusText.Text = "Добавьте хотя бы одно действие."; return; }
                var scenario = existing ?? new AutomationScenario();
                scenario.Title = title.Text.Trim(); scenario.Message = message.Text; scenario.ActionType = kind;
                scenario.Enabled = enabled.IsChecked == true; scenario.NotificationEnabled = notifications.IsChecked == true;
                scenario.Conditions = conditions.Select(x => new AutomationCondition { WidgetId = x.WidgetId, Operator = x.Operator, Threshold = x.Threshold, Connector = x.Connector }).ToList();
                scenario.Actions = actions.Select(x => new AutomationAction { DeviceId = x.DeviceId, WidgetId = x.WidgetId, Value = x.Value }).ToList();
                _scenarioStore.Save(scenario); ShowScenarios(); StatusText.Text = "Сценарий сохранён.";
            };
            var cancel = new Button { Content = "Назад", Margin = new Thickness(0, 6, 0, 0) };
            cancel.Click += (s, e) => ShowScenarios();
            root.Children.Add(save); root.Children.Add(cancel);
            DashboardHost.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        }

        private void Voice_Click(object sender, RoutedEventArgs e) { SetSection("Голос", "Настройки голоса", "Настройки распознавания и синтеза речи."); }
        private void Journal_Click(object sender, RoutedEventArgs e) { ShowJournal(); }

        private void ShowJournal()
        {
            SetSection("Журнал", "Журнал событий", "Диагностика приложения, MQTT и UDP. Хранится до 500 последних записей.");
            DashboardHost.Visibility = Visibility.Visible;
            DashboardHost.Children.Clear();
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
            var refresh = new Button { Content = "Обновить" };
            refresh.Click += (sender, args) => RenderJournalEntries();
            var clear = new Button { Content = "Очистить журнал", Margin = new Thickness(8, 0, 0, 0) };
            clear.Click += async (sender, args) =>
            {
                var dialog = new ContentDialog { Title = "Очистить журнал?", Content = "Все диагностические записи будут удалены.", PrimaryButtonText = "Очистить", CloseButtonText = "Отмена" };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary) { _diagnostics.Clear(); RenderJournalEntries(); }
            };
            actions.Children.Add(refresh); actions.Children.Add(clear);
            DashboardHost.Children.Add(actions);
            RenderJournalEntries();
        }

        private void RenderJournalEntries()
        {
            if (PageTitle.Text != "Журнал") return;
            var old = DashboardHost.Children.OfType<StackPanel>().FirstOrDefault(x => Convert.ToString(x.Tag) == "journal-list");
            if (old != null) DashboardHost.Children.Remove(old);
            var list = new StackPanel { Tag = "journal-list" };
            var entries = _diagnostics.GetAll();
            if (entries.Count == 0) list.Children.Add(new TextBlock { Text = "Записей пока нет.", Margin = new Thickness(0, 8, 0, 8) });
            foreach (var entry in entries)
            {
                list.Children.Add(new Border {
                    Background = new SolidColorBrush(Color.FromArgb(255, 42, 44, 54)),
                    Padding = new Thickness(10), Margin = new Thickness(0, 3, 0, 3),
                    Child = new StackPanel {
                        Children = {
                            new TextBlock { Text = entry.Time.ToString("dd.MM.yyyy HH:mm:ss") + " · " + entry.Level + " · " + entry.Source, FontSize = 12, Foreground = new SolidColorBrush(Color.FromArgb(255, 180, 180, 194)) },
                            new TextBlock { Text = entry.Message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) }
                        }
                    }
                });
            }
            DashboardHost.Children.Add(list);
            StatusText.Text = "Записей в журнале: " + entries.Count;
        }
        private void MqttSettings_Click(object sender, RoutedEventArgs e)
        {
            SetSection("MQTT", "Подключение MQTT", "Настройки брокера и состояние связи с ESP32.");
            DashboardHost.Visibility = Visibility.Visible;
            DashboardHost.Children.Clear();

            var panel = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var host = new TextBox { Header = "Адрес брокера", PlaceholderText = "192.168.1.10", Text = ReadMqttSetting("host", "") };
            var port = new TextBox { Header = "Порт", Text = ReadMqttSetting("port", "1883") };
            var prefix = new TextBox { Header = "MQTT prefix", Text = ReadMqttSetting("prefix", "IoTManager") };
            var username = new TextBox { Header = "Пользователь", Text = ReadMqttSetting("username", "") };
            var passwordLabel = new TextBlock { Text = "Пароль", Margin = new Thickness(0, 8, 0, 2) };
            var password = new PasswordBox { PlaceholderText = "Пароль", Password = ReadMqttSetting("password", "") };
            var tls = new CheckBox { Content = "Использовать TLS", IsChecked = ReadMqttSetting("tls", "false") == "true" };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var connect = new Button { Content = "Подключить", MinWidth = 120 };
            var disconnect = new Button { Content = "Отключить", MinWidth = 110 };
            var hello = new Button { Content = "Отправить HELLO", MinWidth = 130 };
            connect.Click += async (buttonSender, args) =>
            {
                int portNumber;
                if (!Int32.TryParse(port.Text, out portNumber) || portNumber < 1 || portNumber > 65535)
                {
                    StatusText.Text = "Порт должен быть числом от 1 до 65535.";
                    return;
                }
                SaveMqttSetting("host", host.Text);
                SaveMqttSetting("port", portNumber.ToString());
                SaveMqttSetting("prefix", prefix.Text);
                SaveMqttSetting("username", username.Text);
                SaveMqttSetting("password", password.Password);
                SaveMqttSetting("tls", tls.IsChecked == true ? "true" : "false");
                try
                {
                    StatusText.Text = "Подключение к MQTT…";
                    await _mqtt.StartAsync(new MqttConnectionSettings {
                        Host = host.Text, Port = portNumber, Prefix = prefix.Text,
                        Username = username.Text, Password = password.Password, UseTls = tls.IsChecked == true
                    });
                }
                catch (Exception ex) { StatusText.Text = "Ошибка MQTT: " + ex.Message; }
            };
            disconnect.Click += (buttonSender, args) => { _mqtt.Stop(); StatusText.Text = "MQTT отключён вручную."; };
            hello.Click += async (buttonSender, args) =>
            {
                bool sent = await _mqtt.SendHelloAsync();
                StatusText.Text = sent ? "HELLO отправлен." : "HELLO не отправлен: нет соединения.";
            };
            connect.Margin = new Thickness(0, 8, 6, 0); disconnect.Margin = new Thickness(0, 8, 6, 0); hello.Margin = new Thickness(0, 8, 0, 0);
            buttons.Children.Add(connect); buttons.Children.Add(disconnect); buttons.Children.Add(hello);
            panel.Children.Add(host); panel.Children.Add(port); panel.Children.Add(prefix);
            panel.Children.Add(username); panel.Children.Add(passwordLabel); panel.Children.Add(password); panel.Children.Add(tls);
            panel.Children.Add(buttons);
            DashboardHost.Children.Add(panel);
        }

        private string ReadMqttSetting(string key, string fallback)
        {
            object value;
            return ApplicationData.Current.LocalSettings.Values.TryGetValue("mqtt_" + key, out value)
                ? Convert.ToString(value) : fallback;
        }

        private void SaveMqttSetting(string key, string value)
        {
            ApplicationData.Current.LocalSettings.Values["mqtt_" + key] = value ?? "";
        }

        private async void TryRestoreMqttConnection()
        {
            if (_mqtt.IsConnected) return;
            string host = ReadMqttSetting("host", "");
            int port;
            if (String.IsNullOrWhiteSpace(host) || !Int32.TryParse(ReadMqttSetting("port", "1883"), out port))
                return;
            try
            {
                await _mqtt.StartAsync(new MqttConnectionSettings {
                    Host = host, Port = port, Prefix = ReadMqttSetting("prefix", "IoTManager"),
                    Username = ReadMqttSetting("username", ""), Password = ReadMqttSetting("password", ""),
                    UseTls = ReadMqttSetting("tls", "false") == "true"
                });
            }
            catch (Exception ex) { StatusText.Text = "Не удалось восстановить MQTT: " + ex.Message; }
        }
        private void UdpSettings_Click(object sender, RoutedEventArgs e) { ShowUdpSettings(); }

        private void ShowUdpSettings()
        {
            SetSection("UDP", "Поиск устройств в сети", "ESP32 отправляет объявления по UDP broadcast. Данные датчиков и управление продолжают работать через MQTT.");
            DashboardHost.Visibility = Visibility.Visible;
            DashboardHost.Children.Clear();

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
            var start = new Button { Content = "Запустить поиск", Margin = new Thickness(0, 0, 8, 0) };
            start.Click += async (sender, args) =>
            {
                try { await _udpDiscovery.StartAsync(); StatusText.Text = "UDP-поиск запущен."; }
                catch (Exception ex) { StatusText.Text = "Не удалось запустить UDP: " + ex.Message; }
            };
            var stop = new Button { Content = "Остановить поиск" };
            stop.Click += (sender, args) => _udpDiscovery.Stop();
            var refresh = new Button { Content = "Обновить список", Margin = new Thickness(8, 0, 0, 0) };
            refresh.Click += (sender, args) => RenderUdpDeviceList(_udpDiscovery.GetSnapshot());
            actions.Children.Add(start); actions.Children.Add(stop); actions.Children.Add(refresh);
            DashboardHost.Children.Add(actions);
            DashboardHost.Children.Add(new TextBlock {
                Text = "Порт UDP 4210 · broadcast 255.255.255.255 · срок актуальности устройства 5 минут",
                TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromArgb(255, 170, 172, 184)),
                Margin = new Thickness(0, 0, 0, 8)
            });
            RenderUdpDeviceList(_udpDiscovery.GetSnapshot());
            StatusText.Text = _udpDiagnostic;
        }

        private void RenderUdpDeviceList(IList<UdpDiscoveredDevice> devices)
        {
            if (PageTitle.Text != "UDP") return;
            var old = DashboardHost.Children.OfType<StackPanel>().FirstOrDefault(x => Convert.ToString(x.Tag) == "udp-device-list");
            if (old != null) DashboardHost.Children.Remove(old);
            var list = new StackPanel { Tag = "udp-device-list", Margin = new Thickness(0, 4, 0, 0) };
            if (devices == null || devices.Count == 0)
            {
                list.Children.Add(new TextBlock {
                    Text = "Устройства пока не найдены. Убедитесь, что телефон и ESP32 находятся в одной локальной сети и устройство отправляет UDP-объявления.",
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8)
                });
            }
            else
            {
                foreach (var device in devices)
                {
                    var cardBody = new StackPanel();
                    cardBody.Children.Add(new TextBlock {
                        Text = String.IsNullOrWhiteSpace(device.Name) ? (String.IsNullOrWhiteSpace(device.Id) ? device.Wg : device.Id) : device.Name,
                        FontSize = 18, FontWeight = Windows.UI.Text.FontWeights.SemiBold
                    });
                    cardBody.Children.Add(new TextBlock { Text = "IP: " + device.Ip + "    ID: " + device.Id, TextWrapping = TextWrapping.Wrap });
                    cardBody.Children.Add(new TextBlock { Text = "WG: " + device.Wg + "    Статус: " + device.Status, TextWrapping = TextWrapping.Wrap });
                    cardBody.Children.Add(new TextBlock { Text = "Прошивка: " + device.FirmwareVersion + "    Последний пакет: " + device.LastSeenAt.ToLocalTime().ToString("HH:mm:ss"), TextWrapping = TextWrapping.Wrap });
                    list.Children.Add(new Border {
                        Background = new SolidColorBrush(Color.FromArgb(255, 42, 44, 54)),
                        Padding = new Thickness(12), Margin = new Thickness(0, 4, 0, 4), Child = cardBody
                    });
                }
            }
            list.Children.Add(new TextBlock {
                Text = "Диагностика: " + _udpDiagnostic, TextWrapping = TextWrapping.Wrap,
                FontSize = 12, Margin = new Thickness(0, 10, 0, 8),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 170, 172, 184))
            });
            DashboardHost.Children.Add(list);
        }
        private void AppSettings_Click(object sender, RoutedEventArgs e) { ShowAppSettings(); }

        private void ShowAppSettings()
        {
            SetSection("Настройки", "Служебные настройки", "Состояние приложения, разрешения и управление локальными данными.");
            DashboardHost.Visibility = Visibility.Visible;
            DashboardHost.Children.Clear();
            var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var lastUpdated = new CheckBox { Content = "Показывать время последнего обновления виджетов", IsChecked = _dashboardRenderer.ShowLastUpdated };
            lastUpdated.Checked += (sender, args) => { _dashboardRenderer.ShowLastUpdated = true; ApplicationData.Current.LocalSettings.Values["show_last_updated"] = true; if (PageTitle.Text == "Главная") _dashboardRenderer.Render(); };
            lastUpdated.Unchecked += (sender, args) => { _dashboardRenderer.ShowLastUpdated = false; ApplicationData.Current.LocalSettings.Values["show_last_updated"] = false; if (PageTitle.Text == "Главная") _dashboardRenderer.Render(); };
            panel.Children.Add(lastUpdated);
            panel.Children.Add(new TextBlock { Text = "Разрешения пакета", FontSize = 18, Margin = new Thickness(0, 18, 0, 6) });
            panel.Children.Add(new TextBlock { Text = "Интернет (internetClient) — MQTT и проверка обновлений. Частная сеть (privateNetworkClientServer) — поиск ESP32 по UDP. Микрофон — голосовые функции. Разрешения объявлены в манифесте приложения; Windows может дополнительно управлять доступом к микрофону в настройках конфиденциальности.", TextWrapping = TextWrapping.Wrap });
            var permissionButton = new Button { Content = "Проверить доступ к микрофону", Margin = new Thickness(0, 8, 0, 0) };
            permissionButton.Click += async (sender, args) =>
            {
                try
                {
                    var capture = new Windows.Media.Capture.MediaCapture();
                    await capture.InitializeAsync();
                    StatusText.Text = "Микрофон доступен приложению.";
                    capture.Dispose();
                }
                catch (Exception ex) { StatusText.Text = "Микрофон недоступен: " + ex.Message; }
            };
            panel.Children.Add(permissionButton);
            panel.Children.Add(new TextBlock { Text = "Локальные данные", FontSize = 18, Margin = new Thickness(0, 18, 0, 6) });
            var clear = new Button { Content = "Очистить историю и служебные очереди" };
            clear.Click += ClearLocalData_Click;
            panel.Children.Add(clear);
            var info = new Button { Content = "Информация о приложении", Margin = new Thickness(0, 8, 0, 0) };
            info.Click += InfoButton_Click;
            panel.Children.Add(info);
            DashboardHost.Children.Add(panel);
            StatusText.Text = "Локальные данные хранятся на устройстве.";
        }

        private async void ClearLocalData_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog {
                Title = "Очистить локальные данные?",
                Content = "Будут удалены история телеметрии, отложенные команды и сценарии автоматизации. Настройки MQTT и голосовые команды останутся.",
                PrimaryButtonText = "Очистить", CloseButtonText = "Отмена"
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            try
            {
                await _historyStore.ClearAsync();
                foreach (var item in _delayedCommandStore.GetAll()) _delayedCommandStore.Remove(item.Id);
                foreach (var item in _scenarioStore.GetAll()) _scenarioStore.Delete(item.Id);
                _udpDiscovery.Clear();
                _diagnostics.Add("Settings", "Пользователь очистил историю, очередь отложенных команд, сценарии и список UDP-устройств.");
                StatusText.Text = "История, отложенные команды и сценарии очищены.";
            }
            catch (Exception ex) { StatusText.Text = "Ошибка очистки данных: " + ex.Message; }
        }
        private void TextSize_Click(object sender, RoutedEventArgs e) { SetSection("Размер текста", "Размер текста", "Масштаб интерфейса и читаемость на разных экранах."); }
        private void ToggleLastUpdated_Click(object sender, RoutedEventArgs e)
        {
            _dashboardRenderer.ShowLastUpdated = !_dashboardRenderer.ShowLastUpdated;
            StatusText.Text = _dashboardRenderer.ShowLastUpdated
                ? "Показано время последнего обновления элементов."
                : "Время последнего обновления скрыто.";
            if (PageTitle.Text == "Главная") _dashboardRenderer.Render();
        }

        private async void InfoButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "ESP32-AI-Control",
                Content = "ESP32-AI-Control для Windows 10 Mobile. Перенос основных разделов выполняется поэтапно.",
                PrimaryButtonText = "Закрыть"
            };
            await dialog.ShowAsync();
        }
    }
}
