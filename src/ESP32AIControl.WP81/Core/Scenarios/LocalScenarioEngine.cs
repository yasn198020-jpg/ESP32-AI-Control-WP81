using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ESP32AIControl.WP81.Core.Protocol;

namespace ESP32AIControl.WP81.Core.Scenarios
{
    /// <summary>
    /// Local user-defined automations. Kept separate from IoTManager script
    /// analysis: these rules are stored and evaluated by the phone application.
    /// </summary>
    public sealed class LocalScenarioEngine
    {
        private readonly LocalScenarioStore _store;
        private readonly Action<LocalScenarioDefinition, string> _onTrigger;
        private readonly object _sync = new object();
        private readonly Dictionary<string, double> _values =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, LocalScenarioRuntimeState> _runtime =
            new Dictionary<string, LocalScenarioRuntimeState>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _scheduledDurationChecks =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public LocalScenarioEngine(LocalScenarioStore store, Action<LocalScenarioDefinition, string> onTrigger)
        {
            if (store == null) throw new ArgumentNullException("store");
            _store = store;
            _onTrigger = onTrigger;
            foreach (var pair in _store.LoadRuntimeStates())
                _runtime[pair.Key] = pair.Value;
        }

        public void Reload()
        {
            lock (_sync)
            {
                var definitions = _store.Load();
            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var scenario in definitions)
            {
                active.Add(scenario.Id);
                if (!_runtime.ContainsKey(scenario.Id))
                    _runtime[scenario.Id] = new LocalScenarioRuntimeState();
            }

            var stale = new List<string>();
            foreach (var id in _runtime.Keys)
                if (!active.Contains(id)) stale.Add(id);
            foreach (var id in stale)
                _runtime.Remove(id);
            }
        }

        public void ResetScenario(string id)
        {
            lock (_sync)
            {
                var scenario = _store.Load().FirstOrDefault(item => item.Id == id);
                if (scenario == null)
                {
                    _runtime.Remove(id);
                    _store.DeleteRuntimeState(id);
                    return;
                }

                var state = new LocalScenarioRuntimeState();
                var allKnown = true;
                for (var i = 0; i < scenario.Conditions.Count; i++)
                {
                    double value;
                    if (!_values.TryGetValue(scenario.Conditions[i].WidgetId, out value))
                    {
                        allKnown = false;
                        continue;
                    }
                    state.ConditionValues[i] = EvaluateCondition(scenario, state, i, value);
                }

                if (allKnown && state.ConditionValues.Count == scenario.Conditions.Count)
                {
                    var expression = state.ConditionValues[0];
                    for (var i = 1; i < scenario.Conditions.Count; i++)
                    {
                        expression = string.Equals(scenario.Conditions[i].Connector, "OR", StringComparison.OrdinalIgnoreCase)
                            ? expression || state.ConditionValues[i]
                            : expression && state.ConditionValues[i];
                    }
                    // Editing/enabling establishes a baseline; an already-true
                    // expression must wait for a fresh FALSE -> TRUE edge.
                    state.Fired = expression;
                }

                _runtime[id] = state;
                _store.SaveRuntimeState(id, state);
            }
        }

        public void OnMessage(Esp32Message message, bool allowTrigger)
        {
            lock (_sync)
                OnMessageLocked(message, allowTrigger);
        }

        private void OnMessageLocked(Esp32Message message, bool allowTrigger)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.WidgetId))
                return;
            if (message.Kind != Esp32MessageKind.Status && message.Kind != Esp32MessageKind.Event)
                return;

            double value;
            var raw = (message.Value ?? string.Empty).Trim().Replace(',', '.');
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase))
                    value = 1;
                else if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(raw, "off", StringComparison.OrdinalIgnoreCase))
                    value = 0;
                else
                    return;
            }
            if (double.IsNaN(value) || double.IsInfinity(value))
                return;

            _values[message.WidgetId] = value;
            var scenarios = _store.Load();
            foreach (var scenario in scenarios)
            {
                if (!scenario.Enabled || scenario.Conditions.Count == 0 ||
                    !scenario.Conditions.Any(condition =>
                        string.Equals(condition.WidgetId, message.WidgetId, StringComparison.OrdinalIgnoreCase)))
                    continue;

                LocalScenarioRuntimeState state;
                if (!_runtime.TryGetValue(scenario.Id, out state))
                {
                    state = new LocalScenarioRuntimeState();
                    _runtime[scenario.Id] = state;
                }

                var current = new Dictionary<int, bool>();
                var known = true;
                var fallingEdge = false;
                for (var i = 0; i < scenario.Conditions.Count; i++)
                {
                    var condition = scenario.Conditions[i];
                    double actual;
                    if (!_values.TryGetValue(condition.WidgetId, out actual))
                    {
                        known = false;
                        continue;
                    }

                    var matched = EvaluateCondition(scenario, state, i, actual);
                    current[i] = matched;
                    bool previous;
                    if (string.Equals(condition.WidgetId, message.WidgetId, StringComparison.OrdinalIgnoreCase) &&
                        state.ConditionValues.TryGetValue(i, out previous) && previous && !matched)
                        fallingEdge = true;
                }

                if (fallingEdge)
                {
                    state.Fired = false;
                    state.AwaitingTrueUpdate = true;
                    CopyConditionValues(state, current);
                    _store.SaveRuntimeState(scenario.Id, state);
                    continue;
                }

                if (!known || current.Count != scenario.Conditions.Count)
                {
                    CopyConditionValues(state, current);
                    _store.SaveRuntimeState(scenario.Id, state);
                    continue;
                }

                var expression = current[0];
                for (var i = 1; i < scenario.Conditions.Count; i++)
                {
                    expression = string.Equals(scenario.Conditions[i].Connector, "OR", StringComparison.OrdinalIgnoreCase)
                        ? expression || current[i]
                        : expression && current[i];
                }

                if (!allowTrigger)
                {
                    // Retained MQTT values and the initial WebSocket /params
                    // snapshot establish a baseline but are never trigger edges.
                    if (!state.AwaitingTrueUpdate)
                        state.Fired = expression;
                    CopyConditionValues(state, current);
                    _store.SaveRuntimeState(scenario.Id, state);
                    continue;
                }

                if (!expression)
                {
                    state.Fired = false;
                    CopyConditionValues(state, current);
                    _store.SaveRuntimeState(scenario.Id, state);
                    continue;
                }

                if (state.AwaitingTrueUpdate)
                {
                    var changedConditionTrue = false;
                    for (var i = 0; i < scenario.Conditions.Count; i++)
                        if (string.Equals(scenario.Conditions[i].WidgetId, message.WidgetId, StringComparison.OrdinalIgnoreCase) &&
                            current[i])
                            changedConditionTrue = true;

                    if (!changedConditionTrue)
                    {
                        CopyConditionValues(state, current);
                        _store.SaveRuntimeState(scenario.Id, state);
                        continue;
                    }

                    state.AwaitingTrueUpdate = false;
                }

                if (state.Fired)
                {
                    CopyConditionValues(state, current);
                    _store.SaveRuntimeState(scenario.Id, state);
                    continue;
                }

                // Persist the one-shot latch before dispatching actions. A
                // process restart cannot replay the same TRUE edge.
                state.Fired = true;
                CopyConditionValues(state, current);
                _store.SaveRuntimeState(scenario.Id, state);
                if (_onTrigger != null)
                    _onTrigger(scenario, message.Value ?? string.Empty);
            }
        }

        private static void CopyConditionValues(LocalScenarioRuntimeState state, IDictionary<int, bool> values)
        {
            // Preserve previously known conditions while other widgets have not
            // reported yet; this is required to detect a later TRUE -> FALSE edge.
            foreach (var pair in values)
                state.ConditionValues[pair.Key] = pair.Value;
        }

        private bool EvaluateCondition(
            LocalScenarioDefinition scenario,
            LocalScenarioRuntimeState state,
            int index,
            double actual)
        {
            var condition = scenario.Conditions[index];
            if (!string.Equals(condition.Operator, "ON_FOR_GT", StringComparison.OrdinalIgnoreCase))
                return Compare(actual, condition.Operator, condition.Threshold);

            if (actual <= condition.Threshold)
            {
                state.ConditionStartedAt.Remove(index);
                return false;
            }

            DateTimeOffset started;
            if (!state.ConditionStartedAt.TryGetValue(index, out started))
            {
                started = DateTimeOffset.UtcNow;
                state.ConditionStartedAt[index] = started;
            }

            var duration = Math.Max(0, condition.DurationMs);
            var elapsed = (DateTimeOffset.UtcNow - started).TotalMilliseconds;
            if (elapsed >= duration) return true;
            ScheduleDurationCheck(scenario.Id, index, duration - elapsed);
            return false;
        }

        private void ScheduleDurationCheck(string scenarioId, int conditionIndex, double remainingMs)
        {
            var key = scenarioId + "#" + conditionIndex.ToString(CultureInfo.InvariantCulture);
            if (!_scheduledDurationChecks.Add(key)) return;

            var delay = (int)Math.Max(1, Math.Min(int.MaxValue, Math.Ceiling(remainingMs)));
            Task.Run(async delegate
            {
                try { await Task.Delay(delay); }
                catch { }

                LocalScenarioDefinition triggered = null;
                string triggerValue = string.Empty;
                lock (_sync)
                {
                    _scheduledDurationChecks.Remove(key);
                    var scenario = _store.Load().FirstOrDefault(item => item.Id == scenarioId);
                    if (scenario == null || !scenario.Enabled || scenario.Conditions.Count == 0)
                        return;

                    LocalScenarioRuntimeState state;
                    if (!_runtime.TryGetValue(scenario.Id, out state))
                    {
                        state = new LocalScenarioRuntimeState();
                        _runtime[scenario.Id] = state;
                    }

                    var current = new Dictionary<int, bool>();
                    var known = true;
                    for (var i = 0; i < scenario.Conditions.Count; i++)
                    {
                        double actual;
                        if (!_values.TryGetValue(scenario.Conditions[i].WidgetId, out actual))
                        {
                            known = false;
                            continue;
                        }
                        current[i] = EvaluateCondition(scenario, state, i, actual);
                    }

                    if (!known || current.Count != scenario.Conditions.Count)
                    {
                        CopyConditionValues(state, current);
                        _store.SaveRuntimeState(scenario.Id, state);
                        return;
                    }

                    var expression = current[0];
                    for (var i = 1; i < scenario.Conditions.Count; i++)
                    {
                        expression = string.Equals(scenario.Conditions[i].Connector, "OR", StringComparison.OrdinalIgnoreCase)
                            ? expression || current[i]
                            : expression && current[i];
                    }

                    if (!expression)
                    {
                        state.Fired = false;
                        CopyConditionValues(state, current);
                        _store.SaveRuntimeState(scenario.Id, state);
                        return;
                    }

                    if (!state.Fired && !state.AwaitingTrueUpdate)
                    {
                        state.Fired = true;
                        triggered = scenario;
                        triggerValue = _values.ContainsKey(scenario.Conditions[conditionIndex].WidgetId)
                            ? _values[scenario.Conditions[conditionIndex].WidgetId].ToString(CultureInfo.InvariantCulture)
                            : string.Empty;
                    }

                    CopyConditionValues(state, current);
                    _store.SaveRuntimeState(scenario.Id, state);
                }

                if (triggered != null && _onTrigger != null)
                    _onTrigger(triggered, triggerValue);
            });
        }

        private static bool Compare(double actual, string op, double expected)
        {
            switch (op)
            {
                case ">": return actual > expected;
                case ">=": return actual >= expected;
                case "<": return actual < expected;
                case "<=": return actual <= expected;
                case "=": case "==": return Math.Abs(actual - expected) < 0.000001;
                case "!=": return Math.Abs(actual - expected) >= 0.000001;
                default: return false;
            }
        }
    }
}
