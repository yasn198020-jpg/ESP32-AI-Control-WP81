using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ESP32AIControl.UWP.Core.Marfa
{
    /// <summary>
    /// Platform-neutral Marfa natural-language command router.
    /// It parses and resolves intent only; device transport and MQTT remain outside this class.
    /// </summary>
    public sealed class MarfaCommandEngine
    {
        private sealed class ActionSpec
        {
            public string Value;
            public string Reply;
            public string Infinitive;
        }
        private sealed class Candidate
        {
            public MarfaDevice Device;
            public MarfaWidgetState Widget;
            public int Score;
        }
        private sealed class Pending
        {
            public string OriginalText;
            public LocalCommandAction Kind;
            public ActionSpec Action;
            public IList<Candidate> Candidates = new List<Candidate>();
            public long DelayMs;
        }

        private const long MaxDelayMs = 30L * 24L * 60L * 60L * 1000L;
        private readonly MarfaAnalyticalEngine analytical = new MarfaAnalyticalEngine();
        private readonly DialogContext dialog = new DialogContext();
        private readonly IMarfaScenarioAdvisor scenarioAdvisor;
        private readonly List<Pending> pending = new List<Pending>();
        private IList<Candidate> lastControlCandidates = new List<Candidate>();
        private ActionSpec lastAction;

        public MarfaCommandEngine(IMarfaScenarioAdvisor advisor = null) { scenarioAdvisor = advisor; }

        public LocalCommandResult Parse(string command, IList<MarfaDevice> devices)
        {
            devices = devices ?? new List<MarfaDevice>();
            string original = command ?? "";
            string text = Normalize(original);
            if (IsStop(text))
            {
                ClearDialogContext();
                return Remember(Result(LocalCommandAction.STOP, "Стоп. Текущая команда отменена."), text);
            }
            if (HasNegatedAction(text))
            {
                ClearDialogContext();
                return Result(LocalCommandAction.NOT_FOUND,
                    "В команде есть отрицание. Я ничего не буду выполнять. Скажите прямо, какое действие нужно сделать.");
            }
            if (String.IsNullOrWhiteSpace(text))
                return Result(LocalCommandAction.NOT_FOUND, "Я не услышала команду");

            IList<string> pieces = MarfaMultiCommandSplitter.Split(text, devices);
            if (pieces.Count > 1)
            {
                var branchResults = pieces.Select(p => new MarfaCommandEngine(scenarioAdvisor).Parse(p, devices)).ToList();
                var unresolved = branchResults.Where(x => x.Action == LocalCommandAction.CLARIFY).ToList();
                if (unresolved.Count > 0)
                    return Result(LocalCommandAction.CLARIFY, String.Join(" Затем уточните: ", unresolved.Select(x => x.Reply)));
                if (branchResults.All(x => x.Action == LocalCommandAction.CONTROL))
                {
                    var all = branchResults.SelectMany(x => x.ActionItems).GroupBy(x => x.DeviceId + "/" + x.WidgetId + "/" + x.Value).Select(g => g.First()).ToList();
                    var combined = new LocalCommandResult
                    {
                        Action = LocalCommandAction.CONTROL, DeviceId = all[0].DeviceId, WidgetId = all[0].WidgetId,
                        Value = all[0].Value, ActionItems = all, NeedsConfirmation = true,
                        DelayMs = branchResults.Max(x => x.DelayMs),
                        Reply = String.Join("; ", branchResults.Select(x => x.Reply)),
                        SubResults = branchResults
                    };
                    return Remember(combined, text);
                }
            }

            dialog.BeginTurn(text);
            var pendingResult = ResolvePending(text, devices);
            if (pendingResult != null) return Remember(pendingResult, text);

            // Follow-up pronouns and short time phrases must be resolved against
            // the remembered dialog target before trying a fresh semantic search.
            var followUp = ParseFollowUp(text, devices);
            if (followUp != null) return Remember(followUp, text);

            if (LooksLikeSmartRule(text))
            {
                var smartRule = ParseSmartRule(text, devices);
                if (smartRule != null) return Remember(smartRule, text);
            }

            ActionSpec action = DetectAction(text);
            long delay = ParseTime(text);
            if (action != null)
            {
                var resolution = analytical.ResolveControl(text, devices, action.Value);
                IList<string> scenarioIds = new List<string>();
                if (resolution.Candidate == null && scenarioAdvisor != null)
                    scenarioIds = scenarioAdvisor.RefineControlCandidatesBeforeClarification(
                        resolution.Candidates.Select(x => x.Widget.Id).ToList(), action.Value, devices) ?? new List<string>();

                var selected = resolution.Candidate;
                if (selected == null && scenarioIds.Count == 1)
                    selected = resolution.Candidates.FirstOrDefault(x => x.Widget.Id == scenarioIds[0]);

                if (selected == null)
                {
                    var candidates = resolution.Candidates
                        .Where(x => scenarioIds.Count == 0 || scenarioIds.Contains(x.Widget.Id))
                        .Select(x => new Candidate { Device = x.Device, Widget = x.Widget, Score = x.Score }).ToList();
                    pending.Clear();
                    pending.Add(new Pending { OriginalText = text, Kind = LocalCommandAction.CONTROL, Action = action, Candidates = candidates, DelayMs = delay });
                    dialog.SetPending(text);
                    string clarification = candidates.Count > 0 ? BuildClarification(candidates, action) :
                        (resolution.Clarification ?? "Уточните, чем именно управлять.");
                    return Result(LocalCommandAction.CLARIFY, clarification, delay);
                }

                lastControlCandidates = resolution.Candidates
                    .Where(x => scenarioIds.Count == 0 || scenarioIds.Contains(x.Widget.Id))
                    .Select(x => new Candidate { Device = x.Device, Widget = x.Widget, Score = x.Score }).ToList();
                if (lastControlCandidates.Count == 0)
                    lastControlCandidates.Add(new Candidate { Device = selected.Device, Widget = selected.Widget, Score = selected.Score });
                lastAction = action;
                pending.Clear();
                dialog.ClearPending();

                var actions = new List<LocalCommandActionItem>
                {
                    new LocalCommandActionItem { DeviceId = selected.Device.Id, WidgetId = selected.Widget.Id, Value = action.Value }
                };
                var extra = ParseAdditionalActions(text, action, selected.Device, selected.Widget, devices);
                foreach (var item in extra)
                    if (!actions.Any(x => x.DeviceId == item.DeviceId && x.WidgetId == item.WidgetId && x.Value == item.Value)) actions.Add(item);

                var result = new LocalCommandResult
                {
                    Action = LocalCommandAction.CONTROL, DeviceId = actions[0].DeviceId, WidgetId = actions[0].WidgetId,
                    Value = actions[0].Value, ActionItems = actions, DelayMs = delay, NeedsConfirmation = true,
                    Reply = ControlReply(action, selected.Widget.Title, delay, actions.Count)
                };
                return Remember(result, text);
            }

            if (IsValueQuestion(text))
            {
                var resolution = analytical.ResolveSensor(text, devices);
                if (resolution.Candidate == null)
                {
                    pending.Clear();
                    if (resolution.Candidates.Count > 0)
                        pending.Add(new Pending
                        {
                            OriginalText = text, Kind = LocalCommandAction.READ_VALUE,
                            Candidates = resolution.Candidates.Select(x => new Candidate { Device = x.Device, Widget = x.Widget, Score = x.Score }).ToList()
                        });
                    dialog.SetPending(text);
                    return Result(LocalCommandAction.CLARIFY, resolution.Clarification ?? "Уточните, какой датчик использовать.");
                }
                var w = resolution.Candidate.Widget;
                return Remember(new LocalCommandResult
                {
                    Action = LocalCommandAction.READ_VALUE, DeviceId = resolution.Candidate.Device.Id,
                    WidgetId = w.Id, Value = w.Value ?? "", Reply = MarfaSpeechTextFormatter.FormatTemperatureForSpeech(w.Value, w.Unit, w.Title).Insert(0, (w.Title ?? w.Id) + ": ")
                }, text);
            }

            return Remember(Result(LocalCommandAction.NOT_FOUND,
                "Не поняла. Скажите, например: «открой форточку», «выключи насос через 20 минут», «какая температура?», «закрой её»."),
                text);
        }

        private LocalCommandResult ResolvePending(string text, IList<MarfaDevice> devices)
        {
            if (pending.Count == 0) return null;
            if (ContainsAny(text, "отмена", "отменяй", "не надо", "не выполняй", "забудь"))
            {
                pending.Clear(); dialog.ClearPending();
                return Result(LocalCommandAction.NOT_FOUND, "Хорошо, отменяю уточнение");
            }
            var p = pending[0];
            if (p.Candidates.Count == 0)
            {
                string combined = p.OriginalText + " " + text;
                pending.Clear(); dialog.ClearPending();
                return Parse(combined, devices);
            }
            var options = p.Candidates.Select(x => new MarfaClarificationCandidate
            {
                DeviceId = x.Device.Id, WidgetId = x.Widget.Id, Title = x.Widget.Title,
                Page = x.Widget.Page, DeviceName = x.Device.Name
            }).ToList();
            var selection = MarfaClarificationResolver.Resolve(text, options);
            if (selection == null || selection.Count == 0)
                return Result(LocalCommandAction.CLARIFY, BuildClarification(p.Candidates, p.Action));
            pending.Clear(); dialog.ClearPending();
            var selected = selection.Select(i => p.Candidates[i]).Where(x => x != null).ToList();
            if (p.Kind == LocalCommandAction.READ_VALUE)
            {
                if (selected.Count != 1) return Result(LocalCommandAction.CLARIFY, "Выберите один датчик, значение которого нужно сообщить.");
                var c = selected[0];
                return new LocalCommandResult { Action = LocalCommandAction.READ_VALUE, DeviceId = c.Device.Id,
                    WidgetId = c.Widget.Id, Value = c.Widget.Value ?? "",
                    Reply = (c.Widget.Title ?? c.Widget.Id) + ": " + MarfaSpeechTextFormatter.FormatTemperatureForSpeech(c.Widget.Value, c.Widget.Unit, c.Widget.Title) };
            }
            var action = p.Action;
            var items = selected.Select(c => new LocalCommandActionItem { DeviceId = c.Device.Id, WidgetId = c.Widget.Id, Value = action.Value }).ToList();
            var first = selected[0];
            lastAction = action; lastControlCandidates = selected;
            return new LocalCommandResult
            {
                Action = LocalCommandAction.CONTROL, DeviceId = first.Device.Id, WidgetId = first.Widget.Id,
                Value = action.Value, ActionItems = items, DelayMs = p.DelayMs, NeedsConfirmation = true,
                Reply = ControlReply(action, first.Widget.Title, p.DelayMs, items.Count)
            };
        }

        private LocalCommandResult ParseFollowUp(string text, IList<MarfaDevice> devices)
        {
            ActionSpec action = DetectAction(text);
            if (action == null && lastControlCandidates.Count > 1)
            {
                var options = lastControlCandidates.Select(c => new MarfaClarificationCandidate
                { DeviceId = c.Device.Id, WidgetId = c.Widget.Id, Title = c.Widget.Title, Page = c.Widget.Page, DeviceName = c.Device.Name }).ToList();
                var selection = MarfaClarificationResolver.Resolve(text, options);
                if (selection != null && selection.Count == 1)
                {
                    var c = lastControlCandidates[selection[0]];
                    var a = lastAction ?? new ActionSpec { Value = dialog.LastActionValue, Reply = dialog.LastActionValue == "1" ? "Открываю" : "Закрываю", Infinitive = dialog.LastActionValue == "1" ? "открыть" : "закрыть" };
                    return ControlResult(c, a, 0);
                }
            }

            long delay = ParseTime(text);
            bool question = IsValueQuestion(text);
            if (action == null && !question && delay > 0 && dialog.LastTarget != null && !String.IsNullOrEmpty(dialog.LastActionValue))
            {
                var target = dialog.LastTarget;
                return new LocalCommandResult
                {
                    Action = LocalCommandAction.CONTROL, DeviceId = target.DeviceId, WidgetId = target.WidgetId,
                    Value = dialog.LastActionValue, ActionItems = new List<LocalCommandActionItem> { new LocalCommandActionItem { DeviceId = target.DeviceId, WidgetId = target.WidgetId, Value = dialog.LastActionValue } },
                    DelayMs = delay, NeedsConfirmation = true,
                    Reply = "Запланировала повтор: " + dialog.LastTargetTitle + " через " + FormatDelay(delay)
                };
            }

            if (action != null && HasReference(text))
            {
                var target = ResolveReference(text, devices);
                if (target != null) return ControlResult(target, action, delay);
            }

            if (question && ContainsAny(text, "там", "здесь") && dialog.LastIntent == LocalCommandAction.READ_VALUE && dialog.LastTarget != null)
            {
                var d = devices.FirstOrDefault(x => x.Id == dialog.LastTarget.DeviceId);
                var w = d == null ? null : d.Widgets.FirstOrDefault(x => x.Id == dialog.LastTarget.WidgetId);
                if (w != null) return new LocalCommandResult { Action = LocalCommandAction.READ_VALUE, DeviceId = d.Id, WidgetId = w.Id,
                    Value = w.Value ?? "", Reply = w.Title + ": " + MarfaSpeechTextFormatter.FormatTemperatureForSpeech(w.Value, w.Unit, w.Title) };
            }
            return null;
        }

        private static LocalCommandResult ControlResult(Candidate c, ActionSpec a, long delay)
        {
            var item = new LocalCommandActionItem { DeviceId = c.Device.Id, WidgetId = c.Widget.Id, Value = a.Value };
            return new LocalCommandResult { Action = LocalCommandAction.CONTROL, DeviceId = item.DeviceId, WidgetId = item.WidgetId,
                Value = item.Value, ActionItems = new List<LocalCommandActionItem> { item }, DelayMs = delay, NeedsConfirmation = true,
                Reply = ControlReply(a, c.Widget.Title, delay, 1) };
        }

        private Candidate ResolveReference(string text, IList<MarfaDevice> devices)
        {
            var all = devices.SelectMany(d => (d.Widgets ?? new List<MarfaWidgetState>())
                .Where(w => w.Type == MarfaWidgetType.TOGGLE || w.Type == MarfaWidgetType.BUTTON || w.Type == MarfaWidgetType.INPUT)
                .Select(w => new Candidate { Device = d, Widget = w, Score = 1 }))
                .OrderBy(x => x.Widget.Order).ThenBy(x => x.Widget.Title).ToList();
            if (ContainsAny(text, "первую", "первая", "первый", "первое", "номер один")) return all.ElementAtOrDefault(0);
            if (ContainsAny(text, "вторую", "вторая", "второй", "второе", "номер два")) return all.ElementAtOrDefault(1);
            if (ContainsAny(text, "третью", "третья", "третий", "третье", "номер три")) return all.ElementAtOrDefault(2);
            var last = dialog.LastTarget;
            if (last == null) return null;
            var lastDevice = devices.FirstOrDefault(x => x.Id == last.DeviceId);
            var lastWidget = lastDevice == null ? null : lastDevice.Widgets.FirstOrDefault(x => x.Id == last.WidgetId);
            return lastDevice == null || lastWidget == null ? null :
                new Candidate { Device = lastDevice, Widget = lastWidget, Score = 100 };
        }

        private static bool HasReference(string text)
        {
            return ContainsAny(text, "ее", "его", "их", "эту", "этот", "это", "там", "здесь",
                "первую", "вторую", "третью", "первый", "второй", "третий", "номер один", "номер два", "номер три");
        }

        private IList<LocalCommandActionItem> ParseAdditionalActions(string text, ActionSpec action, MarfaDevice chosenDevice, MarfaWidgetState chosenWidget, IList<MarfaDevice> devices)
        {
            var parts = Regex.Split(text, @"\s+и\s+", RegexOptions.None);
            if (parts.Length < 2) return new List<LocalCommandActionItem>();
            string second = parts[parts.Length - 1];
            if (!ContainsAny(second, "форточ", "окн", "двер", "ворот", "насос", "помп", "вент", "обогрев", "отоп", "нагрев", "клапан", "кран", "свет", "ламп", "полив"))
                return new List<LocalCommandActionItem>();
            var resolution = analytical.ResolveControl(second, devices, action.Value);
            var c = resolution.Candidate;
            if (c == null || (c.Device.Id == chosenDevice.Id && c.Widget.Id == chosenWidget.Id)) return new List<LocalCommandActionItem>();
            return new List<LocalCommandActionItem> { new LocalCommandActionItem { DeviceId = c.Device.Id, WidgetId = c.Widget.Id, Value = action.Value } };
        }

        private static ActionSpec DetectAction(string text)
        {
            if (Regex.IsMatch(text, @"\b(?:открой|открыть|открывай|подними|поднять|распахни|раскрой)\b"))
                return new ActionSpec { Value = "1", Reply = "Открываю", Infinitive = "открыть" };
            if (Regex.IsMatch(text, @"\b(?:закрой|закрыть|закрывай|опусти|опустить|запечатай)\b"))
                return new ActionSpec { Value = "0", Reply = "Закрываю", Infinitive = "закрыть" };
            if (Regex.IsMatch(text, @"\b(?:включи|включить|включай|запусти|запустить|зажги)\b"))
                return new ActionSpec { Value = "1", Reply = "Включаю", Infinitive = "включить" };
            if (Regex.IsMatch(text, @"\b(?:выключи|выключить|выключай|останови|остановить|погаси)\b"))
                return new ActionSpec { Value = "0", Reply = "Выключаю", Infinitive = "выключить" };
            var set = Regex.Match(text, @"(?:установи|установить|поставь|поставить|задай|задать|назначь)\s+(-?\d+(?:[.,]\d+)?)");
            if (set.Success) return new ActionSpec { Value = set.Groups[1].Value.Replace(',', '.'), Reply = "Устанавливаю " + set.Groups[1].Value, Infinitive = "установить значение" };
            return null;
        }

        private LocalCommandResult ParseSmartRule(string text, IList<MarfaDevice> devices)
        {
            var thresholdMatch = Regex.Match(text,
                @"(?:выше|больше|превысит|достигнет|ниже|меньше|опустится)\s+(-?\d+(?:[.,]\d+)?)");
            if (!thresholdMatch.Success)
                return Result(LocalCommandAction.CLARIFY, "Укажите порог условия, например: «если температура выше 25 градусов, открой форточку».");

            double threshold;
            if (!Double.TryParse(thresholdMatch.Groups[1].Value.Replace(',', '.'),
                NumberStyles.Number, CultureInfo.InvariantCulture, out threshold))
                return Result(LocalCommandAction.CLARIFY, "Не удалось разобрать числовой порог условия.");

            string operatorText = ContainsAny(thresholdMatch.Value, "ниже", "меньше", "опустится") ? "<" : ">";
            var action = DetectAction(text);
            if (action == null)
                return Result(LocalCommandAction.CLARIFY, "Укажите действие после условия: что Марфа должна включить, выключить, открыть или закрыть?");

            string sensorText = text.Substring(0, thresholdMatch.Index);
            var sensorResolution = analytical.ResolveSensor(sensorText, devices);
            if (sensorResolution.Candidate == null)
                return Result(LocalCommandAction.CLARIFY,
                    sensorResolution.Clarification ?? "Уточните датчик, по которому проверять условие.");

            string targetText = (sensorText + " " +
                text.Substring(thresholdMatch.Index + thresholdMatch.Length)).Trim();
            var targetResolution = analytical.ResolveControl(targetText, devices, action.Value);
            if (targetResolution.Candidate == null)
                return Result(LocalCommandAction.CLARIFY,
                    targetResolution.Clarification ?? "Уточните устройство, которым управлять при выполнении условия.");

            var sensor = sensorResolution.Candidate;
            var target = targetResolution.Candidate;
            var item = new LocalCommandActionItem
            {
                DeviceId = target.Device.Id, WidgetId = target.Widget.Id, Value = action.Value
            };
            var plan = new ScenarioCommandPlan { Actions = new List<LocalCommandActionItem> { item } };
            return new LocalCommandResult
            {
                Action = LocalCommandAction.SMART_RULE,
                Reply = "Поняла правило: если " + (sensor.Widget.Title ?? sensor.Widget.Id) + " " +
                    operatorText + " " + threshold.ToString(CultureInfo.InvariantCulture) + ", " +
                    action.Infinitive + " " + (target.Widget.Title ?? target.Widget.Id),
                ConditionDeviceId = sensor.Device.Id,
                ConditionWidgetId = sensor.Widget.Id,
                ConditionOperator = operatorText,
                ConditionThreshold = threshold,
                ActionDeviceId = target.Device.Id,
                ActionWidgetId = target.Widget.Id,
                ActionValue = action.Value,
                ActionItems = new List<LocalCommandActionItem> { item },
                ScenarioPlan = plan
            };
        }

        private static bool IsValueQuestion(string text)
        {
            return ContainsAny(text, "сколько", "какая", "какое", "покажи", "показать", "скажи", "сказать", "узнай", "узнать", "что там");
        }
        private static bool LooksLikeSmartRule(string text)
        {
            return (ContainsAny(text, "если ", "когда ", "при температур", "как только", "при условии")) &&
                ContainsAny(text, "температур", "градус", "жарко", "холодно");
        }
        private static bool IsStop(string text) { return text == "стоп" || text == "остановись" || text == "отмена" || text == "отменить"; }
        private static bool HasNegatedAction(string text)
        {
            return Regex.IsMatch(text, @"\bне\s+(?:надо\s+|нужно\s+|следует\s+)?(?:открой|открывай|открыть|закрой|закрывай|закрыть|включи|включай|включить|выключи|выключай|выключить|запусти|запустить|останови|остановить|погаси|погасить)\b");
        }

        private static long ParseTime(string text)
        {
            var relative = Regex.Match(text, @"(?:через|спустя)\s+(.+)");
            if (!relative.Success) return 0;
            string tail = relative.Groups[1].Value;
            if (ContainsAny(tail, "полчаса", "полчасика")) return 30L * 60000L;
            if (ContainsAny(tail, "полтора часа", "полтора час")) return 90L * 60000L;
            double minutes = 0; bool found = false;
            foreach (Match m in Regex.Matches(tail, @"(\d+(?:[.,]\d+)?)\s*(час(?:а|ов)?|ч|минут(?:а|ы)?|мин)"))
            {
                double n; if (!Double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out n)) continue;
                minutes += m.Groups[2].Value.StartsWith("ч") ? n * 60 : n; found = true;
            }
            if (!found)
            {
                var word = Regex.Match(tail, @"\b(один|одна|одно|два|две|три|четыре|пять|десять|пятнадцать|двадцать|тридцать|сорок|пятьдесят|шестьдесят)\s+(час(?:а|ов)?|ч|минут(?:а|ы)?|мин)\b");
                if (word.Success)
                {
                    var nums = new Dictionary<string, int> { {"один",1},{"одна",1},{"одно",1},{"два",2},{"две",2},{"три",3},{"четыре",4},{"пять",5},{"десять",10},{"пятнадцать",15},{"двадцать",20},{"тридцать",30},{"сорок",40},{"пятьдесят",50},{"шестьдесят",60} };
                    int n; if (nums.TryGetValue(word.Groups[1].Value, out n)) { minutes = word.Groups[2].Value.StartsWith("ч") ? n * 60 : n; found = true; }
                }
            }
            if (!found || minutes <= 0) return 0;
            return (long)Math.Min(MaxDelayMs, minutes * 60000.0);
        }

        private static string BuildClarification(IList<Candidate> candidates, ActionSpec action)
        {
            var contexts = candidates.Select(c => ContextLabel(c)).Where(x => x.Length > 0).Distinct().ToList();
            var objects = candidates.Select(c => ObjectLabel(c)).Where(x => x.Length > 0).Distinct().ToList();
            string verb = action == null ? "выбрать" : action.Infinitive;
            string objectText = objects.Count == 0 ? "" : " " + String.Join(" и ", objects);
            if (contexts.Count > 1) return "Уточните, где " + verb + objectText + ": " + String.Join(" или ", contexts) + "?";
            if (objects.Count > 1) return "Уточните, какой объект выбрать: " + String.Join(" или ", objects) + "?";
            return "Уточните, чем именно управлять.";
        }
        private static string ContextLabel(Candidate c)
        {
            string page = (c.Widget.Page ?? "").Trim();
            if (page.Length > 0 && !page.Equals("Основная", StringComparison.OrdinalIgnoreCase)) return page;
            string name = (c.Device.Name ?? "").Trim();
            return Regex.IsMatch(name, @"^[0-9a-fA-F]{8,}(?:-[0-9a-fA-F]{4,})*$") ? "" : name;
        }
        private static string ObjectLabel(Candidate c)
        {
            string title = String.IsNullOrWhiteSpace(c.Widget.Title) ? "объект" : c.Widget.Title;
            title = Regex.Replace(title, @"(?i)\b(?:закрыта|закрыто|закрыт|открыта|открыто|открыт|состояние|статус|индикатор|положение)\b", " ");
            title = Regex.Replace(title, @"\s+", " ").Trim();
            return title.Length == 0 ? "объект" : title;
        }
        private static string ControlReply(ActionSpec action, string title, long delay, int count)
        {
            string clean = Regex.Replace(title ?? "", @"(?i)\b(?:закрыта|закрыто|закрыт|открыта|открыто|открыт)\b", " ");
            clean = Regex.Replace(clean, @"\s+", " ").Trim();
            if (clean.Length == 0) clean = title ?? "объект";
            string baseText = count > 1 ? action.Reply + ": выполняю " + count + " элементов" : action.Reply + ": " + clean;
            return delay > 0 ? baseText + " через " + FormatDelay(delay) : baseText;
        }
        private static string FormatDelay(long ms)
        {
            long minutes = Math.Max(1, ms / 60000), hours = minutes / 60, rest = minutes % 60;
            if (hours > 0 && rest > 0) return hours + " ч " + rest + " мин";
            if (hours > 0) return hours + " ч";
            return minutes + " мин";
        }
        private LocalCommandResult Remember(LocalCommandResult result, string text)
        {
            dialog.Remember(text, result);
            return result;
        }
        public DialogTargetContext DialogSnapshot() { return dialog.Snapshot(); }
        public void ClearDialogContext()
        {
            dialog.Clear(); pending.Clear(); lastControlCandidates.Clear(); lastAction = null;
        }
        private static LocalCommandResult Result(LocalCommandAction action, string reply, long delay = 0)
        { return new LocalCommandResult { Action = action, Reply = reply, DelayMs = delay }; }
        private static string Normalize(string value)
        {
            return RussianSemanticNormalizer.Normalize(EmojiSemanticText.Normalize(value ?? ""))
                .Replace(':', ' ').Replace(',', ' ').Replace('.', ' ').Trim();
        }
        private static bool ContainsAny(string text, params string[] words) { return words.Any(w => (text ?? "").Contains(w)); }
    }
}