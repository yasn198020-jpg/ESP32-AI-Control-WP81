using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace ESP32AIControl.WP81.Core.Protocol
{
    internal static class Esp32JsonParser
    {
        [DataContract]
        private sealed class StatusDto
        {
            [DataMember(Name="status")]
            public object Status { get; set; }
        }

        [DataContract]
        private sealed class EventDto
        {
            [DataMember(Name="id")]
            public string Id { get; set; }

            [DataMember(Name="val")]
            public object Value { get; set; }

            [DataMember(Name="int")]
            public int IntValue { get; set; }
        }

        [DataContract]
        private sealed class ConfigDto
        {
            [DataMember(Name="topic")]
            public string Topic { get; set; }

            [DataMember(Name="descr")]
            public string Description { get; set; }

            [DataMember(Name="label")]
            public string Label { get; set; }

            [DataMember(Name="name")]
            public string Name { get; set; }

            [DataMember(Name="widget")]
            public string Widget { get; set; }

            [DataMember(Name="page")]
            public string Page { get; set; }

            [DataMember(Name="order")]
            public int Order { get; set; }
        }

        public static Esp32Message Parse(Esp32MessageKind kind, string topic, string payload, Esp32TopicParser topics)
        {
            switch (kind)
            {
                case Esp32MessageKind.Status:
                    return ParseStatus(topic, payload, topics);
                case Esp32MessageKind.Event:
                    return ParseEvent(topic, payload, topics);
                case Esp32MessageKind.Config:
                    return ParseConfig(topic, payload, topics);
                default:
                    return new Esp32Message { Kind = Esp32MessageKind.Unknown, Topic = topic, RawPayload = payload };
            }
        }

        private static Esp32Message ParseStatus(string topic, string payload, Esp32TopicParser topics)
        {
            var parts = topics.SplitRelative(topic);
            var widgetId = parts.Length >= 2 ? parts[parts.Length - 2] : string.Empty;
            return new Esp32Message
            {
                Kind = Esp32MessageKind.Status,
                Topic = topic,
                DeviceId = parts.Length >= 2 ? parts[0] : string.Empty,
                WidgetId = widgetId,
                Value = ExtractStatus(payload),
                RawPayload = payload
            };
        }

        private static Esp32Message ParseEvent(string topic, string payload, Esp32TopicParser topics)
        {
            var dto = Deserialize<EventDto>(payload);
            var parts = topics.SplitRelative(topic);
            return new Esp32Message
            {
                Kind = Esp32MessageKind.Event,
                Topic = topic,
                DeviceId = parts.Length >= 2 ? parts[0] : string.Empty,
                WidgetId = !string.IsNullOrEmpty(dto.Id) ? dto.Id : (parts.Length >= 2 ? parts[parts.Length - 2] : string.Empty),
                Value = ValueToString(dto.Value),
                RawPayload = payload
            };
        }

        private static Esp32Message ParseConfig(string topic, string payload, Esp32TopicParser topics)
        {
            var dto = Deserialize<ConfigDto>(payload);
            var parts = topics.SplitRelative(topic);
            var widgetId = GetLastSegment(dto.Topic);
            return new Esp32Message
            {
                Kind = Esp32MessageKind.Config,
                Topic = topic,
                DeviceId = parts.Length >= 2 ? parts[0] : string.Empty,
                WidgetId = widgetId,
                Description = First(dto.Description, dto.Label, dto.Name),
                WidgetType = dto.Widget,
                Page = dto.Page,
                Order = dto.Order,
                RawPayload = payload
            };
        }

        private static string ExtractStatus(string payload)
        {
            try
            {
                var dto = Deserialize<StatusDto>(payload);
                if (dto.Status != null)
                    return ValueToString(dto.Status);
            }
            catch { }
            return payload;
        }

        private static string GetLastSegment(string topic)
        {
            if (string.IsNullOrEmpty(topic))
                return string.Empty;
            var s = topic.Trim('/');
            var p = s.LastIndexOf('/');
            return p >= 0 ? s.Substring(p + 1) : s;
        }

        private static string First(params string[] values)
        {
            for (var i = 0; i < values.Length; i++)
                if (!string.IsNullOrWhiteSpace(values[i]))
                    return values[i];
            return string.Empty;
        }

        private static string ValueToString(object value)
        {
            return value == null ? string.Empty : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static T Deserialize<T>(string json)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? string.Empty)))
            {
                var serializer = new DataContractJsonSerializer(typeof(T));
                return (T)serializer.ReadObject(stream);
            }
        }
    }
}
