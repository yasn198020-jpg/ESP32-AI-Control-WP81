using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace ESP32AIControl.UWP
{
    // Platform-neutral dashboard state. It deliberately contains no Lumia-specific sizing.
    public sealed class DeviceModel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool Online { get; set; }
        public ObservableCollection<WidgetModel> Widgets { get; private set; }

        public DeviceModel()
        {
            Id = "";
            Name = "";
            Widgets = new ObservableCollection<WidgetModel>();
        }
    }

    public sealed class WidgetModel
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Type { get; set; }
        public string DefinitionName { get; set; }
        public string Value { get; set; }
        public string Unit { get; set; }
        public string Page { get; set; }
        public int Order { get; set; }
        public long LastUpdatedUnixMs { get; set; }
        public string ConfigJson { get; set; }
        public string CommandState { get; set; }
        public string ExpectedCommandValue { get; set; }

        public WidgetModel()
        {
            Id = ""; Title = ""; Type = "VALUE"; DefinitionName = "";
            Value = ""; Unit = ""; Page = ""; ConfigJson = "{}"; CommandState = ""; ExpectedCommandValue = "";
        }

        public string EffectiveDefinition
        {
            get
            {
                if (!String.IsNullOrWhiteSpace(DefinitionName)) return DefinitionName.Trim().ToLowerInvariant();
                switch ((Type ?? "").Trim().ToUpperInvariant())
                {
                    case "INPUT": return "input";
                    case "BUTTON": return "button";
                    case "TOGGLE": return "toggle";
                    case "STATUS": return "status";
                    default: return "anydata";
                }
            }
        }

        public IDictionary<string, object> ReadConfig()
        {
            try { return JsonConvert.DeserializeObject<Dictionary<string, object>>(ConfigJson ?? "{}") ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }

        public double ConfigNumber(string key, double fallback)
        {
            object raw;
            double value;
            if (ReadConfig().TryGetValue(key, out raw) &&
                Double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                return value;
            return fallback;
        }
    }

    public sealed class DashboardSnapshot
    {
        public ObservableCollection<DeviceModel> Devices { get; private set; }
        public DashboardSnapshot() { Devices = new ObservableCollection<DeviceModel>(); }
    }
}


namespace ESP32AIControl.UWP
{
    // MQTT 3.1.1 transport used by the UWP application. The implementation is
    // deliberately independent of the UI so the same runtime serves phone and PC.
    public sealed class MqttConnectionSettings
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public string Prefix { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public bool UseTls { get; set; }

        public MqttConnectionSettings()
        {
            Host = ""; Port = 1883; Prefix = "IoTManager";
            Username = ""; Password = ""; UseTls = false;
        }
    }

    public sealed class MqttDeviceService : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly object _stateLock = new object();
        private StreamSocket _socket;
        private DataReader _reader;
        private DataWriter _writer;
        private CancellationTokenSource _lifetime;
        private MqttConnectionSettings _settings;
        private readonly Dictionary<string, DeviceModel> _devices =
            new Dictionary<string, DeviceModel>(StringComparer.OrdinalIgnoreCase);
        private sealed class PendingCommand
        {
            public string Id;
            public string ExpectedStatus;
        }
        private readonly Dictionary<string, PendingCommand> _pendingCommands =
            new Dictionary<string, PendingCommand>(StringComparer.OrdinalIgnoreCase);
        private ushort _packetId;
        private Task _runTask = Task.CompletedTask;
        private bool _connected;
        private bool _disposed;

        public event Action<bool> ConnectionChanged;
        public event Action<string> Log;
        public event Action<IList<DeviceModel>> DevicesChanged;

        public bool IsConnected { get { lock (_stateLock) return _connected; } }

        public async Task StartAsync(MqttConnectionSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            if (String.IsNullOrWhiteSpace(settings.Host)) throw new ArgumentException("Не указан адрес MQTT-брокера.");
            if (String.IsNullOrWhiteSpace(settings.Prefix)) throw new ArgumentException("Не указан MQTT prefix.");
            if (settings.Port < 1 || settings.Port > 65535) throw new ArgumentOutOfRangeException("Port");

            var previousRun = _runTask;
            Stop();
            try { await previousRun; } catch { }
            string cleanHost = settings.Host.Trim();
            bool tls = settings.UseTls;
            if (cleanHost.StartsWith("mqtts://", StringComparison.OrdinalIgnoreCase)) { cleanHost = cleanHost.Substring(8); tls = true; }
            else if (cleanHost.StartsWith("mqtt://", StringComparison.OrdinalIgnoreCase)) cleanHost = cleanHost.Substring(7);
            else if (cleanHost.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase)) cleanHost = cleanHost.Substring(6);
            else if (cleanHost.StartsWith("ssl://", StringComparison.OrdinalIgnoreCase)) { cleanHost = cleanHost.Substring(6); tls = true; }
            _settings = new MqttConnectionSettings {
                Host = cleanHost.TrimEnd('/'), Port = settings.Port,
                Prefix = settings.Prefix.Trim().Trim('/'),
                Username = settings.Username ?? "", Password = settings.Password ?? "",
                UseTls = tls
            };
            _lifetime = new CancellationTokenSource();
            var token = _lifetime.Token;
            _runTask = Task.Run(() => ConnectionLoopAsync(token));
        }

        public void Stop()
        {
            if (_lifetime != null) { _lifetime.Cancel(); _lifetime.Dispose(); _lifetime = null; }
            CloseSocket();
            SetConnected(false);
        }

        public async Task<bool> SendControlAsync(string deviceId, string widgetId, string value)
        {
            if (!IsConnected || String.IsNullOrWhiteSpace(deviceId) || String.IsNullOrWhiteSpace(widgetId))
                return false;
            string key = deviceId + "/" + widgetId;
            string commandId = Guid.NewGuid().ToString("N");
            lock (_stateLock)
            {
                DeviceModel device;
                if (!_devices.TryGetValue(deviceId, out device)) return false;
                var widget = device.Widgets.FirstOrDefault(x => x.Id == widgetId);
                if (widget == null || widget.CommandState == "PENDING" || _pendingCommands.ContainsKey(key)) return false;
                widget.CommandState = "PENDING";
                widget.ExpectedCommandValue = value ?? "";
                _pendingCommands[key] = new PendingCommand { Id = commandId, ExpectedStatus = ExpectedStatusValue(widget, value) };
                PublishSnapshotLocked();
            }
            var payload = JsonConvert.SerializeObject(new { status = value ?? "" });
            bool sent = await PublishAsync("/" + _settings.Prefix + "/" + deviceId + "/" + widgetId + "/control", payload);
            if (!sent)
            {
                lock (_stateLock)
                {
                    PendingCommand pending;
                    if (_pendingCommands.TryGetValue(key, out pending) && pending.Id == commandId)
                    {
                        _pendingCommands.Remove(key);
                        var widget = _devices[deviceId].Widgets.FirstOrDefault(x => x.Id == widgetId);
                        if (widget != null) widget.CommandState = "SEND_FAILED";
                        PublishSnapshotLocked();
                    }
                }
                return false;
            }
            Task.Run(async () =>
            {
                await Task.Delay(10000);
                lock (_stateLock)
                {
                    PendingCommand pending;
                    if (_pendingCommands.TryGetValue(key, out pending) && pending.Id == commandId)
                    {
                        _pendingCommands.Remove(key);
                        var widget = _devices[deviceId].Widgets.FirstOrDefault(x => x.Id == widgetId);
                        if (widget != null) widget.CommandState = "TIMED_OUT";
                        PublishSnapshotLocked();
                    }
                }
            });
            return true;
        }

        private static string ExpectedStatusValue(WidgetModel widget, string requested)
        {
            decimal value;
            if (!Decimal.TryParse((requested ?? "").Trim().Replace(',', '.'), NumberStyles.Any,
                CultureInfo.InvariantCulture, out value)) return requested ?? "";
            var config = widget.ReadConfig();
            object raw;
            int digits = config.TryGetValue("round", out raw) ? Convert.ToInt32(raw, CultureInfo.InvariantCulture) : -1;
            Func<decimal, decimal> round = input => digits >= 0 && digits <= 6
                ? Decimal.Round(input, digits, MidpointRounding.AwayFromZero) : input;
            value = round(value);
            decimal multiply = config.TryGetValue("multiply", out raw) ? Convert.ToDecimal(raw, CultureInfo.InvariantCulture) : 1m;
            value = value * multiply;
            decimal plus = config.TryGetValue("plus", out raw) ? Convert.ToDecimal(raw, CultureInfo.InvariantCulture) : 0m;
            value += plus;
            string map = config.TryGetValue("map", out raw) ? Convert.ToString(raw, CultureInfo.InvariantCulture) : "";
            var mapValues = (map ?? "").Split(',').Select(x => { long n; return Int64.TryParse(x.Trim(), out n) ? (long?)n : null; }).Where(x => x.HasValue).Select(x => x.Value).ToArray();
            if (mapValues.Length == 4 && mapValues[0] != mapValues[1])
            {
                long x = (long)value;
                value = (x - mapValues[0]) * (mapValues[3] - mapValues[2]) / (mapValues[1] - mapValues[0]) + mapValues[2];
            }
            return round(value).ToString(CultureInfo.InvariantCulture);
        }

        private static bool ValuesMatch(string expected, string actual)
        {
            decimal left, right;
            if (Decimal.TryParse((expected ?? "").Trim().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out left) &&
                Decimal.TryParse((actual ?? "").Trim().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out right))
                return left == right;
            return String.Equals((expected ?? "").Trim(), (actual ?? "").Trim(), StringComparison.Ordinal);
        }

        public async Task<bool> SendHelloAsync()
        {
            if (!IsConnected) return false;
            return await PublishAsync("/" + _settings.Prefix, "HELLO");
        }

        private async Task ConnectionLoopAsync(CancellationToken token)
        {
            var delay = 2000;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await OpenConnectionAsync(token);
                    delay = 2000;
                    SetConnected(true);
                    WriteLog("MQTT: соединение установлено.");
                    await SendPacketAsync(0x82, BuildSubscribe("/" + _settings.Prefix + "/#"), token);
                    await PublishAsync("/" + _settings.Prefix, "HELLO");
                    using (var session = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        var keepAlive = KeepAliveLoopAsync(session.Token);
                        try { await ReadLoopAsync(session.Token); }
                        finally { session.Cancel(); try { await keepAlive; } catch { } }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { WriteLog("MQTT: " + ex.Message); }
                finally
                {
                    SetConnected(false);
                    CloseSocket();
                }
                if (!token.IsCancellationRequested)
                {
                    WriteLog("MQTT: повторное подключение через " + (delay / 1000) + " с.");
                    try { await Task.Delay(delay, token); } catch (OperationCanceledException) { break; }
                    delay = Math.Min(delay * 2, 30000);
                }
            }
        }

        private async Task OpenConnectionAsync(CancellationToken token)
        {
            CloseSocket();
            _socket = new StreamSocket();
            var protection = _settings.UseTls ? SocketProtectionLevel.Tls12 : SocketProtectionLevel.PlainSocket;
            await _socket.ConnectAsync(new HostName(_settings.Host), _settings.Port.ToString(), protection);
            _reader = new DataReader(_socket.InputStream) { InputStreamOptions = InputStreamOptions.None };
            _writer = new DataWriter(_socket.OutputStream) { UnicodeEncoding = UnicodeEncoding.Utf8 };
            var connect = BuildConnect();
            await SendPacketAsync(0x10, connect, token);
            var response = await ReadPacketAsync(token);
            if (response == null || response.Length < 4 || (response[0] >> 4) != 2 || response[3] != 0)
                throw new InvalidOperationException("Брокер отклонил MQTT CONNECT.");
        }

        private byte[] BuildConnect()
        {
            var body = new List<byte>();
            AddString(body, "MQTT"); body.Add(4);
            byte flags = 0x02;
            if (!String.IsNullOrEmpty(_settings.Username) || !String.IsNullOrEmpty(_settings.Password)) flags |= 0x80;
            if (!String.IsNullOrEmpty(_settings.Password)) flags |= 0x40;
            body.Add(flags); body.Add(0); body.Add(45);
            AddString(body, "ESP32AIControl-UWP-" + Guid.NewGuid().ToString("N").Substring(0, 12));
            if ((flags & 0x80) != 0) AddString(body, _settings.Username);
            if ((flags & 0x40) != 0) AddString(body, _settings.Password);
            return body.ToArray();
        }

        private static byte[] BuildSubscribe(string topic)
        {
            var body = new List<byte>();
            body.Add(0); body.Add(1); AddString(body, topic); body.Add(1);
            return body.ToArray();
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var packet = await ReadPacketAsync(token);
                if (packet == null || packet.Length == 0) throw new InvalidOperationException("MQTT broker closed connection.");
                int type = packet[0] >> 4;
                if (type == 3) await HandlePublishAsync(packet, token);
                else if (type == 13) { /* PINGRESP */ }
                else if (type == 14) throw new InvalidOperationException("MQTT broker sent DISCONNECT.");
            }
        }

        private async Task KeepAliveLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(25000, token);
                await SendPacketAsync(0xC0, new byte[0], token);
            }
        }

        private async Task<byte[]> ReadPacketAsync(CancellationToken token)
        {
            if (_reader == null) throw new InvalidOperationException("MQTT input stream is closed.");
            if (await _reader.LoadAsync(1) != 1) return null;
            byte header = _reader.ReadByte();
            int remaining = 0, multiplier = 1, encoded;
            var lengthBytes = new List<byte>();
            do
            {
                if (await _reader.LoadAsync(1) != 1) throw new InvalidOperationException("MQTT packet length truncated.");
                encoded = _reader.ReadByte();
                lengthBytes.Add((byte)encoded);
                remaining += (encoded & 127) * multiplier;
                multiplier *= 128;
                if (multiplier > 128 * 128 * 128 * 128) throw new InvalidOperationException("Invalid MQTT packet length.");
            } while ((encoded & 128) != 0);
            if (remaining > 1024 * 1024) throw new InvalidOperationException("MQTT packet exceeds 1 MB.");
            if (remaining > 0 && await _reader.LoadAsync((uint)remaining) < remaining)
                throw new InvalidOperationException("MQTT packet payload truncated.");
            var result = new byte[1 + lengthBytes.Count + remaining];
            result[0] = header;
            lengthBytes.CopyTo(result, 1);
            if (remaining > 0)
            {
                var body = new byte[remaining];
                _reader.ReadBytes(body);
                Buffer.BlockCopy(body, 0, result, 1 + lengthBytes.Count, remaining);
            }
            return result;
        }

        private async Task HandlePublishAsync(byte[] packet, CancellationToken token)
        {
            int offset = 1;
            int remaining = 0, multiplier = 1, digit;
            do { digit = packet[offset++]; remaining += (digit & 127) * multiplier; multiplier *= 128; } while ((digit & 128) != 0);
            int topicLength = (packet[offset++] << 8) | packet[offset++];
            string topic = Encoding.UTF8.GetString(packet, offset, topicLength); offset += topicLength;
            int qos = (packet[0] >> 1) & 3;
            ushort id = 0;
            if (qos > 0) { id = (ushort)((packet[offset++] << 8) | packet[offset++]); }
            string payload = Encoding.UTF8.GetString(packet, offset, packet.Length - offset);
            ProcessMessage(topic, payload);
            if (qos == 1) await SendPacketAsync(0x40, new byte[] { (byte)(id >> 8), (byte)id }, token);
        }

        private void ProcessMessage(string topic, string payload)
        {
            string root = "/" + _settings.Prefix;
            if (topic.TrimEnd('/') == root && String.Equals(payload.Trim(), "HELLO", StringComparison.OrdinalIgnoreCase))
                return;
            if (!topic.StartsWith(root + "/", StringComparison.Ordinal)) return;
            string[] parts = topic.Substring(root.Length).Trim('/').Split('/');
            if (parts.Length < 2) return;
            string deviceId = parts[0];
            lock (_stateLock)
            {
                DeviceModel device;
                if (!_devices.TryGetValue(deviceId, out device))
                {
                    device = new DeviceModel { Id = deviceId, Name = deviceId, Online = true };
                    _devices.Add(deviceId, device);
                }
                device.Online = true;
                string leaf = parts[parts.Length - 1];
                try
                {
                    JObject json = null;
                    if (payload.TrimStart().StartsWith("{")) json = JObject.Parse(payload);
                    if (leaf == "config" && parts.Length == 2 && json != null)
                    {
                        string configTopic = (string)json["topic"] ?? "";
                        string widgetId = configTopic.Trim('/').Split('/').LastOrDefault();
                        if (String.IsNullOrWhiteSpace(widgetId)) return;
                        var widget = device.Widgets.FirstOrDefault(x => x.Id == widgetId);
                        if (widget == null) { widget = new WidgetModel { Id = widgetId }; device.Widgets.Add(widget); }
                        string widgetType = (string)json["widget"] ?? "status";
                        string normalizedType = widgetType.Trim().ToLowerInvariant();
                        widget.Title = (string)json["descr"] ?? (string)json["label"] ?? (string)json["name"] ?? widgetId;
                        widget.Type = normalizedType == "toggle" ? "TOGGLE" :
                            (normalizedType == "button" || normalizedType == "vbtn" || normalizedType == "btn") ? "BUTTON" :
                            (normalizedType.StartsWith("input") || new[] { "text", "number", "slider", "range", "rangeservo", "select" }.Contains(normalizedType)) ? "INPUT" :
                            (normalizedType.StartsWith("anydata") || normalizedType.StartsWith("chart") ||
                             new[] { "value", "fillgauge", "progressline", "progress-line", "progressround", "progress-round" }.Contains(normalizedType)) ? "VALUE" : "STATUS";
                        widget.DefinitionName = ((string)json["name"] ?? widgetType).Trim();
                        if (widget.DefinitionName.Length == 0) widget.DefinitionName = widgetType;
                        widget.Page = (string)json["page"] ?? "";
                        widget.Order = (int?)json["order"] ?? 0;
                        widget.ConfigJson = json.ToString(Formatting.None);
                        widget.Unit = ((string)json["after"] ?? "").Trim();
                    }
                    else if ((leaf == "status" || leaf == "event") && parts.Length >= 3)
                    {
                        string widgetId = leaf == "event" && json != null
                            ? ((string)json["id"] ?? parts[parts.Length - 2]) : parts[parts.Length - 2];
                        string value = "";
                        JToken token = json == null ? null : json[leaf == "event" ? "val" : "status"];
                        if (token != null) value = token.Type == JTokenType.String ? (string)token : token.ToString(Formatting.None);
                        else value = payload;
                        var widget = device.Widgets.FirstOrDefault(x => x.Id == widgetId);
                        if (widget == null) { widget = new WidgetModel { Id = widgetId, Title = widgetId }; device.Widgets.Add(widget); }
                        widget.Value = value;
                        widget.LastUpdatedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (leaf == "status")
                        {
                            PendingCommand pending;
                            string pendingKey = deviceId + "/" + widgetId;
                            if (_pendingCommands.TryGetValue(pendingKey, out pending) && ValuesMatch(pending.ExpectedStatus, value))
                            {
                                _pendingCommands.Remove(pendingKey);
                                widget.CommandState = "CONFIRMED";
                            }
                        }
                    }
                    else if (leaf == "state" && parts.Length == 2)
                    {
                        string state = json == null ? payload.Trim().Trim('"') : ((string)json["status"] ?? payload);
                        device.Online = !String.Equals(state, "offline", StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch (Exception ex) { WriteLog("MQTT parse " + topic + ": " + ex.Message); }
                PublishSnapshotLocked();
            }
        }

        private async Task<bool> PublishAsync(string topic, string payload)
        {
            if (!IsConnected) return false;
            var body = new List<byte>(); AddString(body, topic);
            body.AddRange(Encoding.UTF8.GetBytes(payload ?? ""));
            try { await SendPacketAsync(0x30, body.ToArray(), _lifetime.Token); return true; }
            catch (Exception ex) { WriteLog("MQTT publish: " + ex.Message); return false; }
        }

        private async Task SendPacketAsync(byte header, byte[] body, CancellationToken token)
        {
            await _writeLock.WaitAsync(token);
            try
            {
                if (_writer == null) throw new InvalidOperationException("MQTT output stream is closed.");
                var packet = new List<byte> { header };
                int length = body == null ? 0 : body.Length;
                do { int digit = length % 128; length /= 128; if (length > 0) digit |= 128; packet.Add((byte)digit); } while (length > 0);
                if (body != null) packet.AddRange(body);
                _writer.WriteBytes(packet.ToArray());
                await _writer.StoreAsync();
                await _writer.FlushAsync();
            }
            finally { _writeLock.Release(); }
        }

        private static void AddString(List<byte> target, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
            target.Add((byte)(bytes.Length >> 8)); target.Add((byte)bytes.Length); target.AddRange(bytes);
        }

        private void SetConnected(bool value)
        {
            bool changed;
            lock (_stateLock)
            {
                changed = _connected != value;
                _connected = value;
                if (changed && !value)
                {
                    foreach (var device in _devices.Values) device.Online = false;
                    PublishSnapshotLocked();
                }
            }
            if (changed && ConnectionChanged != null) ConnectionChanged(value);
        }

        private void PublishSnapshotLocked()
        {
            if (DevicesChanged == null) return;
            var copy = _devices.Values.Select(d => new DeviceModel {
                Id = d.Id, Name = d.Name, Online = d.Online,
                Widgets = new ObservableCollection<WidgetModel>(d.Widgets.Select(w => new WidgetModel {
                    Id=w.Id, Title=w.Title, Type=w.Type, DefinitionName=w.DefinitionName, Value=w.Value,
                    Unit=w.Unit, Page=w.Page, Order=w.Order, LastUpdatedUnixMs=w.LastUpdatedUnixMs,
                    ConfigJson=w.ConfigJson, CommandState=w.CommandState, ExpectedCommandValue=w.ExpectedCommandValue
                }))
            }).ToList();
            DevicesChanged(copy);
        }

        private void WriteLog(string message) { if (Log != null) Log(message); }

        private void CloseSocket()
        {
            if (_reader != null) { _reader.Dispose(); _reader = null; }
            if (_writer != null) { _writer.Dispose(); _writer = null; }
            if (_socket != null) { _socket.Dispose(); _socket = null; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; Stop(); _writeLock.Dispose();
        }
    }
}


namespace ESP32AIControl.UWP
{
    // Persistent equivalent of Android TrainedCommandStore.
    public sealed class TrainedVoiceCommand
    {
        public string Phrase { get; set; }
        public string DeviceId { get; set; }
        public string WidgetId { get; set; }
        public string Value { get; set; }
        public List<string> Variants { get; set; }

        public TrainedVoiceCommand()
        {
            Phrase = ""; DeviceId = ""; WidgetId = ""; Value = "1";
            Variants = new List<string>();
        }
    }

    public sealed class TrainedVoiceCommandStore
    {
        private const string StorageKey = "trained_voice_commands";

        public IList<TrainedVoiceCommand> Load()
        {
            try
            {
                var raw = Windows.Storage.ApplicationData.Current.LocalSettings.Values[StorageKey] as string;
                if (String.IsNullOrWhiteSpace(raw)) return new List<TrainedVoiceCommand>();
                var array = JArray.Parse(raw);
                var result = new List<TrainedVoiceCommand>();
                foreach (var item in array.OfType<JObject>())
                {
                    var phrase = ((string)item["phrase"] ?? "").Trim();
                    if (phrase.Length == 0) continue;
                    var variants = item["variants"] is JArray
                        ? ((JArray)item["variants"]).Values<string>().Where(x => !String.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                        : new List<string>();
                    if (variants.Count == 0) variants.Add(phrase);
                    result.Add(new TrainedVoiceCommand {
                        Phrase = phrase, DeviceId = (string)item["deviceId"] ?? "",
                        WidgetId = (string)item["widgetId"] ?? "", Value = (string)item["value"] ?? "1",
                        Variants = variants
                    });
                }
                return result;
            }
            catch { return new List<TrainedVoiceCommand>(); }
        }

        public void Save(IEnumerable<TrainedVoiceCommand> commands)
        {
            var array = new JArray();
            foreach (var command in commands ?? Enumerable.Empty<TrainedVoiceCommand>())
                array.Add(new JObject {
                    ["phrase"] = command.Phrase ?? "",
                    ["deviceId"] = command.DeviceId ?? "",
                    ["widgetId"] = command.WidgetId ?? "",
                    ["value"] = command.Value ?? "",
                    ["variants"] = new JArray((command.Variants ?? new List<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
                });
            Windows.Storage.ApplicationData.Current.LocalSettings.Values[StorageKey] = array.ToString(Formatting.None);
        }

        public void Add(TrainedVoiceCommand command)
        {
            var all = Load().Where(x => !(String.Equals(x.Phrase, command.Phrase, StringComparison.OrdinalIgnoreCase)
                && x.DeviceId == command.DeviceId && x.WidgetId == command.WidgetId)).ToList();
            command.Variants = (command.Variants ?? new List<string>()).Concat(new[] { command.Phrase })
                .Where(x => !String.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            all.Add(command);
            Save(all);
        }

        public void Remove(TrainedVoiceCommand command)
        {
            Save(Load().Where(x => !(x.Phrase == command.Phrase && x.DeviceId == command.DeviceId
                && x.WidgetId == command.WidgetId && x.Value == command.Value)));
        }

        public void AddVariant(string phrase, string variant)
        {
            phrase = (phrase ?? "").Trim(); variant = (variant ?? "").Trim();
            if (phrase.Length == 0 || variant.Length == 0) return;
            var all = Load();
            foreach (var command in all.Where(x => String.Equals(x.Phrase, phrase, StringComparison.OrdinalIgnoreCase)))
                if (!command.Variants.Contains(variant, StringComparer.OrdinalIgnoreCase)) command.Variants.Add(variant);
            Save(all);
        }

        public void Clear() { Windows.Storage.ApplicationData.Current.LocalSettings.Values.Remove(StorageKey); }
    }
}


namespace ESP32AIControl.UWP
{
    public sealed class HistoryPoint
    {
        public long Timestamp { get; set; }
        public string DeviceId { get; set; }
        public string WidgetId { get; set; }
        public string RawValue { get; set; }
        public double Value { get; set; }
    }

    public sealed class HistoryGraphConfig
    {
        public string DeviceId { get; set; }
        public string WidgetId { get; set; }
        public string Mode { get; set; }
        public long IntervalMs { get; set; }
    }

    // File-backed telemetry history. MQTT callbacks only update the latest-value
    // cache; a sampler writes points at the configured interval, like Android.
    public sealed class HistoryStore : IDisposable
    {
        private const string FileName = "telemetry-history.jsonl";
        private readonly object _sync = new object();
        private readonly System.Collections.Generic.Dictionary<string, HistoryPoint> _latest =
            new System.Collections.Generic.Dictionary<string, HistoryPoint>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Generic.List<HistoryPoint> _cache =
            new System.Collections.Generic.List<HistoryPoint>();
        private readonly System.Collections.Generic.Dictionary<string, long> _configuredLastRecorded =
            new System.Collections.Generic.Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Generic.Dictionary<string, double> _configuredLastValue =
            new System.Collections.Generic.Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Generic.List<HistoryGraphConfig> _graphConfigs =
            new System.Collections.Generic.List<HistoryGraphConfig>();
        private readonly System.Threading.SemaphoreSlim _fileGate = new System.Threading.SemaphoreSlim(1, 1);
        private System.Threading.Timer _sampler;
        private Windows.Storage.StorageFile _file;
        private bool _loaded;
        private bool _disposed;
        private int _samplePeriodMs = 10000;
        private int _retentionDays = 30;
        private long _displayPeriodMs = 86400000;

        public HistoryStore()
        {
            try
            {
                var settings = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
                if (settings.ContainsKey("history_sample_period_ms"))
                    Int32.TryParse(Convert.ToString(settings["history_sample_period_ms"]), out _samplePeriodMs);
                if (settings.ContainsKey("history_retention_days"))
                    Int32.TryParse(Convert.ToString(settings["history_retention_days"]), out _retentionDays);
                if (settings.ContainsKey("history_display_period_ms"))
                    Int64.TryParse(Convert.ToString(settings["history_display_period_ms"]), out _displayPeriodMs);
            }
            catch { }
            _samplePeriodMs = NormalizeSamplePeriod(_samplePeriodMs);
            _retentionDays = NormalizeRetention(_retentionDays);
            _displayPeriodMs = NormalizeDisplayPeriod(_displayPeriodMs);
            try
            {
                var raw = Convert.ToString(Windows.Storage.ApplicationData.Current.LocalSettings.Values["history_graph_configs"]);
                if (!String.IsNullOrWhiteSpace(raw))
                    foreach (var item in JArray.Parse(raw).OfType<JObject>())
                        _graphConfigs.Add(new HistoryGraphConfig {
                            DeviceId = (string)item["d"] ?? "", WidgetId = (string)item["w"] ?? "",
                            Mode = (string)item["m"] ?? "change_interval", IntervalMs = (long?)item["i"] ?? 1800000L
                        });
            }
            catch { }
            _sampler = new System.Threading.Timer(SampleTick, null, _samplePeriodMs, _samplePeriodMs);
        }

        public int SamplePeriodMs { get { lock (_sync) return _samplePeriodMs; } }
        public int RetentionDays { get { lock (_sync) return _retentionDays; } }
        public long DisplayPeriodMs { get { lock (_sync) return _displayPeriodMs; } }

        public void UpdateLatest(string deviceId, string widgetId, string rawValue, long timestamp)
        {
            if (String.IsNullOrWhiteSpace(deviceId) || String.IsNullOrWhiteSpace(widgetId)) return;
            double number;
            string normalized = (rawValue ?? "").Trim().Replace(',', '.');
            if (!Double.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out number) ||
                Double.IsNaN(number) || Double.IsInfinity(number)) return;
            var point = new HistoryPoint {
                Timestamp = timestamp > 0 ? timestamp : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DeviceId = deviceId, WidgetId = widgetId, RawValue = rawValue ?? "", Value = number
            };
            bool recordChange = false;
            lock (_sync)
            {
                _latest[deviceId + "/" + widgetId] = point;
                var config = _graphConfigs.FirstOrDefault(x => x.DeviceId == deviceId && x.WidgetId == widgetId);
                if (config != null && (config.Mode == "change" || config.Mode == "change_interval"))
                {
                    double previous;
                    recordChange = !_configuredLastValue.TryGetValue(deviceId + "/" + widgetId, out previous) || previous != number;
                    if (recordChange) { _configuredLastValue[deviceId + "/" + widgetId] = number; _configuredLastRecorded[deviceId + "/" + widgetId] = point.Timestamp; }
                }
            }
            if (recordChange) { var ignored = AppendPointAsync(point); }
        }

        public IList<HistoryGraphConfig> GraphConfigs()
        {
            lock (_sync) return _graphConfigs.Select(x => new HistoryGraphConfig {
                DeviceId = x.DeviceId, WidgetId = x.WidgetId, Mode = x.Mode, IntervalMs = x.IntervalMs
            }).ToList();
        }

        public void AddGraphConfig(HistoryGraphConfig config)
        {
            if (config == null || String.IsNullOrWhiteSpace(config.DeviceId) || String.IsNullOrWhiteSpace(config.WidgetId)) return;
            lock (_sync)
            {
                _graphConfigs.RemoveAll(x => x.DeviceId == config.DeviceId && x.WidgetId == config.WidgetId);
                _graphConfigs.Add(config);
                _configuredLastRecorded.Remove(config.DeviceId + "/" + config.WidgetId);
                _configuredLastValue.Remove(config.DeviceId + "/" + config.WidgetId);
                var array = new JArray(_graphConfigs.Select(x => new JObject {
                    ["d"] = x.DeviceId, ["w"] = x.WidgetId, ["m"] = x.Mode, ["i"] = x.IntervalMs
                }));
                Windows.Storage.ApplicationData.Current.LocalSettings.Values["history_graph_configs"] = array.ToString(Formatting.None);
            }
        }

        public void RemoveGraphConfig(string deviceId, string widgetId)
        {
            lock (_sync)
            {
                _graphConfigs.RemoveAll(x => x.DeviceId == deviceId && x.WidgetId == widgetId);
                _configuredLastRecorded.Remove(deviceId + "/" + widgetId);
                _configuredLastValue.Remove(deviceId + "/" + widgetId);
                var array = new JArray(_graphConfigs.Select(x => new JObject {
                    ["d"] = x.DeviceId, ["w"] = x.WidgetId, ["m"] = x.Mode, ["i"] = x.IntervalMs
                }));
                Windows.Storage.ApplicationData.Current.LocalSettings.Values["history_graph_configs"] = array.ToString(Formatting.None);
            }
        }

        public void SetSamplePeriod(int milliseconds)
        {
            int value = NormalizeSamplePeriod(milliseconds);
            lock (_sync) _samplePeriodMs = value;
            Windows.Storage.ApplicationData.Current.LocalSettings.Values["history_sample_period_ms"] = value;
            if (_sampler != null) _sampler.Change(value, value);
        }

        public void SetRetentionDays(int days)
        {
            int value = NormalizeRetention(days);
            lock (_sync) _retentionDays = value;
            Windows.Storage.ApplicationData.Current.LocalSettings.Values["history_retention_days"] = value;
            PruneCache();
        }

        public void SetDisplayPeriod(long milliseconds)
        {
            long value = NormalizeDisplayPeriod(milliseconds);
            lock (_sync) _displayPeriodMs = value;
            Windows.Storage.ApplicationData.Current.LocalSettings.Values["history_display_period_ms"] = value;
        }

        public long DisplaySince()
        {
            long period;
            lock (_sync) period = _displayPeriodMs;
            if (period == 0) return 0;
            if (period == 86400000)
            {
                DateTime now = DateTime.Now;
                return new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeOffset.Now.Offset).ToUnixTimeMilliseconds();
            }
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - period;
        }

        public async Task<IList<HistoryPoint>> LoadAsync(string deviceId, string widgetId, long since)
        {
            await EnsureLoadedAsync();
            lock (_sync)
            {
                return _cache.Where(p => p.DeviceId == deviceId && p.WidgetId == widgetId && (since <= 0 || p.Timestamp >= since))
                    .OrderBy(p => p.Timestamp).TakeLastCompat(5000).ToList();
            }
        }

        public async Task<int> CountAsync()
        {
            await EnsureLoadedAsync();
            lock (_sync) return _cache.Count;
        }

        public async Task ClearAsync()
        {
            await EnsureLoadedAsync();
            await _fileGate.WaitAsync();
            try
            {
                lock (_sync) { _cache.Clear(); _latest.Clear(); }
                if (_file != null) await Windows.Storage.FileIO.WriteTextAsync(_file, "");
            }
            finally { _fileGate.Release(); }
        }

        private async Task EnsureLoadedAsync()
        {
            if (_loaded) return;
            await _fileGate.WaitAsync();
            try
            {
                if (_loaded) return;
                var folder = await Windows.Storage.ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    "history", Windows.Storage.CreationCollisionOption.OpenIfExists);
                _file = await folder.CreateFileAsync(FileName, Windows.Storage.CreationCollisionOption.OpenIfExists);
                string text = await Windows.Storage.FileIO.ReadTextAsync(_file);
                long cutoff = RetentionCutoff();
                var loaded = new System.Collections.Generic.List<HistoryPoint>();
                foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    try
                    {
                        JObject json = JObject.Parse(line);
                        var point = new HistoryPoint {
                            Timestamp = (long?)json["t"] ?? 0,
                            DeviceId = (string)json["d"] ?? "",
                            WidgetId = (string)json["w"] ?? "",
                            RawValue = (string)json["r"] ?? "",
                            Value = (double?)json["v"] ?? 0
                        };
                        if (point.Timestamp > 0 && (cutoff == 0 || point.Timestamp >= cutoff))
                            loaded.Add(point);
                    }
                    catch { }
                }
                lock (_sync) { _cache.Clear(); _cache.AddRange(loaded.OrderBy(p => p.Timestamp)); }
                _loaded = true;
            }
            finally { _fileGate.Release(); }
        }

        private async void SampleTick(object state)
        {
            if (_disposed) return;
            try
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                System.Collections.Generic.List<HistoryPoint> points;
                lock (_sync)
                {
                    points = _latest.Values.Select(p => new HistoryPoint {
                        Timestamp = now, DeviceId = p.DeviceId, WidgetId = p.WidgetId,
                        RawValue = p.RawValue, Value = p.Value
                    }).ToList();
                    foreach (var config in _graphConfigs.Where(x => x.Mode == "interval" || x.Mode == "change" || x.Mode == "change_interval"))
                    {
                        HistoryPoint current;
                        if (!_latest.TryGetValue(config.DeviceId + "/" + config.WidgetId, out current)) continue;
                        long last;
                        if (!_configuredLastRecorded.TryGetValue(config.DeviceId + "/" + config.WidgetId, out last) || now - last >= config.IntervalMs)
                        {
                            points.Add(new HistoryPoint { Timestamp = now, DeviceId = current.DeviceId, WidgetId = current.WidgetId, RawValue = current.RawValue, Value = current.Value });
                            _configuredLastRecorded[config.DeviceId + "/" + config.WidgetId] = now;
                            _configuredLastValue[config.DeviceId + "/" + config.WidgetId] = current.Value;
                        }
                    }
                }
                if (points.Count == 0) return;
                foreach (var point in points) await AppendPointAsync(point);
            }
            catch { }
        }

        private async Task AppendPointAsync(HistoryPoint point)
        {
            await EnsureLoadedAsync();
            await _fileGate.WaitAsync();
            try
            {
                lock (_sync)
                {
                    _cache.Add(point);
                    if (_cache.Count > 5000) _cache.RemoveRange(0, _cache.Count - 5000);
                }
                JObject json = new JObject {
                    ["t"] = point.Timestamp, ["d"] = point.DeviceId,
                    ["w"] = point.WidgetId, ["r"] = point.RawValue, ["v"] = point.Value
                };
                await Windows.Storage.FileIO.AppendTextAsync(_file, json.ToString(Formatting.None) + "\n");
                PruneCache();
            }
            finally { _fileGate.Release(); }
        }

        private void PruneCache()
        {
            long cutoff = RetentionCutoff();
            if (cutoff == 0) return;
            lock (_sync)
            {
                _cache.RemoveAll(p => p.Timestamp < cutoff);
                if (_cache.Count > 5000) _cache.RemoveRange(0, _cache.Count - 5000);
            }
            // The archive is filtered again when it is loaded; runtime cache stays bounded.
        }

        private long RetentionCutoff()
        {
            int days;
            lock (_sync) days = _retentionDays;
            return days <= 0 ? 0 : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - days * 86400000L;
        }

        private static int NormalizeSamplePeriod(int value)
        {
            int[] values = { 1000, 5000, 10000, 30000, 60000, 300000, 600000, 1800000, 3600000 };
            return values.OrderBy(x => Math.Abs((long)x - value)).First();
        }

        private static int NormalizeRetention(int value)
        {
            int[] values = { 1, 3, 7, 14, 30, 90, 180, 365, 0 };
            return values.OrderBy(x => Math.Abs((long)x - value)).First();
        }

        private static long NormalizeDisplayPeriod(long value)
        {
            long[] values = { 3600000L, 21600000L, 43200000L, 86400000L, 259200000L, 604800000L, 2592000000L, 0L };
            return values.OrderBy(x => Math.Abs(x - value)).First();
        }

        public void Dispose()
        {
            _disposed = true;
            if (_sampler != null) { _sampler.Dispose(); _sampler = null; }
            _fileGate.Dispose();
        }
    }

    internal static class HistoryListCompat
    {
        public static IEnumerable<T> TakeLastCompat<T>(this IEnumerable<T> source, int count)
        {
            var list = source as IList<T> ?? source.ToList();
            return list.Skip(Math.Max(0, list.Count - count));
        }
    }
}
