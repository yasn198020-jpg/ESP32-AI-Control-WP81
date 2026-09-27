using System;
using System.Threading.Tasks;
using ESP32AIControl.WP81.Core.Mqtt;
using ESP32AIControl.WP81.Core.State;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core.Protocol
{
    public sealed class Esp32MqttService
    {
        private readonly MqttClient _mqtt;
        private readonly Esp32Protocol _protocol;
        private readonly DeviceRepository _devices;
        private readonly MqttSettings _settings;

        public event EventHandler<Esp32Message> MessageReceived;
        public event EventHandler<WidgetState> WidgetChanged;
        public event EventHandler<MqttConnectionStateChangedEventArgs> ConnectionStateChanged;

        public MqttConnectionState State
        {
            get { return _mqtt.State; }
        }

        public Esp32MqttService(MqttSettings settings, DeviceRepository devices)
        {
            _settings = settings;
            _devices = devices;
            _mqtt = new MqttClient();
            _protocol = new Esp32Protocol(settings.Prefix);

            _mqtt.MessageReceived += Mqtt_MessageReceived;
            _mqtt.ConnectionStateChanged += Mqtt_ConnectionStateChanged;
            _devices.WidgetChanged += Devices_WidgetChanged;
        }

        public async Task ConnectAsync()
        {
            await _mqtt.ConnectAsync(
                _settings.Host,
                _settings.Port,
                _settings.ClientId,
                _settings.UserName,
                _settings.Password);

            await _mqtt.SubscribeAsync("/" + _settings.Prefix.Trim('/') + "/#", 1);
            await _mqtt.PublishAsync("/" + _settings.Prefix.Trim('/'), "HELLO", 1, false);
        }

        public Task DisconnectAsync()
        {
            return _mqtt.DisconnectAsync();
        }

        public Task PublishControlAsync(string deviceId, string widgetId, string value)
        {
            var topic = "/" + _settings.Prefix.Trim('/') + "/" +
                        (deviceId ?? string.Empty).Trim('/') + "/" +
                        (widgetId ?? string.Empty).Trim('/') + "/control";
            var payload = "{\"status\":\"" +
                          EscapeJson(value) +
                          "\"}";

            return _mqtt.PublishAsync(topic, payload, 1, false);
        }

        private void Mqtt_MessageReceived(object sender, MqttMessageEventArgs e)
        {
            if (!_protocol.IsForThisApplication(e.Topic))
                return;

            Esp32Message message;
            try
            {
                message = _protocol.Parse(e.Topic, e.Payload);
            }
            catch
            {
                return;
            }

            _devices.Apply(message);

            var handler = MessageReceived;
            if (handler != null)
                handler(this, message);
        }

        private void Devices_WidgetChanged(object sender, WidgetState e)
        {
            var handler = WidgetChanged;
            if (handler != null)
                handler(this, e);
        }

        private void Mqtt_ConnectionStateChanged(object sender, MqttConnectionStateChangedEventArgs e)
        {
            var handler = ConnectionStateChanged;
            if (handler != null)
                handler(this, e);
        }

        private static string EscapeJson(string value)
        {
            return (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }
    }
}
