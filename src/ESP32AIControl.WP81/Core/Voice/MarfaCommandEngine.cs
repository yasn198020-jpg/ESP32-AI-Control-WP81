using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ESP32AIControl.WP81.Core.State;
using ESP32AIControl.WP81.Core.Scenarios;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core.Voice
{
    public enum MarfaCommandKind { Control, ReadValue, SmartRule, Clarify, Stop, NotFound }

    public sealed class MarfaActionItem
    {
        public string DeviceId { get; set; }
        public string WidgetId { get; set; }
        public string Value { get; set; }
        public string Command { get { return DeviceId + "/" + WidgetId + "=" + Value; } }
    }

    public sealed class MarfaCommandResult
    {
        public MarfaCommandKind Kind { get; set; }
        public string Reply { get; set; }
        public long DelayMs { get; set; }
        public bool NeedsConfirmation { get; set; }
        public List<MarfaActionItem> Actions { get; set; }
        public string Value { get; set; }
        public LocalScenarioDefinition Scenario { get; set; }
    }

    /// <summary>
    /// C# port of the platform-independent Marfa command layer for WP8.1.
    /// Speech capture, TTS, MQTT and execution stay in platform adapters.
    /// </summary>
    public sealed class MarfaCommandEngine
    {
        private sealed class Candidate
        {
            public WidgetState Widget;
            public int Score;
        }

        private readonly DeviceRepository _devices;
        private readonly MarfaAnalyticalEngine _analyticalEngine = new MarfaAnalyticalEngine();
        private TrainedCommandMatcher _trainedCommandMatcher;
        private readonly DialogContext _dialogContext = new DialogContext();
        private List<WidgetState> _pending = new List<WidgetState>();
        private List<WidgetState> _lastControlCandidates = new List<WidgetState>();
        private List<MarfaActionItem> _pendingResolvedActions = new List<MarfaActionItem>();
        private string _lastCommandText = string.Empty;
        private string _pendingValue = "1";
        private long _pendingDelay;
        private string _pendingOriginalText = string.Empty;
        private bool _pendingReadValue;
        private WidgetState _lastTarget;
        private string _lastValue = "1";

        public MarfaCommandEngine(DeviceRepository devices)
        {
            _devices = devices;
        }

        public void SetTrainedCommandRepository(ITrainedCommandRepository repository)
        {
            _trainedCommandMatcher = repository == null ? null : new TrainedCommandMatcher(repository);
        }

        public DialogTargetContext DialogSnapshot()
        {
            return _dialogContext.Snapshot();
        }

        public void ClearDialogContext()
        {
            _dialogContext.Clear();
            ClearPending();
            _lastTarget = null;
        }

        public MarfaCommandResult Parse(string command)
        {
            return Parse(command, _devices == null ? new List<WidgetState>() : _devices.GetWidgetsSnapshot());
        }

        public DialogTargetContext DialogSnapshot()
        {
            return _dialogContext.Snapshot();
        }

        public void ClearDialogContext()
        {
            _dialogContext.Clear();
            ClearPending();
            _lastTarget = null;
        }

        public MarfaCommandResult Parse(string command, IList<WidgetState> widgets)
        {
            _dialogContext.BeginTurn(command);
            var result = ParseCore(command, widgets);
            RememberDialogResult(command, result);
            return result;
        }

        private void RememberDialogResult(string command, MarfaCommandResult result)
        {
            if (result == null) return;
            var core = new LocalCommandResult
            {
                Action = result.Kind == MarfaCommandKind.Control ? LocalCommandAction.Control :
                    result.Kind == MarfaCommandKind.ReadValue ? LocalCommandAction.ReadValue :
                    result.Kind == MarfaCommandKind.SmartRule ? LocalCommandAction.SmartRule :
                    result.Kind == MarfaCommandKind.Clarify ? LocalCommandAction.Clarify :
                    result.Kind == MarfaCommandKind.Stop ? LocalCommandAction.Stop : LocalCommandAction.NotFound,
                Reply = result.Reply ?? string.Empty,
                DelayMs = result.DelayMs,
                Value = result.Value ?? string.Empty
            };
            if (result.Actions != null)
                foreach (var action in result.Actions)
                    core.ActionItems.Add(new LocalCommandActionItem
                    {
                        DeviceId = action.DeviceId, WidgetId = action.WidgetId, Value = action.Value
                    });
            if (result.Actions != null && result.Actions.Count > 0)
            {
                core.DeviceId = result.Actions[0].DeviceId;
                core.WidgetId = result.Actions[0].WidgetId;
                core.Value = result.Actions[0].Value;
                core.ActionDeviceId = result.Actions[0].DeviceId;
                core.ActionWidgetId = result.Actions[0].WidgetId;
                core.ActionValue = result.Actions[0].Value;
            }
            if (result.Scenario != null)
            {
                core.ScenarioPlan = result.Scenario;
                var firstAction = result.Scenario.Actions.FirstOrDefault();
                if (firstAction != null)
                {
                    core.ActionDeviceId = firstAction.DeviceId;
                    core.ActionWidgetId = firstAction.WidgetId;
                    core.ActionValue = firstAction.Value;
                }
            }
            if (result.Kind == MarfaCommandKind.Clarify)
                _dialogContext.SetPending(command);
            else
                _dialogContext.ClearPending();
            _dialogContext.Remember(command, core);
        }

        private MarfaCommandResult ParseCore(string command, IList<WidgetState> widgets)
        {
            var text = Normalize(command);
            MarfaCoreDiagnostics.Log("command=" + text);
            if (text.Length == 0) return Result(MarfaCommandKind.NotFound, "Я не услышала команду.");

            if (IsStop(text))
            {
                ClearPending();
                _lastTarget = null;
                return Result(MarfaCommandKind.Stop, "Стоп. Текущая команда отменена.");
            }

            if (HasNegation(text))
            {
                ClearPending();
                return Result(MarfaCommandKind.NotFound,
                    "В команде есть отрицание. Я ничего не буду выполнять. Скажите прямо, что нужно сделать.");
            }

            if (_pending.Count > 0 || !string.IsNullOrWhiteSpace(_pendingOriginalText))
                return ResolvePending(text, widgets);

            var followUp = TryParseFollowUp(text, widgets);
            if (followUp != null) return followUp;

            var smartScenario = MarfaSmartRuleParser.TryParse(text, _analyticalEngine, widgets);
            if (smartScenario != null)
                return new MarfaCommandResult
                {
                    Kind = MarfaCommandKind.SmartRule,
                    Reply = "Поняла правило: " + smartScenario.Title,
                    Scenario = smartScenario,
                    Actions = new List<MarfaActionItem>()
                };

            var trained = TryResolveTrainedCommand(text, widgets);
            if (trained != null) return trained;

            var split = MarfaMultiCommandSplitter.Split(text, widgets).ToList();
            if (split.Count > 1)
            {
                var combined = new List<MarfaActionItem>();
                long maxDelay = 0;
                foreach (var part in split)
                {
                    var item = ParseSingle(part, widgets);
                    if (item.Kind == MarfaCommandKind.Clarify)
                    {
                        _pendingResolvedActions = combined.ToList();
                        return item;
                    }
                    if (item.Kind != MarfaCommandKind.Control)
                    {
                        _pendingResolvedActions.Clear();
                        return Result(MarfaCommandKind.NotFound, "Не удалось однозначно разобрать составную команду.");
                    }
                    combined.AddRange(item.Actions);
                    maxDelay = Math.Max(maxDelay, item.DelayMs);
                }
                _pendingResolvedActions.Clear();
                return ControlResult(combined, maxDelay);
            }

            return ParseSingle(text, widgets);
        }

        private MarfaCommandResult TryParseFollowUp(string text, IList<WidgetState> widgets)
        {
            var action = DetectAction(text);
            var question = IsValueQuestion(text);

            if (action == null && !question)
            {
                var refined = TryRefineLastControlTarget(text);
                if (refined != null) return refined;
            }

            // A time-only follow-up repeats the last resolved control command.
            var previous = _dialogContext.Snapshot();
            if (action == null && !question && _lastTarget != null &&
                previous != null && !string.IsNullOrWhiteSpace(previous.ActionValue))
            {
                var delay = ParseDelay(text);
                if (delay > 0)
                {
                    var item = new MarfaActionItem
                    {
                        DeviceId = _lastTarget.DeviceId,
                        WidgetId = _lastTarget.Id,
                        Value = previous.ActionValue
                    };
                    return ControlResult(new[] { item }, delay,
                        "Запланировала повтор: " +
                        (string.IsNullOrWhiteSpace(_lastTarget.Description) ? _lastTarget.Id : _lastTarget.Description) +
                        " через " + FormatDelay(delay) + ".");
                }
            }

            // "А какая там температура?" refers to the last sensor when the
            // current phrase contains no new explicit sensor target.
            if (action == null && question && _lastTarget != null && IsSensor(_lastTarget) &&
                previous != null && previous.Intent == LocalCommandAction.ReadValue &&
                (text.Contains("там") || text.Contains("здесь") || text.Contains("это")))
            {
                var current = (widgets ?? new List<WidgetState>()).FirstOrDefault(w =>
                    string.Equals(w.DeviceId, _lastTarget.DeviceId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(w.Id, _lastTarget.Id, StringComparison.OrdinalIgnoreCase));
                if (current != null)
                {
                    var raw = string.IsNullOrWhiteSpace(current.Value) ? "значение пока неизвестно" :
                        MarfaSpeechTextFormatter.FormatValue(current.Value, current.Unit, current.Description);
                    var title = string.IsNullOrWhiteSpace(current.Description) ? current.Id : current.Description;
                    var result = Result(MarfaCommandKind.ReadValue, title + ": " + raw + ".");
                    result.Value = current.Value;
                    return result;
                }
            }
            return null;
        }

        private static bool HasOrdinalReference(string text)
        {
            return Regex.IsMatch(text ?? string.Empty,
                @"\b(перв|втор|трет|четверт|последн|номер\s*[123])");
        }

        private WidgetState ResolveReference(string text, IList<WidgetState> widgets)
        {
            var candidates = (widgets ?? new List<WidgetState>())
                .Where(IsControl)
                .OrderBy(w => w.Order)
                .ThenBy(w => w.Page ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ThenBy(w => w.Description ?? w.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var index = -1;
            if (Regex.IsMatch(text ?? string.Empty, @"\b(перв|номер\s*1)")) index = 0;
            else if (Regex.IsMatch(text ?? string.Empty, @"\b(втор|номер\s*2)")) index = 1;
            else if (Regex.IsMatch(text ?? string.Empty, @"\b(трет|номер\s*3)")) index = 2;
            else if (Regex.IsMatch(text ?? string.Empty, @"\bпоследн")) index = candidates.Count - 1;
            if (index >= 0 && index < candidates.Count) return candidates[index];

            var previous = _dialogContext.Snapshot();
            if (IsPronoun(text) && previous != null && !string.IsNullOrWhiteSpace(previous.ActionValue))
                return candidates.FirstOrDefault(w =>
                    string.Equals(w.DeviceId, previous.DeviceId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(w.Id, previous.WidgetId, StringComparison.OrdinalIgnoreCase));
            return null;
        }

        private MarfaCommandResult TryRefineLastControlTarget(string text)
        {
            if (_lastControlCandidates == null || _lastControlCandidates.Count < 2 ||
                string.IsNullOrWhiteSpace(text))
                return null;

            var options = _lastControlCandidates.Select(w => new MarfaClarificationCandidate
            {
                DeviceId = w.DeviceId, WidgetId = w.Id, Title = w.Description,
                Page = w.Page, DeviceName = w.DeviceId
            }).ToList();
            var selection = MarfaClarificationResolver.Resolve(text, options);
            if (selection == null || selection.Count != 1) return null;

            var index = selection[0];
            if (index < 0 || index >= _lastControlCandidates.Count) return null;
            var chosen = _lastControlCandidates[index];
            var previous = _dialogContext.Snapshot();
            if (previous == null || string.IsNullOrWhiteSpace(previous.ActionValue)) return null;

            _lastTarget = chosen;
            _lastValue = previous.ActionValue;
            var action = new MarfaActionItem
            {
                DeviceId = chosen.DeviceId, WidgetId = chosen.Id, Value = previous.ActionValue
            };
            var verb = previous.ActionValue == "1" ? "Включаю " :
                previous.ActionValue == "0" ? "Выключаю " :
                "Устанавливаю значение " + previous.ActionValue + " для ";
            return ControlResult(new[] { action }, 0,
                verb + (string.IsNullOrWhiteSpace(chosen.Description) ? chosen.Id : chosen.Description) + ".");
        }

        private MarfaCommandResult TryResolveTrainedCommand(string text, IList<WidgetState> widgets)
        {
            if (_trainedCommandMatcher == null) return null;
            var matches = _trainedCommandMatcher.MatchAll(text);
            if (matches == null || matches.Count == 0) return null;

            var actions = new List<MarfaActionItem>();
            WidgetState readTarget = null;
            foreach (var match in matches)
            {
                var widget = (widgets ?? new List<WidgetState>()).FirstOrDefault(w =>
                    string.Equals(w.DeviceId, match.DeviceId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(w.Id, match.WidgetId, StringComparison.OrdinalIgnoreCase));
                if (widget == null) return null;

                if (string.Equals(match.Value, TrainedCommandConstants.ReadValue, StringComparison.Ordinal))
                {
                    if (readTarget != null) return null;
                    readTarget = widget;
                }
                else
                {
                    actions.Add(new MarfaActionItem
                    {
                        DeviceId = widget.DeviceId, WidgetId = widget.Id,
                        Value = string.IsNullOrWhiteSpace(match.Value) ? "1" : match.Value
                    });
                }
            }

            if (readTarget != null)
            {
                if (actions.Count != 0) return null;
                _lastTarget = readTarget;
                return new MarfaCommandResult
                {
                    Kind = MarfaCommandKind.ReadValue,
                    Value = readTarget.Value,
                    Reply = MarfaSpeechTextFormatter.FormatValue(readTarget.Value, readTarget.Unit, readTarget.Description),
                    Actions = new List<MarfaActionItem>()
                };
            }

            if (actions.Count == 0) return null;
            _lastTarget = (widgets ?? new List<WidgetState>()).FirstOrDefault(w =>
                string.Equals(w.DeviceId, actions[0].DeviceId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(w.Id, actions[0].WidgetId, StringComparison.OrdinalIgnoreCase));
            _lastValue = actions[0].Value;
            return ControlResult(actions, 0, actions.Count == 1
                ? "Выполняю обученную команду."
                : "Выполняю обученную группу команд.");
        }

        private MarfaCommandResult ParseSingle(string text, IList<WidgetState> widgets)
        {
            var delay = ParseDelay(text);
            var action = DetectAction(text);
            if (action == null)
            {
                if (IsValueQuestion(text))
                    return ResolveValueQuestion(text, widgets);
                return Result(MarfaCommandKind.NotFound,
                    "Не поняла. Скажите, например: «включи свет», «закрой форточку» или «какая температура?»");
            }

            var referenced = ResolveReference(text, widgets);
            if (referenced != null && (HasOrdinalReference(text) || IsPronoun(text)))
            {
                _lastTarget = referenced;
                _lastValue = action;
                _lastCommandText = text;
                var referencedAction = new MarfaActionItem
                {
                    DeviceId = referenced.DeviceId, WidgetId = referenced.Id, Value = action
                };
                var referencedTitle = string.IsNullOrWhiteSpace(referenced.Description) ? referenced.Id : referenced.Description;
                return ControlResult(new[] { referencedAction }, delay,
                    (action == "1" ? "Включаю " : action == "0" ? "Выключаю " : "Устанавливаю значение " + action + " для ") +
                    referencedTitle + (delay > 0 ? " через " + FormatDelay(delay) : string.Empty) + ".");
            }

            var analytical = _analyticalEngine.ResolveControl(text, widgets, action);
            var candidates = new List<Candidate>();
            if (analytical.Candidate != null)
                candidates.Add(new Candidate { Widget = analytical.Candidate.Widget, Score = analytical.Candidate.Score });
            else if (analytical.Candidates != null && analytical.Candidates.Count > 0)
            {
                _pending = analytical.Candidates.Select(c => c.Widget).ToList();
                _lastControlCandidates = _pending.ToList();
                _pendingValue = action;
                _pendingOriginalText = text;
                _pendingReadValue = false;
                _pendingDelay = delay;
                return Result(MarfaCommandKind.Clarify, analytical.Clarification ?? BuildClarification(_pending));
            }
            else if (!string.IsNullOrWhiteSpace(analytical.Clarification))
            {
                _pending = new List<WidgetState>();
                _pendingValue = action;
                _pendingDelay = delay;
                _pendingOriginalText = text;
                _pendingReadValue = false;
                return Result(MarfaCommandKind.Clarify, analytical.Clarification);
            }
            else
                candidates = RankCandidates(text, widgets, true);

            var dialogTarget = _dialogContext.Snapshot();
            if (candidates.Count == 0 && IsPronoun(text) && _lastTarget != null &&
                dialogTarget != null && !string.IsNullOrWhiteSpace(dialogTarget.ActionValue))
                candidates.Add(new Candidate { Widget = _lastTarget, Score = 100 });

            if (candidates.Count == 0)
            {
                _pending = new List<WidgetState>();
                _pendingValue = action;
                _pendingDelay = delay;
                _pendingOriginalText = text;
                _pendingReadValue = false;
                return Result(MarfaCommandKind.Clarify, "Что именно нужно " +
                    (action == "1" ? "включить или открыть" : "выключить или закрыть") + "? Назовите устройство или комнату.");
            }

            var best = candidates[0];
            var ambiguous = candidates.Count > 1 && candidates[1].Score >= best.Score - 1;
            if (ambiguous || best.Score < 2)
            {
                _pending = candidates.Where(c => c.Score >= best.Score - 2).Select(c => c.Widget).Take(6).ToList();
                _lastControlCandidates = _pending.ToList();
                _pendingValue = action;
                _pendingDelay = delay;
                return Result(MarfaCommandKind.Clarify, BuildClarification(_pending));
            }

            _lastTarget = best.Widget;
            _lastValue = action;
            _lastCommandText = text;
            var actionItem = new MarfaActionItem
            {
                DeviceId = best.Widget.DeviceId,
                WidgetId = best.Widget.Id,
                Value = action
            };
            var verb = action == "1" ? "Включаю" : action == "0" ? "Выключаю" : "Устанавливаю значение " + action + " для";
            var title = string.IsNullOrWhiteSpace(best.Widget.Description) ? best.Widget.Id : best.Widget.Description;
            return ControlResult(new[] { actionItem }, delay,
                verb + " " + title + (delay > 0 ? " через " + FormatDelay(delay) : string.Empty) + ".");
        }

        private MarfaCommandResult ResolvePending(string text, IList<WidgetState> widgets)
        {
            if (text.Contains("отмена") || text.Contains("не надо") || text.Contains("забудь"))
            {
                ClearPending();
                return Result(MarfaCommandKind.Stop, "Хорошо, отменяю уточнение.");
            }

            var selected = new List<WidgetState>();
            var clarificationCandidates = _pending.Select(w => new MarfaClarificationCandidate
            {
                DeviceId = w.DeviceId, WidgetId = w.Id, Title = w.Description,
                Page = w.Page, DeviceName = w.DeviceId
            }).ToList();
            var resolvedIndexes = MarfaClarificationResolver.Resolve(text, clarificationCandidates);
            if (resolvedIndexes != null)
                foreach (var index in resolvedIndexes)
                    if (index >= 0 && index < _pending.Count) selected.Add(_pending[index]);

            if (selected.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(_pendingOriginalText))
                {
                    var combined = _pendingOriginalText + " " + text;
                    ClearPending();
                    return ParseSingle(combined, widgets);
                }
                return Result(MarfaCommandKind.Clarify, BuildClarification(_pending) +
                    " Скажите номер варианта или его название.");
            }

            if (_pendingReadValue)
            {
                var sensor = selected[0];
                _lastTarget = sensor;
                var sensorName = string.IsNullOrWhiteSpace(sensor.Description) ? sensor.Id : sensor.Description;
                var raw = string.IsNullOrWhiteSpace(sensor.Value) ? "значение пока неизвестно" : sensor.Value;
                var spoken = string.IsNullOrWhiteSpace(sensor.Value) ? raw :
                    MarfaSpeechTextFormatter.FormatValue(sensor.Value, sensor.Unit, sensor.Description);
                ClearPending();
                var read = Result(MarfaCommandKind.ReadValue, sensorName + ": " + spoken + ".");
                read.Value = raw;
                return read;
            }

            var actions = new List<MarfaActionItem>(_pendingResolvedActions);
            actions.AddRange(selected.Select(w => new MarfaActionItem
            {
                DeviceId = w.DeviceId, WidgetId = w.Id, Value = _pendingValue
            }));
            var delay = _pendingDelay;
            _lastTarget = selected[0];
            _lastValue = _pendingValue;
            _pendingResolvedActions.Clear();
            ClearPending();
            var pendingVerb = _lastValue == "1" ? "Включаю " :
                _lastValue == "0" ? "Выключаю " :
                "Устанавливаю значение " + _lastValue + " для ";
            return ControlResult(actions, delay, "Поняла. " +
                pendingVerb + (selected[0].Description ?? selected[0].Id) + ".");
        }

        private MarfaCommandResult ResolveValueQuestion(string text, IList<WidgetState> widgets)
        {
            var sensors = widgets.Where(IsSensor).ToList();
            var analytical = _analyticalEngine.ResolveSensor(text, sensors);
            var candidates = new List<Candidate>();
            if (analytical.Candidate != null)
                candidates.Add(new Candidate { Widget = analytical.Candidate.Widget, Score = analytical.Candidate.Score });
            else if (analytical.Candidates != null && analytical.Candidates.Count > 0)
            {
                _pending = analytical.Candidates.Select(c => c.Widget).ToList();
                _pendingOriginalText = text;
                _pendingReadValue = true;
                return Result(MarfaCommandKind.Clarify, analytical.Clarification ?? BuildClarification(_pending));
            }
            else if (!string.IsNullOrWhiteSpace(analytical.Clarification))
            {
                _pending = new List<WidgetState>();
                _pendingOriginalText = text;
                _pendingReadValue = true;
                return Result(MarfaCommandKind.Clarify, analytical.Clarification);
            }
            else
                candidates = RankCandidates(text, sensors, false);

            if (candidates.Count == 0)
                candidates = sensors.Select(w => new Candidate { Widget = w, Score = 0 }).ToList();
            if (candidates.Count == 0)
                return Result(MarfaCommandKind.NotFound, "Среди подключённых виджетов не нашла датчиков.");
            var best = candidates[0];
            if (candidates.Count > 1 && (best.Score == 0 || candidates[1].Score >= best.Score))
            {
                _pending = candidates.Take(6).Select(c => c.Widget).ToList();
                _pendingOriginalText = text;
                _pendingReadValue = true;
                return Result(MarfaCommandKind.Clarify, "Какой датчик проверить? " + BuildClarification(_pending));
            }
            _lastTarget = best.Widget;
            var name = string.IsNullOrWhiteSpace(best.Widget.Description) ? best.Widget.Id : best.Widget.Description;
            var value = string.IsNullOrWhiteSpace(best.Widget.Value) ? "значение пока неизвестно" : best.Widget.Value;
            var spokenValue = string.IsNullOrWhiteSpace(best.Widget.Value)
                ? value
                : MarfaSpeechTextFormatter.FormatValue(best.Widget.Value, best.Widget.Unit, best.Widget.Description);
            var result = Result(MarfaCommandKind.ReadValue, name + ": " + spokenValue + ".");
            result.Value = value;
            return result;
        }

        private static List<Candidate> RankCandidates(string text, IEnumerable<WidgetState> widgets, bool controlsOnly)
        {
            var words = Tokens(text);
            var result = new List<Candidate>();
            foreach (var widget in widgets)
            {
                if (widget == null || string.IsNullOrWhiteSpace(widget.Id)) continue;
                if (controlsOnly ? !IsControl(widget) : false) continue;
                var haystack = Normalize((widget.Description ?? "") + " " + widget.Id + " " +
                    (widget.Page ?? "") + " " + (widget.DeviceId ?? ""));
                var score = 0;
                foreach (var word in words)
                {
                    if (word.Length < 2 || IsCommandWord(word)) continue;
                    if (haystack.Contains(word)) score += word.Length >= 5 ? 3 : 2;
                    else if (Tokens(haystack).Any(t => t.StartsWith(word, StringComparison.Ordinal) ||
                             word.StartsWith(t, StringComparison.Ordinal))) score++;
                }
                if (score > 0) result.Add(new Candidate { Widget = widget, Score = score });
            }
            return result.OrderByDescending(c => c.Score)
                .ThenBy(c => c.Widget.Order).ToList();
        }

        private static bool IsControl(WidgetState w)
        {
            var type = Normalize((w.WidgetType ?? "") + " " + (w.DefinitionName ?? ""));
            return type.Contains("toggle") || type.Contains("button") || type.Contains("switch") ||
                type.Contains("relay") || type.Contains("slider") || type.Contains("select") ||
                type.Contains("dropdown") || type.Contains("btn") || type.Contains("vbtn");
        }

        private static bool IsSensor(WidgetState w)
        {
            if (w == null) return false;
            var type = Normalize((w.WidgetType ?? "") + " " + (w.DefinitionName ?? ""));
            return type.Contains("value") || type.Contains("status") || type.Contains("sensor") ||
                type.Contains("temperature") || type.Contains("humidity") ||
                !string.IsNullOrWhiteSpace(w.Unit);
        }

        private static string DetectAction(string text)
        {
            var setValue = Regex.Match(text,
                @"(?:установи|установить|поставь|поставить|задай|задать|назначь|назначить)\s+(-?\d+(?:[.,]\d+)?)");
            if (setValue.Success) return setValue.Groups[1].Value.Replace(',', '.');
            if (HasAny(text, "включи", "включить", "запусти", "запустить", "открой", "открыть",
                "подними", "поднять", "активируй", "активировать", "зажги", "распахни", "on")) return "1";
            if (HasAny(text, "выключи", "выключить", "останови", "остановить", "закрой", "закрыть",
                "опусти", "опустить", "отключи", "отключить", "погаси", "запечатай", "off")) return "0";
            return null;
        }

        private static bool IsValueQuestion(string text)
        {
            return HasAny(text, "сколько", "какая", "какой", "какое", "покажи", "скажи", "значение",
                "температур", "влажност", "давлен", "освещен");
        }

        private static bool IsCommandWord(string word)
        {
            return HasAny(word, "включ", "выключ", "открой", "откры", "закрой", "закры", "запусти",
                "останов", "через", "пожалуйста", "марфа", "сколько", "какая", "какой", "покажи", "скажи");
        }

        private static bool IsPronoun(string text)
        {
            return HasAny(text, "ее", "её", "его", "их", "это", "ту", "тот", "этот");
        }

        private static bool IsStop(string text)
        {
            return HasAny(text, "стоп", "отмена", "отмени команду", "прекрати", "отбой");
        }

        private static bool HasNegation(string text)
        {
            return Regex.IsMatch(text, @"\bне\s+(включ|выключ|открой|открыть|закрой|закрыть|запускай|запустить|останавливай|остановить)");
        }

        private static long ParseDelay(string text)
        {
            var numeric = Regex.Match(text, @"(?:через|спустя)\s+(\d+(?:[.,]\d+)?)\s*(секунда|секунд|сек|минута|минут|мин|час|день|дней)");
            if (numeric.Success)
            {
                double amount;
                if (double.TryParse(numeric.Groups[1].Value.Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out amount))
                {
                    var unit = numeric.Groups[2].Value;
                    var multiplier = unit.StartsWith("сек") ? 1000.0 : unit.StartsWith("мин") ? 60000.0 :
                        unit.StartsWith("час") ? 3600000.0 : 86400000.0;
                    var milliseconds = amount * multiplier;
                    if (milliseconds > 0 && milliseconds < long.MaxValue)
                        return (long)Math.Round(milliseconds);
                }
            }

            var words = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "один", 1 }, { "одну", 1 }, { "одна", 1 }, { "два", 2 }, { "две", 2 },
                { "три", 3 }, { "четыре", 4 }, { "пять", 5 }, { "десять", 10 },
                { "двадцать", 20 }, { "тридцать", 30 }, { "сорок", 40 },
                { "пятьдесят", 50 }, { "шестьдесят", 60 }
            };
            var word = Regex.Match(text, @"(?:через|спустя)\s+(один|одну|одна|два|две|три|четыре|пять|десять|двадцать|тридцать|сорок|пятьдесят|шестьдесят)\s*(секунд(?:у|ы)?|сек|минут(?:у|ы)?|мин|час(?:а|ов)?|дн(?:я|ей)?)");
            if (word.Success)
            {
                var amount = words[word.Groups[1].Value];
                var unit = word.Groups[2].Value;
                var multiplier = unit.StartsWith("сек") ? 1000L : unit.StartsWith("мин") ? 60000L :
                    unit.StartsWith("час") ? 3600000L : 86400000L;
                return (long)amount * multiplier;
            }

            var clock = Regex.Match(text, @"\b(?:в|к)\s+(\d{1,2})(?::(\d{2}))?(?:\s*(?:час(?:а|ов)?|ч))?\b");
            int hour, minute;
            if (clock.Success &&
                int.TryParse(clock.Groups[1].Value, out hour) &&
                int.TryParse(string.IsNullOrWhiteSpace(clock.Groups[2].Value) ? "0" : clock.Groups[2].Value, out minute) &&
                hour >= 0 && hour <= 23 && minute >= 0 && minute <= 59)
            {
                var now = DateTimeOffset.Now;
                var due = new DateTimeOffset(now.Year, now.Month, now.Day, hour, minute, 0, now.Offset);
                if (due <= now) due = due.AddDays(1);
                return (long)(due - now).TotalMilliseconds;
            }

            var wordClock = Regex.Match(text, @"\b(?:в|к)\s+(один|два|три|четыре|пять|шесть|семь|восемь|девять|десять|одиннадцать|двенадцать|тринадцать|четырнадцать|пятнадцать|шестнадцать|семнадцать|восемнадцать|девятнадцать|двадцать|двадцать один|двадцать два|двадцать три)\s*(?:час(?:а|ов)?|ч)\b");
            if (wordClock.Success)
            {
                var hourWords = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    { "один", 1 }, { "два", 2 }, { "три", 3 }, { "четыре", 4 }, { "пять", 5 },
                    { "шесть", 6 }, { "семь", 7 }, { "восемь", 8 }, { "девять", 9 }, { "десять", 10 },
                    { "одиннадцать", 11 }, { "двенадцать", 12 }, { "тринадцать", 13 }, { "четырнадцать", 14 },
                    { "пятнадцать", 15 }, { "шестнадцать", 16 }, { "семнадцать", 17 }, { "восемнадцать", 18 },
                    { "девятнадцать", 19 }, { "двадцать", 20 }, { "двадцать один", 21 },
                    { "двадцать два", 22 }, { "двадцать три", 23 }
                };
                var key = Regex.Replace(wordClock.Groups[1].Value, @"\s+", " ").Trim();
                if (hourWords.ContainsKey(key))
                {
                    var now = DateTimeOffset.Now;
                    var due = new DateTimeOffset(now.Year, now.Month, now.Day, hourWords[key], 0, 0, now.Offset);
                    if (due <= now) due = due.AddDays(1);
                    return (long)(due - now).TotalMilliseconds;
                }
            }
            return 0;
        }

        private static List<string> SplitCommands(string text, IList<WidgetState> widgets)
        {
            var parts = Regex.Split(text, @"\s*(?:;|,\s*а\s+также\s+|\s+и\s+)\s*");
            if (parts.Length > 1)
            {
                var explicitCommands = new List<string>();
                foreach (var part in parts)
                {
                    var trimmed = part.Trim();
                    if (DetectAction(trimmed) != null) explicitCommands.Add(trimmed);
                }
                if (explicitCommands.Count > 1) return explicitCommands;
            }

            // Android Marfa supports a shared verb with several targets:
            // "открой дверь и форточку" and context-specific pairs such as
            // "открой дверь огурцов и форточку помидоров".
            var tokens = Tokens(text);
            var actionIndex = tokens.FindIndex(t => DetectAction(t) != null);
            if (actionIndex < 0) return new List<string>();
            var entities = new HashSet<string>(new[]
            {
                "дверь", "форточка", "окно", "ворота", "насос", "вентилятор",
                "обогрев", "кран", "клапан", "свет", "лампа", "полив",
                "реле", "канал", "кнопка", "выход", "gpio"
            }, StringComparer.OrdinalIgnoreCase);
            var targetIndices = new List<int>();
            for (var i = actionIndex + 1; i < tokens.Count; i++)
                if (entities.Contains(tokens[i])) targetIndices.Add(i);
            if (targetIndices.Count < 2) return new List<string>();

            var pageWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (widgets != null)
            {
                foreach (var widget in widgets)
                    foreach (var word in Tokens(widget.Page ?? string.Empty))
                        if (word.Length > 1 && !IsCommandWord(word)) pageWords.Add(word);
            }

            var actionValue = DetectAction(tokens[actionIndex]);
            var result = new List<string>();
            for (var n = 0; n < targetIndices.Count; n++)
            {
                var targetIndex = targetIndices[n];
                var nextTarget = n + 1 < targetIndices.Count ? targetIndices[n + 1] : tokens.Count;
                var context = new List<string>();
                for (var i = targetIndex + 1; i < nextTarget; i++)
                    if (pageWords.Contains(tokens[i])) context.Add(tokens[i]);
                if (context.Count == 0)
                {
                    for (var i = 0; i < targetIndex; i++)
                        if (pageWords.Contains(tokens[i])) context.Add(tokens[i]);
                }

                var command = actionValue == "1" ? "включить" : "выключить";
                var verb = tokens[actionIndex];
                if (verb == "открыть") command = "открыть";
                else if (verb == "закрыть") command = "закрыть";
                else if (verb == "запустить") command = "запустить";
                else if (verb == "остановить") command = "остановить";
                command += " " + tokens[targetIndex];
                if (context.Count > 0) command += " " + string.Join(" ", context.Distinct().ToArray());
                result.Add(command);
            }
            return result;
        }

        private static List<string> Tokens(string text)
        {
            return RussianSemanticNormalizer.Tokens(text).ToList();
        }

        private static string Normalize(string text)
        {
            return EmojiSemanticText.Normalize(text);
        }

        private static string NormalizeLegacy(string text)
        {
            return RussianSemanticNormalizer.Normalize(text);
        }

        private static bool HasAny(string text, params string[] values)
        {
            foreach (var value in values)
                if (text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        private static string BuildClarification(IEnumerable<WidgetState> widgets)
        {
            var list = widgets.Take(6).ToList();
            if (list.Count == 0) return "Уточните название устройства или комнаты.";
            var names = list.Select((w, i) => (i + 1).ToString(CultureInfo.InvariantCulture) + ") " +
                (string.IsNullOrWhiteSpace(w.Description) ? w.Id : w.Description) +
                (string.IsNullOrWhiteSpace(w.Page) ? string.Empty : " (" + w.Page + ")"));
            return "Уточните, пожалуйста: " + string.Join("; ", names) + ".";
        }

        private static string FormatDelay(long ms)
        {
            if (ms >= 3600000) return (ms / 3600000).ToString(CultureInfo.InvariantCulture) + " ч";
            if (ms >= 60000) return (ms / 60000).ToString(CultureInfo.InvariantCulture) + " мин";
            return (ms / 1000).ToString(CultureInfo.InvariantCulture) + " сек";
        }

        private static MarfaCommandResult ControlResult(IEnumerable<MarfaActionItem> actions, long delay, string reply = null)
        {
            var items = actions.GroupBy(a => a.DeviceId + "/" + a.WidgetId + "=" + a.Value)
                .Select(g => g.First()).ToList();
            return new MarfaCommandResult
            {
                Kind = MarfaCommandKind.Control, Actions = items, DelayMs = delay,
                NeedsConfirmation = true,
                Reply = reply ?? (items.Count == 1 ? "Поняла команду." : "Выполняю несколько команд.")
            };
        }

        private static MarfaCommandResult Result(MarfaCommandKind kind, string reply)
        {
            return new MarfaCommandResult { Kind = kind, Reply = reply, Actions = new List<MarfaActionItem>() };
        }

        private void ClearPending()
        {
            _pending.Clear();
            _pendingValue = "1";
            _pendingDelay = 0;
            _pendingOriginalText = string.Empty;
            _pendingReadValue = false;
            _pendingResolvedActions.Clear();
        }
    }
}