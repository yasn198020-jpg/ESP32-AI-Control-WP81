using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core.Scenarios
{
    public sealed class ScenarioCondition
    {
        public string Variable { get; set; }
        public string Operator { get; set; }
        public string Value { get; set; }

        public string Key
        {
            get { return (Variable ?? string.Empty) + (Operator ?? string.Empty) + (Value ?? string.Empty); }
        }
    }

    public sealed class ScenarioAssignment
    {
        public string Variable { get; set; }
        public string Operator { get; set; }
        public string Value { get; set; }
        public IList<ScenarioCondition> Conditions { get; private set; }

        public ScenarioAssignment()
        {
            Conditions = new List<ScenarioCondition>();
        }
    }

    internal sealed class ScenarioRule
    {
        public IList<ScenarioCondition> Conditions { get; private set; }
        public IList<ScenarioAssignment> Assignments { get; private set; }

        public ScenarioRule()
        {
            Conditions = new List<ScenarioCondition>();
            Assignments = new List<ScenarioAssignment>();
        }
    }

    public sealed class ScenarioDependency
    {
        public string Variable { get; set; }
        public string Value { get; set; }
        public string Reason { get; set; }
    }

    public sealed class ScenarioAnalysis
    {
        public string TargetVariable { get; set; }
        public string TargetValue { get; set; }
        public string ActuatorVariable { get; set; }
        public string ActuatorValue { get; set; }
        public IList<ScenarioDependency> Dependencies { get; private set; }

        public ScenarioAnalysis()
        {
            Dependencies = new List<ScenarioDependency>();
        }
    }

    public sealed class IoTManagerScenarioAnalyzer
    {
        private static readonly Regex ConditionRegex = new Regex(
            @"([A-Za-z_][A-Za-z0-9_]*)\s*(==|!=|<=|>=|<|>)\s*(-?[0-9]+(?:\.[0-9]+)?)",
            RegexOptions.IgnoreCase);

        private static readonly Regex AssignmentRegex = new Regex(
            @"([A-Za-z_][A-Za-z0-9_]*)\s*(?::=|(?<![=!:<>])=(?!=))\s*(-?[0-9]+(?:\.[0-9]+)?)",
            RegexOptions.IgnoreCase);

        private readonly List<ScenarioRule> _rules = new List<ScenarioRule>();

        public IoTManagerScenarioAnalyzer(string script)
        {
            Load(script);
        }

        public bool HasRules
        {
            get { return _rules.Count > 0; }
        }

        public void Load(string script)
        {
            _rules.Clear();

            if (string.IsNullOrWhiteSpace(script))
                return;

            var normalized = ExtractScenario(script);
            ParseBlock(normalized, 0, normalized.Length, new List<ScenarioCondition>(), _rules);
        }

        public ScenarioAnalysis AnalyzeControl(
            WidgetState targetState,
            string targetValue,
            DeviceRepository devices)
        {
            if (!HasRules || targetState == null || devices == null)
                return null;

            var analysis = FindActuatorPath(targetState, targetValue, devices);
            if (analysis == null)
                return null;

            BuildDependencies(
                analysis,
                devices,
                targetState.DeviceId,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));

            return analysis;
        }

        private ScenarioAnalysis FindActuatorPath(
            WidgetState targetState,
            string targetValue,
            DeviceRepository devices)
        {
            var wantedOpen = IsValue(targetValue, "1");
            var targetObject = targetState.Description ?? string.Empty;
            var expectedDirection = wantedOpen ? "open" : "close";
            var bestScore = -1;
            ScenarioAssignment best = null;
            WidgetState bestWidget = null;

            for (var r = 0; r < _rules.Count; r++)
            {
                var rule = _rules[r];

                if (!HasCondition(rule.Conditions, targetState.Id, targetValue))
                    continue;

                for (var a = 0; a < rule.Assignments.Count; a++)
                {
                    var assignment = rule.Assignments[a];
                    if (!IsValue(assignment.Value, "1"))
                        continue;

                    var widget = devices.Get(targetState.DeviceId, assignment.Variable);
                    if (widget == null || !IsActuator(widget, expectedDirection))
                        continue;

                    var score = 0;

                    if (HasSharedObjectWords(targetObject, widget.Description))
                        score += 10;

                    if (IsValue(assignment.Value, "1"))
                        score += 2;

                    if (string.Equals(widget.Page, targetState.Page, StringComparison.OrdinalIgnoreCase))
                        score += 2;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = assignment;
                        bestWidget = widget;
                    }
                }
            }

            if (best == null || bestWidget == null)
                return null;

            return new ScenarioAnalysis
            {
                TargetVariable = targetState.Id,
                TargetValue = targetValue,
                ActuatorVariable = best.Variable,
                ActuatorValue = best.Value
            };
        }

        private void BuildDependencies(
            ScenarioAnalysis analysis,
            DeviceRepository devices,
            string deviceId,
            HashSet<string> visiting)
        {
            if (analysis == null || string.IsNullOrEmpty(analysis.ActuatorVariable))
                return;

            BuildDependenciesForAssignment(
                analysis,
                analysis.ActuatorVariable,
                analysis.ActuatorValue,
                devices,
                deviceId,
                visiting,
                true);
        }

        private void BuildDependenciesForAssignment(
            ScenarioAnalysis analysis,
            string variable,
            string value,
            DeviceRepository devices,
            string deviceId,
            HashSet<string> visiting,
            bool requireTargetCondition)
        {
            var key = (variable ?? string.Empty) + "=" + (value ?? string.Empty);
            if (!visiting.Add(key))
                return;

            var candidates = FindAssignments(variable, value);
            ScenarioAssignment selected = null;

            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];

                if (!requireTargetCondition ||
                    HasCondition(candidate.Conditions, analysis.TargetVariable, analysis.TargetValue))
                {
                    selected = candidate;
                    break;
                }
            }

            if (selected == null && candidates.Count > 0 && !requireTargetCondition)
                selected = candidates[0];

            if (selected == null)
            {
                visiting.Remove(key);
                return;
            }

            for (var i = 0; i < selected.Conditions.Count; i++)
            {
                var condition = selected.Conditions[i];

                if (!string.Equals(condition.Operator, "==", StringComparison.Ordinal))
                    continue;

                if (string.Equals(condition.Variable, analysis.TargetVariable, StringComparison.OrdinalIgnoreCase) &&
                    IsValue(condition.Value, analysis.TargetValue))
                    continue;

                var state = devices.Get(deviceId, condition.Variable);

                if (state != null && IsUserControllable(state))
                {
                    AddDependency(
                        analysis,
                        state,
                        condition.Value,
                        "Условие сценария для " + GetDescription(variable, devices, deviceId));
                    continue;
                }

                BuildDependenciesForAssignment(
                    analysis,
                    condition.Variable,
                    condition.Value,
                    devices,
                    deviceId,
                    visiting,
                    false);
            }

            visiting.Remove(key);
        }

        private IList<ScenarioAssignment> FindAssignments(string variable, string value)
        {
            var result = new List<ScenarioAssignment>();

            for (var r = 0; r < _rules.Count; r++)
            {
                var rule = _rules[r];

                for (var a = 0; a < rule.Assignments.Count; a++)
                {
                    var assignment = rule.Assignments[a];

                    if (string.Equals(assignment.Variable, variable, StringComparison.OrdinalIgnoreCase) &&
                        IsValue(assignment.Value, value))
                        result.Add(assignment);
                }
            }

            return result;
        }

        private static void AddDependency(
            ScenarioAnalysis analysis,
            WidgetState state,
            string value,
            string reason)
        {
            if (analysis == null || state == null)
                return;

            for (var i = 0; i < analysis.Dependencies.Count; i++)
            {
                var item = analysis.Dependencies[i];
                if (string.Equals(item.Variable, state.Id, StringComparison.OrdinalIgnoreCase) &&
                    IsValue(item.Value, value))
                    return;
            }

            analysis.Dependencies.Add(new ScenarioDependency
            {
                Variable = state.Id,
                Value = value,
                Reason = reason
            });
        }

        private static bool IsActuator(WidgetState state, string direction)
        {
            if (state == null)
                return false;

            var type = (state.WidgetType ?? string.Empty).ToLowerInvariant();
            if (type != "buttonout" && type != "button" && type != "action")
                return false;

            var description = (state.Description ?? string.Empty).ToLowerInvariant();

            if (direction == "open")
                return ContainsAny(description, new[] { "откр", "open", "подним", "отпер" });

            return ContainsAny(description, new[] { "закр", "close", "опуст", "запер" });
        }

        private static bool IsUserControllable(WidgetState state)
        {
            if (state == null)
                return false;

            var type = (state.WidgetType ?? string.Empty).ToLowerInvariant();

            return type == "vbutton" ||
                   type == "vbtn" ||
                   type == "buttonin" ||
                   type == "toggle" ||
                   type == "switch" ||
                   type == "checkbox" ||
                   type == "range" ||
                   type == "slider";
        }

        private static bool HasCondition(
            IList<ScenarioCondition> conditions,
            string variable,
            string value)
        {
            if (conditions == null)
                return false;

            for (var i = 0; i < conditions.Count; i++)
            {
                var c = conditions[i];

                if (string.Equals(c.Variable, variable, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(c.Operator, "==", StringComparison.Ordinal) &&
                    IsValue(c.Value, value))
                    return true;
            }

            return false;
        }

        private static bool IsValue(string first, string second)
        {
            return string.Equals(
                (first ?? string.Empty).Trim(),
                (second ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsAny(string text, string[] words)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            var value = text.ToLowerInvariant();

            for (var i = 0; i < words.Length; i++)
                if (value.Contains(words[i]))
                    return true;

            return false;
        }

        private static bool HasSharedObjectWords(string first, string second)
        {
            var a = Tokenize(first);
            var b = Tokenize(second);

            for (var i = 0; i < a.Count; i++)
                for (var j = 0; j < b.Count; j++)
                    if (a[i].Length >= 3 && string.Equals(a[i], b[j], StringComparison.Ordinal))
                        return true;

            return false;
        }

        private static IList<string> Tokenize(string text)
        {
            var result = new List<string>();
            var parts = (text ?? string.Empty).ToLowerInvariant().Split(
                new[] { ' ', '\t', '\r', '\n', '-', '_', '/', '(', ')', ':', ',', '.', '«', '»' },
                StringSplitOptions.RemoveEmptyEntries);

            for (var i = 0; i < parts.Length; i++)
            {
                var word = parts[i];

                if (word.Length < 3)
                    continue;

                if (ContainsAny(word, new[] { "откр", "закр", "open", "close", "автомат", "ручн" }))
                    continue;

                result.Add(word);
            }

            return result;
        }

        private static string GetDescription(
            string variable,
            DeviceRepository devices,
            string deviceId)
        {
            var state = devices.Get(deviceId, variable);
            return state == null || string.IsNullOrWhiteSpace(state.Description)
                ? variable
                : state.Description;
        }

        private static string ExtractScenario(string script)
        {
            var marker = script.IndexOf("scenario=>", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
                return script.Substring(marker + "scenario=>".Length);

            return script;
        }

        private void ParseBlock(
            string text,
            int start,
            int end,
            IList<ScenarioCondition> inherited,
            IList<ScenarioRule> output)
        {
            var pos = start;

            while (pos < end)
            {
                while (pos < end &&
                       (char.IsWhiteSpace(text[pos]) || text[pos] == ';'))
                    pos++;

                if (pos >= end)
                    break;

                if (text[pos] == '#')
                {
                    pos = SkipLine(text, pos, end);
                    continue;
                }

                if (StartsWithWord(text, pos, "if"))
                {
                    var ifStart = pos;
                    var then = FindThen(text, pos + 2, end);

                    if (then < 0)
                    {
                        pos = SkipStatement(text, pos, end);
                        continue;
                    }

                    var conditionText = text.Substring(pos + 2, then - (pos + 2));
                    var ownConditions = ParseConditions(conditionText);
                    var merged = MergeConditions(inherited, ownConditions);
                    var bodyStart = then + 4;

                    while (bodyStart < end && char.IsWhiteSpace(text[bodyStart]))
                        bodyStart++;

                    if (bodyStart < end && text[bodyStart] == '{')
                    {
                        var close = FindMatchingBrace(text, bodyStart, end);

                        if (close < 0)
                        {
                            pos = end;
                            continue;
                        }

                        ParseBlock(text, bodyStart + 1, close, merged, output);
                        pos = close + 1;
                    }
                    else
                    {
                        var statementEnd = FindStatementEnd(text, bodyStart, end);
                        var rule = CreateRule(merged, text.Substring(bodyStart, statementEnd - bodyStart));

                        if (rule.Assignments.Count > 0)
                            output.Add(rule);

                        pos = statementEnd + 1;
                    }

                    if (pos <= ifStart)
                        pos++;
                    continue;
                }

                var plainEnd = FindStatementEnd(text, pos, end);
                pos = plainEnd + 1;
            }
        }

        private static ScenarioRule CreateRule(
            IList<ScenarioCondition> conditions,
            string statement)
        {
            var rule = new ScenarioRule();

            for (var i = 0; i < conditions.Count; i++)
                rule.Conditions.Add(conditions[i]);

            var matches = AssignmentRegex.Matches(statement ?? string.Empty);

            for (var i = 0; i < matches.Count; i++)
            {
                rule.Assignments.Add(new ScenarioAssignment
                {
                    Variable = matches[i].Groups[1].Value,
                    Operator = matches[i].Value.Contains(":=") ? ":=" : "=",
                    Value = matches[i].Groups[2].Value
                });
            }

            for (var i = 0; i < rule.Assignments.Count; i++)
            {
                for (var c = 0; c < rule.Conditions.Count; c++)
                    rule.Assignments[i].Conditions.Add(rule.Conditions[c]);
            }

            return rule;
        }

        private static IList<ScenarioCondition> ParseConditions(string text)
        {
            var result = new List<ScenarioCondition>();
            var matches = ConditionRegex.Matches(text ?? string.Empty);

            for (var i = 0; i < matches.Count; i++)
            {
                result.Add(new ScenarioCondition
                {
                    Variable = matches[i].Groups[1].Value,
                    Operator = matches[i].Groups[2].Value,
                    Value = matches[i].Groups[3].Value
                });
            }

            return result;
        }

        private static IList<ScenarioCondition> MergeConditions(
            IList<ScenarioCondition> first,
            IList<ScenarioCondition> second)
        {
            var result = new List<ScenarioCondition>();

            if (first != null)
                for (var i = 0; i < first.Count; i++)
                    result.Add(first[i]);

            if (second != null)
                for (var i = 0; i < second.Count; i++)
                    result.Add(second[i]);

            return result;
        }

        private static int FindThen(string text, int start, int end)
        {
            for (var i = start; i + 4 <= end; i++)
            {
                if (char.ToLowerInvariant(text[i]) != 't' ||
                    char.ToLowerInvariant(text[i + 1]) != 'h' ||
                    char.ToLowerInvariant(text[i + 2]) != 'e' ||
                    char.ToLowerInvariant(text[i + 3]) != 'n')
                    continue;

                var beforeOk = i == start || !char.IsLetterOrDigit(text[i - 1]);
                var afterOk = i + 4 >= end || !char.IsLetterOrDigit(text[i + 4]);

                if (beforeOk && afterOk)
                    return i;
            }

            return -1;
        }

        private static int FindMatchingBrace(string text, int open, int end)
        {
            var depth = 0;

            for (var i = open; i < end; i++)
            {
                if (text[i] == '{')
                    depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return i;
                }
            }

            return -1;
        }

        private static int FindStatementEnd(string text, int start, int end)
        {
            for (var i = start; i < end; i++)
            {
                if (text[i] == ';' || text[i] == '\n')
                    return i;
            }

            return end;
        }

        private static int SkipStatement(string text, int start, int end)
        {
            return FindStatementEnd(text, start, end) + 1;
        }

        private static int SkipLine(string text, int start, int end)
        {
            return FindStatementEnd(text, start, end) + 1;
        }

        private static bool StartsWithWord(string text, int start, string word)
        {
            if (start < 0 || start + word.Length > text.Length)
                return false;

            if (!string.Equals(
                    text.Substring(start, word.Length),
                    word,
                    StringComparison.OrdinalIgnoreCase))
                return false;

            var beforeOk = start == 0 || !char.IsLetterOrDigit(text[start - 1]);
            var after = start + word.Length;

            return beforeOk && (after >= text.Length || !char.IsLetterOrDigit(text[after]));
        }
    }
}
