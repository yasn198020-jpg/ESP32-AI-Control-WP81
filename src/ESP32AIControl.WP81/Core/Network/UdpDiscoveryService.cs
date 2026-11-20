using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using Windows.Storage;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using Windows.Data.Json;

namespace ESP32AIControl.WP81.Core.Network
{
    public sealed class UdpDiscoveredDevice
    {
        public string Wg { get; set; }
        public string Ip { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string Status { get; set; }
        public string FirmwareVersion { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
    }

    public sealed class UdpDiscoveryService : IDisposable
    {
        public const string Port = "4210";
        private const string SnapshotKey = "udp_discovered_devices_v1";
        private readonly object _sync = new object();
        private readonly ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;
        private DateTimeOffset _lastSnapshotPersistAt = DateTimeOffset.MinValue;
        private readonly Dictionary<string, UdpDiscoveredDevice> _devices =
            new Dictionary<string, UdpDiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        private DatagramSocket _socket;
        private bool _disposed;
        private int _generation;
        private CancellationTokenSource _recoveryCancellation;
        private int _recoveryAttempt;

        public event EventHandler<UdpDiscoveredDevice> DeviceDiscovered;
        public event EventHandler<UdpDiscoveredDevice> DeviceExpired;
        public event EventHandler<string> Diagnostic;

        public UdpDiscoveryService()
        {
            LoadSnapshot();
        }

        public IList<UdpDiscoveredDevice> GetSnapshot()
        {
            lock (_sync)
                return new List<UdpDiscoveredDevice>(_devices.Values);
        }

        public void PruneExpired()
        {
            var expired = new List<UdpDiscoveredDevice>();
            lock (_sync)
            {
                var keys = new List<string>();
                foreach (var pair in _devices)
                {
                    if ((DateTimeOffset.Now - pair.Value.LastSeenAt).TotalMilliseconds > 300000)
                    {
                        keys.Add(pair.Key);
                        expired.Add(pair.Value);
                    }
                }
                for (var i = 0; i < keys.Count; i++)
                    _devices.Remove(keys[i]);
            }

            if (expired.Count > 0)
                PersistSnapshot();

            var handler = DeviceExpired;
            if (handler != null)
                for (var i = 0; i < expired.Count; i++)
                    handler(this, expired[i]);
        }

        public async System.Threading.Tasks.Task StartAsync()
        {
            int generation;
            lock (_sync)
            {
                if (_disposed || _socket != null)
                    return;
                generation = ++_generation;
            }

            var socket = new DatagramSocket();
            socket.MessageReceived += Socket_MessageReceived;
            try
            {
                // ESP32 IoTManager broadcasts JSON announcements to UDP/4210.
                await socket.BindServiceNameAsync(Port);
                lock (_sync)
                {
                    if (_disposed || generation != _generation)
                    {
                        socket.Dispose();
                        return;
                    }
                    _socket = socket;
                    _recoveryAttempt = 0;
                }
                RaiseDiagnostic("UDP discovery слушает *:" + Port);
            }
            catch (Exception ex)
            {
                socket.MessageReceived -= Socket_MessageReceived;
                socket.Dispose();
                RaiseDiagnostic("UDP discovery: " + ex.Message);
                ScheduleRecovery(ex.Message);
                throw;
            }
        }

        public void Stop()
        {
            DatagramSocket socket;
            CancellationTokenSource recovery;
            lock (_sync)
            {
                ++_generation;
                socket = _socket;
                _socket = null;
                recovery = _recoveryCancellation;
                _recoveryCancellation = null;
                _recoveryAttempt = 0;
            }

            if (recovery != null)
            {
                try { recovery.Cancel(); } catch { }
            }
            if (socket != null)
            {
                socket.MessageReceived -= Socket_MessageReceived;
                socket.Dispose();
            }
            RaiseDiagnostic("UDP discovery остановлен");
        }

        private void ScheduleRecovery(string reason)
        {
            CancellationTokenSource cancellation;
            lock (_sync)
            {
                if (_disposed || _socket != null || _recoveryCancellation != null)
                    return;
                cancellation = new CancellationTokenSource();
                _recoveryCancellation = cancellation;
            }

            Task.Run(async delegate
            {
                while (!cancellation.IsCancellationRequested)
                {
                    int attempt;
                    lock (_sync)
                    {
                        if (_disposed || _socket != null)
                        {
                            if (_recoveryCancellation == cancellation)
                                _recoveryCancellation = null;
                            return;
                        }
                        _recoveryAttempt = Math.Min(6, _recoveryAttempt + 1);
                        attempt = _recoveryAttempt;
                    }

                    var delayMs = Math.Min(60000, 1000 * (1 << (attempt - 1)));
                    RaiseDiagnostic("UDP recovery: попытка " + attempt +
                        " через " + delayMs + " мс; причина: " + reason);
                    try
                    {
                        await Task.Delay(delayMs, cancellation.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    try
                    {
                        await StartAsync();
                        lock (_sync)
                        {
                            if (_recoveryCancellation == cancellation)
                                _recoveryCancellation = null;
                            _recoveryAttempt = 0;
                        }
                        return;
                    }
                    catch
                    {
                        // StartAsync leaves this recovery worker registered;
                        // continue with the next exponential backoff interval.
                    }
                }
            });
        }

        private void Socket_MessageReceived(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
        {
            try
            {
                using (var reader = args.GetDataReader())
                {
                    reader.UnicodeEncoding = UnicodeEncoding.Utf8;
                    var payload = reader.ReadString(reader.UnconsumedBufferLength);
                    ParseAnnouncements(payload, args.RemoteAddress == null ? string.Empty : args.RemoteAddress.DisplayName);
                }
            }
            catch (Exception ex)
            {
                RaiseDiagnostic("UDP packet parse error: " + ex.Message);
            }
        }

        private void ParseAnnouncements(string payload, string senderIp)
        {
            var text = (payload ?? string.Empty).Trim();
            if (text.Length == 0)
                return;

            try
            {
                if (text.StartsWith("[", StringComparison.Ordinal))
                {
                    var array = JsonArray.Parse(text);
                    for (uint i = 0; i < array.Count; i++)
                    {
                        if (array[i].ValueType == JsonValueType.Object)
                            AcceptObject(array[i].GetObject(), senderIp);
                    }
                }
                else
                {
                    AcceptObject(JsonObject.Parse(text), senderIp);
                }
            }
            catch (Exception ex)
            {
                RaiseDiagnostic("UDP invalid JSON: " + ex.Message);
            }
        }

        private void AcceptObject(JsonObject json, string senderIp)
        {
            var id = ReadString(json, "id");
            var wg = ReadString(json, "wg");
            if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(wg))
                return;

            var ip = ReadString(json, "ip");
            if (string.IsNullOrWhiteSpace(ip))
                ip = senderIp;
            if (string.IsNullOrWhiteSpace(ip))
                return;

            var device = new UdpDiscoveredDevice
            {
                Id = id,
                Wg = wg,
                Ip = ip,
                Name = ReadString(json, "name"),
                Status = ReadString(json, "status"),
                FirmwareVersion = ReadString(json, "fv"),
                LastSeenAt = DateTimeOffset.Now
            };

            lock (_sync)
                _devices[ip] = device;

            if (DateTimeOffset.Now - _lastSnapshotPersistAt > TimeSpan.FromSeconds(10))
                PersistSnapshot();

            var handler = DeviceDiscovered;
            if (handler != null)
                handler(this, device);
        }

        private void LoadSnapshot()
        {
            try
            {
                object raw;
                if (!_settings.Values.TryGetValue(SnapshotKey, out raw) || !(raw is string))
                    return;

                var array = JsonArray.Parse((string)raw);
                var now = DateTimeOffset.Now;
                lock (_sync)
                {
                    for (uint i = 0; i < array.Count; i++)
                    {
                        if (array[i].ValueType != JsonValueType.Object) continue;
                        var item = array[i].GetObject();
                        DateTimeOffset lastSeen;
                        if (!DateTimeOffset.TryParse(ReadString(item, "lastSeenAt"),
                            CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out lastSeen))
                            continue;
                        if ((now - lastSeen).TotalMilliseconds > 300000) continue;

                        var device = new UdpDiscoveredDevice
                        {
                            Wg = ReadString(item, "wg"),
                            Ip = ReadString(item, "ip"),
                            Id = ReadString(item, "id"),
                            Name = ReadString(item, "name"),
                            Status = ReadString(item, "status"),
                            FirmwareVersion = ReadString(item, "fv"),
                            LastSeenAt = lastSeen
                        };
                        if (!string.IsNullOrWhiteSpace(device.Ip) &&
                            (!string.IsNullOrWhiteSpace(device.Id) || !string.IsNullOrWhiteSpace(device.Wg)))
                            _devices[device.Ip] = device;
                    }
                }
                RaiseDiagnostic("UDP snapshot восстановлен: " + _devices.Count + " устройств");
            }
            catch (Exception ex)
            {
                RaiseDiagnostic("UDP snapshot read error: " + ex.Message);
            }
        }

        private void PersistSnapshot()
        {
            try
            {
                var array = new JsonArray();
                lock (_sync)
                {
                    foreach (var device in _devices.Values)
                    {
                        var item = new JsonObject();
                        item["wg"] = JsonValue.CreateStringValue(device.Wg ?? string.Empty);
                        item["ip"] = JsonValue.CreateStringValue(device.Ip ?? string.Empty);
                        item["id"] = JsonValue.CreateStringValue(device.Id ?? string.Empty);
                        item["name"] = JsonValue.CreateStringValue(device.Name ?? string.Empty);
                        item["status"] = JsonValue.CreateStringValue(device.Status ?? string.Empty);
                        item["fv"] = JsonValue.CreateStringValue(device.FirmwareVersion ?? string.Empty);
                        item["lastSeenAt"] = JsonValue.CreateStringValue(device.LastSeenAt.ToString("o", CultureInfo.InvariantCulture));
                        array.Add(item);
                    }
                }
                _settings.Values[SnapshotKey] = array.Stringify();
                _lastSnapshotPersistAt = DateTimeOffset.Now;
            }
            catch (Exception ex)
            {
                RaiseDiagnostic("UDP snapshot write error: " + ex.Message);
            }
        }

        private static string ReadString(JsonObject json, string key)
        {
            IJsonValue value;
            if (!json.TryGetValue(key, out value) || value == null)
                return string.Empty;

            try
            {
                if (value.ValueType == JsonValueType.String)
                    return value.GetString();
                if (value.ValueType == JsonValueType.Number)
                    return value.GetNumber().ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (value.ValueType == JsonValueType.Boolean)
                    return value.GetBoolean() ? "true" : "false";
            }
            catch { }
            return string.Empty;
        }

        private void RaiseDiagnostic(string message)
        {
            var handler = Diagnostic;
            if (handler != null)
                handler(this, message);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }
            Stop();
        }
    }
}
