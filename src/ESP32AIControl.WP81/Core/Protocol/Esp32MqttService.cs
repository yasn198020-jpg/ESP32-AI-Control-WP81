using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.System.Threading;
using ESP32AIControl.WP81.Core.Mqtt;
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
        private readonly SemaphoreSlim _connectionGate = new SemaphoreSlim(1, 1);
        private readonly object _reconnectSync = new object();
        private ThreadPoolTimer _reconnectTimer;
        private bool _reconnectEnabled;
        private int _reconnectDelaySeconds = 5;

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
            await _connectionGate.WaitAsync();
            try
            {
                if (_mqtt.State == MqttConnectionState.Connected)
                    return;

                lock (_reconnectSync)
                {
                    _reconnectEnabled = true;
                    CancelReconnectTimerLocked();
                }

                await ConnectCoreAsync();
                lock (_reconnectSync) _reconnectDelaySeconds = 5;
            }
            catch
            {
                // A failed first connection attempt must not silently disable
                // automatic recovery. Only an explicit DisconnectAsync turns
                // reconnect off.
                ScheduleReconnect();
                throw;
            }
            finally
            {
                _connectionGate.Release();
            }
        }

        private async Task ConnectCoreAsync()
        {
            _settings.Prefix = (_settings.Prefix ?? string.Empty).Trim('/');
            _protocol = new Esp32Protocol(_settings.Prefix);

            await _mqtt.ConnectAsync(
                _settings.Host,
                _settings.Port,
                _settings.ClientId,
                _settings.UserName,
                _settings.Password);

            try
            {
                await _mqtt.SubscribeAsync("/" + _settings.Prefix + "/#", 1);
                await PublishHelloAsync();
            }
            catch
            {
                try { await _mqtt.DisconnectAsync(); } catch { }
                throw;
            }
        }

        public Task PublishHelloAsync()
        {
            return _mqtt.PublishAsync("/" + _settings.Prefix, "HELLO", 1, false);
        }

        public async Task DisconnectAsync()
        {
            lock (_reconnectSync)
            {
                _reconnectEnabled = false;
                CancelReconnectTimerLocked();
            }

            await _connectionGate.WaitAsync();
            try
            {
                await _mqtt.DisconnectAsync();
            }
            finally
            {
                _connectionGate.Release();
            }
        }

        public Task PublishControlAsync(string deviceId, string widgetId, string value)
        {
            var topic = "/" + _settings.Prefix + "/" +
                        (deviceId ?? string.Empty).Trim('/') + "/" +
                        (widgetId ?? string.Empty).Trim('/') + "/control";
            var payload = "{\"status\":\"" + EscapeJson(value) + "\"}";

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

            if (e != null && e.State == MqttConnectionState.Disconnected)
                ScheduleReconnect();
        }

        private void ScheduleReconnect()
        {
            lock (_reconnectSync)
            {
                if (!_reconnectEnabled || _reconnectTimer != null)
                    return;

                var delay = _reconnectDelaySeconds;
                _reconnectDelaySeconds = Math.Min(60, Math.Max(5, delay * 2));
                _reconnectTimer = ThreadPoolTimer.CreateTimer(
                    ReconnectTimer_Tick,
                    TimeSpan.FromSeconds(delay));
            }
        }

        private async void ReconnectTimer_Tick(ThreadPoolTimer timer)
        {
            lock (_reconnectSync)
            {
                if (!object.ReferenceEquals(_reconnectTimer, timer))
                    return;
                _reconnectTimer = null;
                if (!_reconnectEnabled)
                    return;
            }

            var connected = false;
            try
            {
                await _connectionGate.WaitAsync();
                try
                {
                    lock (_reconnectSync)
                    {
                        if (!_reconnectEnabled)
                            return;
                    }

                    if (_mqtt.State != MqttConnectionState.Connected)
                        await ConnectCoreAsync();

                    connected = _mqtt.State == MqttConnectionState.Connected;
                }
                finally
                {
                    _connectionGate.Release();
                }
            }
            catch
            {
                // Connection state is already reflected in the UI. Retry below
                // with capped exponential backoff instead of surfacing a popup.
            }
            finally
            {
                if (connected)
                {
                    lock (_reconnectSync) _reconnectDelaySeconds = 5;
                }
                else
                {
                    ScheduleReconnect();
                }
            }
        }

        private void CancelReconnectTimerLocked()
        {
            if (_reconnectTimer == null) return;
            try { _reconnectTimer.Cancel(); } catch { }
            _reconnectTimer = null;
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
