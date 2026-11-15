using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.System.Threading;
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
        private readonly object _packetIdGate = new object();
        private readonly object _publishAckGate = new object();
        private readonly Dictionary<ushort, TaskCompletionSource<bool>> _pendingPublishAcks =
            new Dictionary<ushort, TaskCompletionSource<bool>>();
        private bool _running;
        private int _pingOutstanding;
        private long _pingSentUtcTicks;
        private ushort _pendingSubscribeId;
        private TaskCompletionSource<bool> _subscribeCompletion;
        private Task _receiveLoopTask;
        private ThreadPoolTimer _keepAliveTimer;
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
            if (State == MqttConnectionState.Connecting)
                throw new InvalidOperationException("MQTT connection is already being established.");

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
                // MQTT 3.1.1 keep-alive is 60 seconds in CONNECT. Send PINGREQ
                // well before the broker deadline so an idle dashboard stays connected.
                _keepAliveTimer = ThreadPoolTimer.CreatePeriodicTimer(
                    KeepAliveTick, TimeSpan.FromSeconds(25));
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
                var completion = _subscribeCompletion.Task;
                var completed = await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(15)));
                if (!object.ReferenceEquals(completed, completion))
                    throw new TimeoutException("MQTT SUBACK was not received within 15 seconds.");
                var success = await completion;
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
            if (qos > 1)
                throw new ArgumentOutOfRangeException("qos", "Only MQTT QoS 0 and 1 are supported.");

            var packetId = qos == 0 ? (ushort)0 : NextPacketId();
            TaskCompletionSource<bool> acknowledgement = null;
            if (qos == 1)
            {
                acknowledgement = new TaskCompletionSource<bool>();
                lock (_publishAckGate)
                {
                    while (_pendingPublishAcks.ContainsKey(packetId))
                        packetId = NextPacketId();
                    _pendingPublishAcks.Add(packetId, acknowledgement);
                }
            }

            try
            {
                await WriteAsync(MqttPacketWriter.PublishPacket(topic, payload, qos, retain, packetId));
                if (acknowledgement == null)
                    return;

                var completed = await Task.WhenAny(
                    acknowledgement.Task,
                    Task.Delay(TimeSpan.FromSeconds(15)));

                if (!object.ReferenceEquals(completed, acknowledgement.Task) &&
                    !acknowledgement.Task.IsCompleted)
                {
                    // Do not report a QoS 1 publish as successful when the broker
                    // never acknowledged it. Close the uncertain session so the
                    // service follows its normal reconnect path.
                    Close();
                    throw new TimeoutException("MQTT PUBACK was not received within 15 seconds.");
                }

                if (!await acknowledgement.Task)
                    throw new InvalidOperationException("MQTT publish was interrupted before PUBACK.");
            }
            catch
            {
                if (acknowledgement != null)
                {
                    lock (_publishAckGate)
                    {
                        TaskCompletionSource<bool> current;
                        if (_pendingPublishAcks.TryGetValue(packetId, out current) &&
                            object.ReferenceEquals(current, acknowledgement))
                            _pendingPublishAcks.Remove(packetId);
                    }
                }
                throw;
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
                    // PINGRESP: the keep-alive exchange is complete.
                    Interlocked.Exchange(ref _pingOutstanding, 0);
                    Interlocked.Exchange(ref _pingSentUtcTicks, 0);
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
                handler(this, new MqttMessageEventArgs(topic, payload, qos));
        }

        private void HandlePubAck(MqttPacket packet)
        {
            if (packet.Body.Length < 2)
                return;

            var id = (ushort)((packet.Body[0] << 8) | packet.Body[1]);
            TaskCompletionSource<bool> completion = null;
            lock (_publishAckGate)
            {
                if (_pendingPublishAcks.TryGetValue(id, out completion))
                    _pendingPublishAcks.Remove(id);
            }

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

        private async void KeepAliveTick(ThreadPoolTimer timer)
        {
            if (!_running) return;

            if (Interlocked.CompareExchange(ref _pingOutstanding, 0, 0) != 0)
            {
                var sentAt = Interlocked.Read(ref _pingSentUtcTicks);
                if (sentAt > 0 &&
                    DateTime.UtcNow.Ticks - sentAt >= TimeSpan.FromSeconds(50).Ticks)
                {
                    // A silent/broken TCP connection may still accept writes.
                    // Require PINGRESP so the service can reconnect instead of
                    // remaining stuck in Connected forever.
                    Close();
                }
                return;
            }

            Interlocked.Exchange(ref _pingSentUtcTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref _pingOutstanding, 1);
            try
            {
                await WriteAsync(MqttPacketWriter.PingReq());
            }
            catch
            {
                Interlocked.Exchange(ref _pingOutstanding, 0);
                Interlocked.Exchange(ref _pingSentUtcTicks, 0);
                Close();
            }
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
            lock (_packetIdGate)
            {
                if (_packetId == 0)
                    _packetId = 1;

                var id = _packetId;
                _packetId = id == ushort.MaxValue ? (ushort)1 : (ushort)(id + 1);
                return id;
            }
        }

        private void EnsureConnected()
        {
            if (!_running || _writer == null)
                throw new InvalidOperationException("MQTT is not connected.");
        }

        private void SetState(MqttConnectionState state)
        {
            if (State == state) return;
            State = state;
            var handler = ConnectionStateChanged;
            if (handler != null)
                handler(this, new MqttConnectionStateChangedEventArgs(state));
        }

        private void Close()
        {
            _running = false;
            Interlocked.Exchange(ref _pingOutstanding, 0);
            Interlocked.Exchange(ref _pingSentUtcTicks, 0);
            if (_keepAliveTimer != null)
            {
                try { _keepAliveTimer.Cancel(); } catch { }
                _keepAliveTimer = null;
            }

            var completion = _subscribeCompletion;
            if (completion != null)
                completion.TrySetResult(false);

            List<TaskCompletionSource<bool>> interruptedPublishes =
                new List<TaskCompletionSource<bool>>();
            lock (_publishAckGate)
            {
                foreach (var pending in _pendingPublishAcks.Values)
                    interruptedPublishes.Add(pending);
                _pendingPublishAcks.Clear();
            }
            foreach (var pending in interruptedPublishes)
                pending.TrySetResult(false);

            try { if (_writer != null) _writer.DetachStream(); } catch { }
            try { if (_socket != null) _socket.Dispose(); } catch { }

            _writer = null;
            _reader = null;
            _socket = null;

            SetState(MqttConnectionState.Disconnected);
        }
    }
}
