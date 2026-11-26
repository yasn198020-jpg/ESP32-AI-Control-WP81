using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ESP32AIControl.WP81.Core.State;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core.Voice
{
    public enum MarfaCommandKind { Control, ReadValue, Clarify, Stop, NotFound }

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
        private List<WidgetState> _pending = new List<WidgetState>();
        private string _pendingValue = "1";
        private long _pendingDelay;
        private WidgetState _lastTarget;
        private string _lastValue = "1";

        public MarfaCommandEngine(DeviceRepository devices)
        {
            _devices = devices;
        }

        public MarfaCommandResult Parse(string command)
        {
            return Parse(command, _devices == null ? new List<WidgetState>() : _devices.GetWidgetsSnapshot());
        }

        public MarfaCommandResult Parse(string command, IList<WidgetState> widgets)
        {
            var text = Normalize(command);
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

            if (_pending.Count > 0)
                return ResolvePending(text);

            var split = SplitCommands(text);
            if (split.Count > 1)
            {
                var combined = new List<MarfaActionItem>();
                long maxDelay = 0;
                foreach (var part in split)
                {
                    var item = ParseSingle(part, widgets);
                    if (item.Kind == MarfaCommandKind.Clarify) return item;
                    if (item.Kind != MarfaCommandKind.Control)
                        return Result(MarfaCommandKind.NotFound, "Не удалось однозначно разобрать составную команду.");
                    combined.AddRange(item.Actions);
                    maxDelay = Math.Max(maxDelay, item.DelayMs);
                }
                return ControlResult(combined, maxDelay);
            }

            return ParseSingle(text, widgets);
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

            var candidates = RankCandidates(text, widgets, true);
            if (candidates.Count == 0 && IsPronoun(text) && _lastTarget != null)
                candidates.Add(new Candidate { Widget = _lastTarget, Score = 100 });

            if (candidates.Count == 0)
            {
                _pending = new List<WidgetState>();
                _pendingValue = action;
                _pendingDelay = delay;
                return Result(MarfaCommandKind.Clarify, "Что именно нужно " +
                    (action == "1" ? "включить или открыть" : "выключить или закрыть") + "? Назовите устройство или комнату.");
            }

            var best = candidates[0];
            var ambiguous = candidates.Count > 1 && candidates[1].Score >= best.Score - 1;
            if (ambiguous || best.Score < 2)
            {
                _pending = candidates.Where(c => c.Score >= best.Score - 2).Select(c => c.Widget).Take(6).ToList();
                _pendingValue = action;
                _pendingDelay = delay;
                return Result(MarfaCommandKind.Clarify, BuildClarification(_pending));
            }

            _lastTarget = best.Widget;
            _lastValue = action;
            var actionItem = new MarfaActionItem
            {
                DeviceId = best.Widget.DeviceId,
                WidgetId = best.Widget.Id,
                Value = action
            };
            var verb = action == "1" ? "Включаю" : "Выключаю";
            var title = string.IsNullOrWhiteSpace(best.Widget.Description) ? best.Widget.Id : best.Widget.Description;
            return ControlResult(new[] { actionItem }, delay,
                verb + " " + title + (delay > 0 ? " через " + FormatDelay(delay) : string.Empty) + ".");
        }

        private MarfaCommandResult ResolvePending(string text)
        {
            if (text.Contains("отмена") || text.Contains("не надо") || text.Contains("забудь"))
            {
                ClearPending();
                return Result(MarfaCommandKind.Stop, "Хорошо, отменяю уточнение.");
            }

            var selected = new List<WidgetState>();
            var ordinal = Regex.Match(text, @"\b(первый|первая|первое|второй|вторая|второе|третий|третья|третье|четвертый|четвертая|последний)\b");
            if (ordinal.Success && _pending.Count > 0)
            {
                var index = ordinal.Value.StartsWith("перв") ? 0 :
                    ordinal.Value.StartsWith("втор") ? 1 :
                    ordinal.Value.StartsWith("трет") ? 2 :
                    ordinal.Value.StartsWith("чет") ? 3 : _pending.Count - 1;
                if (index >= 0 && index < _pending.Count) selected.Add(_pending[index]);
            }
            else
            {
                var ranked = RankCandidates(text, _pending, false);
                if (ranked.Count > 0 && ranked[0].Score >= 1 &&
                    (ranked.Count == 1 || ranked[0].Score > ranked[1].Score))
                    selected.Add(ranked[0].Widget);
            }

            if (selected.Count == 0)
                return Result(MarfaCommandKind.Clarify, BuildClarification(_pending) +
                    " Скажите номер варианта или его название.");

            var actions = selected.Select(w => new MarfaActionItem
            {
                DeviceId = w.DeviceId, WidgetId = w.Id, Value = _pendingValue
            }).ToList();
            var delay = _pendingDelay;
            _lastTarget = selected[0];
            _lastValue = _pendingValue;
            ClearPending();
            return ControlResult(actions, delay, "Поняла. " +
                (_lastValue == "1" ? "Включаю " : "Выключаю ") +
                (selected[0].Description ?? selected[0].Id) + ".");
        }

        private MarfaCommandResult ResolveValueQuestion(string text, IList<WidgetState> widgets)
        {
            var sensors = widgets.Where(IsSensor).ToList();
            var candidates = RankCandidates(text, sensors, false);
            if (candidates.Count == 0)
                candidates = sensors.Select(w => new Candidate { Widget = w, Score = 0 }).ToList();
            if (candidates.Count == 0)
                return Result(MarfaCommandKind.NotFound, "Среди подключённых виджетов не нашла датчиков.");
            var best = candidates[0];
            if (candidates.Count > 1 && (best.Score == 0 || candidates[1].Score >= best.Score))
            {
                _pending = candidates.Take(6).Select(c => c.Widget).ToList();
                return Result(MarfaCommandKind.Clarify, "Какой датчик проверить? " + BuildClarification(_pending));
            }
            _lastTarget = best.Widget;
            var name = string.IsNullOrWhiteSpace(best.Widget.Description) ? best.Widget.Id : best.Widget.Description;
            var value = string.IsNullOrWhiteSpace(best.Widget.Value) ? "значение пока неизвестно" : best.Widget.Value;
            var unit = string.IsNullOrWhiteSpace(best.Widget.Unit) ? string.Empty : " " + best.Widget.Unit;
            var result = Result(MarfaCommandKind.ReadValue, name + ": " + value + unit + ".");
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
            if (HasAny(text, "включи", "включить", "запусти", "запустить", "открой", "открыть",
                "подними", "поднять", "активируй", "активировать", "on")) return "1";
            if (HasAny(text, "выключи", "выключить", "останови", "остановить", "закрой", "закрыть",
                "опусти", "опустить", "отключи", "отключить", "off")) return "0";
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
            return Regex.IsMatch(text, @"\bне\s+(включ|выключ|открой|закрой|запускай|останавливай)");
        }

        private static long ParseDelay(string text)
        {
            var m = Regex.Match(text, @"(?:через|спустя)\s+(\d+)\s*(секунд|сек|минут|мин|час|часа|часов|дней|день)");
            if (!m.Success) return 0;
            long amount;
            if (!long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out amount)) return 0;
            var unit = m.Groups[2].Value;
            var multiplier = unit.StartsWith("сек") ? 1000L : unit.StartsWith("мин") ? 60000L :
                unit.StartsWith("час") ? 3600000L : 86400000L;
            if (amount > long.MaxValue / multiplier) return 0;
            return amount * multiplier;
        }

        private static List<string> SplitCommands(string text)
        {
            var parts = Regex.Split(text, @"\s*(?:;|,\s*а\s+также\s+|\s+и\s+)\s*");
            if (parts.Length < 2) return new List<string>();
            var result = new List<string>();
            foreach (var part in parts)
                if (DetectAction(part) != null) result.Add(part.Trim());
            return result.Count > 1 ? result : new List<string>();
        }

        private static List<string> Tokens(string text)
        {
            return Regex.Split(Normalize(text), @"[^а-яa-z0-9]+")
                .Where(t => t.Length > 0).ToList();
        }

        private static string Normalize(string text)
        {
            var value = (text ?? string.Empty).ToLowerInvariant().Replace('ё', 'е');
            value = Regex.Replace(value, @"[^а-яa-z0-9/=_\s-]", " ");
            return Regex.Replace(value, @"\s+", " ").Trim();
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
        }
    }
}