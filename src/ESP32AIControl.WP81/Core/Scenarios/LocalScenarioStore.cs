using System;
using System.Collections.Generic;
using Windows.Data.Json;
using Windows.Storage;

namespace ESP32AIControl.WP81.Core.Scenarios
{
    public sealed class LocalScenarioStore
    {
        private const string DefinitionsKey = "local_scenarios_v1";
        private const string RuntimeKey = "local_scenario_runtime_v1";
        private readonly ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;

        public IList<LocalScenarioDefinition> Load()
        {
            var result = new List<LocalScenarioDefinition>();
            try
            {
                object rawObject;
                if (!_settings.Values.TryGetValue(DefinitionsKey, out rawObject) || !(rawObject is string))
                    return result;

                var array = JsonArray.Parse((string)rawObject);
                for (uint i = 0; i < array.Count; i++)
                {
                    try
                    {
                        if (array[i].ValueType != JsonValueType.Object) continue;
                        var item = array[i].GetObject();
                        var scenario = new LocalScenarioDefinition
                        {
                            Id = ReadString(item, "id", Guid.NewGuid().ToString("N")),
                            Title = ReadString(item, "title", "Сценарий"),
                            Message = ReadString(item, "message", "Условие выполнено"),
                            Enabled = ReadBoolean(item, "enabled", true),
                            NotificationEnabled = ReadBoolean(item, "notificationEnabled", true)
                        };

                        IJsonValue conditionsValue;
                        if (item.TryGetValue("conditions", out conditionsValue) &&
                            conditionsValue != null && conditionsValue.ValueType == JsonValueType.Array)
                        {
                            var conditions = conditionsValue.GetArray();
                            for (uint c = 0; c < conditions.Count; c++)
                            {
                                if (conditions[c].ValueType != JsonValueType.Object) continue;
                                var condition = conditions[c].GetObject();
                                var widgetId = ReadString(condition, "widgetId", string.Empty).Trim();
                                double threshold;
                                if (widgetId.Length == 0 ||
                                    !double.TryParse(ReadString(condition, "threshold", "0"),
                                        System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out threshold))
                                    continue;

                                scenario.Conditions.Add(new LocalScenarioCondition
                                {
                                    WidgetId = widgetId,
                                    Operator = NormalizeOperator(ReadString(condition, "operator", ">")),
                                    Threshold = threshold,
                                    Connector = c == 0 ? "AND" : NormalizeConnector(ReadString(condition, "connector", "AND"))
                                });
                            }
                        }

                        IJsonValue actionsValue;
                        if (item.TryGetValue("actions", out actionsValue) &&
                            actionsValue != null && actionsValue.ValueType == JsonValueType.Array)
                        {
                            var actions = actionsValue.GetArray();
                            for (uint a = 0; a < actions.Count; a++)
                            {
                                if (actions[a].ValueType != JsonValueType.Object) continue;
                                var action = actions[a].GetObject();
                                var deviceId = ReadString(action, "deviceId", string.Empty).Trim();
                                var widgetId = ReadString(action, "widgetId", string.Empty).Trim();
                                if (deviceId.Length == 0 || widgetId.Length == 0) continue;
                                scenario.Actions.Add(new LocalScenarioAction
                                {
                                    DeviceId = deviceId,
                                    WidgetId = widgetId,
                                    Value = ReadString(action, "value", "1")
                                });
                            }
                        }

                        if (scenario.Conditions.Count > 0)
                            result.Add(scenario);
                    }
                    catch { }
                }
            }
            catch { }
            return result;
        }

        public void Save(IList<LocalScenarioDefinition> scenarios)
        {
            var array = new JsonArray();
            foreach (var scenario in scenarios)
            {
                var item = new JsonObject();
                item["id"] = JsonValue.CreateStringValue(scenario.Id ?? string.Empty);
                item["title"] = JsonValue.CreateStringValue(scenario.Title ?? string.Empty);
                item["message"] = JsonValue.CreateStringValue(scenario.Message ?? string.Empty);
                item["enabled"] = JsonValue.CreateBooleanValue(scenario.Enabled);
                item["notificationEnabled"] = JsonValue.CreateBooleanValue(scenario.NotificationEnabled);

                var conditions = new JsonArray();
                foreach (var condition in scenario.Conditions)
                {
                    var value = new JsonObject();
                    value["widgetId"] = JsonValue.CreateStringValue(condition.WidgetId ?? string.Empty);
                    value["operator"] = JsonValue.CreateStringValue(condition.Operator ?? ">");
                    value["threshold"] = JsonValue.CreateNumberValue(condition.Threshold);
                    value["connector"] = JsonValue.CreateStringValue(condition.Connector ?? "AND");
                    conditions.Add(value);
                }
                item["conditions"] = conditions;

                var actions = new JsonArray();
                foreach (var action in scenario.Actions)
                {
                    var value = new JsonObject();
                    value["deviceId"] = JsonValue.CreateStringValue(action.DeviceId ?? string.Empty);
                    value["widgetId"] = JsonValue.CreateStringValue(action.WidgetId ?? string.Empty);
                    value["value"] = JsonValue.CreateStringValue(action.Value ?? "1");
                    actions.Add(value);
                }
                item["actions"] = actions;
                array.Add(item);
            }
            _settings.Values[DefinitionsKey] = array.Stringify();
        }

        public IDictionary<string, LocalScenarioRuntimeState> LoadRuntimeStates()
        {
            var result = new Dictionary<string, LocalScenarioRuntimeState>(StringComparer.OrdinalIgnoreCase);
            try
            {
                object rawObject;
                if (!_settings.Values.TryGetValue(RuntimeKey, out rawObject) || !(rawObject is string))
                    return result;
                var root = JsonObject.Parse((string)rawObject);
                foreach (var pair in root)
                {
                    if (pair.Value.ValueType != JsonValueType.Object) continue;
                    var item = pair.Value.GetObject();
                    var state = new LocalScenarioRuntimeState
                    {
                        Fired = ReadBoolean(item, "fired", false),
                        AwaitingTrueUpdate = ReadBoolean(item, "awaitingTrueUpdate", false)
                    };
                    IJsonValue values;
                    if (item.TryGetValue("conditions", out values) && values != null &&
                        values.ValueType == JsonValueType.Object)
                    {
                        foreach (var condition in values.GetObject())
                        {
                            int index;
                            if (int.TryParse(condition.Key, out index) &&
                                condition.Value.ValueType == JsonValueType.Boolean)
                                state.ConditionValues[index] = condition.Value.GetBoolean();
                        }
                    }
                    result[pair.Key] = state;
                }
            }
            catch { }
            return result;
        }

        public void SaveRuntimeState(string id, LocalScenarioRuntimeState state)
        {
            try
            {
                JsonObject root;
                object rawObject;
                if (_settings.Values.TryGetValue(RuntimeKey, out rawObject) && rawObject is string)
                {
                    try { root = JsonObject.Parse((string)rawObject); }
                    catch { root = new JsonObject(); }
                }
                else root = new JsonObject();

                var item = new JsonObject();
                item["fired"] = JsonValue.CreateBooleanValue(state.Fired);
                item["awaitingTrueUpdate"] = JsonValue.CreateBooleanValue(state.AwaitingTrueUpdate);
                var values = new JsonObject();
                foreach (var pair in state.ConditionValues)
                    values[pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                        JsonValue.CreateBooleanValue(pair.Value);
                item["conditions"] = values;
                root[id] = item;
                _settings.Values[RuntimeKey] = root.Stringify();
            }
            catch { }
        }

        public void DeleteRuntimeState(string id)
        {
            try
            {
                object rawObject;
                if (!_settings.Values.TryGetValue(RuntimeKey, out rawObject) || !(rawObject is string))
                    return;
                var root = JsonObject.Parse((string)rawObject);
                root.Remove(id);
                _settings.Values[RuntimeKey] = root.Stringify();
            }
            catch { }
        }

        private static string ReadString(JsonObject item, string key, string fallback)
        {
            IJsonValue value;
            if (!item.TryGetValue(key, out value) || value == null) return fallback;
            try
            {
                if (value.ValueType == JsonValueType.String) return value.GetString();
                if (value.ValueType == JsonValueType.Number)
                    return value.GetNumber().ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { }
            return fallback;
        }

        private static bool ReadBoolean(JsonObject item, string key, bool fallback)
        {
            IJsonValue value;
            if (!item.TryGetValue(key, out value) || value == null ||
                value.ValueType != JsonValueType.Boolean) return fallback;
            return value.GetBoolean();
        }

        private static string NormalizeOperator(string value)
        {
            switch ((value ?? string.Empty).Trim())
            {
                case ">": case ">=": case "<": case "<=": case "=": case "==": case "!=":
                    return value.Trim();
                default: return ">";
            }
        }

        private static string NormalizeConnector(string value)
        {
            return string.Equals(value, "OR", StringComparison.OrdinalIgnoreCase) ? "OR" : "AND";
        }
    }
}
