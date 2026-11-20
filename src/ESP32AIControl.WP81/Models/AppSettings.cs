using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace ESP32AIControl.WP81.Models
{
    [DataContract]
    public sealed class AppSettings
    {
        [DataMember] public string Host { get; set; }
        [DataMember] public string Port { get; set; }
        [DataMember] public string Prefix { get; set; }
        [DataMember] public string UserName { get; set; }
        [DataMember] public string Password { get; set; }
        [DataMember] public string ClientId { get; set; }
        [DataMember] public bool UseTls { get; set; }
        [DataMember] public List<SavedMqttProfile> SavedMqttProfiles { get; set; }
        [DataMember] public string DefaultMqttProfileName { get; set; }
        [DataMember] public float UiFontScale { get; set; }
        [DataMember] public int MeasurementIntervalSeconds { get; set; }
        [DataMember] public int RetentionDays { get; set; }
        [DataMember] public string AutoExportPeriod { get; set; }
        [DataMember] public string VoiceOpenCommand { get; set; }
        [DataMember] public string VoiceCloseCommand { get; set; }
        [DataMember] public string IoTManagerScenario { get; set; }

        public AppSettings()
        {
            Host = "m4.wqtt.ru";
            Port = "2815";
            Prefix = "dghjko";
            UserName = string.Empty;
            Password = string.Empty;
            ClientId = "ESP32-AI-Control-WP81";
            UseTls = false;
            SavedMqttProfiles = new List<SavedMqttProfile>();
            DefaultMqttProfileName = string.Empty;
            UiFontScale = 0.85f;
            MeasurementIntervalSeconds = 60;
            RetentionDays = 30;
            AutoExportPeriod = "Never";
            VoiceOpenCommand = string.Empty;
            VoiceCloseCommand = string.Empty;
            IoTManagerScenario = string.Empty;
        }

        public ESP32AIControl.WP81.Core.Mqtt.MqttSettings ToMqtt()
        {
            return new ESP32AIControl.WP81.Core.Mqtt.MqttSettings
            {
                Host = Host,
                Port = Port,
                Prefix = Prefix,
                UserName = UserName,
                Password = Password,
                ClientId = ClientId,
                UseTls = UseTls
            };
        }
    }

    [DataContract]
    public sealed class SavedMqttProfile
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public string Host { get; set; }
        [DataMember] public string Port { get; set; }
        [DataMember] public string Prefix { get; set; }
        [DataMember] public string UserName { get; set; }
        [DataMember] public string Password { get; set; }
        [DataMember] public string ClientId { get; set; }
        [DataMember] public bool UseTls { get; set; }

        public SavedMqttProfile()
        {
            Name = string.Empty;
            Host = string.Empty;
            Port = "1883";
            Prefix = string.Empty;
            UserName = string.Empty;
            Password = string.Empty;
            ClientId = "ESP32-AI-Control-WP81";
            UseTls = false;
        }
    }
}
