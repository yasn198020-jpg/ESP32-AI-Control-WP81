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
        public string Unit { get; set; }
        public string Minimum { get; set; }
        public string Maximum { get; set; }
        public string Step { get; set; }
        public string WarningMinimum { get; set; }
        public string WarningMaximum { get; set; }
        public string DefinitionName { get; set; }
        public string ScaleFactor { get; set; }
        public string[] Options { get; set; }
        public string ConfigJson { get; set; }
        public string ColorConfig { get; set; }
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
                Unit = Unit,
                Minimum = Minimum,
                Maximum = Maximum,
                Step = Step,
                WarningMinimum = WarningMinimum,
                WarningMaximum = WarningMaximum,
                DefinitionName = DefinitionName,
                ScaleFactor = ScaleFactor,
                Options = Options,
                ConfigJson = ConfigJson,
                ColorConfig = ColorConfig,
                UpdatedAt = UpdatedAt
            };
        }
    }
}
