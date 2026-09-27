using System;
using Windows.Storage.Streams;

namespace ESP32AIControl.WP81.Core.Mqtt
{
    internal sealed class MqttPacketReader
    {
        private readonly DataReader _reader;

        public MqttPacketReader(IInputStream input)
        {
            _reader = new DataReader(input);
            _reader.InputStreamOptions = InputStreamOptions.Partial;
        }

        public async System.Threading.Tasks.Task<MqttPacket> ReadAsync()
        {
            await _reader.LoadAsync(1);
            var header = _reader.ReadByte();

            var multiplier = 1;
            var remaining = 0;
            byte encoded;
            do
            {
                await _reader.LoadAsync(1);
                encoded = _reader.ReadByte();
                remaining += (encoded & 127) * multiplier;
                multiplier *= 128;
                if (multiplier > 128 * 128 * 128)
                    throw new InvalidOperationException("Invalid MQTT remaining length.");
            } while ((encoded & 128) != 0);

            if (remaining == 0)
                return new MqttPacket(header, new byte[0]);

            var bytes = new byte[remaining];
            var read = 0;
            while (read < remaining)
            {
                await _reader.LoadAsync((uint)(remaining - read));
                var available = _reader.UnconsumedBufferLength;
                if (available == 0)
                    throw new InvalidOperationException("MQTT stream closed.");
                var take = (int)Math.Min((uint)(remaining - read), available);
                _reader.ReadBytes(new ArraySegment<byte>(bytes, read, take).ToArray());
                read += take;
            }

            return new MqttPacket(header, bytes);
        }
    }

    internal sealed class MqttPacket
    {
        public MqttPacket(byte header, byte[] body)
        {
            Header = header;
            Body = body;
        }

        public byte Header { get; private set; }
        public byte[] Body { get; private set; }
        public int PacketType { get { return Header >> 4; } }
        public byte Flags { get { return (byte)(Header & 0x0F); } }
    }
}
