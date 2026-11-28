using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.Storage;
using Windows.UI.Core;
using Windows.Media.SpeechRecognition;\nusing Windows.UI.Xaml.Shapes;

namespace ESP32AIControl.UWP
{
    public sealed partial class MainPage : Page
    {
        private bool _menuOpen;
        private readonly DashboardSnapshot _dashboardSnapshot = new DashboardSnapshot();
        private DashboardRenderer _dashboardRenderer;
        private UpdateManifest _pendingUpdateManifest;
        private readonly MqttDeviceService _mqtt = new MqttDeviceService();\n        private readonly HistoryStore _historyStore = new HistoryStore();

        public event EventHandler<DeviceCommandRequestedEventArgs> DeviceCommandRequested;

        public MainPage()
        {
            InitializeComponent();
            _dashboardRenderer = new DashboardRenderer(DashboardHost, _dashboardSnapshot,
                page => { StatusText.Text = "Страница: " + page; },
                (device, widget, value) => DeviceCommandRequested?.Invoke(this,
                    new DeviceCommandRequestedEventArgs(device, widget, value)));
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
                var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => StatusText.Text = message);
            };
            _mqtt.DevicesChanged += devices =>
            {
                var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => SetDeviceSnapshot(devices));
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
            SizeChanged += MainPage_SizeChanged;
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
            if (PageTitle.Text == "Главная") _dashboardRenderer.Render();
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
            SetSection("Марфа", "Голосовое управление", "Распознавание русской речи и выполнение обученных команд.");
            DashboardHost.Visibility = Visibility.Visible;
            DashboardHost.Children.Clear();

            var root = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            root.Children.Add(new TextBlock { Text = "Марфа", FontSize = 24, FontWeight = Windows.UI.Text.FontWeights.SemiBold });
            root.Children.Add(new TextBlock { Text = "Нажмите кнопку и произнесите одну из сохранённых фраз. Для распознавания требуется установленный русский речевой пакет Windows.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) });
            var recognized = new TextBlock { Text = "Ожидание команды…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
            var listen = new Button { Content = "🎙 Слушать команду", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 48 };
            listen.Click += async (s, a) =>
            {
                listen.IsEnabled = false;
                recognized.Text = "Слушаю…";
                try
                {
                    using (var recognizer = new SpeechRecognizer())
                    {
                        recognizer.Constraints.Add(new DictationGrammarConstraint());
                        var compile = await recognizer.CompileConstraintsAsync();
                        if (compile.Status != SpeechRecognitionResultStatus.Success)
                        {
                            recognized.Text = "Не удалось подготовить распознавание: " + compile.Status +
                                ". Проверьте, установлен ли русский речевой пакет Windows.";
                            return;
                        }
                        var result = await recognizer.RecognizeWithUIAsync();
                        if (result.Status != SpeechRecognitionResultStatus.Success || String.IsNullOrWhiteSpace(result.Text))
                        {
                            recognized.Text = "Речь не распознана: " + result.Status;
                            return;
                        }
                        recognized.Text = "Распознано: «" + result.Text + "»";
                        await ExecuteTrainedVoiceCommandAsync(result.Text, recognized);
                    }
                }
                catch (Exception ex)
                {
                    recognized.Text = "Ошибка распознавания: " + ex.Message;
                }
                finally { listen.IsEnabled = true; }
            };
            root.Children.Add(listen);
            root.Children.Add(recognized);
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

        private static string NormalizeVoicePhrase(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            var chars = value.Trim().ToLowerInvariant().Select(ch =>
                Char.IsLetterOrDigit(ch) || Char.IsWhiteSpace(ch) ? ch : ' ').ToArray();
            return String.Join(" ", new string(chars).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private async Task ExecuteTrainedVoiceCommandAsync(string recognizedText, TextBlock output)
        {
            string normalized = NormalizeVoicePhrase(recognizedText);
            var commands = _trainedCommandStore.Load();
            var matches = commands.Where(command =>
                (command.Variants ?? new List<string>()).Concat(new[] { command.Phrase })
                    .Any(variant => NormalizeVoicePhrase(variant) == normalized)).ToList();

            if (matches.Count == 0)
            {
                output.Text += "\nКоманда не найдена. Добавьте эту фразу или её вариант в разделе «Команды».";
                return;
            }
            if (matches.Count > 1)
            {
                output.Text += "\nНайдено несколько одинаковых команд. Уточните обученные фразы.";
                return;
            }

            var match = matches[0];
            var device = _dashboardSnapshot.Devices.FirstOrDefault(x => x.Id == match.DeviceId);
            var widget = device == null ? null : device.Widgets.FirstOrDefault(x => x.Id == match.WidgetId);
            if (device == null || widget == null)
            {
                output.Text += "\nУстройство или виджет сейчас не найден в MQTT-конфигурации.";
                return;
            }
            if (match.Value == "__READ_VALUE__")
            {
                output.Text += "\nТекущее значение: " + widget.Value + (String.IsNullOrWhiteSpace(widget.Unit) ? "" : " " + widget.Unit);
                return;
            }

            output.Text += "\nОтправляю команду устройству…";
            bool sent = await _mqtt.SendControlAsync(match.DeviceId, match.WidgetId, match.Value);
            output.Text += sent ? "\nКоманда отправлена. Ожидаю подтверждение MQTT." : "\nНе удалось отправить: проверьте MQTT-соединение.";
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
            var chart = new Canvas { Width = Math.Max(320, ActualWidth - 70), Height = 220, Background = new SolidColorBrush(Color.FromArgb(255, 35, 36, 42)) };
            var chartScroll = new ScrollViewer { Content = chart, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Height = 230 };
            var refresh = new Button { Content = "Обновить график", HorizontalAlignment = HorizontalAlignment.Stretch };
            var clear = new Button { Content = "Очистить историю", HorizontalAlignment = HorizontalAlignment.Stretch };
            var selectedDeviceId = "";
            var selectedWidgetId = "";

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
                for (int i = 1; i < points.Count; i++)
                {
                    double x1 = left + (right - left) * (i - 1) / Math.Max(1.0, points.Count - 1);
                    double x2 = left + (right - left) * i / Math.Max(1.0, points.Count - 1);
                    double y1 = bottom - (points[i - 1].Value - min) * (bottom - top) / (max - min);
                    double y2 = bottom - (points[i].Value - min) * (bottom - top) / (max - min);
                    chart.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = new SolidColorBrush(Color.FromArgb(255, 117, 190, 255)), StrokeThickness = 2 });
                }
                info.Text = "Точек: " + points.Count + " • минимум: " + min.ToString("0.###", CultureInfo.InvariantCulture) +
                    " • максимум: " + max.ToString("0.###", CultureInfo.InvariantCulture) +
                    " • последнее: " + points[points.Count - 1].RawValue;
                var device = devices.FirstOrDefault(x => x.Id == selectedDeviceId);
                var widget = device == null ? null : device.Widgets.FirstOrDefault(x => x.Id == selectedWidgetId);
                if (widget != null && !String.IsNullOrWhiteSpace(widget.Unit)) info.Text += " " + widget.Unit;
            };

            periodPicker.SelectionChanged += async (s, a) =>
            {
                var item = periodPicker.SelectedItem as ComboBoxItem;
                if (item != null) _historyStore.SetDisplayPeriod(Convert.ToInt64(item.Tag));
                await loadGraph();
            };
            devicePicker.SelectionChanged += (s, a) => { refreshWidgets(); };
            widgetPicker.SelectionChanged += async (s, a) => { await loadGraph(); };
            refresh.Click += async (s, a) => await loadGraph();
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
            root.Children.Add(new TextBlock { Text = "Интервал записи" }); root.Children.Add(samplePicker);
            root.Children.Add(new TextBlock { Text = "Срок хранения" }); root.Children.Add(retentionPicker);
            root.Children.Add(new TextBlock { Text = "Запись выполняется независимо от частоты MQTT; по умолчанию каждые 10 секунд.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) });
            root.Children.Add(refresh); root.Children.Add(info); root.Children.Add(chartScroll); root.Children.Add(clear);
            DashboardHost.Children.Add(new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var ignored = loadGraph();
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
            SetSection("Отложенные", "Отложенные команды", "Список запланированных команд и их статусы.");
            UpdateNavigation(PendingNav);
        }

        private void Scenarios_Click(object sender, RoutedEventArgs e) { SetSection("Сценарии", "Сценарии", "Перенос сценариев будет выполнен отдельным этапом."); }
        private void Voice_Click(object sender, RoutedEventArgs e) { SetSection("Голос", "Настройки голоса", "Настройки распознавания и синтеза речи."); }
        private void Journal_Click(object sender, RoutedEventArgs e) { SetSection("Журнал", "Журнал", "Системные события и диагностика подключения."); }
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
            connect.Click += async (sender, args) =>
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
            disconnect.Click += (sender, args) => { _mqtt.Stop(); StatusText.Text = "MQTT отключён вручную."; };
            hello.Click += async (sender, args) =>
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

        private void TryRestoreMqttConnection()
        {
            string host = ReadMqttSetting("host", "");
            int port;
            if (String.IsNullOrWhiteSpace(host) || !Int32.TryParse(ReadMqttSetting("port", "1883"), out port))
                return;
            try
            {
                var ignored = _mqtt.StartAsync(new MqttConnectionSettings {
                    Host = host, Port = port, Prefix = ReadMqttSetting("prefix", "IoTManager"),
                    Username = ReadMqttSetting("username", ""), Password = ReadMqttSetting("password", ""),
                    UseTls = ReadMqttSetting("tls", "false") == "true"
                });
            }
            catch (Exception ex) { StatusText.Text = "Не удалось восстановить MQTT: " + ex.Message; }
        }
        private void UdpSettings_Click(object sender, RoutedEventArgs e) { SetSection("UDP", "UDP устройства", "Обнаружение устройств в локальной сети."); }
        private void AppSettings_Click(object sender, RoutedEventArgs e) { SetSection("Настройки", "Настройки приложения", "Общие параметры приложения."); }
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
