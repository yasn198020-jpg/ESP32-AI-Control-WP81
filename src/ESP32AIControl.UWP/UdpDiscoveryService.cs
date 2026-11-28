using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Windows.Data.Json;
using Windows.Networking.Sockets;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ESP32AIControl.UWP
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

    // IoTManager sends metadata-only JSON announcements by limited broadcast to UDP/4210.
    // Sensor values and widget states continue to arrive through MQTT.
    public sealed class UdpDiscoveryService : IDisposable
    {
        public const string Port = "4210";
        private const string SnapshotKey = "udp_discovered_devices_v1";
        private const long DeviceTimeoutMs = 5 * 60 * 1000;
        private readonly object _sync = new object();
        private readonly Dictionary<string, UdpDiscoveredDevice> _devices =
            new Dictionary<string, UdpDiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        private DatagramSocket _socket;
        private bool _disposed;
        private DateTimeOffset _lastSnapshotPersistAt = DateTimeOffset.MinValue;

        public event Action<IList<UdpDiscoveredDevice>> DevicesChanged;
        public event Action<string> Diagnostic;
        public bool IsRunning { get { lock (_sync) return _socket != null; } }

        public UdpDiscoveryService()
        {
            LoadSnapshot();
        }

        public IList<UdpDiscoveredDevice> GetSnapshot()
        {
            lock (_sync) return SortedSnapshotLocked();
        }

        public async System.Threading.Tasks.Task StartAsync()
        {
            lock (_sync)
            {
                if (_disposed || _socket != null) return;
            }

            var socket = new DatagramSocket();
            socket.MessageReceived += Socket_MessageReceived;
            try
            {
                await socket.BindServiceNameAsync(Port);
                lock (_sync)
                {
                    if (_disposed || _socket != null)
                    {
                        socket.MessageReceived -= Socket_MessageReceived;
                        socket.Dispose();
                        return;
                    }
                    _socket = socket;
                }
                RaiseDiagnostic("UDP discovery слушает *:" + Port + "; ожидание broadcast");
            }
            catch (Exception ex)
            {
                socket.MessageReceived -= Socket_MessageReceived;
                socket.Dispose();
                RaiseDiagnostic("Ошибка запуска UDP discovery: " + ex.Message);
                throw;
            }
        }

        public void Stop()
        {
            DatagramSocket socket;
            lock (_sync) { socket = _socket; _socket = null; }
            if (socket != null)
            {
                socket.MessageReceived -= Socket_MessageReceived;
                socket.Dispose();
            }
            RaiseDiagnostic("UDP discovery остановлен");
        }

        public void Clear()
        {
            lock (_sync) _devices.Clear();
            SaveSnapshot(new List<UdpDiscoveredDevice>());
            var handler = DevicesChanged;
            if (handler != null) handler(new List<UdpDiscoveredDevice>());
            RaiseDiagnostic("Список обнаруженных UDP-устройств очищен");
        }

        public void PruneExpired()
        {
            bool changed = false;
            lock (_sync)
            {
                var now = DateTimeOffset.UtcNow;
                var expired = _devices.Where(x => (now - x.Value.LastSeenAt).TotalMilliseconds > DeviceTimeoutMs)
                    .Select(x => x.Key).ToList();
                foreach (var key in expired) { _devices.Remove(key); changed = true; }
            }
            if (changed) PublishSnapshot(true);
        }

        private void Socket_MessageReceived(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
        {
            try
            {
                using (var reader = args.GetDataReader())
                {
                    reader.UnicodeEncoding = UnicodeEncoding.Utf8;
                    var payload = reader.ReadString(reader.UnconsumedBufferLength);
                    ParseAnnouncements(payload, args.RemoteAddress == null ? "" : args.RemoteAddress.DisplayName);
                }
            }
            catch (Exception ex) { RaiseDiagnostic("Ошибка чтения UDP-пакета: " + ex.Message); }
        }

        private void ParseAnnouncements(string payload, string senderIp)
        {
            var text = (payload ?? "").Trim();
            if (text.Length == 0) return;
            try
            {
                if (text.StartsWith("[", StringComparison.Ordinal))
                {
                    var array = JsonArray.Parse(text);
                    for (uint i = 0; i < array.Count; i++)
                        if (array[(int)i].ValueType == JsonValueType.Object)
                            AcceptObject(array[(int)i].GetObject(), senderIp);
                }
                else AcceptObject(JsonObject.Parse(text), senderIp);
            }
            catch (Exception ex) { RaiseDiagnostic("Некорректный UDP JSON: " + ex.Message); }
        }

        private void AcceptObject(JsonObject json, string senderIp)
        {
            var id = ReadString(json, "id");
            var wg = ReadString(json, "wg");
            if (String.IsNullOrWhiteSpace(id) && String.IsNullOrWhiteSpace(wg)) return;
            var ip = ReadString(json, "ip");
            if (String.IsNullOrWhiteSpace(ip)) ip = senderIp;
            if (String.IsNullOrWhiteSpace(ip)) return;

            var device = new UdpDiscoveredDevice {
                Id = id, Wg = wg, Ip = ip, Name = ReadString(json, "name"),
                Status = ReadString(json, "status"), FirmwareVersion = ReadString(json, "fv"),
                LastSeenAt = DateTimeOffset.UtcNow
            };
            bool persist;
            lock (_sync)
            {
                _devices[ip] = device;
                persist = DateTimeOffset.UtcNow - _lastSnapshotPersistAt >= TimeSpan.FromSeconds(10);
                if (persist) _lastSnapshotPersistAt = DateTimeOffset.UtcNow;
            }
            PublishSnapshot(persist);
            RaiseDiagnostic("Найдено устройство: " + (String.IsNullOrWhiteSpace(device.Name) ? device.Id : device.Name) + " · " + ip);
        }

        private static string ReadString(JsonObject json, string key)
        {
            IJsonValue value;
            if (!json.TryGetValue(key, out value) || value == null) return "";
            try
            {
                if (value.ValueType == JsonValueType.String) return value.GetString();
                if (value.ValueType == JsonValueType.Number) return value.GetNumber().ToString(CultureInfo.InvariantCulture);
                if (value.ValueType == JsonValueType.Boolean) return value.GetBoolean() ? "true" : "false";
            }
            catch { }
            return "";
        }

        private IList<UdpDiscoveredDevice> SortedSnapshotLocked()
        {
            return _devices.Values.OrderBy(x => x.Name ?? "", StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(x => x.Ip, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void PublishSnapshot(bool persist)
        {
            IList<UdpDiscoveredDevice> snapshot;
            lock (_sync) snapshot = SortedSnapshotLocked();
            if (persist) SaveSnapshot(snapshot);
            var handler = DevicesChanged;
            if (handler != null) handler(snapshot);
        }

        private void LoadSnapshot()
        {
            try
            {
                object raw;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(SnapshotKey, out raw) || !(raw is string)) return;
                var array = JsonArray.Parse((string)raw);
                var now = DateTimeOffset.UtcNow;
                for (uint i = 0; i < array.Count; i++)
                {
                    if (array[(int)i].ValueType != JsonValueType.Object) continue;
                    var item = array[(int)i].GetObject();
                    var last = ReadString(item, "lastSeenAt");
                    long unixMs;
                    if (!Int64.TryParse(last, NumberStyles.Integer, CultureInfo.InvariantCulture, out unixMs)) continue;
                    var seen = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
                    if ((now - seen).TotalMilliseconds > DeviceTimeoutMs) continue;
                    var device = new UdpDiscoveredDevice {
                        Wg = ReadString(item, "wg"), Ip = ReadString(item, "ip"),
                        Id = ReadString(item, "id"), Name = ReadString(item, "name"),
                        Status = ReadString(item, "status"), FirmwareVersion = ReadString(item, "fv"),
                        LastSeenAt = seen
                    };
                    if (!String.IsNullOrWhiteSpace(device.Ip) &&
                        (!String.IsNullOrWhiteSpace(device.Id) || !String.IsNullOrWhiteSpace(device.Wg)))
                        _devices[device.Ip] = device;
                }
            }
            catch (Exception ex) { RaiseDiagnostic("Ошибка восстановления UDP-списка: " + ex.Message); }
        }

        private void SaveSnapshot(IList<UdpDiscoveredDevice> devices)
        {
            try
            {
                var array = new JsonArray();
                foreach (var d in devices)
                {
                    var item = new JsonObject();
                    item["wg"] = JsonValue.CreateStringValue(d.Wg ?? "");
                    item["ip"] = JsonValue.CreateStringValue(d.Ip ?? "");
                    item["id"] = JsonValue.CreateStringValue(d.Id ?? "");
                    item["name"] = JsonValue.CreateStringValue(d.Name ?? "");
                    item["status"] = JsonValue.CreateStringValue(d.Status ?? "");
                    item["fv"] = JsonValue.CreateStringValue(d.FirmwareVersion ?? "");
                    item["lastSeenAt"] = JsonValue.CreateStringValue(d.LastSeenAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
                    array.Add(item);
                }
                ApplicationData.Current.LocalSettings.Values[SnapshotKey] = array.Stringify();
            }
            catch (Exception ex) { RaiseDiagnostic("Ошибка сохранения UDP-списка: " + ex.Message); }
        }

        private void RaiseDiagnostic(string message)
        {
            var handler = Diagnostic;
            if (handler != null) handler(message);
        }

        public void Dispose()
        {
            lock (_sync) { if (_disposed) return; _disposed = true; }
            Stop();
        }
    }
}
