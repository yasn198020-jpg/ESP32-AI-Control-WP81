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
            [DataMember(Name="status")] public object Status { get; set; }
            [DataMember(Name="val")] public object Value { get; set; }
        }

        [DataContract]
        private sealed class EventDto
        {
            [DataMember(Name="id")] public string Id { get; set; }
            [DataMember(Name="val")] public object Value { get; set; }
            [DataMember(Name="int")] public int IntValue { get; set; }
        }

        [DataContract]
        private sealed class ConfigDto
        {
            [DataMember(Name="topic")] public string Topic { get; set; }
            [DataMember(Name="id")] public string Id { get; set; }
            [DataMember(Name="descr")] public string Description { get; set; }
            [DataMember(Name="label")] public string Label { get; set; }
            [DataMember(Name="name")] public string Name { get; set; }
            [DataMember(Name="widget")] public string Widget { get; set; }
            [DataMember(Name="type")] public string Type { get; set; }
            [DataMember(Name="subtype")] public string Subtype { get; set; }
            [DataMember(Name="page")] public string Page { get; set; }
            [DataMember(Name="order")] public int Order { get; set; }
            [DataMember(Name="val")] public object Value { get; set; }
            [DataMember(Name="unit")] public string Unit { get; set; }
            [DataMember(Name="after")] public string After { get; set; }
            [DataMember(Name="color")] public object Color { get; set; }
            [DataMember(Name="min")] public object Minimum { get; set; }
            [DataMember(Name="max")] public object Maximum { get; set; }
            [DataMember(Name="step")] public object Step { get; set; }
            [DataMember(Name="minWarning")] public object WarningMinimum { get; set; }
            [DataMember(Name="maxWarning")] public object WarningMaximum { get; set; }
            [DataMember(Name="definition")] public string Definition { get; set; }
            [DataMember(Name="definitionName")] public string DefinitionName { get; set; }
            [DataMember(Name="k")] public object ScaleFactor { get; set; }
            [DataMember(Name="options")] public string[] Options { get; set; }
            [DataMember(Name="maxValue")] public object MaximumAlias { get; set; }
            [DataMember(Name="minValue")] public object MinimumAlias { get; set; }
            [DataMember(Name="stepValue")] public object StepAlias { get; set; }
        }

        public static Esp32Message Parse(Esp32MessageKind kind, string topic, string payload, Esp32TopicParser topics)
        {
            switch (kind)
            {
                case Esp32MessageKind.Status: return ParseStatus(topic, payload, topics);
                case Esp32MessageKind.Event: return ParseEvent(topic, payload, topics);
                case Esp32MessageKind.Config: return ParseConfig(topic, payload, topics);
                default: return new Esp32Message { Kind = Esp32MessageKind.Unknown, Topic = topic, RawPayload = payload };
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
                DeviceId = parts.Length >= 1 ? parts[0] : string.Empty,
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
                DeviceId = parts.Length >= 1 ? parts[0] : string.Empty,
                WidgetId = !string.IsNullOrEmpty(dto.Id) ? dto.Id : (parts.Length >= 2 ? parts[parts.Length - 2] : string.Empty),
                Value = ValueToString(dto.Value),
                RawPayload = payload
            };
        }

        private static Esp32Message ParseConfig(string topic, string payload, Esp32TopicParser topics)
        {
            var dto = Deserialize<ConfigDto>(payload);
            var parts = topics.SplitRelative(topic);
            var widgetId = First(dto.Id, GetLastSegment(dto.Topic), parts.Length >= 3 ? parts[parts.Length - 2] : string.Empty);
            return new Esp32Message
            {
                Kind = Esp32MessageKind.Config,
                Topic = topic,
                DeviceId = parts.Length >= 1 ? parts[0] : string.Empty,
                WidgetId = widgetId,
                Description = First(dto.Description, dto.Label),
                WidgetType = First(dto.Widget, dto.Subtype, dto.Type),
                Page = dto.Page,
                Order = dto.Order,
                Value = ValueToString(dto.Value),
                Unit = First(dto.Unit, dto.After),
                Minimum = First(ValueToString(dto.Minimum), ValueToString(dto.MinimumAlias)),
                Maximum = First(ValueToString(dto.Maximum), ValueToString(dto.MaximumAlias)),
                Step = First(ValueToString(dto.Step), ValueToString(dto.StepAlias)),
                WarningMinimum = ValueToString(dto.WarningMinimum),
                WarningMaximum = ValueToString(dto.WarningMaximum),
                DefinitionName = First(dto.DefinitionName, dto.Definition, dto.Name),
                ScaleFactor = ValueToString(dto.ScaleFactor),
                Options = dto.Options,
                RawPayload = payload
            };
        }

        private static string ExtractStatus(string payload)
        {
            try
            {
                var dto = Deserialize<StatusDto>(payload);
                if (dto.Status != null) return ValueToString(dto.Status);
                if (dto.Value != null) return ValueToString(dto.Value);
            }
            catch { }
            return payload;
        }

        private static string GetLastSegment(string topic)
        {
            if (string.IsNullOrEmpty(topic)) return string.Empty;
            var s = topic.Trim('/');
            var p = s.LastIndexOf('/');
            return p >= 0 ? s.Substring(p + 1) : s;
        }

        private static string First(params string[] values)
        {
            for (var i = 0; i < values.Length; i++)
                if (!string.IsNullOrWhiteSpace(values[i])) return values[i];
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