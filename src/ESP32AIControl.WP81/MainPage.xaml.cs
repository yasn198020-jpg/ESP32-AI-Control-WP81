using System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using ESP32AIControl.WP81.Core.Mqtt;
using ESP32AIControl.WP81.Core.Protocol;
using ESP32AIControl.WP81.Core.State;

namespace ESP32AIControl.WP81
{
    public sealed partial class MainPage : Page
    {
        private readonly MqttSettings _settings;
        private readonly DeviceRepository _devices;
        private readonly Esp32MqttService _mqtt;

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

                await _mqtt.PublishControlAsync(string.Empty, string.Empty, string.Empty);
                Log("TX control test");
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
        }

        private async void Mqtt_MessageReceived(object sender, Esp32Message e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                Log("RX [" + e.Kind + "] " + e.Topic + " = " + e.Value);
            });
        }

        private async void Mqtt_WidgetChanged(object sender, Models.WidgetState e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                Log("WIDGET " + e.Id + " = " + e.Value);
            });
        }

        private async void Mqtt_ConnectionStateChanged(object sender, MqttConnectionStateChangedEventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                ConnectionText.Text = "MQTT: " + e.State;
            });
        }

        private void Log(string text)
        {
            LogText.Text = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text +
                           Environment.NewLine + LogText.Text;
        }
    }
}
