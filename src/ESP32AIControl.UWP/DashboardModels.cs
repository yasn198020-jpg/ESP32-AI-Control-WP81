using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Newtonsoft.Json;

namespace ESP32AIControl.UWP
{
    // Platform-neutral dashboard state. It deliberately contains no Lumia-specific sizing.
    public sealed class DeviceModel
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool Online { get; set; }
        public ObservableCollection<WidgetModel> Widgets { get; private set; }

        public DeviceModel()
        {
            Id = "";
            Name = "";
            Widgets = new ObservableCollection<WidgetModel>();
        }
    }

    public sealed class WidgetModel
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Type { get; set; }
        public string DefinitionName { get; set; }
        public string Value { get; set; }
        public string Unit { get; set; }
        public string Page { get; set; }
        public int Order { get; set; }
        public long LastUpdatedUnixMs { get; set; }
        public string ConfigJson { get; set; }

        public WidgetModel()
        {
            Id = ""; Title = ""; Type = "VALUE"; DefinitionName = "";
            Value = ""; Unit = ""; Page = ""; ConfigJson = "{}";
        }

        public string EffectiveDefinition
        {
            get
            {
                if (!String.IsNullOrWhiteSpace(DefinitionName)) return DefinitionName.Trim().ToLowerInvariant();
                switch ((Type ?? "").Trim().ToUpperInvariant())
                {
                    case "INPUT": return "input";
                    case "BUTTON": return "button";
                    case "TOGGLE": return "toggle";
                    case "STATUS": return "status";
                    default: return "anydata";
                }
            }
        }

        public IDictionary<string, object> ReadConfig()
        {
            try { return JsonConvert.DeserializeObject<Dictionary<string, object>>(ConfigJson ?? "{}") ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }

        public double ConfigNumber(string key, double fallback)
        {
            object raw;
            double value;
            if (ReadConfig().TryGetValue(key, out raw) &&
                Double.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out value))
                return value;
            return fallback;
        }
    }

    public sealed class DashboardSnapshot
    {
        public ObservableCollection<DeviceModel> Devices { get; private set; }
        public DashboardSnapshot() { Devices = new ObservableCollection<DeviceModel>(); }
    }
}
