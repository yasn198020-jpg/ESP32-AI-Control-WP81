using System;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices.WindowsRuntime;
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
        private readonly object _publishLock = new object();
        private readonly object _packetIdLock = new object();
        private readonly System.Collections.Generic.Dictionary<ushort, TaskCompletionSource<bool>> _pendingPublishes =
            new System.Collections.Generic.Dictionary<ushort, TaskCompletionSource<bool>>();

        public event EventHandler<MqttMessageEventArgs> MessageReceived;
        public event EventHandler<MqttConnectionStateChangedEventArgs> ConnectionStateChanged;

        public MqttConnectionState State { get; private set; }

        public MqttClient()
        {
            State = MqttConnectionState.Disconnected;
        }

        public async Task ConnectAsync(string host, string port, string clientId, string userName, string password, bool useTls)
        {
            if (_running)
                return;

            SetState(MqttConnectionState.Connecting);
            _socket = new StreamSocket();

            try
            {
                var socketConnect = _socket.ConnectAsync(
                    new Windows.Networking.HostName(host),
                    port,
                    useTls ? SocketProtectionLevel.Tls12 : SocketProtectionLevel.PlainSocket).AsTask();
                var socketConnected = await Task.WhenAny(socketConnect, Task.Delay(12000));
                if (socketConnected != socketConnect)
                    throw new TimeoutException("MQTT TCP connection timeout.");
                await socketConnect;

                _writer = new DataWriter(_socket.OutputStream);
                _reader = new MqttPacketReader(_socket.InputStream);

                await WriteAsync(MqttPacketWriter.ConnectPacket(clientId, userName, password, true));

                var connAckTask = _reader.ReadAsync();
                var connAckReceived = await Task.WhenAny(connAckTask, Task.Delay(8000));
                if (connAckReceived != connAckTask)
                    throw new TimeoutException("MQTT CONNACK timeout.");
                var response = await connAckTask;
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
                var completed = await Task.WhenAny(_subscribeCompletion.Task, Task.Delay(5000));
                if (completed != _subscribeCompletion.Task)
                    throw new TimeoutException("MQTT SUBACK timeout.");
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
            var packetId = qos > 0 ? NextPacketId() : (ushort)0;
            TaskCompletionSource<bool> completion = null;

            if (qos > 0)
            {
                completion = new TaskCompletionSource<bool>();
                lock (_publishLock)
                    _pendingPublishes[packetId] = completion;
            }

            try
            {
                await WriteAsync(MqttPacketWriter.PublishPacket(topic, payload, qos, retain, packetId));
                if (completion == null)
                    return;

                var finished = await Task.WhenAny(completion.Task, Task.Delay(5000));
                if (finished != completion.Task)
                    throw new TimeoutException("MQTT PUBACK timeout for packet " + packetId);

                if (!await completion.Task)
                    throw new InvalidOperationException("MQTT publish was interrupted before PUBACK.");
            }
            finally
            {
                if (completion != null)
                {
                    lock (_publishLock)
                        _pendingPublishes.Remove(packetId);
                }
            }
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
                    HandlePubAck(packet);
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
                handler(this, new MqttMessageEventArgs(topic, payload, qos, (packet.Header & 0x01) != 0));
        }

        private void HandlePubAck(MqttPacket packet)
        {
            if (packet.Body.Length < 2)
                return;

            var id = (ushort)((packet.Body[0] << 8) | packet.Body[1]);
            TaskCompletionSource<bool> completion;
            lock (_publishLock)
                _pendingPublishes.TryGetValue(id, out completion);

            if (completion != null)
                completion.TrySetResult(true);
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
            lock (_packetIdLock)
            {
                if (_packetId == 0)
                    _packetId = 1;

                return _packetId++;
            }
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

            lock (_publishLock)
            {
                foreach (var pending in _pendingPublishes.Values)
                    pending.TrySetResult(false);
                _pendingPublishes.Clear();
            }

            try { if (_writer != null) _writer.DetachStream(); } catch { }
            try { if (_socket != null) _socket.Dispose(); } catch { }

            _writer = null;
            _reader = null;
            _socket = null;

            SetState(MqttConnectionState.Disconnected);
        }
    }
}
