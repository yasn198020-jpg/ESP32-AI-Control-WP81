using System;
using System.Threading.Tasks;
using ESP32AIControl.WP81.Core.Mqtt;
using ESP32AIControl.WP81.Core.Network;
using ESP32AIControl.WP81.Core.State;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core.Protocol
{
    public sealed class Esp32MqttService
    {
        private readonly MqttClient _mqtt;
        private Esp32Protocol _protocol;
        private readonly DeviceRepository _devices;
        private readonly MqttSettings _settings;
        private DeviceWebSocketFallbackService _webSocketFallback;

        public event EventHandler<Esp32Message> MessageReceived;
        public event EventHandler<WidgetState> WidgetChanged;
        public event EventHandler<MqttConnectionStateChangedEventArgs> ConnectionStateChanged;

        public MqttConnectionState State
        {
            get { return _mqtt.State; }
        }

        public Esp32MqttService(MqttSettings settings, DeviceRepository devices)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            if (devices == null) throw new ArgumentNullException("devices");

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
            _settings.Prefix = (_settings.Prefix ?? string.Empty).Trim('/');
            _protocol = new Esp32Protocol(_settings.Prefix);

            await _mqtt.ConnectAsync(
                _settings.Host,
                _settings.Port,
                _settings.ClientId,
                _settings.UserName,
                _settings.Password,
                _settings.UseTls);

            await _mqtt.SubscribeAsync("/" + _settings.Prefix + "/#", 1);
            await PublishHelloAsync();
            await FlushPendingControlsAsync();
        }

        public Task PublishHelloAsync()
        {
            return _mqtt.PublishAsync("/" + _settings.Prefix, "HELLO", 1, false);
        }

        public Task DisconnectAsync()
        {
            return _mqtt.DisconnectAsync();
        }

        public void SetWebSocketFallbackService(DeviceWebSocketFallbackService service)
        {
            _webSocketFallback = service;
            if (_webSocketFallback != null)
                _webSocketFallback.SetMqttOnline(_mqtt.State != MqttConnectionState.Disconnected);
        }

        private sealed class PendingControl
        {
            public string Topic;
            public string Payload;
        }

        private readonly object _pendingLock = new object();
        private readonly System.Collections.Generic.LinkedList<PendingControl> _pendingControls =
            new System.Collections.Generic.LinkedList<PendingControl>();
        private const int MaxPendingControls = 100;
        private bool _flushingPendingControls;

        public async Task PublishControlAsync(string deviceId, string widgetId, string value)
        {
            var topic = "/" + _settings.Prefix + "/" +
                        (deviceId ?? string.Empty).Trim('/') + "/" +
                        (widgetId ?? string.Empty).Trim('/') + "/control";
            var payload = "{\"status\":\"" + EscapeJson(value) + "\"}";

            if (_mqtt.State != MqttConnectionState.Connected)
            {
                if (_webSocketFallback != null &&
                    await _webSocketFallback.SendControlAsync(deviceId, widgetId, value))
                    return;

                EnqueueControl(topic, payload);
                return;
            }

            try
            {
                await _mqtt.PublishAsync(topic, payload, 1, false);
            }
            catch
            {
                // A Wi-Fi transition can happen between the state check and
                // the socket write. Preserve the latest requested value and
                // send it after the next successful MQTT subscription.
                EnqueueControl(topic, payload);
            }
        }

        private void EnqueueControl(string topic, string payload)
        {
            lock (_pendingLock)
            {
                // Keep only the newest requested value for a given control.
                var node = _pendingControls.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (string.Equals(node.Value.Topic, topic, StringComparison.Ordinal))
                        _pendingControls.Remove(node);
                    node = next;
                }

                _pendingControls.AddLast(new PendingControl { Topic = topic, Payload = payload });
                while (_pendingControls.Count > MaxPendingControls)
                    _pendingControls.RemoveFirst();
            }
        }

        private async Task FlushPendingControlsAsync()
        {
            lock (_pendingLock)
            {
                if (_flushingPendingControls || _pendingControls.Count == 0)
                    return;
                _flushingPendingControls = true;
            }

            try
            {
                while (_mqtt.State == MqttConnectionState.Connected)
                {
                    PendingControl item;
                    lock (_pendingLock)
                    {
                        if (_pendingControls.Count == 0)
                            break;
                        item = _pendingControls.First.Value;
                        _pendingControls.RemoveFirst();
                    }

                    try
                    {
                        await _mqtt.PublishAsync(item.Topic, item.Payload, 1, false);
                    }
                    catch
                    {
                        lock (_pendingLock)
                            _pendingControls.AddFirst(item);
                        break;
                    }
                }
            }
            finally
            {
                lock (_pendingLock)
                    _flushingPendingControls = false;
            }
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
            if (_webSocketFallback != null)
            {
                if (e.State == MqttConnectionState.Connected)
                    _webSocketFallback.SetMqttOnline(true);
                else if (e.State == MqttConnectionState.Disconnected)
                    _webSocketFallback.SetMqttOnline(false);
            }

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
