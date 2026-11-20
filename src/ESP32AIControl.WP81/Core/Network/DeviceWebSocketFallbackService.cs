using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace ESP32AIControl.WP81.Core.Network
{
    public sealed class WebSocketWidgetUpdateEventArgs : EventArgs
    {
        public string DeviceId { get; set; }
        public string WidgetId { get; set; }
        public string Value { get; set; }
    }

    /// <summary>
    /// Local WebSocket transport used only while the MQTT broker is unavailable.
    /// Device IP addresses are supplied by the UDP/4210 discovery listener.
    /// </summary>
    public sealed class DeviceWebSocketFallbackService : IDisposable
    {
        private sealed class Session
        {
            public string DeviceId;
            public string Ip;
            public MessageWebSocket Socket;
            public DataWriter Writer;
            public TaskCompletionSource<bool> Closed;
            public CancellationTokenSource Cancellation;
            public Task Worker;
            public long LastPongAt;
            public TaskCompletionSource<bool> Ready = new TaskCompletionSource<bool>();
            public readonly SemaphoreSlim WriteGate = new SemaphoreSlim(1, 1);
        }

        private const string Port = "81";
        private const string FrameSeparator = "|0012|";
        private readonly object _sync = new object();
        private readonly Dictionary<string, UdpDiscoveredDevice> _devices =
            new Dictionary<string, UdpDiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Session> _sessions =
            new Dictionary<string, Session>(StringComparer.OrdinalIgnoreCase);
        private bool _mqttOnline = true;
        private bool _disposed;

        public event EventHandler<string> Diagnostic;
        public event EventHandler<WebSocketWidgetUpdateEventArgs> WidgetUpdated;

        public void UpdateDevice(UdpDiscoveredDevice device)
        {
            if (device == null || string.IsNullOrWhiteSpace(device.Id) || string.IsNullOrWhiteSpace(device.Ip))
                return;

            Session restart = null;
            lock (_sync)
            {
                _devices[device.Id] = device;
                if (!_mqttOnline && _sessions.TryGetValue(device.Id, out restart) &&
                    !string.Equals(restart.Ip, device.Ip, StringComparison.OrdinalIgnoreCase))
                {
                    _sessions.Remove(device.Id);
                }
                else
                {
                    restart = null;
                }
            }

            if (restart != null)
                StopSession(restart);
            if (!_mqttOnline)
                EnsureSession(device.Id);
        }

        public void SetMqttOnline(bool online)
        {
            List<Session> stop = null;
            List<string> start = null;
            lock (_sync)
            {
                if (_disposed || _mqttOnline == online)
                    return;

                _mqttOnline = online;
                if (online)
                {
                    stop = new List<Session>(_sessions.Values);
                    _sessions.Clear();
                }
                else
                {
                    start = new List<string>(_devices.Keys);
                }
            }

            if (stop != null)
            {
                for (var i = 0; i < stop.Count; i++)
                    StopSession(stop[i]);
                RaiseDiagnostic("WebSocket fallback остановлен: MQTT снова доступен");
            }
            if (start != null)
            {
                RaiseDiagnostic("MQTT недоступен: запускаю WebSocket fallback");
                for (var i = 0; i < start.Count; i++)
                    EnsureSession(start[i]);
            }
        }

        public async Task<bool> SendControlAsync(string deviceId, string widgetId, string value)
        {
            Session session;
            lock (_sync)
            {
                if (_mqttOnline || !_sessions.TryGetValue(deviceId ?? string.Empty, out session))
                    return false;
            }

            if (session.Writer == null || session.Socket == null)
            {
                var ready = session.Ready.Task;
                var completed = await Task.WhenAny(ready, Task.Delay(8000));
                if (completed != ready || !await ready)
                    return false;
            }

            try
            {
                await session.WriteGate.WaitAsync();
                try
                {
                    if (session.Writer == null || session.Socket == null)
                        return false;
                    session.Writer.WriteString("/control|" + (widgetId ?? string.Empty) + "/" + (value ?? string.Empty));
                    await session.Writer.StoreAsync();
                    await session.Writer.FlushAsync();
                }
                finally
                {
                    session.WriteGate.Release();
                }
                RaiseDiagnostic("WS TX control device=" + deviceId + " widget=" + widgetId + " value=" + value);
                return true;
            }
            catch (Exception ex)
            {
                RaiseDiagnostic("WS TX error device=" + deviceId + ": " + ex.Message);
                return false;
            }
        }

        private void EnsureSession(string deviceId)
        {
            Session session;
            lock (_sync)
            {
                if (_disposed || _mqttOnline || _sessions.ContainsKey(deviceId))
                    return;
                UdpDiscoveredDevice device;
                if (!_devices.TryGetValue(deviceId, out device))
                    return;

                session = new Session
                {
                    DeviceId = deviceId,
                    Ip = device.Ip,
                    Cancellation = new CancellationTokenSource()
                };
                _sessions[deviceId] = session;
                session.Worker = RunSessionAsync(session);
            }
        }

        private async Task RunSessionAsync(Session session)
        {
            var token = session.Cancellation.Token;
            while (!token.IsCancellationRequested)
            {
                MessageWebSocket socket = null;
                session.Ready = new TaskCompletionSource<bool>();
                try
                {
                    socket = new MessageWebSocket();
                    socket.Control.MessageType = SocketMessageType.Utf8;
                    session.Closed = new TaskCompletionSource<bool>();
                    socket.MessageReceived += (sender, args) => HandleMessage(session, args);
                    socket.Closed += (sender, args) =>
                    {
                        var closed = session.Closed;
                        if (closed != null) closed.TrySetResult(true);
                    };

                    RaiseDiagnostic("WS CONNECTING device=" + session.DeviceId + " ip=" + session.Ip + ":" + Port);
                    var connectTask = socket.ConnectAsync(
                        new Uri("ws://" + session.Ip + ":" + Port + "/")).AsTask();
                    var completed = await Task.WhenAny(connectTask, Task.Delay(6000, token));
                    if (completed != connectTask)
                        throw new TimeoutException("WebSocket connect timeout");
                    await connectTask;

                    session.Socket = socket;
                    session.Writer = new DataWriter(socket.OutputStream);
                    session.Ready.TrySetResult(true);
                    await SendFrameAsync(session, "/params|");
                    session.LastPongAt = UtcNowMilliseconds();
                    RaiseDiagnostic("WS CONNECTED device=" + session.DeviceId + " ip=" + session.Ip);

                    while (!token.IsCancellationRequested)
                    {
                        var tick = await Task.WhenAny(session.Closed.Task, Task.Delay(2000, token));
                        if (tick == session.Closed.Task)
                            break;

                        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - session.LastPongAt > 6000)
                        {
                            RaiseDiagnostic("WS heartbeat timeout device=" + session.DeviceId);
                            try { socket.Close(); } catch { }
                            break;
                        }

                        await SendFrameAsync(session, "/pi|");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                        RaiseDiagnostic("WS error device=" + session.DeviceId + ": " + ex.Message);
                }
                finally
                {
                    session.Writer = null;
                    session.Socket = null;
                    if (socket != null)
                    {
                        try { socket.Close(); } catch { }
                        socket.Dispose();
                    }
                }

                if (!token.IsCancellationRequested)
                {
                    try { await Task.Delay(5000, token); }
                    catch (OperationCanceledException) { break; }
                }
            }

            RaiseDiagnostic("WS session stopped device=" + session.DeviceId);
        }

        private async Task SendFrameAsync(Session session, string frame)
        {
            await session.WriteGate.WaitAsync();
            try
            {
                if (session.Writer == null)
                    throw new InvalidOperationException("WebSocket output is not ready");
                session.Writer.WriteString(frame);
                await session.Writer.StoreAsync();
                await session.Writer.FlushAsync();
            }
            finally
            {
                session.WriteGate.Release();
            }
        }

        private void HandleMessage(Session session, MessageWebSocketMessageReceivedEventArgs args)
        {
            try
            {
                using (var reader = args.GetDataReader())
                {
                    reader.UnicodeEncoding = UnicodeEncoding.Utf8;
                    var frame = reader.ReadString(reader.UnconsumedBufferLength);
                    if (frame == "/po|")
                    {
                        session.LastPongAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        return;
                    }

                    var separator = frame.IndexOf(FrameSeparator, StringComparison.Ordinal);
                    if (separator < 0 || separator < 6)
                        return;

                    var header = frame.Substring(0, 6);
                    var payload = frame.Substring(separator + FrameSeparator.Length);
                    if (string.Equals(header, "status", StringComparison.Ordinal))
                    {
                        var json = Windows.Data.Json.JsonObject.Parse(payload);
                        var topic = ReadJsonString(json, "topic").Trim('/');
                        if (topic.EndsWith("/status", StringComparison.OrdinalIgnoreCase))
                            topic = topic.Substring(0, topic.Length - 7).Trim('/');
                        var widgetId = topic.SubstringAfterLast('/');
                        var value = ReadJsonString(json, "status");
                        RaiseWidgetUpdated(session.DeviceId, widgetId, value);
                    }
                    else if (string.Equals(header, "params", StringComparison.Ordinal))
                    {
                        var json = Windows.Data.Json.JsonObject.Parse(payload);
                        foreach (var pair in json)
                            RaiseWidgetUpdated(session.DeviceId, pair.Key, JsonValueToString(pair.Value));
                    }
                }
            }
            catch (Exception ex)
            {
                RaiseDiagnostic("WS frame parse error device=" + session.DeviceId + ": " + ex.Message);
            }
        }

        private static long UtcNowMilliseconds()
        {
            return DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
        }

        private static string ReadJsonString(Windows.Data.Json.JsonObject json, string key)
        {
            Windows.Data.Json.IJsonValue value;
            if (!json.TryGetValue(key, out value) || value == null)
                return string.Empty;
            return JsonValueToString(value);
        }

        private static string JsonValueToString(Windows.Data.Json.IJsonValue value)
        {
            if (value == null) return string.Empty;
            switch (value.ValueType)
            {
                case Windows.Data.Json.JsonValueType.String: return value.GetString();
                case Windows.Data.Json.JsonValueType.Number:
                    return value.GetNumber().ToString(System.Globalization.CultureInfo.InvariantCulture);
                case Windows.Data.Json.JsonValueType.Boolean: return value.GetBoolean() ? "1" : "0";
                case Windows.Data.Json.JsonValueType.Null: return string.Empty;
                default: return value.Stringify();
            }
        }

        private void RaiseWidgetUpdated(string deviceId, string widgetId, string value)
        {
            if (string.IsNullOrWhiteSpace(widgetId))
                return;
            var handler = WidgetUpdated;
            if (handler != null)
                handler(this, new WebSocketWidgetUpdateEventArgs
                {
                    DeviceId = deviceId,
                    WidgetId = widgetId,
                    Value = value
                });
        }

        private void StopSession(Session session)
        {
            if (session == null) return;
            try { session.Cancellation.Cancel(); } catch { }
            try { if (session.Socket != null) session.Socket.Close(); } catch { }
        }

        private void RaiseDiagnostic(string message)
        {
            var handler = Diagnostic;
            if (handler != null)
                handler(this, message);
        }

        public void Dispose()
        {
            List<Session> sessions;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                sessions = new List<Session>(_sessions.Values);
                _sessions.Clear();
            }
            for (var i = 0; i < sessions.Count; i++)
                StopSession(sessions[i]);
        }
    }

    internal static class WebSocketStringExtensions
    {
        public static string SubstringAfterLast(this string value, char separator)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var index = value.LastIndexOf(separator);
            return index < 0 ? value : value.Substring(index + 1);
        }
    }
}
