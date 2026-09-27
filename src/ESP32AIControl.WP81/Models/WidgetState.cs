using System;

namespace ESP32AIControl.WP81.Models
{
    public sealed class WidgetState
    {
        public string Id { get; set; }
        public string Description { get; set; }
        public string WidgetType { get; set; }
        public string Page { get; set; }
        public int Order { get; set; }
        public string Value { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
