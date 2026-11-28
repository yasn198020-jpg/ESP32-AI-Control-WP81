using System;
using System.Collections.Generic;

namespace ESP32AIControl.UWP.Core.Marfa
{
    /// <summary>Platform-neutral persisted representation of a user-defined device scenario.</summary>
    public sealed class StoredDeviceScenario
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Source { get; set; }
        public IList<string> SensorIds { get; set; }
        public bool Enabled { get; set; }

        public StoredDeviceScenario()
        {
            Id = Guid.NewGuid().ToString();
            Title = "";
            Source = "";
            SensorIds = new List<string>();
            Enabled = true;
        }
    }
}