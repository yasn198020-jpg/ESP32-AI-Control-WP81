using System;
using System.Collections.Generic;

namespace ESP32AIControl.WP81.Core.Mqtt
{
    internal static class MqttPacketWriter
    {
        public static byte[] EncodeRemainingLength(int length)
        {
            var result = new List<byte>(4);
            do
            {
                var digit = length % 128;
                length /= 128;
                if (length > 0)
                {
                    digit |= 128;
                }
                result.Add((byte)digit);
            } while (length > 0);
            return result.ToArray();
        }

        public static void WriteUtf8(List<byte> buffer, string value)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(value ?? string.Empty);
            if (bytes.Length > 65535)
            {
                throw new ArgumentException("MQTT UTF-8 field is too long.");
            }

            buffer.Add((byte)(bytes.Length >> 8));
            buffer.Add((byte)(bytes.Length & 0xFF));
            buffer.AddRange(bytes);
        }

        public static void WriteBinary(List<byte> buffer, byte[] value)
        {
            value = value ?? new byte[0];
            if (value.Length > 65535)
            {
                throw new ArgumentException("MQTT binary field is too long.");
            }

            buffer.Add((byte)(value.Length >> 8));
            buffer.Add((byte)(value.Length & 0xFF));
            buffer.AddRange(value);
        }

        public static byte[] ConnectPacket(string clientId, string userName, string password, bool cleanSession)
        {
            var variable = new List<byte>();
            WriteUtf8(variable, "MQTT");
            variable.Add(4);
            byte flags = 0;
            if (cleanSession) flags |= 0x02;
            if (!string.IsNullOrEmpty(userName)) flags |= 0x80;
            if (!string.IsNullOrEmpty(password)) flags |= 0x40;
            variable.Add(flags);
            variable.Add(0);
            variable.Add(60);

            var payload = new List<byte>();
            WriteUtf8(payload, clientId);
            if (!string.IsNullOrEmpty(userName)) WriteUtf8(payload, userName);
            if (!string.IsNullOrEmpty(password)) WriteUtf8(payload, password);

            var body = new List<byte>(variable.Count + payload.Count);
            body.AddRange(variable);
            body.AddRange(payload);
            return Packet(0x10, body.ToArray());
        }

        public static byte[] PublishPacket(string topic, string payload, byte qos, bool retain, ushort packetId)
        {
            var body = new List<byte>();
            WriteUtf8(body, topic);

            if (qos > 0)
            {
                body.Add((byte)(packetId >> 8));
                body.Add((byte)(packetId & 0xFF));
            }

            body.AddRange(System.Text.Encoding.UTF8.GetBytes(payload ?? string.Empty));
            var header = (byte)(0x30 | ((qos & 0x03) << 1) | (retain ? 1 : 0));
            return Packet(header, body.ToArray());
        }

        public static byte[] SubscribePacket(ushort packetId, string topic, byte qos)
        {
            var body = new List<byte>();
            body.Add((byte)(packetId >> 8));
            body.Add((byte)(packetId & 0xFF));
            WriteUtf8(body, topic);
            body.Add(qos);
            return Packet(0x82, body.ToArray());
        }

        public static byte[] PubAck(ushort packetId)
        {
            return new byte[] { 0x40, 0x02, (byte)(packetId >> 8), (byte)(packetId & 0xFF) };
        }

        public static byte[] PingReq()
        {
            return new byte[] { 0xC0, 0x00 };
        }

        public static byte[] Disconnect()
        {
            return new byte[] { 0xE0, 0x00 };
        }

        private static byte[] Packet(byte header, byte[] body)
        {
            var length = EncodeRemainingLength(body.Length);
            var result = new byte[1 + length.Length + body.Length];
            result[0] = header;
            Buffer.BlockCopy(length, 0, result, 1, length.Length);
            Buffer.BlockCopy(body, 0, result, 1 + length.Length, body.Length);
            return result;
        }
    }
}
