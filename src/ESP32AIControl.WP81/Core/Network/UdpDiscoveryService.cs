using System;
using System.Collections.Generic;
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
        private readonly object _sync = new object();
        private readonly Dictionary<string, UdpDiscoveredDevice> _devices =
            new Dictionary<string, UdpDiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        private DatagramSocket _socket;
        private bool _disposed;
        private int _generation;

        public event EventHandler<UdpDiscoveredDevice> DeviceDiscovered;
        public event EventHandler<string> Diagnostic;

        public IList<UdpDiscoveredDevice> GetSnapshot()
        {
            lock (_sync)
                return new List<UdpDiscoveredDevice>(_devices.Values);
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
                }
                RaiseDiagnostic("UDP discovery слушает *:" + Port);
            }
            catch (Exception ex)
            {
                socket.MessageReceived -= Socket_MessageReceived;
                socket.Dispose();
                RaiseDiagnostic("UDP discovery: " + ex.Message);
                throw;
            }
        }

        public void Stop()
        {
            DatagramSocket socket;
            lock (_sync)
            {
                ++_generation;
                socket = _socket;
                _socket = null;
            }

            if (socket != null)
            {
                socket.MessageReceived -= Socket_MessageReceived;
                socket.Dispose();
            }
            RaiseDiagnostic("UDP discovery остановлен");
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

            var handler = DeviceDiscovered;
            if (handler != null)
                handler(this, device);
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
