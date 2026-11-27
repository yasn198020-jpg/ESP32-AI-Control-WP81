using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace ESP32AIControl.UWP
{
    public sealed partial class MainPage : Page
    {
        private bool _menuOpen;

        public MainPage()
        {
            InitializeComponent();
            SetSection("Главная", "ESP32-AI-Control",
                "Интерфейсный каркас Windows 10 Mobile. Содержимое Android-экранов будет перенесено следующим этапом.");
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
            StatusText.Text = "Каркас раздела создан. Реальная функциональность будет перенесена из Android 5.1.1.";
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

        private void Home_Click(object sender, RoutedEventArgs e)
        {
            SetSection("Главная", "Рабочий стол", "Здесь появятся страницы ESP32 и настраиваемые виджеты Android.");
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
        private async void InfoButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new ContentDialog
            {
                Title = "ESP32-AI-Control",
                Content = "Версия Windows 10 Mobile. Сейчас реализован только адаптивный интерфейсный каркас.",
                CloseButtonText = "Закрыть"
            };
            await dialog.ShowAsync();
        }
    }
}
