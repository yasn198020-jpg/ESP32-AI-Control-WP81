using System;

namespace ESP32AIControl.WP81.Core.Mqtt
{
    public sealed class MqttMessageEventArgs : EventArgs
    {
        public MqttMessageEventArgs(string topic, string payload, byte qos)
        {
            Topic = topic;
            Payload = payload;
            Qos = qos;
        }

        public string Topic { get; private set; }
        public string Payload { get; private set; }
        public byte Qos { get; private set; }
    }

    public sealed class MqttConnectionStateChangedEventArgs : EventArgs
    {
        public MqttConnectionStateChangedEventArgs(MqttConnectionState state)
        {
            State = state;
        }

        public MqttConnectionState State { get; private set; }
    }
}
