using System;

namespace ESP32AIControl.WP81.Core.Protocol
{
    public sealed class Esp32TopicParser
    {
        private readonly string _root;

        public Esp32TopicParser(string mqttPrefix)
        {
            _root = "/" + (mqttPrefix ?? string.Empty).Trim('/') + "/";
        }

        public bool IsForThisApplication(string topic)
        {
            return !string.IsNullOrEmpty(topic) &&
                   topic.StartsWith(_root, StringComparison.OrdinalIgnoreCase);
        }

        public Esp32MessageKind GetKind(string topic)
        {
            if (!IsForThisApplication(topic))
                return Esp32MessageKind.Unknown;

            if (topic.EndsWith("/config", StringComparison.OrdinalIgnoreCase))
                return Esp32MessageKind.Config;
            if (topic.EndsWith("/status", StringComparison.OrdinalIgnoreCase))
                return Esp32MessageKind.Status;
            if (topic.EndsWith("/event", StringComparison.OrdinalIgnoreCase))
                return Esp32MessageKind.Event;
            return Esp32MessageKind.Unknown;
        }

        public string[] SplitRelative(string topic)
        {
            if (!IsForThisApplication(topic))
                return new string[0];

            var relative = topic.Substring(_root.Length);
            return relative.Split(new[] {'/'}, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
