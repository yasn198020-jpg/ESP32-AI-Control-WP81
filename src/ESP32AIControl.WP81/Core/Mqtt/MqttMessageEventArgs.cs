using System;

namespace ESP32AIControl.WP81.Core.Mqtt
{
    public sealed class MqttMessageEventArgs : EventArgs
    {
        public MqttMessageEventArgs(string topic, string payload, byte qos, bool retained = false)
        {
            Topic = topic;
            Payload = payload;
            Qos = qos;
            Retained = retained;
        }

        public string Topic { get; private set; }
        public string Payload { get; private set; }
        public byte Qos { get; private set; }
        public bool Retained { get; private set; }
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
