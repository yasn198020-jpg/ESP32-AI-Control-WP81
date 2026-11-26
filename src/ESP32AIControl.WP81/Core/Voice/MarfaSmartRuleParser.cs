using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ESP32AIControl.WP81.Core.Scenarios;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core.Voice
{
    /// <summary>Builds local temperature-triggered rules from natural-language commands.</summary>
    public static class MarfaSmartRuleParser
    {
        public static LocalScenarioDefinition TryParse(string text, MarfaAnalyticalEngine engine, System.Collections.Generic.IList<WidgetState> widgets)
        {
            if (string.IsNullOrWhiteSpace(text) || engine == null) return null;
            var normalized = Regex.Replace(EmojiSemanticText.ForSpeech(text).ToLowerInvariant().Replace('ё', 'е'), @"\s+", " ").Trim();
            if (!Regex.IsMatch(normalized, @"\b(если|когда|при температуре|как только|при условии)\b")) return null;

            var durationScenario = TryParseDurationRule(normalized, engine, widgets);
            if (durationScenario != null) return durationScenario;

            if (!Regex.IsMatch(normalized, @"температур|темп|градус|жарк|холод")) return null;

            var upper = Regex.Match(normalized, @"(?:стала?\s+|поднялась?\s+|станет?\s+)?(?:выше|больше|превысит|достигнет)\s*(-?\d+(?:[.,]\d+)?)");
            var lower = Regex.Match(normalized, @"(?:стала?\s+|опустилась?\s+|станет?\s+)?(?:ниже|меньше|опустится)\s*(-?\d+(?:[.,]\d+)?)");
            var thresholdMatch = upper.Success ? upper : lower;
            if (!thresholdMatch.Success) return null;

            double threshold;
            if (!double.TryParse(thresholdMatch.Groups[1].Value.Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out threshold)) return null;

            var sensor = engine.ResolveSensor(normalized, widgets);
            if (sensor == null || sensor.Candidate == null) return null;

            var tail = normalized.Substring(Math.Min(normalized.Length, thresholdMatch.Index + thresholdMatch.Length));
            tail = Regex.Replace(tail, @"^\s*(?:градусов?|градуса?|град)?\s*(?:тогда|то|и)?\s*", string.Empty).Trim(' ', ',', '.', ':', ';', '-');
            var actionText = tail.Length == 0 ? normalized : tail;
            var value = DetectActionValue(actionText);
            if (value == null) return null;
            var targetQuery = normalized.Substring(0, thresholdMatch.Index) + " " + actionText;
            var target = engine.ResolveControl(targetQuery, widgets, value);
            if (target == null || target.Candidate == null) return null;

            var sensorWidget = sensor.Candidate.Widget;
            var targetWidget = target.Candidate.Widget;
            var scenario = new LocalScenarioDefinition
            {
                Title = "Если " + (sensorWidget.Description ?? sensorWidget.Id) + " " +
                    (lower.Success ? "<" : ">") + " " +
                    threshold.ToString(CultureInfo.InvariantCulture) + " — " +
                    (value == "1" ? "включить " : "выключить ") +
                    (targetWidget.Description ?? targetWidget.Id),
                Message = "Условие Марфы выполнено",
                Enabled = true,
                NotificationEnabled = true
            };
            scenario.Conditions.Add(new LocalScenarioCondition
            {
                WidgetId = sensorWidget.Id,
                Operator = lower.Success ? "<" : ">",
                Threshold = threshold,
                Connector = "AND"
            });
            scenario.Actions.Add(new LocalScenarioAction
            {
                DeviceId = targetWidget.DeviceId,
                WidgetId = targetWidget.Id,
                Value = value
            });
            return scenario;
        }

        private static LocalScenarioDefinition TryParseDurationRule(
            string text,
            MarfaAnalyticalEngine engine,
            System.Collections.Generic.IList<WidgetState> widgets)
        {
            if (!Regex.IsMatch(text, @"\b(автомат|управлен)")) return null;
            if (!Regex.IsMatch(text, @"\b(включен|включена|включено|включены|работает|активен|активна)")) return null;

            var duration = Regex.Match(text,
                @"(?:больше|дольше|свыше|более)\s+(\d+(?:[.,]\d+)?)\s*(минут(?:а|ы)?|мин|час(?:а|ов)?|ч)");
            if (!duration.Success) return null;

            double amount;
            if (!double.TryParse(duration.Groups[1].Value.Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out amount) || amount <= 0) return null;
            var unit = duration.Groups[2].Value;
            var durationMs = amount * (unit.StartsWith("час") || unit == "ч" ? 3600000.0 : 60000.0);
            if (durationMs <= 0 || durationMs > long.MaxValue) return null;

            var action = DetectActionValue(text);
            if (action == null) return null;
            var resolution = engine.ResolveControl(text, widgets, action);
            var candidates = resolution.Candidates.Where(candidate =>
                IsToggle(candidate.Widget) && IsAutomationWidget(candidate.Widget)).ToList();
            var chosen = resolution.Candidate != null &&
                IsToggle(resolution.Candidate.Widget) && IsAutomationWidget(resolution.Candidate.Widget)
                ? resolution.Candidate : candidates.Count == 1 ? candidates[0] : null;
            if (chosen == null) return null;

            var widget = chosen.Widget;
            var title = "Если " + (widget.Description ?? widget.Id) + " включён дольше " +
                amount.ToString(CultureInfo.InvariantCulture) + (unit.StartsWith("час") || unit == "ч" ? " ч" : " мин") +
                " — " + (action == "1" ? "включить" : "выключить");
            var scenario = new LocalScenarioDefinition
            {
                Title = title,
                Message = "Автомат включён дольше заданного времени",
                Enabled = true,
                NotificationEnabled = true
            };
            scenario.Conditions.Add(new LocalScenarioCondition
            {
                WidgetId = widget.Id,
                Operator = "ON_FOR_GT",
                Threshold = 0,
                DurationMs = (long)Math.Round(durationMs),
                Connector = "AND"
            });
            scenario.Actions.Add(new LocalScenarioAction
            {
                DeviceId = widget.DeviceId,
                WidgetId = widget.Id,
                Value = action
            });
            return scenario;
        }

        private static bool IsToggle(WidgetState widget)
        {
            var type = ((widget == null ? string.Empty : widget.WidgetType) ?? string.Empty).ToLowerInvariant();
            return type.Contains("toggle") || type.Contains("switch");
        }

        private static bool IsAutomationWidget(WidgetState widget)
        {
            var text = ((widget == null ? string.Empty : widget.Description) ?? string.Empty) + " " +
                ((widget == null ? string.Empty : widget.DefinitionName) ?? string.Empty);
            return text.IndexOf("автомат", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("управлен", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string DetectActionValue(string text)
        {
            if (Regex.IsMatch(text, @"\b(открой|открыть|включи|включить|запусти|запустить|зажги)\b")) return "1";
            if (Regex.IsMatch(text, @"\b(закрой|закрыть|выключи|выключить|останови|остановить|погаси)\b")) return "0";
            return null;
        }
    }
}