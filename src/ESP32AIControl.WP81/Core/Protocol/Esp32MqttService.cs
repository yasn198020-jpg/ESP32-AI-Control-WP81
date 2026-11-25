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

            try
            {
                await _mqtt.SubscribeAsync("/" + _settings.Prefix + "/#", 1);
                await PublishHelloAsync();
                await FlushPendingControlsAsync();
                await FlushPendingWidgetPublishesAsync();
            }
            catch
            {
                // A missing SUBACK or failed initial publish must not leave
                // the UI believing that an unusable MQTT session is healthy.
                await _mqtt.DisconnectAsync();
                throw;
            }
        }

        public Task PublishHelloAsync()
        {
            return _mqtt.PublishAsync("/" + _settings.Prefix, "HELLO", 1, false);
        }

        public async Task PublishWidgetAsync(string topic, string value)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("Widget topic is empty.", "topic");

            var payload = value ?? string.Empty;
            if (_mqtt.State != MqttConnectionState.Connected)
            {
                EnqueueWidgetPublish(topic, payload);
                return;
            }

            try
            {
                await _mqtt.PublishAsync(topic, payload, 1, false);
            }
            catch
            {
                EnqueueWidgetPublish(topic, payload);
                if (_mqtt.State == MqttConnectionState.Connected)
                    await _mqtt.DisconnectAsync();
            }
        }

        private void EnqueueWidgetPublish(string topic, string payload)
        {
            lock (_pendingWidgetLock)
            {
                var node = _pendingWidgetPublishes.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (string.Equals(node.Value.Topic, topic, StringComparison.Ordinal))
                        _pendingWidgetPublishes.Remove(node);
                    node = next;
                }

                _pendingWidgetPublishes.AddLast(new PendingWidgetPublish
                {
                    Topic = topic,
                    Payload = payload
                });
                while (_pendingWidgetPublishes.Count > MaxPendingWidgetPublishes)
                    _pendingWidgetPublishes.RemoveFirst();
            }
        }

        private async Task FlushPendingWidgetPublishesAsync()
        {
            while (_mqtt.State == MqttConnectionState.Connected)
            {
                PendingWidgetPublish item;
                lock (_pendingWidgetLock)
                {
                    if (_pendingWidgetPublishes.Count == 0)
                        return;
                    item = _pendingWidgetPublishes.First.Value;
                    _pendingWidgetPublishes.RemoveFirst();
                }

                try
                {
                    await _mqtt.PublishAsync(item.Topic, item.Payload, 1, false);
                }
                catch
                {
                    lock (_pendingWidgetLock)
                        _pendingWidgetPublishes.AddFirst(item);
                    return;
                }
            }
        }

        public Task DisconnectAsync()
        {
            return _mqtt.DisconnectAsync();
        }

        public void SetWebSocketFallbackService(DeviceWebSocketFallbackService service)
        {
            if (_webSocketFallback != null)
                _webSocketFallback.DeviceSessionConnected -= WebSocketFallback_DeviceSessionConnected;

            _webSocketFallback = service;
            if (_webSocketFallback != null)
            {
                _webSocketFallback.DeviceSessionConnected += WebSocketFallback_DeviceSessionConnected;
                _webSocketFallback.SetMqttOnline(_mqtt.State != MqttConnectionState.Disconnected);
            }
        }

        private async void WebSocketFallback_DeviceSessionConnected(object sender, string deviceId)
        {
            if (_mqtt.State == MqttConnectionState.Connected)
                return;

            await FlushPendingControlsViaWebSocketAsync(deviceId);
        }

        private sealed class PendingControl
        {
            public string Topic;
            public string Payload;
            public string DeviceId;
            public string WidgetId;
            public string Value;
        }

        private sealed class PendingWidgetPublish
        {
            public string Topic;
            public string Payload;
        }

        private readonly object _pendingWidgetLock = new object();
        private readonly System.Collections.Generic.LinkedList<PendingWidgetPublish> _pendingWidgetPublishes =
            new System.Collections.Generic.LinkedList<PendingWidgetPublish>();
        private const int MaxPendingWidgetPublishes = 100;

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
                {
                    RemovePendingControl(deviceId, widgetId);
                    return;
                }

                EnqueueControl(topic, payload, deviceId, widgetId, value);
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
                EnqueueControl(topic, payload, deviceId, widgetId, value);
                if (_mqtt.State == MqttConnectionState.Connected)
                    await _mqtt.DisconnectAsync();
            }
        }

        private void EnqueueControl(string topic, string payload, string deviceId, string widgetId, string value)
        {
            lock (_pendingLock)
            {
                // Keep only the newest requested value for a given control.
                var node = _pendingControls.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (string.Equals(node.Value.DeviceId, deviceId, StringComparison.Ordinal) &&
                        string.Equals(node.Value.WidgetId, widgetId, StringComparison.Ordinal))
                        _pendingControls.Remove(node);
                    node = next;
                }

                _pendingControls.AddLast(new PendingControl
                {
                    Topic = topic, Payload = payload, DeviceId = deviceId,
                    WidgetId = widgetId, Value = value
                });
                while (_pendingControls.Count > MaxPendingControls)
                    _pendingControls.RemoveFirst();
            }
        }

        private void RemovePendingControl(string deviceId, string widgetId)
        {
            lock (_pendingLock)
            {
                var node = _pendingControls.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (string.Equals(node.Value.DeviceId, deviceId, StringComparison.Ordinal) &&
                        string.Equals(node.Value.WidgetId, widgetId, StringComparison.Ordinal))
                        _pendingControls.Remove(node);
                    node = next;
                }
            }
        }

        private async Task FlushPendingControlsViaWebSocketAsync(string deviceId)
        {
            if (_webSocketFallback == null || _mqtt.State == MqttConnectionState.Connected)
                return;

            var pending = new System.Collections.Generic.List<PendingControl>();
            lock (_pendingLock)
            {
                for (var node = _pendingControls.First; node != null; node = node.Next)
                    if (string.Equals(node.Value.DeviceId, deviceId, StringComparison.Ordinal))
                        pending.Add(node.Value);
            }

            foreach (var item in pending)
            {
                if (_mqtt.State == MqttConnectionState.Connected)
                    return;
                if (await _webSocketFallback.SendControlAsync(item.DeviceId, item.WidgetId, item.Value))
                {
                    lock (_pendingLock)
                        _pendingControls.Remove(item);
                }
                else
                {
                    return;
                }
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

            message.Retained = e.Retained;
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
