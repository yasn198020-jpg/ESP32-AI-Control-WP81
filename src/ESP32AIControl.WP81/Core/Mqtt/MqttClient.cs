using System;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace ESP32AIControl.WP81.Core.Mqtt
{
    public sealed class MqttClient
    {
        private StreamSocket _socket;
        private DataWriter _writer;
        private MqttPacketReader _reader;
        private ushort _packetId = 1;
        private bool _running;

        public event EventHandler<MqttMessageEventArgs> MessageReceived;
        public event EventHandler<MqttConnectionStateChangedEventArgs> ConnectionStateChanged;

        public MqttConnectionState State { get; private set; } = MqttConnectionState.Disconnected;

        public async Task ConnectAsync(string host, string port, string clientId, string userName, string password)
        {
            if (_running)
                return;

            SetState(MqttConnectionState.Connecting);
            _socket = new StreamSocket();

            try
            {
                await _socket.ConnectAsync(new Windows.Networking.HostName(host), port);
                _writer = new DataWriter(_socket.OutputStream);
                _reader = new MqttPacketReader(_socket.InputStream);

                await WriteAsync(MqttPacketWriter.ConnectPacket(clientId, userName, password, true));
                var response = await _reader.ReadAsync();
                if (response.PacketType != 2 || response.Body.Length < 2 || response.Body[1] != 0)
                    throw new InvalidOperationException("MQTT CONNACK rejected.");

                _running = true;
                SetState(MqttConnectionState.Connected);
                _ = ReceiveLoopAsync();
            }
            catch
            {
                Close();
                throw;
            }
        }

        public async Task SubscribeAsync(string topicFilter, byte qos)
        {
            EnsureConnected();
            var id = NextPacketId();
            await WriteAsync(MqttPacketWriter.SubscribePacket(id, topicFilter, qos));

            while (true)
            {
                var packet = await _reader.ReadAsync();
                if (packet.PacketType == 9)
                {
                    return;
                }
                await HandleIncomingAsync(packet);
            }
        }

        public async Task PublishAsync(string topic, string payload, byte qos, bool retain)
        {
            EnsureConnected();
            await WriteAsync(MqttPacketWriter.PublishPacket(topic, payload, qos, retain, NextPacketId()));
        }

        public async Task DisconnectAsync()
        {
            if (_writer != null)
            {
                try
                {
                    await WriteAsync(MqttPacketWriter.Disconnect());
                }
                catch { }
            }
            Close();
        }

        private async Task ReceiveLoopAsync()
        {
            try
            {
                while (_running)
                {
                    var packet = await _reader.ReadAsync();
                    await HandleIncomingAsync(packet);
                }
            }
            catch
            {
                Close();
            }
        }

        private async Task HandleIncomingAsync(MqttPacket packet)
        {
            switch (packet.PacketType)
            {
                case 3:
                    var topicLength = (packet.Body[0] << 8) | packet.Body[1];
                    var offset = 2;
                    var topic = System.Text.Encoding.UTF8.GetString(packet.Body, offset, topicLength);
                    offset += topicLength;
                    var qos = (byte)((packet.Header >> 1) & 0x03);
                    ushort id = 0;
                    if (qos > 0)
                    {
                        id = (ushort)((packet.Body[offset] << 8) | packet.Body[offset + 1]);
                        offset += 2;
                        await WriteAsync(MqttPacketWriter.PubAck(id));
                    }

                    var payload = System.Text.Encoding.UTF8.GetString(packet.Body, offset, packet.Body.Length - offset);
                    var handler = MessageReceived;
                    if (handler != null)
                        handler(this, new MqttMessageEventArgs(topic, payload, qos));
                    break;

                case 13:
                    break;
            }
        }

        private async Task WriteAsync(byte[] bytes)
        {
            _writer.WriteBytes(bytes);
            await _writer.StoreAsync();
            await _writer.FlushAsync();
        }

        private ushort NextPacketId()
        {
            if (_packetId == 0) _packetId = 1;
            return _packetId++;
        }

        private void EnsureConnected()
        {
            if (!_running || _writer == null)
                throw new InvalidOperationException("MQTT is not connected.");
        }

        private void SetState(MqttConnectionState state)
        {
            State = state;
            var handler = ConnectionStateChanged;
            if (handler != null)
                handler(this, new MqttConnectionStateChangedEventArgs(state));
        }

        private void Close()
        {
            _running = false;
            try { if (_writer != null) _writer.DetachStream(); } catch { }
            try { if (_socket != null) _socket.Dispose(); } catch { }
            _writer = null;
            _reader = null;
            _socket = null;
            SetState(MqttConnectionState.Disconnected);
        }
    }
}
