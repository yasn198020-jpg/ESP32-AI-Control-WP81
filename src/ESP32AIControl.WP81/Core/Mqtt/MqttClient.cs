using System;
using System.Threading;
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
        private ushort _pendingSubscribeId;
        private TaskCompletionSource<bool> _subscribeCompletion;
        private Task _receiveLoopTask;
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);

        public event EventHandler<MqttMessageEventArgs> MessageReceived;
        public event EventHandler<MqttConnectionStateChangedEventArgs> ConnectionStateChanged;

        public MqttConnectionState State { get; private set; }

        public MqttClient()
        {
            State = MqttConnectionState.Disconnected;
        }

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
                _receiveLoopTask = ReceiveLoopAsync();
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

            if (_subscribeCompletion != null)
                throw new InvalidOperationException("Another MQTT SUBSCRIBE is already pending.");

            var id = NextPacketId();
            _pendingSubscribeId = id;
            _subscribeCompletion = new TaskCompletionSource<bool>();

            try
            {
                await WriteAsync(MqttPacketWriter.SubscribePacket(id, topicFilter, qos));
                var success = await _subscribeCompletion.Task;
                if (!success)
                    throw new InvalidOperationException("MQTT SUBSCRIBE was rejected.");
            }
            finally
            {
                _subscribeCompletion = null;
                _pendingSubscribeId = 0;
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
                catch
                {
                }
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
                    await HandlePublishAsync(packet);
                    break;

                case 9:
                    HandleSubAck(packet);
                    break;

                case 13:
                    break;

                case 4:
                    break;
            }
        }

        private async Task HandlePublishAsync(MqttPacket packet)
        {
            if (packet.Body.Length < 2)
                return;

            var topicLength = (packet.Body[0] << 8) | packet.Body[1];
            var offset = 2;

            if (topicLength < 1 || offset + topicLength > packet.Body.Length)
                return;

            var topic = System.Text.Encoding.UTF8.GetString(packet.Body, offset, topicLength);
            offset += topicLength;

            var qos = (byte)((packet.Header >> 1) & 0x03);
            if (qos == 3)
                return;

            if (qos > 0)
            {
                if (offset + 2 > packet.Body.Length)
                    return;

                var id = (ushort)((packet.Body[offset] << 8) | packet.Body[offset + 1]);
                offset += 2;
                await WriteAsync(MqttPacketWriter.PubAck(id));
            }

            var payload = System.Text.Encoding.UTF8.GetString(packet.Body, offset, packet.Body.Length - offset);
            var handler = MessageReceived;
            if (handler != null)
                handler(this, new MqttMessageEventArgs(topic, payload, qos));
        }

        private void HandleSubAck(MqttPacket packet)
        {
            if (packet.Body.Length < 3 || _subscribeCompletion == null)
                return;

            var id = (ushort)((packet.Body[0] << 8) | packet.Body[1]);
            if (id != _pendingSubscribeId)
                return;

            var granted = packet.Body[2];
            _subscribeCompletion.TrySetResult(granted != 0x80);
        }

        private async Task WriteAsync(byte[] bytes)
        {
            await _writeGate.WaitAsync();

            try
            {
                if (_writer == null)
                    throw new InvalidOperationException("MQTT output stream is closed.");

                _writer.WriteBytes(bytes);
                await _writer.StoreAsync();
                await _writer.FlushAsync();
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private ushort NextPacketId()
        {
            if (_packetId == 0)
                _packetId = 1;

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

            var completion = _subscribeCompletion;
            if (completion != null)
                completion.TrySetResult(false);

            try { if (_writer != null) _writer.DetachStream(); } catch { }
            try { if (_socket != null) _socket.Dispose(); } catch { }

            _writer = null;
            _reader = null;
            _socket = null;

            SetState(MqttConnectionState.Disconnected);
        }
    }
}
