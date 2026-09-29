using System;

namespace ESP32AIControl.WP81.Models
{
    public sealed class WidgetState
    {
        public string DeviceId { get; set; }
        public string Id { get; set; }
        public string Description { get; set; }
        public string WidgetType { get; set; }
        public string Page { get; set; }
        public int Order { get; set; }
        public string Value { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }

        public string Key
        {
            get { return (DeviceId ?? string.Empty) + "/" + (Id ?? string.Empty); }
        }

        public WidgetState Clone()
        {
            return new WidgetState
            {
                DeviceId = DeviceId,
                Id = Id,
                Description = Description,
                WidgetType = WidgetType,
                Page = Page,
                Order = Order,
                Value = Value,
                UpdatedAt = UpdatedAt
            };
        }
    }
}
