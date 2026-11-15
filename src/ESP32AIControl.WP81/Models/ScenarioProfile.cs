using System.Runtime.Serialization;

namespace ESP32AIControl.WP81.Models
{
    [DataContract]
    public sealed class ScenarioProfile
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string Title { get; set; }
        [DataMember] public string SensorIds { get; set; }
        [DataMember] public string Source { get; set; }
        [DataMember] public bool Enabled { get; set; }

        public ScenarioProfile Clone()
        {
            return new ScenarioProfile
            {
                Id = Id,
                Title = Title,
                SensorIds = SensorIds,
                Source = Source,
                Enabled = Enabled
            };
        }
    }
}