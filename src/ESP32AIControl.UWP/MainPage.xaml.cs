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

namespace ESP32AIControl.UWP
{
    public sealed partial class MainPage : Page
    {
        private bool _menuOpen;
        private readonly DashboardSnapshot _dashboardSnapshot = new DashboardSnapshot();
        private DashboardRenderer _dashboardRenderer;
        private UpdateManifest _pendingUpdateManifest;

        public event EventHandler<DeviceCommandRequestedEventArgs> DeviceCommandRequested;

        public MainPage()
        {
            InitializeComponent();
            _dashboardRenderer = new DashboardRenderer(DashboardHost, _dashboardSnapshot,
                page => { StatusText.Text = "Страница: " + page; },
                (device, widget, value) => DeviceCommandRequested?.Invoke(this,
                    new DeviceCommandRequestedEventArgs(device, widget, value)));
            ShowDashboard();
            UpdateNavigation(HomeNav);
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

        // Called by the future MQTT/device service when a fresh device snapshot arrives.
        public void SetDeviceSnapshot(IEnumerable<DeviceModel> devices)
        {
            _dashboardSnapshot.Devices.Clear();
            if (devices != null)
                foreach (var device in devices) _dashboardSnapshot.Devices.Add(device);
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
                UpdateProgressText.Text = "Передача пакета Device Portal. Не закрывайте приложение…";
                string installMessage = await UpdateService.InstallViaDevicePortalAsync(
                    file, portal.Address, portal.UserName, portal.Password);

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

        private async Task<DevicePortalCredentials> RequestDevicePortalCredentialsAsync()
        {
            var addressBox = new TextBox
            {
                Text = UpdateService.GetDevicePortalAddress(),
                PlaceholderText = "Например, 192.168.1.58"
            };
            var userBox = new TextBox
            {
                Text = UpdateService.GetDevicePortalUserName(),
                PlaceholderText = "Имя пользователя Device Portal"
            };
            var passwordBox = new PasswordBox
            {
                Password = UpdateService.GetDevicePortalPassword(),
                PlaceholderText = "Пароль Device Portal"
            };

            var fields = new StackPanel();
            var addressLabel = new TextBlock { Text = "Адрес Lumia (HTTPS)", Margin = new Thickness(0, 0, 0, 4) };
            var userLabel = new TextBlock { Text = "Логин Device Portal", Margin = new Thickness(0, 12, 0, 4) };
            var passwordLabel = new TextBlock { Text = "Пароль Device Portal", Margin = new Thickness(0, 12, 0, 4) };
            fields.Children.Add(addressLabel);
            fields.Children.Add(addressBox);
            fields.Children.Add(userLabel);
            fields.Children.Add(userBox);
            fields.Children.Add(passwordLabel);
            fields.Children.Add(passwordBox);
            fields.Children.Add(new TextBlock
            {
                Text = "Пароль сохранится в защищённом хранилище Windows. Для REST-запроса приложение использует служебный префикс auto- к логину.",
                TextWrapping = Windows.UI.Xaml.TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.75
            });

            var dialog = new ContentDialog
            {
                Title = "Установка обновления",
                Content = fields,
                PrimaryButtonText = "Продолжить",
                SecondaryButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return null;

            if (string.IsNullOrWhiteSpace(addressBox.Text) ||
                string.IsNullOrWhiteSpace(userBox.Text) ||
                string.IsNullOrEmpty(passwordBox.Password))
            {
                StatusText.Text = "Для установки укажите адрес Lumia, логин и пароль Device Portal.";
                return null;
            }

            UpdateService.SaveDevicePortalCredentials(
                addressBox.Text, userBox.Text, passwordBox.Password);

            return new DevicePortalCredentials
            {
                Address = addressBox.Text.Trim(),
                UserName = userBox.Text.Trim(),
                Password = passwordBox.Password
            };
        }

        private void Home_Click(object sender, RoutedEventArgs e)
        {
            ShowDashboard();
            UpdateNavigation(HomeNav);
        }

        private void Marfa_Click(object sender, RoutedEventArgs e)
        {
            SetSection("Марфа", "Марфа", "Экран и состояние голосового помощника. Само распознавание пока не подключено.");
            UpdateNavigation(MarfaNav);
        }

        private void Commands_Click(object sender, RoutedEventArgs e)
        {
            SetSection("Команды", "Обученные команды", "Список команд и управление ими будут перенесены из Android.");
            UpdateNavigation(CommandsNav);
        }

        private void History_Click(object sender, RoutedEventArgs e)
        {
            SetSection("История", "История и графики", "История измерений и графики будут подключены к общей модели данных.");
            UpdateNavigation(HistoryNav);
        }

        private void Pending_Click(object sender, RoutedEventArgs e)
        {
            SetSection("Отложенные", "Отложенные команды", "Список запланированных команд и их статусы.");
            UpdateNavigation(PendingNav);
        }

        private void Scenarios_Click(object sender, RoutedEventArgs e) { SetSection("Сценарии", "Сценарии", "Перенос сценариев будет выполнен отдельным этапом."); }
        private void Voice_Click(object sender, RoutedEventArgs e) { SetSection("Голос", "Настройки голоса", "Настройки распознавания и синтеза речи."); }
        private void Journal_Click(object sender, RoutedEventArgs e) { SetSection("Журнал", "Журнал", "Системные события и диагностика подключения."); }
        private void MqttSettings_Click(object sender, RoutedEventArgs e) { SetSection("MQTT", "Подключение MQTT", "Параметры MQTT и состояние соединения."); }
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
