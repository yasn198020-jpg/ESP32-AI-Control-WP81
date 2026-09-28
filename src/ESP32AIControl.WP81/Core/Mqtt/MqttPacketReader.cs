using System;
using System.Threading.Tasks;
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

        public async Task<MqttPacket> ReadAsync()
        {
            await _reader.LoadAsync(1);
            var header = _reader.ReadByte();

            var multiplier = 1;
            var remaining = 0;
            byte encoded;
            var bytesUsed = 0;

            do
            {
                if (bytesUsed == 4)
                    throw new InvalidOperationException("Invalid MQTT remaining length.");

                await _reader.LoadAsync(1);
                encoded = _reader.ReadByte();
                remaining += (encoded & 127) * multiplier;
                multiplier *= 128;
                bytesUsed++;
            }
            while ((encoded & 128) != 0);

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
                var chunk = new byte[take];
                _reader.ReadBytes(chunk);
                System.Buffer.BlockCopy(chunk, 0, bytes, read, take);
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
