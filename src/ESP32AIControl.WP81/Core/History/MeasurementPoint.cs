using System;

namespace ESP32AIControl.WP81.Core.History
{
    public sealed class MeasurementPoint
    {
        public string DeviceId { get; set; }
        public string WidgetId { get; set; }
        public string Value { get; set; }
        public DateTimeOffset Timestamp { get; set; }
    }
}
