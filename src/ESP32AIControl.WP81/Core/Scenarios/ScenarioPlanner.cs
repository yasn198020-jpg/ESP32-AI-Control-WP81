using System;
using System.Collections.Generic;
using System.Linq;
using ESP32AIControl.WP81.Models;
using ESP32AIControl.WP81.Core.State;

namespace ESP32AIControl.WP81.Core.Scenarios
{
    public sealed class ScenarioAction
    {
        public string Command { get; set; }
        public string DeviceId { get; set; }
        public string WidgetId { get; set; }
        public string Value { get; set; }
        public string Reason { get; set; }
        public bool IsDependency { get; set; }
    }

    public sealed class ScenarioPlan
    {
        public IList<ScenarioAction> Actions { get; private set; }

        public ScenarioPlan()
        {
            Actions = new List<ScenarioAction>();
        }
    }

    public sealed class ScenarioPlanner
    {
        private static readonly string[] ActionWords =
        {
            "отк", "закр", "включ", "выключ", "запуск", "останов",
            "подним", "опуст", "отпер", "запер", "open", "close",
            "turn on", "turn off", "start", "stop"
        };

        private static readonly string[] AutomationWords =
        {
            "автомат", "автоматика", "automatic", "automation", "auto"
        };

        private static readonly string[] ManualWords =
        {
            "ручн", "manual"
        };

        private static readonly string[] OpenWords =
        {
            "отк", "open", "подним", "отпер"
        };

        private static readonly string[] CloseWords =
        {
            "закр", "close", "опуст", "запер"
        };

        private readonly DeviceRepository _devices;
        private IoTManagerScenarioAnalyzer _scenarioAnalyzer;

        public ScenarioPlanner(DeviceRepository devices)
        {
            if (devices == null) throw new ArgumentNullException("devices");
            _devices = devices;
            _scenarioAnalyzer = new IoTManagerScenarioAnalyzer(string.Empty);
        }

        public void SetScenarioScript(string script)
        {
            _scenarioAnalyzer = new IoTManagerScenarioAnalyzer(script ?? string.Empty);
        }

        public int GetScenarioRuleCount()
        {
            return _scenarioAnalyzer == null ? 0 : _scenarioAnalyzer.RuleCount;
        }

        public DeviceScenarioModel GetScenarioLanguageModel()
        {
            return _scenarioAnalyzer == null ? null : _scenarioAnalyzer.LanguageModel;
        }

        public IList<ScenarioVariableDiagnostic> GetScenarioDiagnostics()
        {
            return _scenarioAnalyzer == null
                ? new List<ScenarioVariableDiagnostic>()
                : _scenarioAnalyzer.ValidateReferences(_devices);
        }

        public ScenarioPlan BuildPlan(string command)
        {
            var plan = new ScenarioPlan();
            var commands = SplitCommands(command);

            for (var i = 0; i < commands.Count; i++)
            {
                var action = ParseAction(commands[i]);
                if (action == null)
                    throw new ArgumentException("Формат команды: device/widget=value", "command");

                // A natural-language match can land on the physical relay.
                // Resolve it through the scenario before planning the command.
                DeviceScenarioRule actuatorRule;
                ScenarioAction logicalTarget;
                bool ambiguousLogicalTarget;
                if (TryResolveLogicalTargetFromActuator(action, out logicalTarget,
                    out actuatorRule, out ambiguousLogicalTarget))
                {
                    ScenarioAction modeAction;
                    if (TryBuildManualModeActuatorPlan(action, logicalTarget, actuatorRule, out modeAction))
                    {
                        if (modeAction != null) plan.Actions.Add(modeAction);
                        plan.Actions.Add(action);
                        continue;
                    }
                    action = logicalTarget;
                }
                else if (ambiguousLogicalTarget)
                {
                    throw new InvalidOperationException(
                        "Нельзя однозначно определить логическое состояние для исполнительного устройства " +
                        action.DeviceId + "/" + action.WidgetId + ". Команда не отправлена.");
                }

                if (!TryAddReverseScenarioPlan(plan, action) && !TryAddExpressionDependencyPlan(plan, action))
                {
                    AddAutomationDependency(plan, action);
                    AddInterlockDependency(plan, action);
                    plan.Actions.Add(action);
                }
            }

            if (plan.Actions.Count == 0)
                throw new ArgumentException("Пустой сценарий.", "command");

            ValidatePlanConflicts(plan);
            OrderPlanByScenarioDependencies(plan);
            return plan;
        }

        private void OrderPlanByScenarioDependencies(ScenarioPlan plan)
        {
            if (plan == null || plan.Actions.Count < 2 ||
                _scenarioAnalyzer == null || _scenarioAnalyzer.LanguageModel == null)
                return;

            var model = _scenarioAnalyzer.LanguageModel;
            var byTarget = plan.Actions.ToDictionary(
                a => a.DeviceId + "/" + a.WidgetId, StringComparer.OrdinalIgnoreCase);
            var outgoing = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var indegree = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var action in plan.Actions)
            {
                var key = action.DeviceId + "/" + action.WidgetId;
                outgoing[key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                indegree[key] = 0;
            }

            foreach (var rule in model.Rules)
            {
                foreach (var action in rule.Actions)
                {
                    var targetMatches = _devices.GetWidgetsById(action.TargetId);
                    if (targetMatches.Count != 1) continue;
                    var targetKey = targetMatches[0].DeviceId + "/" + targetMatches[0].Id;
                    if (!byTarget.ContainsKey(targetKey)) continue;

                    foreach (var prerequisiteId in rule.Condition.Identifiers)
                    {
                        var prerequisiteMatches = _devices.GetWidgetsById(prerequisiteId);
                        if (prerequisiteMatches.Count != 1) continue;
                        var prerequisiteKey = prerequisiteMatches[0].DeviceId + "/" + prerequisiteMatches[0].Id;
                        if (string.Equals(prerequisiteKey, targetKey, StringComparison.OrdinalIgnoreCase) ||
                            !byTarget.ContainsKey(prerequisiteKey))
                            continue;

                        if (outgoing[prerequisiteKey].Add(targetKey))
                            indegree[targetKey]++;
                    }
                }
            }

            var ordered = new List<ScenarioAction>();
            var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (ordered.Count < plan.Actions.Count)
            {
                ScenarioAction next = null;
                foreach (var action in plan.Actions)
                {
                    var key = action.DeviceId + "/" + action.WidgetId;
                    if (!emitted.Contains(key) && indegree[key] == 0)
                    {
                        next = action;
                        break;
                    }
                }
                if (next == null)
                    throw new InvalidOperationException(
                        "В графе сценарных зависимостей обнаружен цикл. Команды не отправлены.");

                var nextKey = next.DeviceId + "/" + next.WidgetId;
                emitted.Add(nextKey);
                ordered.Add(next);
                foreach (var dependent in outgoing[nextKey])
                    indegree[dependent]--;
            }

            plan.Actions.Clear();
            foreach (var action in ordered) plan.Actions.Add(action);
        }

        /// <summary>
        /// Refuse plans that would write different values to the same widget.
        /// This check runs after scenario prerequisites have been expanded, so it
        /// also catches conflicts between a requested command and an inferred
        /// manual-mode/interlock dependency.
        /// </summary>
        private static void ValidatePlanConflicts(ScenarioPlan plan)
        {
            var valuesByTarget = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var uniqueActions = new List<ScenarioAction>();

            for (var i = 0; i < plan.Actions.Count; i++)
            {
                var action = plan.Actions[i];
                if (action == null || string.IsNullOrWhiteSpace(action.DeviceId) ||
                    string.IsNullOrWhiteSpace(action.WidgetId))
                    throw new InvalidOperationException("План содержит действие без однозначного устройства или виджета.");

                var key = action.DeviceId.Trim() + "/" + action.WidgetId.Trim();
                string previous;
                if (valuesByTarget.TryGetValue(key, out previous))
                {
                    if (!string.Equals((previous ?? string.Empty).Trim(),
                        (action.Value ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "Конфликт команд для " + key + ": одновременно требуются значения '" +
                            previous + "' и '" + action.Value + "'. Команда не отправлена.");
                    }

                    // Repeated identical writes are unnecessary. Preserve the
                    // first action and its ordering/reason.
                    continue;
                }

                valuesByTarget[key] = action.Value ?? string.Empty;
                uniqueActions.Add(action);
            }

            plan.Actions.Clear();
            for (var i = 0; i < uniqueActions.Count; i++)
                plan.Actions.Add(uniqueActions[i]);
        }

        /// <summary>
        /// Resolves a physical output back to the logical state written by the
        /// same IoTManager rule. IDs must resolve to exactly one widget; no
        /// fuzzy ID matching or cross-device binding is allowed.
        /// </summary>
        private bool TryResolveLogicalTargetFromActuator(
            ScenarioAction physicalAction,
            out ScenarioAction logicalTarget,
            out DeviceScenarioRule matchedRule,
            out bool ambiguous)
        {
            logicalTarget = null;
            matchedRule = null;
            ambiguous = false;
            if (physicalAction == null || _scenarioAnalyzer == null ||
                _scenarioAnalyzer.LanguageModel == null ||
                (physicalAction.Value != "0" && physicalAction.Value != "1"))
                return false;

            var model = _scenarioAnalyzer.LanguageModel;
            if (!model.Valid) return false;

            var candidates = new List<Tuple<ScenarioAction, DeviceScenarioRule>>();
            foreach (var rule in model.Rules)
            {
                var drivesRequestedOutput = rule.Actions.Any(a =>
                {
                    string resolvedValue;
                    return string.Equals(a.TargetId, physicalAction.WidgetId, StringComparison.OrdinalIgnoreCase) &&
                        TryResolveScenarioLiteral(a.Expression, model,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase), out resolvedValue) &&
                        ScenarioValuesEquivalent(resolvedValue, physicalAction.Value);
                });
                if (!drivesRequestedOutput) continue;

                foreach (var stateAction in rule.Actions)
                {
                    if (string.Equals(stateAction.TargetId, physicalAction.WidgetId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string stateValue;
                    if (!TryResolveScenarioLiteral(stateAction.Expression, model,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase), out stateValue) ||
                        (stateValue != "0" && stateValue != "1"))
                        continue;
                    if (!ExpressionHasEqualityThroughAliases(rule.Condition.Expression,
                        stateAction.TargetId, stateValue, model))
                        continue;

                    var matches = _devices.GetWidgetsById(stateAction.TargetId);
                    if (matches.Count != 1 ||
                        !string.Equals(matches[0].DeviceId, physicalAction.DeviceId, StringComparison.OrdinalIgnoreCase) ||
                        !IsUserControl(matches[0]) || IsPhysicalActuator(matches[0]))
                        continue;

                    candidates.Add(Tuple.Create(new ScenarioAction
                    {
                        Command = matches[0].DeviceId + "/" + matches[0].Id + "=" + stateValue,
                        DeviceId = matches[0].DeviceId,
                        WidgetId = matches[0].Id,
                        Value = stateValue,
                        Reason = "Логическое состояние найдено по сценарию исполнительного устройства",
                        IsDependency = false
                    }, rule));
                }
            }

            var unique = candidates
                .GroupBy(x => x.Item1.DeviceId + "/" + x.Item1.WidgetId + "=" + x.Item1.Value,
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).ToList();
            if (unique.Count > 1)
            {
                ambiguous = true;
                return false;
            }
            if (unique.Count == 0) return false;

            logicalTarget = unique[0].Item1;
            matchedRule = unique[0].Item2;
            return true;
        }

        /// <summary>
        /// If the matched actuator branch is guarded by automatic mode, switch
        /// to manual mode first and then issue the requested physical action.
        /// Only a unique, same-device mode control is accepted.
        /// </summary>
        private bool TryBuildManualModeActuatorPlan(
            ScenarioAction physicalAction,
            ScenarioAction logicalTarget,
            DeviceScenarioRule rule,
            out ScenarioAction modeAction)
        {
            modeAction = null;
            if (rule == null || logicalTarget == null) return false;

            var model = _scenarioAnalyzer == null ? null : _scenarioAnalyzer.LanguageModel;
            if (model == null) return false;

            var modeCandidates = new List<WidgetState>();
            foreach (var id in rule.Condition.Identifiers)
            {
                var matches = _devices.GetWidgetsById(id);
                if (matches.Count != 1 ||
                    !string.Equals(matches[0].DeviceId, physicalAction.DeviceId, StringComparison.OrdinalIgnoreCase) ||
                    !IsUserControl(matches[0]) || !IsModeWidget(matches[0]))
                    continue;

                var gateValues = GetEqualityValuesThroughAliases(rule.Condition.Expression, id, model);
                if (gateValues.Count == 1 && gateValues[0] == "0")
                    modeCandidates.Add(matches[0]);
            }

            // IoTManager can split the manual branch into another rule. Link
            // it only through an explicit shared action/condition identifier,
            // never through a similar-looking widget title.
            foreach (var manualRule in model.Rules)
            {
                foreach (var id in manualRule.Condition.Identifiers)
                {
                    var manualValues = GetEqualityValuesThroughAliases(
                        manualRule.Condition.Expression, id, model);
                    if (manualValues.Count != 1 || manualValues[0] != "1")
                        continue;

                    var linked = manualRule.Actions.Any(a =>
                        string.Equals(a.TargetId, physicalAction.WidgetId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(a.TargetId, logicalTarget.WidgetId, StringComparison.OrdinalIgnoreCase) ||
                        rule.Condition.Identifiers.Contains(a.TargetId) ||
                        ScenarioExpressionDependsOnIdentifier(a.Expression, physicalAction.WidgetId, model,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase)) ||
                        ScenarioExpressionDependsOnIdentifier(a.Expression, logicalTarget.WidgetId, model,
                            new HashSet<string>(StringComparer.OrdinalIgnoreCase))) ||
                        ScenarioExpressionDependsOnIdentifier(manualRule.Condition.Expression,
                            physicalAction.WidgetId, model, new HashSet<string>(StringComparer.OrdinalIgnoreCase)) ||
                        ScenarioExpressionDependsOnIdentifier(manualRule.Condition.Expression,
                            logicalTarget.WidgetId, model, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    if (!linked) continue;

                    var matches = _devices.GetWidgetsById(id);
                    if (matches.Count == 1 &&
                        string.Equals(matches[0].DeviceId, physicalAction.DeviceId, StringComparison.OrdinalIgnoreCase) &&
                        IsUserControl(matches[0]) && IsModeWidget(matches[0]))
                        modeCandidates.Add(matches[0]);
                }
            }

            var distinct = modeCandidates.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).ToList();
            if (distinct.Count == 0) return false;
            if (distinct.Count != 1)
                throw new InvalidOperationException(
                    "В сценарии найдено несколько переключателей режима для " +
                    physicalAction.DeviceId + "/" + physicalAction.WidgetId +
                    ". Команда не отправлена.");

            var mode = distinct[0];
            if (!ScenarioValuesEquivalent(mode.Value, "1"))
            {
                modeAction = new ScenarioAction
                {
                    Command = mode.DeviceId + "/" + mode.Id + "=1",
                    DeviceId = mode.DeviceId,
                    WidgetId = mode.Id,
                    Value = "1",
                    Reason = "Переключить управление в ручной режим перед прямым управлением исполнительным устройством",
                    IsDependency = true
                };
            }
            return true;
        }

        private static bool IsModeWidget(WidgetState state)
        {
            if (state == null) return false;
            var description = (state.Description ?? string.Empty) + " " +
                (state.DefinitionName ?? string.Empty) + " " + (state.WidgetType ?? string.Empty);
            return HasAny(description, AutomationWords) || HasAny(description, ManualWords) ||
                HasAny(description, new[] { "режим", "mode" });
        }

        private static bool IsPhysicalActuator(WidgetState state)
        {
            if (state == null) return false;
            return string.Equals(state.WidgetType, "ButtonOut", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(state.DefinitionName, "ButtonOut", StringComparison.OrdinalIgnoreCase) ||
                HasAny(state.Description, ActionWords);
        }

        private static bool TryGetLiteral(IoTExpr expression, out string value)
        {
            value = null;
            var number = expression as IoTExpr.NumberLiteral;
            if (number != null)
            {
                value = number.Value.ToString("0.################", System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            var text = expression as IoTExpr.StringLiteral;
            if (text != null) { value = text.Value; return true; }
            var variable = expression as IoTExpr.Variable;
            if (variable != null && (variable.Name.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                variable.Name.Equals("false", StringComparison.OrdinalIgnoreCase)))
            {
                value = variable.Name.Equals("true", StringComparison.OrdinalIgnoreCase) ? "1" : "0";
                return true;
            }
            return false;
        }

        private static bool LiteralMatches(IoTExpr expression, string expected)
        {
            string value;
            return TryGetLiteral(expression, out value) && ScenarioValuesEquivalent(value, expected);
        }

        private static bool ExpressionHasEquality(IoTExpr expression, string identifier)
        {
            string ignored;
            return TryGetEqualityValue(expression, identifier, out ignored);
        }

        // Resolve intermediate IoTManager variables without guessing. A variable
        // is accepted only when every reachable assignment resolves to one value.
        private static bool TryResolveScenarioLiteral(
            IoTExpr expression,
            DeviceScenarioModel model,
            ISet<string> visiting,
            out string value)
        {
            value = null;
            if (TryGetLiteral(expression, out value)) return true;
            var variable = expression as IoTExpr.Variable;
            if (variable == null || model == null || !visiting.Add(variable.Name))
                return false;

            var definitions = model.Rules.SelectMany(r => r.Actions)
                .Where(a => string.Equals(a.TargetId, variable.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (definitions.Count == 0) return false;

            var resolved = new List<string>();
            foreach (var action in definitions)
            {
                string candidate;
                if (!TryResolveScenarioLiteral(action.Expression, model,
                    new HashSet<string>(visiting, StringComparer.OrdinalIgnoreCase), out candidate))
                    return false;
                resolved.Add(candidate);
            }

            var unique = resolved.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (unique.Count != 1) return false;
            value = unique[0];
            return true;
        }

        private static bool ExpressionHasEqualityThroughAliases(
            IoTExpr expression,
            string targetIdentifier,
            string expectedValue,
            DeviceScenarioModel model)
        {
            var binary = expression as IoTExpr.Binary;
            if (binary == null) return false;
            if (binary.Operator == "==")
            {
                var left = binary.Left as IoTExpr.Variable;
                var right = binary.Right as IoTExpr.Variable;
                string literal;
                if (left != null &&
                    VariableResolvesToIdentifier(left.Name, targetIdentifier, model,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase)) &&
                    TryResolveScenarioLiteral(binary.Right, model,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase), out literal) &&
                    ScenarioValuesEquivalent(literal, expectedValue))
                    return true;
                if (right != null &&
                    VariableResolvesToIdentifier(right.Name, targetIdentifier, model,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase)) &&
                    TryResolveScenarioLiteral(binary.Left, model,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase), out literal) &&
                    ScenarioValuesEquivalent(literal, expectedValue))
                    return true;
            }
            return ExpressionHasEqualityThroughAliases(binary.Left, targetIdentifier, expectedValue, model) ||
                ExpressionHasEqualityThroughAliases(binary.Right, targetIdentifier, expectedValue, model);
        }

        private sealed class ScenarioDependencyGraph
        {
            public readonly Dictionary<string, IList<IList<string>>> Definitions =
                new Dictionary<string, IList<IList<string>>>(StringComparer.OrdinalIgnoreCase);
        }

        private static ScenarioDependencyGraph BuildScenarioDependencyGraph(DeviceScenarioModel model)
        {
            var graph = new ScenarioDependencyGraph();
            if (model == null || model.Rules == null) return graph;

            foreach (var rule in model.Rules)
            {
                foreach (var action in rule.Actions)
                {
                    if (string.IsNullOrWhiteSpace(action.TargetId)) continue;
                    IList<IList<string>> definitions;
                    if (!graph.Definitions.TryGetValue(action.TargetId, out definitions))
                    {
                        definitions = new List<IList<string>>();
                        graph.Definitions[action.TargetId] = definitions;
                    }
                    // A variable assignment depends both on its right-hand
                    // expression and on the branch condition that makes the
                    // assignment active. Include both in the same definition
                    // edge set so aliases assigned constants inside IF blocks
                    // still retain their upstream control dependencies.
                    var identifiers = new List<string>();
                    CollectExpressionIdentifiers(action.Expression, identifiers);
                    if (rule.Condition != null)
                        CollectRequiredConditionIdentifiers(rule.Condition.Expression, identifiers);
                    definitions.Add(identifiers
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList());
                }
            }
            return graph;
        }

        private static void CollectExpressionIdentifiers(IoTExpr expression, IList<string> identifiers)
        {
            if (expression == null) return;
            var variable = expression as IoTExpr.Variable;
            if (variable != null) { identifiers.Add(variable.Name); return; }
            var unary = expression as IoTExpr.Unary;
            if (unary != null) { CollectExpressionIdentifiers(unary.Expression, identifiers); return; }
            var binary = expression as IoTExpr.Binary;
            if (binary != null)
            {
                CollectExpressionIdentifiers(binary.Left, identifiers);
                CollectExpressionIdentifiers(binary.Right, identifiers);
                return;
            }
            var call = expression as IoTExpr.Call;
            if (call != null)
                foreach (var argument in call.Arguments)
                    CollectExpressionIdentifiers(argument, identifiers);
        }

        // Return only identifiers that are structurally required by a
        // condition. In an OR expression neither side is individually
        // required, so retain only identifiers required by both branches.
        // This prevents a conditional constant assignment from being linked
        // to an unrelated control mentioned in an alternative OR branch.
        private static void CollectRequiredConditionIdentifiers(
            IoTExpr expression,
            IList<string> identifiers)
        {
            if (expression == null) return;

            var variable = expression as IoTExpr.Variable;
            if (variable != null)
            {
                identifiers.Add(variable.Name);
                return;
            }

            var unary = expression as IoTExpr.Unary;
            if (unary != null)
            {
                CollectRequiredConditionIdentifiers(unary.Expression, identifiers);
                return;
            }

            var binary = expression as IoTExpr.Binary;
            if (binary == null) return;

            if (binary.Operator == "|" || binary.Operator == "||")
            {
                var left = new List<string>();
                var right = new List<string>();
                CollectRequiredConditionIdentifiers(binary.Left, left);
                CollectRequiredConditionIdentifiers(binary.Right, right);
                foreach (var id in left.Distinct(StringComparer.OrdinalIgnoreCase))
                    if (right.Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase)))
                        identifiers.Add(id);
                return;
            }

            CollectRequiredConditionIdentifiers(binary.Left, identifiers);
            CollectRequiredConditionIdentifiers(binary.Right, identifiers);
        }

        private static bool GraphVariableDependsOnIdentifier(
            string variableName,
            string targetIdentifier,
            ScenarioDependencyGraph graph,
            ISet<string> visiting,
            int depth)
        {
            if (string.Equals(variableName, targetIdentifier, StringComparison.OrdinalIgnoreCase))
                return true;
            if (graph == null || depth >= 16 || !visiting.Add(variableName)) return false;

            IList<IList<string>> definitions;
            if (!graph.Definitions.TryGetValue(variableName, out definitions) || definitions.Count == 0)
                return false;

            // Every assignment definition must retain a path to the target.
            // A single unrelated/unknown definition makes the link ambiguous.
            foreach (var definition in definitions)
            {
                bool definitionDepends = false;
                foreach (var identifier in definition)
                {
                    if (GraphVariableDependsOnIdentifier(identifier, targetIdentifier, graph,
                        new HashSet<string>(visiting, StringComparer.OrdinalIgnoreCase), depth + 1))
                    {
                        definitionDepends = true;
                        break;
                    }
                }
                if (!definitionDepends) return false;
            }
            return true;
        }

        private static bool VariableResolvesToIdentifier(
            string variableName,
            string targetIdentifier,
            DeviceScenarioModel model,
            ISet<string> visiting)
        {
            if (string.Equals(variableName, targetIdentifier, StringComparison.OrdinalIgnoreCase))
                return true;
            if (visiting != null && visiting.Contains(variableName)) return false;
            return GraphVariableDependsOnIdentifier(variableName, targetIdentifier,
                BuildScenarioDependencyGraph(model),
                visiting == null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : visiting, 0);
        }

        // Resolve data-flow links through a bounded graph. This answers only
        // whether a dependency path exists; it does not infer boolean implication.
        private static bool ScenarioExpressionDependsOnIdentifier(
            IoTExpr expression,
            string targetIdentifier,
            DeviceScenarioModel model,
            ISet<string> visiting)
        {
            if (expression == null || model == null) return false;
            var identifiers = new List<string>();
            CollectExpressionIdentifiers(expression, identifiers);
            var graph = BuildScenarioDependencyGraph(model);
            foreach (var identifier in identifiers)
                if (GraphVariableDependsOnIdentifier(identifier, targetIdentifier, graph,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0))
                    return true;
            return false;
        }

        private static IList<string> GetEqualityValuesThroughAliases(
            IoTExpr expression,
            string identifier,
            DeviceScenarioModel model)
        {
            var values = new List<string>();
            CollectEqualityValuesThroughAliases(expression, identifier, model, values);
            return values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void CollectEqualityValuesThroughAliases(
            IoTExpr expression,
            string identifier,
            DeviceScenarioModel model,
            IList<string> values)
        {
            var binary = expression as IoTExpr.Binary;
            if (binary == null) return;
            if (binary.Operator == "==")
            {
                var left = binary.Left as IoTExpr.Variable;
                var right = binary.Right as IoTExpr.Variable;
                string literal;
                if (left != null &&
                    VariableResolvesToIdentifier(left.Name, identifier, model,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase)) &&
                    TryResolveScenarioLiteral(binary.Right, model,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase), out literal))
                    values.Add(literal);
                if (right != null &&
                    VariableResolvesToIdentifier(right.Name, identifier, model,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase)) &&
                    TryResolveScenarioLiteral(binary.Left, model,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase), out literal))
                    values.Add(literal);
            }
            CollectEqualityValuesThroughAliases(binary.Left, identifier, model, values);
            CollectEqualityValuesThroughAliases(binary.Right, identifier, model, values);
        }

        private static bool TryGetEqualityValue(IoTExpr expression, string identifier, out string value)
        {
            value = null;
            var binary = expression as IoTExpr.Binary;
            if (binary == null) return false;
            if (binary.Operator == "==")
            {
                var left = binary.Left as IoTExpr.Variable;
                var right = binary.Right as IoTExpr.Variable;
                string literal;
                if (left != null && string.Equals(left.Name, identifier, StringComparison.OrdinalIgnoreCase) &&
                    TryGetLiteral(binary.Right, out literal)) { value = literal; return true; }
                if (right != null && string.Equals(right.Name, identifier, StringComparison.OrdinalIgnoreCase) &&
                    TryGetLiteral(binary.Left, out literal)) { value = literal; return true; }
            }
            return TryGetEqualityValue(binary.Left, identifier, out value) ||
                TryGetEqualityValue(binary.Right, identifier, out value);
        }

        private bool TryAddReverseScenarioPlan(ScenarioPlan plan, ScenarioAction target)
        {
            var targetState = _devices.Get(target.DeviceId, target.WidgetId);

            if (targetState == null || _scenarioAnalyzer == null || !_scenarioAnalyzer.HasRules)
                return false;

            var analysis = _scenarioAnalyzer.AnalyzeControl(targetState, target.Value, _devices);
            if (analysis == null || string.IsNullOrEmpty(analysis.ActuatorVariable))
                return false;

            for (var i = 0; i < analysis.Dependencies.Count; i++)
                AddScenarioDependency(plan, target.DeviceId, analysis.Dependencies[i]);

            plan.Actions.Add(target);
            return true;
        }

        private bool TryAddExpressionDependencyPlan(ScenarioPlan plan, ScenarioAction target)
        {
            if (_scenarioAnalyzer == null || _scenarioAnalyzer.LanguageModel == null ||
                !_scenarioAnalyzer.LanguageModel.Valid || target == null)
                return false;

            var targetMatches = _devices.GetWidgetsById(target.WidgetId);
            if (targetMatches.Count != 1 ||
                !string.Equals(targetMatches[0].DeviceId, target.DeviceId, StringComparison.OrdinalIgnoreCase))
                return false;

            var model = _scenarioAnalyzer.LanguageModel;
            var variables = BuildScenarioVariableContext(model);

            var candidates = new List<IList<ScenarioAction>>();
            foreach (var rule in model.Rules)
            {
                var matchingAction = rule.Actions.FirstOrDefault(a =>
                    string.Equals(a.TargetId, target.WidgetId, StringComparison.OrdinalIgnoreCase));
                if (matchingAction == null)
                    continue;

                var context = new IoTScenarioEvaluationContext(variables);
                var actionValue = IoTScenarioEvaluator.Evaluate(matchingAction.Expression, context);
                if (!ScenarioValueMatches(actionValue, target.Value))
                    continue;

                if (IoTScenarioEvaluator.IsTruthy(IoTScenarioEvaluator.Evaluate(rule.Condition.Expression, context)))
                {
                    candidates.Add(new List<ScenarioAction>());
                    continue;
                }

                var controllable = new List<WidgetState>();
                foreach (var id in rule.Condition.Identifiers)
                {
                    var matches = _devices.GetWidgetsById(id);
                    if (matches.Count != 1 || !IsUserControl(matches[0]))
                        continue;
                    if (!variables.ContainsKey(id))
                        continue;
                    controllable.Add(matches[0]);
                }

                // Bound the search: scenario expressions with many controls are
                // not guessed or exhaustively enumerated on the Lumia.
                if (controllable.Count == 0 || controllable.Count > 8)
                    continue;

                var bestCount = int.MaxValue;
                var bestPlans = new List<IList<ScenarioAction>>();
                var combinations = 1 << controllable.Count;
                for (var mask = 0; mask < combinations; mask++)
                {
                    var simulated = new Dictionary<string, string>(variables, StringComparer.OrdinalIgnoreCase);
                    var proposed = new List<ScenarioAction>();
                    for (var bit = 0; bit < controllable.Count; bit++)
                    {
                        var widget = controllable[bit];
                        var desired = (mask & (1 << bit)) != 0 ? "1" : "0";
                        simulated[widget.Id] = desired;
                        if (!ScenarioValuesEquivalent(widget.Value, desired))
                        {
                            proposed.Add(new ScenarioAction
                            {
                                Command = widget.DeviceId + "/" + widget.Id + "=" + desired,
                                DeviceId = widget.DeviceId,
                                WidgetId = widget.Id,
                                Value = desired,
                                Reason = "Предварительное условие сценария: " +
                                    (string.IsNullOrWhiteSpace(widget.Description) ? widget.Id : widget.Description),
                                IsDependency = true
                            });
                        }
                    }

                    var simulatedContext = new IoTScenarioEvaluationContext(simulated);
                    if (!IoTScenarioEvaluator.IsTruthy(IoTScenarioEvaluator.Evaluate(rule.Condition.Expression, simulatedContext)))
                        continue;

                    if (proposed.Count < bestCount)
                    {
                        bestCount = proposed.Count;
                        bestPlans.Clear();
                    }
                    if (proposed.Count == bestCount)
                        bestPlans.Add(proposed);
                }

                if (bestPlans.Count == 1)
                    candidates.Add(bestPlans[0]);
                else if (bestPlans.Count > 1)
                    continue;
            }

            if (candidates.Count == 0)
                return false;

            var minimum = candidates.Min(x => x.Count);
            var best = candidates.Where(x => x.Count == minimum)
                .GroupBy(x => string.Join("|", x.OrderBy(a => a.DeviceId).ThenBy(a => a.WidgetId)
                    .Select(a => a.DeviceId + "/" + a.WidgetId + "=" + a.Value).ToArray()))
                .Select(x => x.First()).ToList();

            if (best.Count != 1)
                return false;

            foreach (var dependency in best[0])
            {
                if (!ContainsAction(plan, dependency.DeviceId, dependency.WidgetId, dependency.Value))
                    plan.Actions.Add(dependency);
            }
            if (!ContainsAction(plan, target.DeviceId, target.WidgetId, target.Value))
                plan.Actions.Add(target);
            return true;
        }

        private Dictionary<string, string> BuildScenarioVariableContext(DeviceScenarioModel model)
        {
            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (model == null || model.Identifiers == null) return variables;

            var internalIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in model.Identifiers)
            {
                var matches = _devices.GetWidgetsById(id);
                if (matches.Count == 1)
                    variables[id] = matches[0].Value ?? string.Empty;
                else if (matches.Count == 0)
                    internalIdentifiers.Add(id);
                // Duplicate widget IDs are deliberately left unresolved.
            }

            // Resolve internal IoTManager variables from the current device
            // snapshot. Each variable is added only if all its definitions
            // evaluate to the same known value; iterate to reveal multi-hop
            // assignment chains without overwriting live widget values.
            for (var pass = 0; pass < 12 && internalIdentifiers.Count > 0; pass++)
            {
                bool changed = false;
                foreach (var id in internalIdentifiers.ToList())
                {
                    var definitions = model.Rules.SelectMany(r => r.Actions)
                        .Where(a => string.Equals(a.TargetId, id, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (definitions.Count == 0) continue;

                    var resolved = new List<string>();
                    bool complete = true;
                    bool hasActiveDefinition = false;
                    var context = new IoTScenarioEvaluationContext(
                        new Dictionary<string, string>(variables, StringComparer.OrdinalIgnoreCase));
                    foreach (var rule in model.Rules)
                    {
                        foreach (var definition in rule.Actions.Where(a =>
                            string.Equals(a.TargetId, id, StringComparison.OrdinalIgnoreCase)))
                        {
                            // An assignment inside IF/ELSE is usable only when
                            // its branch condition is known to be active. Do not
                            // treat an inactive branch as a competing definition.
                            var condition = rule.Condition == null
                                ? null
                                : IoTScenarioEvaluator.Evaluate(rule.Condition.Expression, context);
                            if (condition != null && condition is IoTValue.Unknown)
                            {
                                complete = false;
                                break;
                            }
                            if (condition != null && !IoTScenarioEvaluator.IsTruthy(condition))
                                continue;

                            hasActiveDefinition = true;
                            var value = IoTScenarioEvaluator.Evaluate(definition.Expression, context);
                            string formatted;
                            if (!TryFormatScenarioValue(value, out formatted))
                            {
                                complete = false;
                                break;
                            }
                            resolved.Add(formatted);
                        }
                        if (!complete) break;
                    }

                    var unique = resolved.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (complete && hasActiveDefinition && unique.Count == 1)
                    {
                        variables[id] = unique[0];
                        internalIdentifiers.Remove(id);
                        changed = true;
                        break;
                    }
                }
                if (!changed) break;
            }
            return variables;
        }

        private static bool TryFormatScenarioValue(IoTValue value, out string formatted)
        {
            formatted = null;
            if (value == null || value is IoTValue.Unknown) return false;
            var number = value as IoTValue.Number;
            if (number != null)
            {
                formatted = number.Value.ToString("0.################", System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            var text = value as IoTValue.Text;
            if (text != null) { formatted = text.Value ?? string.Empty; return true; }
            var boolean = value as IoTValue.BooleanValue;
            if (boolean != null) { formatted = boolean.Value ? "1" : "0"; return true; }
            return false;
        }

        private static bool ScenarioValueMatches(IoTValue value, string expected)
        {
            if (value == null || value is IoTValue.Unknown)
                return false;
            var number = value as IoTValue.Number;
            if (number != null)
            {
                double wanted;
                return double.TryParse((expected ?? string.Empty).Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out wanted) &&
                    Math.Abs(number.Value - wanted) < 0.000001;
            }
            var text = value as IoTValue.Text;
            if (text != null)
                return string.Equals(text.Value, expected, StringComparison.OrdinalIgnoreCase);
            var boolean = value as IoTValue.BooleanValue;
            if (boolean != null)
                return boolean.Value == (expected == "1" || string.Equals(expected, "true", StringComparison.OrdinalIgnoreCase));
            return false;
        }

        private static bool ScenarioValuesEquivalent(string first, string second)
        {
            double a, b;
            if (double.TryParse((first ?? string.Empty).Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out a) &&
                double.TryParse((second ?? string.Empty).Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out b))
                return Math.Abs(a - b) < 0.000001;
            return string.Equals((first ?? string.Empty).Trim(), (second ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        private void AddScenarioDependency(ScenarioPlan plan, string deviceId, ScenarioDependency dependency)
        {
            if (dependency == null || string.IsNullOrEmpty(dependency.Variable) ||
                ContainsAction(plan, deviceId, dependency.Variable, dependency.Value))
                return;

            var state = _devices.Get(deviceId, dependency.Variable);
            if (state == null || IsValue(state.Value, dependency.Value))
                return;

            plan.Actions.Add(new ScenarioAction
            {
                Command = deviceId + "/" + dependency.Variable + "=" + dependency.Value,
                DeviceId = deviceId,
                WidgetId = dependency.Variable,
                Value = dependency.Value,
                Reason = dependency.Reason,
                IsDependency = true
            });
        }

        private static bool IsUserControl(WidgetState state)
        {
            if (state == null) return false;
            var type = (state.WidgetType ?? string.Empty).ToLowerInvariant();
            return type == "vbutton" || type == "vbtn" || type == "buttonin" ||
                   type == "toggle" || type == "switch" || type == "checkbox" ||
                   type == "range" || type == "slider";
        }

        private void AddAutomationDependency(ScenarioPlan plan, ScenarioAction target)
        {
            var targetState = _devices.Get(target.DeviceId, target.WidgetId);
            if (!IsOperationalAction(target, targetState)) return;
            if (targetState != null && (HasAny(targetState.Description, AutomationWords) || HasAny(targetState.Description, ManualWords))) return;

            var widgets = _devices.GetWidgetsSnapshot();
            var targetPage = targetState == null ? string.Empty : (targetState.Page ?? string.Empty);
            var targetDescription = targetState == null ? string.Empty : (targetState.Description ?? string.Empty);
            WidgetState candidate = null;
            var bestScore = 0;

            for (var i = 0; i < widgets.Count; i++)
            {
                var w = widgets[i];
                if (string.Equals(w.DeviceId, target.DeviceId, StringComparison.OrdinalIgnoreCase) && string.Equals(w.Id, target.WidgetId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!HasAny(w.Description, AutomationWords)) continue;
                var score = 0;
                if (!string.IsNullOrEmpty(targetPage) && string.Equals(w.Page, targetPage, StringComparison.OrdinalIgnoreCase)) score += 5;
                if (HasSharedObjectWords(targetDescription, w.Description)) score += 5;
                if (string.Equals(w.DeviceId, target.DeviceId, StringComparison.OrdinalIgnoreCase)) score += 2;
                if (HasAny(w.Description, ManualWords)) score += 2;
                if (score == 0) continue;
                if (score > bestScore) { bestScore = score; candidate = w; }
            }

            if (candidate == null || !IsValue(candidate.Value, "0")) return;
            if (ContainsAction(plan, candidate.DeviceId, candidate.Id, "1")) return;

            plan.Actions.Add(new ScenarioAction
            {
                Command = candidate.DeviceId + "/" + candidate.Id + "=1",
                DeviceId = candidate.DeviceId,
                WidgetId = candidate.Id,
                Value = "1",
                Reason = "Отключить автоматическое управление перед ручным действием",
                IsDependency = true
            });
        }

        private void AddInterlockDependency(ScenarioPlan plan, ScenarioAction target)
        {
            if (!IsValue(target.Value, "1")) return;
            var targetState = _devices.Get(target.DeviceId, target.WidgetId);
            if (!IsOperationalAction(target, targetState) || targetState == null || string.IsNullOrWhiteSpace(targetState.Description)) return;
            var oppositeWords = FindOppositeWords(targetState.Description);
            if (oppositeWords == null) return;

            var widgets = _devices.GetWidgetsSnapshot();
            for (var i = 0; i < widgets.Count; i++)
            {
                var w = widgets[i];
                if (!string.Equals(w.DeviceId, target.DeviceId, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(w.Id, target.WidgetId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!HasAny(w.Description, oppositeWords)) continue;
                if (!HasSharedObjectWords(targetState.Description, w.Description)) continue;
                if (!IsValue(w.Value, "1")) continue;
                if (ContainsAction(plan, w.DeviceId, w.Id, "0")) continue;

                plan.Actions.Add(new ScenarioAction
                {
                    Command = w.DeviceId + "/" + w.Id + "=0",
                    DeviceId = w.DeviceId,
                    WidgetId = w.Id,
                    Value = "0",
                    Reason = "Отключить противоположное действие перед включением требуемого",
                    IsDependency = true
                });
            }
        }

        private static ScenarioAction ParseAction(string command)
        {
            var parts = (command ?? string.Empty).Split(new[] { '=' }, 2);
            if (parts.Length != 2) return null;
            var target = parts[0].Trim().Trim('/');
            var value = parts[1].Trim();
            var slash = target.IndexOf('/');
            if (slash <= 0 || slash >= target.Length - 1) return null;
            return new ScenarioAction { Command = target + "=" + value, DeviceId = target.Substring(0, slash), WidgetId = target.Substring(slash + 1), Value = value };
        }

        private static IList<string> SplitCommands(string command)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(command)) return result;
            var normalized = command.Replace("&&", ";").Replace("\r", "");
            var parts = normalized.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++) { var value = parts[i].Trim(); if (!string.IsNullOrEmpty(value)) result.Add(value); }
            return result;
        }

        private static bool IsOperationalAction(ScenarioAction action, WidgetState state)
        {
            if (state == null) return false;
            if (HasAny(state.Description, ActionWords)) return true;
            if (string.Equals(state.WidgetType, "ButtonOut", StringComparison.OrdinalIgnoreCase) || string.Equals(state.WidgetType, "toggle", StringComparison.OrdinalIgnoreCase)) return true;
            return HasAny(action.WidgetId, ActionWords);
        }

        private static bool HasAny(string text, string[] words)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var value = text.ToLowerInvariant();
            for (var i = 0; i < words.Length; i++) if (value.Contains(words[i])) return true;
            return false;
        }

        private static bool IsValue(string current, string expected)
        {
            return string.Equals((current ?? string.Empty).Trim(), (expected ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsAction(ScenarioPlan plan, string deviceId, string widgetId, string value)
        {
            for (var i = 0; i < plan.Actions.Count; i++)
            {
                var item = plan.Actions[i];
                if (string.Equals(item.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) && string.Equals(item.WidgetId, widgetId, StringComparison.OrdinalIgnoreCase) && string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string[] FindOppositeWords(string description)
        {
            if (HasAny(description, OpenWords)) return CloseWords;
            if (HasAny(description, CloseWords)) return OpenWords;
            return null;
        }

        private static bool HasSharedObjectWords(string first, string second)
        {
            var a = TokenizeObjectWords(first);
            var b = TokenizeObjectWords(second);
            for (var i = 0; i < a.Count; i++) for (var j = 0; j < b.Count; j++) if (string.Equals(a[i], b[j], StringComparison.Ordinal) && a[i].Length >= 3) return true;
            return false;
        }

        private static IList<string> TokenizeObjectWords(string text)
        {
            var result = new List<string>();
            var value = (text ?? string.Empty).ToLowerInvariant();
            var separators = new[] { ' ', '\t', '\r', '\n', '-', '_', '/', '(', ')', ':', ',', '.', '«', '»' };
            var parts = value.Split(separators, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var word = parts[i];
                if (word.Length < 3) continue;
                if (HasAny(word, OpenWords) || HasAny(word, CloseWords) || HasAny(word, AutomationWords) || HasAny(word, ManualWords)) continue;
                result.Add(word);
            }
            return result;
        }
    }
}
