namespace ESP32AIControl.WP81.Core.Protocol
{
    public enum Esp32MessageKind
    {
        Config,
        Status,
        Event,
        Unknown
    }

    public sealed class Esp32Message
    {
        public Esp32MessageKind Kind { get; set; }
        public string Topic { get; set; }
        public string WidgetId { get; set; }
        public string DeviceId { get; set; }
        public string Value { get; set; }
        public string Description { get; set; }
        public string Unit { get; set; }
        public string Minimum { get; set; }
        public string Maximum { get; set; }
        public string Step { get; set; }
        public string WarningMinimum { get; set; }
        public string WarningMaximum { get; set; }
        public string DefinitionName { get; set; }
        public string ScaleFactor { get; set; }
        public string[] Options { get; set; }
        public string WidgetType { get; set; }
        public string Page { get; set; }
        public int Order { get; set; }
        public string RawPayload { get; set; }
    }
}
