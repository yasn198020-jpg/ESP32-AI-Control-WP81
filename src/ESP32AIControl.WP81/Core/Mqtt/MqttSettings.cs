namespace ESP32AIControl.WP81.Core.Mqtt
{
    public sealed class MqttSettings
    {
        public string Host { get; set; }
        public string Port { get; set; }
        public string Prefix { get; set; }
        public string UserName { get; set; }
        public string Password { get; set; }
        public string ClientId { get; set; }

        public MqttSettings()
        {
            Host = "m4.wqtt.ru";
            Port = "2815";
            Prefix = "dghjko";
            UserName = string.Empty;
            Password = string.Empty;
            ClientId = "ESP32-AI-Control-WP81";
        }

        public string RootTopic
        {
            get { return "/" + (Prefix ?? string.Empty).Trim('/'); }
        }
    }
}
