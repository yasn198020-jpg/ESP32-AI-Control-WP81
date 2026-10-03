using System;
using System.Collections.Generic;
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

        public ScenarioPlan BuildPlan(string command)
        {
            var plan = new ScenarioPlan();
            var commands = SplitCommands(command);

            for (var i = 0; i < commands.Count; i++)
            {
                var action = ParseAction(commands[i]);
                if (action == null)
                    throw new ArgumentException("Формат команды: device/widget=value", "command");

                if (!TryAddReverseScenarioPlan(plan, action))
                {
                    AddAutomationDependency(plan, action);
                    AddInterlockDependency(plan, action);
                    plan.Actions.Add(action);
                }
            }

            if (plan.Actions.Count == 0)
                throw new ArgumentException("Пустой сценарий.", "command");

            return plan;
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
