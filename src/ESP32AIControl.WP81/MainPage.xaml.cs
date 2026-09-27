using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using ESP32AIControl.WP81.Core.Mqtt;

namespace ESP32AIControl.WP81
{
    public sealed partial class MainPage : Page
    {
        private readonly MqttClient _mqtt;

        public MainPage()
        {
            this.InitializeComponent();

            _mqtt = new MqttClient();
            _mqtt.MessageReceived += Mqtt_MessageReceived;
            _mqtt.ConnectionStateChanged += Mqtt_ConnectionStateChanged;
        }

        private async void HelloButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await _mqtt.PublishAsync("/dghjko", "HELLO", 1, false);
            }
            catch (Exception ex)
            {
                LogText.Text = ex.Message + Environment.NewLine + LogText.Text;
            }
        }

        private void Mqtt_MessageReceived(object sender, MqttMessageEventArgs e)
        {
            var line = "[" + e.Topic + "] " + e.Payload;
            LogText.Text = line + Environment.NewLine + LogText.Text;
        }

        private void Mqtt_ConnectionStateChanged(object sender, MqttConnectionStateChangedEventArgs e)
        {
            ConnectionText.Text = "MQTT: " + e.State;
        }
    }
}
