using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ESP32AIControl.UWP.Core.Marfa
{
    /// <summary>
    /// Scenario-aware command planner. Resolves only unique element IDs and
    /// derives safe, controllable equality prerequisites before a logical write.
    /// It never sends MQTT commands itself.
    /// </summary>
    public static class IoTScenarioCommandPlanner
    {
        private sealed class Requirement
        {
            public string WidgetId;
            public string Value;
        }

        public static ScenarioCommandPlan Plan(
            IList<LocalCommandActionItem> baseActions,
            IList<MarfaDevice> devices,
            IList<StoredDeviceScenario> storedScenarios)
        {
            baseActions = baseActions ?? new List<LocalCommandActionItem>();
            devices = devices ?? new List<MarfaDevice>();
            storedScenarios = storedScenarios ?? new List<StoredDeviceScenario>();
            if (baseActions.Count == 0)
                return new ScenarioCommandPlan { Actions = new List<LocalCommandActionItem>() };

            var plans = new List<ScenarioCommandPlan>();
            foreach (var action in baseActions)
                plans.Add(PlanOne(action, devices, storedScenarios));

            var blocked = plans.FirstOrDefault(x => !String.IsNullOrWhiteSpace(x.BlockedReason));
            if (blocked != null)
                return new ScenarioCommandPlan
                {
                    Actions = plans.SelectMany(x => x.Actions ?? new List<LocalCommandActionItem>())
                        .GroupBy(Key).Select(x => x.First()).ToList(),
                    Prerequisites = plans.SelectMany(x => x.Prerequisites ?? new List<ScenarioPrerequisite>())
                        .GroupBy(Key).Select(x => x.First()).ToList(),
                    BlockedReason = blocked.BlockedReason
                };

            return new ScenarioCommandPlan
            {
                Actions = plans.SelectMany(x => x.Actions ?? new List<LocalCommandActionItem>())
                    .GroupBy(Key).Select(x => x.First()).ToList(),
                Prerequisites = plans.SelectMany(x => x.Prerequisites ?? new List<ScenarioPrerequisite>())
                    .GroupBy(Key).Select(x => x.First()).ToList()
            };
        }

        private static ScenarioCommandPlan PlanOne(
            LocalCommandActionItem requested,
            IList<MarfaDevice> devices,
            IList<StoredDeviceScenario> storedScenarios)
        {
            var baseActions = new List<LocalCommandActionItem> { requested };
            if (requested == null || String.IsNullOrWhiteSpace(requested.WidgetId))
                return new ScenarioCommandPlan { Actions = baseActions, BlockedReason = "Не задан ID элемента команды." };

            var allWidgets = devices.SelectMany(d => (d.Widgets ?? new List<MarfaWidgetState>())
                .Where(w => w != null).Select(w => new { Device = d, Widget = w })).ToList();
            var targetMatches = allWidgets.Where(x => Same(x.Widget.Id, requested.WidgetId)).ToList();
            if (targetMatches.Count != 1)
                return new ScenarioCommandPlan
                {
                    Actions = baseActions,
                    BlockedReason = targetMatches.Count == 0
                        ? "Элемент с ID «" + requested.WidgetId + "» не найден."
                        : "ID элемента «" + requested.WidgetId + "» найден на нескольких ESP. Команда остановлена из соображений безопасности."
                };

            var parsedModels = storedScenarios.Where(x => x != null && x.Enabled)
                .Select(x => new { Stored = x, Parsed = IoTScenarioParser.Parse(x.Source ?? "") })
                .Where(x => x.Parsed.Errors.Count == 0)
                .Select(x => new { x.Stored, Model = IoTScenarioSemanticAnalyzer.Analyze(x.Parsed) })
                .Where(x => x.Model.ParserErrors.Count == 0)
                .ToList();

            var matching = parsedModels.Where(x =>
                (x.Stored.SensorIds ?? new List<string>()).Concat(x.Model.Identifiers)
                    .Any(id => Same(id, requested.WidgetId)))
                .Where(x => (x.Stored.SensorIds ?? new List<string>()).Concat(x.Model.Identifiers)
                    .Any(id => allWidgets.Any(w => Same(w.Widget.Id, id) && Same(w.Device.Id, targetMatches[0].Device.Id))))
                .ToList();

            if (matching.Count == 0)
                return new ScenarioCommandPlan { Actions = baseActions };

            if (matching.Count > 1)
                return new ScenarioCommandPlan
                {
                    Actions = baseActions,
                    BlockedReason = "Для элемента «" + requested.WidgetId + "» подходят несколько сохранённых сценариев. Нельзя безопасно выбрать дерево управления."
                };

            var selected = matching[0];
            var identifiers = (selected.Stored.SensorIds ?? new List<string>())
                .Concat(selected.Model.Identifiers).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var resolved = new Dictionary<string, Tuple<MarfaDevice, MarfaWidgetState>>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in identifiers)
            {
                var found = allWidgets.Where(x => Same(x.Widget.Id, id)).ToList();
                if (found.Count > 1)
                    return new ScenarioCommandPlan { Actions = baseActions,
                        BlockedReason = "Сценарий содержит неоднозначный ID элемента «" + id + "»." };
                if (found.Count == 1) resolved[id] = Tuple.Create(found[0].Device, found[0].Widget);
            }

            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in resolved) variables[pair.Key] = pair.Value.Item2.Value ?? "";
            var context = new IoTScenarioEvaluationContext { Variables = variables };

            var relevantRules = selected.Model.Rules.Where(rule =>
                rule.Actions.Any(a => Same(a.TargetId, requested.WidgetId) &&
                    ValueMatches(a.Expression, requested.Value, context))).ToList();
            if (relevantRules.Count == 0)
                return new ScenarioCommandPlan { Actions = baseActions };

            var candidatePlans = new List<List<Requirement>>();
            foreach (var rule in relevantRules)
            {
                if (IoTScenarioEvaluator.IsTruthy(IoTScenarioEvaluator.Evaluate(rule.Condition.Expression, context)))
                {
                    candidatePlans.Add(new List<Requirement>());
                    continue;
                }

                foreach (var candidate in Derive(rule.Condition.Expression, context, resolved))
                {
                    var simulated = new Dictionary<string, string>(variables, StringComparer.OrdinalIgnoreCase);
                    foreach (var requirement in candidate) simulated[requirement.WidgetId] = requirement.Value;
                    var after = new IoTScenarioEvaluationContext { Variables = simulated };
                    if (IoTScenarioEvaluator.IsTruthy(IoTScenarioEvaluator.Evaluate(rule.Condition.Expression, after)))
                        candidatePlans.Add(candidate);
                }
            }

            if (candidatePlans.Count == 0)
                return new ScenarioCommandPlan { Actions = baseActions };

            int min = candidatePlans.Min(x => x.Count);
            var shortest = candidatePlans.Where(x => x.Count == min)
                .Select(x => x.GroupBy(r => r.WidgetId + "=" + r.Value, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First()).OrderBy(r => r.WidgetId, StringComparer.OrdinalIgnoreCase).ToList())
                .GroupBy(PlanKey, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

            if (shortest.Count > 1)
                return new ScenarioCommandPlan { Actions = baseActions,
                    BlockedReason = "Найдено несколько равнозначных способов выполнить зависимости сценария. Уточните режим работы устройства." };

            var prerequisites = new List<ScenarioPrerequisite>();
            foreach (var requirement in shortest[0])
            {
                Tuple<MarfaDevice, MarfaWidgetState> entry;
                if (!resolved.TryGetValue(requirement.WidgetId, out entry) || !IsControllable(entry.Item2))
                    return new ScenarioCommandPlan { Actions = baseActions,
                        BlockedReason = "Зависимость «" + requirement.WidgetId + "» нельзя безопасно изменить." };
                if (Equivalent(entry.Item2.Value, requirement.Value)) continue;
                prerequisites.Add(new ScenarioPrerequisite
                {
                    DeviceId = entry.Item1.Id,
                    WidgetId = entry.Item2.Id,
                    Value = requirement.Value,
                    Reason = "Требование сценария: " + (String.IsNullOrWhiteSpace(entry.Item2.Title) ? entry.Item2.Id : entry.Item2.Title) + " = " + requirement.Value
                });
            }

            var actions = prerequisites.Select(x => new LocalCommandActionItem
            {
                DeviceId = x.DeviceId, WidgetId = x.WidgetId, Value = x.Value
            }).Concat(baseActions).GroupBy(Key).Select(x => x.First()).ToList();

            return new ScenarioCommandPlan { Actions = actions, Prerequisites = prerequisites };
        }

        private static IList<List<Requirement>> Derive(
            IoTExpr expression,
            IoTScenarioEvaluationContext context,
            IDictionary<string, Tuple<MarfaDevice, MarfaWidgetState>> resolved)
        {
            var result = new List<List<Requirement>>();
            var binary = expression as IoTExpr.Binary;
            if (binary != null && binary.Operator == "&")
            {
                var left = Derive(binary.Left, context, resolved);
                var right = Derive(binary.Right, context, resolved);
                if (left.Count == 0) left.Add(new List<Requirement>());
                if (right.Count == 0) right.Add(new List<Requirement>());
                foreach (var a in left)
                    foreach (var b in right)
                        result.Add(a.Concat(b).GroupBy(x => x.WidgetId + "=" + x.Value, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList());
                return result;
            }
            if (binary != null && binary.Operator == "|")
            {
                result.AddRange(Derive(binary.Left, context, resolved));
                result.AddRange(Derive(binary.Right, context, resolved));
                return result;
            }

            var unary = expression as IoTExpr.Unary;
            if (unary != null && unary.Operator == "!")
            {
                var inner = unary.Expression as IoTExpr.Binary;
                if (inner != null && (inner.Operator == "==" || inner.Operator == "!="))
                {
                    var inverted = new IoTExpr.Binary(inner.Operator == "==" ? "!=" : "==", inner.Left, inner.Right);
                    return Derive(inverted, context, resolved);
                }
                return result;
            }

            var variable = expression as IoTExpr.Variable;
            if (variable != null)
            {
                Tuple<MarfaDevice, MarfaWidgetState> item;
                if (resolved.TryGetValue(variable.Name, out item) && IsControllable(item.Item2))
                    result.Add(new List<Requirement> { new Requirement { WidgetId = variable.Name, Value = "1" } });
                return result;
            }

            if (binary == null || (binary.Operator != "==" && binary.Operator != "!=")) return result;
            var leftVariable = binary.Left as IoTExpr.Variable;
            var rightVariable = binary.Right as IoTExpr.Variable;
            string id = leftVariable != null ? leftVariable.Name : rightVariable != null ? rightVariable.Name : null;
            IoTExpr literal = leftVariable != null ? binary.Right : rightVariable != null ? binary.Left : null;
            if (id == null || literal == null) return result;
            Tuple<MarfaDevice, MarfaWidgetState> widget;
            if (!resolved.TryGetValue(id, out widget) || !IsControllable(widget.Item2)) return result;

            var literalValue = EvaluateText(literal, context);
            if (literalValue == null) return result;
            if (binary.Operator == "!=")
                return result;
            result.Add(new List<Requirement> { new Requirement { WidgetId = id, Value = literalValue } });
            return result;
        }

        private static string EvaluateText(IoTExpr expression, IoTScenarioEvaluationContext context)
        {
            var n = expression as IoTExpr.NumberLiteral;
            if (n != null) return n.Value.ToString("0.################", CultureInfo.InvariantCulture);
            var s = expression as IoTExpr.StringLiteral;
            if (s != null) return s.Value;
            var v = expression as IoTExpr.Variable;
            if (v != null)
            {
                string raw;
                return context.Variables.TryGetValue(v.Name, out raw) ? raw : null;
            }
            return null;
        }

        private static bool ValueMatches(IoTExpr expression, string desired, IoTScenarioEvaluationContext context)
        {
            string value = EvaluateText(expression, context);
            return value != null && Equivalent(value, desired);
        }

        private static bool IsControllable(MarfaWidgetState widget)
        {
            return widget != null && (widget.Type == MarfaWidgetType.TOGGLE ||
                widget.Type == MarfaWidgetType.BUTTON || widget.Type == MarfaWidgetType.INPUT);
        }

        private static bool Equivalent(string left, string right)
        {
            return String.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
        }
        private static string Normalize(string value)
        {
            double number;
            string text = (value ?? "").Trim().Replace(',', '.');
            if (Double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out number))
                return number.ToString("0.################", CultureInfo.InvariantCulture);
            return (value ?? "").Trim().ToLowerInvariant();
        }
        private static bool Same(string left, string right) { return String.Equals(left ?? "", right ?? "", StringComparison.OrdinalIgnoreCase); }
        private static string Key(LocalCommandActionItem x) { return x.DeviceId + "/" + x.WidgetId + "/" + x.Value; }
        private static string Key(ScenarioPrerequisite x) { return x.DeviceId + "/" + x.WidgetId + "/" + x.Value; }
        private static string PlanKey(IEnumerable<Requirement> items) { return String.Join("|", items.Select(x => x.WidgetId + "=" + x.Value)); }
    }
}
