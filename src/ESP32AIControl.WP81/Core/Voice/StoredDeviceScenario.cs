using System;
using System.Collections.Generic;

namespace ESP32AIControl.WP81.Core.Voice
{
    /// <summary>Platform-neutral persisted scenario model; storage belongs to the host app.</summary>
    public sealed class StoredDeviceScenario
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Source { get; set; }
        public IList<string> SensorIds { get; private set; }
        public bool Enabled { get; set; }

        public StoredDeviceScenario()
        {
            Id = Guid.NewGuid().ToString("N");
            Title = string.Empty;
            Source = string.Empty;
            SensorIds = new List<string>();
            Enabled = true;
        }
    }
}