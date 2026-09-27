namespace ESP32AIControl.WP81.Core.Protocol
{
    public sealed class Esp32Protocol
    {
        private readonly Esp32TopicParser _topics;

        public Esp32Protocol(string mqttPrefix)
        {
            _topics = new Esp32TopicParser(mqttPrefix);
        }

        public Esp32Message Parse(string topic, string payload)
        {
            var kind = _topics.GetKind(topic);
            return Esp32JsonParser.Parse(kind, topic, payload, _topics);
        }

        public bool IsForThisApplication(string topic)
        {
            return _topics.IsForThisApplication(topic);
        }
    }
}
