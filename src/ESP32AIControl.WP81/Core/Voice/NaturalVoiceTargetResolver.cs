using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ESP32AIControl.WP81.Core.State;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core.Voice
{
    public sealed class VoiceTargetOption
    {
        public string Command { get; set; }
        public string Title { get; set; }
        public string Page { get; set; }
        public string DeviceId { get; set; }
        public string WidgetId { get; set; }

        public string DisplayText
        {
            get
            {
                var title = string.IsNullOrWhiteSpace(Title) ? WidgetId : Title;
                var page = string.IsNullOrWhiteSpace(Page) ? "без страницы" : Page;
                return title + " — " + page + " [" + DeviceId + "/" + WidgetId + "]";
            }
        }
    }

    public sealed class NaturalVoiceTargetResolver
    {
        private sealed class Candidate
        {
            public WidgetState Widget;
            public int Score;
            public bool IsMomentaryButton;
        }

        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "и","а","но","в","во","на","над","под","у","для","по","из","с","со","к","ко",
            "пожалуйста","мне","надо","нужно","можно","давай","дайте","сделай","сделать",
            "сейчас","там","здесь","его","ее","её","их","мой","моя","мое","моё","этот","эту",
            "потом","сразу","немедленно","через","in","через","минуту","минуты","минут",
            "час","часа","часов","секунду","секунды","секунд","день","дня","дней"
        };

        private static readonly string[] Suffixes =
        {
            "иями","ями","ами","иями","ого","его","ому","ему","ыми","ими",
            "ов","ев","ей","ью","ией","иям","иях","ями","ами",
            "ать","ять","ить","еть","ешь","ете","ют","ут","ишь","ите",
            "ую","юю","ая","яя","ое","ее","ые","ие","ой","ый","ий",
            "ам","ям","ах","ях","ом","ем","ым","им","ою","ею",
            "а","я","о","е","ы","и","у","ю","ь"
        };

        private readonly DeviceRepository _devices;
        private IList<string> _selectedAmbiguousCommands = new List<string>();
        private int _selectionIndex;

        public IList<VoiceTargetOption> LastAmbiguousOptions { get; private set; }

        public NaturalVoiceTargetResolver(DeviceRepository devices)
        {
            if (devices == null) throw new ArgumentNullException("devices");
            _devices = devices;
        }

        public string ResolveCommand(string phrase, out string reason)
        {
            _selectedAmbiguousCommands = new List<string>();
            _selectionIndex = 0;
            LastAmbiguousOptions = new List<VoiceTargetOption>();
            return ResolveSingleCommand(phrase, out reason);
        }

        public IList<string> ResolveCommands(string phrase, out string reason)
        {
            IList<VoiceTargetOption> ignored;
            return ResolveCommands(phrase, null, out reason, out ignored);
        }

        public IList<string> ResolveCommands(
            string phrase,
            IList<string> selectedAmbiguousCommands,
            out string reason,
            out IList<VoiceTargetOption> ambiguousOptions)
        {
            _selectedAmbiguousCommands = selectedAmbiguousCommands ?? new List<string>();
            _selectionIndex = 0;
            LastAmbiguousOptions = new List<VoiceTargetOption>();
            var result = ResolveCommandsInternal(phrase, out reason);
            ambiguousOptions = LastAmbiguousOptions;
            return result;
        }

        private IList<string> ResolveCommandsInternal(string phrase, out string reason)
        {
            reason = null;
            var result = new List<string>();
            var text = phrase ?? string.Empty;
            var matches = Regex.Matches(text, @"[a-z0-9]+|[а-яё]+",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var actionSpans = new List<Match>();

            for (var i = 0; i < matches.Count; i++)
            {
                var token = matches[i].Value;
                if (!IsActionToken(token))
                    continue;

                // In the English construction "turn on/off", on/off is part
                // of the same intent, not the start of another action.
                if ((string.Equals(token, "on", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(token, "off", StringComparison.OrdinalIgnoreCase)) &&
                    i > 0 && string.Equals(matches[i - 1].Value, "turn", StringComparison.OrdinalIgnoreCase))
                    continue;

                actionSpans.Add(matches[i]);
            }

            if (actionSpans.Count == 0)
            {
                var single = ResolveSingleCommand(text, out reason);
                if (!string.IsNullOrWhiteSpace(single))
                    result.Add(single);
                return result;
            }

            if (actionSpans.Count > 1)
            {
                for (var i = 0; i < actionSpans.Count; i++)
                {
                    var start = actionSpans[i].Index;
                    var end = i + 1 < actionSpans.Count
                        ? actionSpans[i + 1].Index
                        : text.Length;
                    var clause = text.Substring(start, end - start)
                        .Trim(' ', '\t', ',', ';', '.', '!', '?');
                    clause = Regex.Replace(clause, @"\s+(?:и|а|затем|после этого|далее|and|then)\s*$", string.Empty,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    string clauseReason;
                    var clauseCommands = ResolveCommandsInternal(clause, out clauseReason);
                    if (clauseCommands == null || clauseCommands.Count == 0)
                    {
                        reason = "Не удалось безопасно разобрать всю фразу. " +
                            (string.IsNullOrWhiteSpace(clauseReason) ? "Уточните каждое действие отдельно." : clauseReason);
                        return new List<string>();
                    }
                    result.AddRange(clauseCommands);
                    if (result.Count > 5)
                    {
                        reason = "За одну фразу можно выполнить не более пяти действий.";
                        return new List<string>();
                    }
                }
            }
            else
            {
                var action = actionSpans[0];
                var prefixEnd = action.Index + action.Length;
                if (string.Equals(action.Value, "turn", StringComparison.OrdinalIgnoreCase))
                {
                    for (var i = 0; i < matches.Count; i++)
                    {
                        if (matches[i].Index < prefixEnd)
                            continue;
                        if (string.Equals(matches[i].Value, "on", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(matches[i].Value, "off", StringComparison.OrdinalIgnoreCase))
                            prefixEnd = matches[i].Index + matches[i].Length;
                        break;
                    }
                }
                var prefix = text.Substring(action.Index, prefixEnd - action.Index);
                var tail = text.Substring(prefixEnd);
                var targets = Regex.Split(tail, @"(?:\s+и\s+|,\s*|\s+плюс\s+|\s+а также\s+|\s+and\s+)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

                if (targets.Length <= 1)
                {
                    var single = ResolveSingleCommand(text, out reason);
                    if (!string.IsNullOrWhiteSpace(single))
                        result.Add(single);
                    return result;
                }

                foreach (var target in targets)
                {
                    if (string.IsNullOrWhiteSpace(target))
                    {
                        reason = "В списке устройств есть пустой элемент. Ничего не отправлено.";
                        return new List<string>();
                    }

                    string targetReason;
                    var command = ResolveSingleCommand(prefix + " " + target, out targetReason);
                    if (string.IsNullOrWhiteSpace(command))
                    {
                        reason = "Не удалось найти все устройства из фразы. " +
                            (string.IsNullOrWhiteSpace(targetReason) ? "Уточните названия устройств." : targetReason);
                        return new List<string>();
                    }
                    result.Add(command);
                    if (result.Count > 5)
                    {
                        reason = "За одну фразу можно выполнить не более пяти действий.";
                        return new List<string>();
                    }
                }
            }

            var unique = new List<string>();
            foreach (var command in result)
                if (!unique.Contains(command, StringComparer.OrdinalIgnoreCase))
                    unique.Add(command);
            return unique;
        }

        private string ResolveSingleCommand(string phrase, out string reason)
        {
            reason = null;
            // Strip only an explicitly parsed delay so widget numbers such as
            // "окно 2" remain available to resolve the correct control.
            var searchable = Regex.Replace(
                phrase ?? string.Empty,
                @"(?:через|in)\s+(?:[0-9]+|один|одну|два|две|три|четыре|пять|десять|двадцать|тридцать|сорок|пятьдесят|шестьдесят)\s*(?:секунд(?:у|ы)?|сек|минут(?:у|ы)?|мин|час(?:а|ов)?|дн(?:я|ей)?)",
                " ",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var tokens = Tokenize(searchable);
            var intent = DetectIntent(tokens);
            if (intent == null)
                return null;

            if (intent == "ambiguous")
            {
                reason = "В одной фразе несколько действий. Скажите каждую команду отдельно.";
                return null;
            }

            var targetTokens = new List<string>();
            foreach (var token in tokens)
            {
                if (IsActionToken(token) || StopWords.Contains(token))
                    continue;

                var normalized = NormalizeWord(token);
                if (!string.IsNullOrEmpty(normalized) && !targetTokens.Contains(normalized))
                    targetTokens.Add(normalized);
            }

            if (targetTokens.Count == 0)
            {
                reason = "Не удалось определить, каким устройством управлять.";
                return null;
            }

            var widgets = _devices.GetWidgetsSnapshot();
            var candidates = new List<Candidate>();
            foreach (var widget in widgets)
            {
                if (!IsControllable(widget))
                    continue;

                var titleTokens = Tokenize(widget.Description)
                    .Select(NormalizeWord).Where(x => !string.IsNullOrEmpty(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var idTokens = Tokenize(widget.Id)
                    .Select(NormalizeWord).Where(x => !string.IsNullOrEmpty(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var pageTokens = Tokenize(widget.Page)
                    .Select(NormalizeWord).Where(x => !string.IsNullOrEmpty(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                var isButton = IsMomentaryButton(widget);
                if (isButton)
                {
                    var buttonIntent = DetectIntent(Tokenize(widget.Description));
                    if (!string.Equals(buttonIntent, intent, StringComparison.Ordinal))
                        continue;
                }

                var titleMatches = CountMatches(targetTokens, titleTokens);
                var idMatches = CountMatches(targetTokens, idTokens);
                if (titleMatches == 0 && idMatches == 0)
                    continue;

                var pageMatches = CountMatches(targetTokens, pageTokens);
                var score = titleMatches * 20 + idMatches * 12 + pageMatches * 4;

                if (titleTokens.Count > 0 && titleTokens.All(targetTokens.Contains))
                    score += 12;

                candidates.Add(new Candidate
                {
                    Widget = widget,
                    Score = score,
                    IsMomentaryButton = IsMomentaryButton(widget)
                });
            }

            if (candidates.Count == 0)
            {
                reason = "Не нашла управляемый элемент с таким названием. Проверьте подпись виджета в MQTT CONFIG.";
                return null;
            }

            candidates.Sort(delegate(Candidate a, Candidate b)
            {
                return b.Score.CompareTo(a.Score);
            });

            Candidate selected;
            if (candidates.Count > 1 && candidates[0].Score == candidates[1].Score)
            {
                var bestScore = candidates[0].Score;
                var tied = candidates.Where(x => x.Score == bestScore).ToList();
                var options = new List<VoiceTargetOption>();
                foreach (var candidate in tied)
                {
                    var optionValue = candidate.IsMomentaryButton ? "1" :
                        (intent == "open" || intent == "on" || intent == "start" ? "1" : "0");
                    options.Add(new VoiceTargetOption
                    {
                        Command = (candidate.Widget.DeviceId ?? string.Empty) + "/" +
                                  (candidate.Widget.Id ?? string.Empty) + "=" + optionValue,
                        Title = candidate.Widget.Description ?? candidate.Widget.Id,
                        Page = candidate.Widget.Page,
                        DeviceId = candidate.Widget.DeviceId,
                        WidgetId = candidate.Widget.Id
                    });
                }

                if (_selectionIndex < _selectedAmbiguousCommands.Count)
                {
                    var requested = _selectedAmbiguousCommands[_selectionIndex];
                    var chosen = options.FirstOrDefault(x =>
                        string.Equals(x.Command, requested, StringComparison.OrdinalIgnoreCase));
                    if (chosen != null)
                    {
                        _selectionIndex++;
                        selected = tied.First(x =>
                            string.Equals((x.Widget.DeviceId ?? string.Empty) + "/" +
                                          (x.Widget.Id ?? string.Empty) + "=" +
                                          (x.IsMomentaryButton ? "1" :
                                              (intent == "open" || intent == "on" || intent == "start" ? "1" : "0")),
                                chosen.Command, StringComparison.OrdinalIgnoreCase));
                    }
                    else
                    {
                        LastAmbiguousOptions = options;
                        reason = "Выбранный вариант больше не доступен. Уточните устройство ещё раз.";
                        return null;
                    }
                }
                else
                {
                    LastAmbiguousOptions = options;
                    reason = "Нашла несколько подходящих элементов. Выберите нужное устройство или страницу.";
                    return null;
                }
            }
            else
            {
                selected = candidates[0];
            }
            var value = selected.IsMomentaryButton ? "1" :
                (intent == "open" || intent == "on" || intent == "start" ? "1" : "0");

            return (selected.Widget.DeviceId ?? string.Empty) + "/" +
                   (selected.Widget.Id ?? string.Empty) + "=" + value;
        }

        private static string DetectIntent(IList<string> tokens)
        {
            var intents = new HashSet<string>(StringComparer.Ordinal);
            var hasTurn = tokens.Contains("turn", StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i].ToLowerInvariant();
                if (IsOneOf(token, "открой", "откройте", "открыть", "открывай", "открывайте",
                    "открыл", "открыла", "открыли", "открою", "откроет", "открытие", "открывание", "open"))
                    intents.Add("open");
                else if (IsOneOf(token, "закрой", "закройте", "закрыть", "закрывай", "закрывайте",
                    "закрыл", "закрыла", "закрыли", "закрою", "закроет", "закрытие", "закрывание", "close", "shut"))
                    intents.Add("close");
                else if (IsOneOf(token, "включи", "включите", "включить", "включай", "включайте",
                    "включение", "вруби", "врубите", "зажги", "зажгите", "запитай") ||
                    (hasTurn && token == "on"))
                    intents.Add("on");
                else if (IsOneOf(token, "выключи", "выключите", "выключить", "выключай", "выключайте",
                    "выключение", "выруби", "вырубите", "погаси", "погасите", "отруби") ||
                    (hasTurn && token == "off"))
                    intents.Add("off");
                else if (IsOneOf(token, "запусти", "запустите", "запустить", "запускать", "запуск", "start"))
                    intents.Add("start");
                else if (IsOneOf(token, "останови", "остановите", "остановить", "остановка", "прекрати", "прекратите", "stop"))
                    intents.Add("stop");
            }

            if (intents.Count == 0) return null;
            if (intents.Count > 1) return "ambiguous";

            var result = intents.First();
            if (result == "close") return "close";
            if (result == "off" || result == "stop") return "off";
            if (result == "start") return "start";
            if (result == "open") return "open";
            return "on";
        }

        private static bool IsActionToken(string token)
        {
            var word = (token ?? string.Empty).ToLowerInvariant();
            return IsOneOf(word,
                "открой", "откройте", "открыть", "открывай", "открывайте",
                "открыл", "открыла", "открыли", "открою", "откроет", "open",
                "закрой", "закройте", "закрыть", "закрывай", "закрывайте",
                "закрыл", "закрыла", "закрыли", "закрою", "закроет", "close", "shut",
                "включи", "включите", "включить", "включай", "включайте",
                "вруби", "врубите", "зажги", "зажгите", "запитай",
                "выключи", "выключите", "выключить", "выключай", "выключайте",
                "выруби", "вырубите", "погаси", "погасите", "отруби",
                "запусти", "запустите", "запустить", "запускать",
                "останови", "остановите", "остановить", "прекрати", "прекратите",
                "turn", "on", "off", "start", "stop");
        }

        private static bool IsOneOf(string value, params string[] options)
        {
            for (var i = 0; i < options.Length; i++)
                if (string.Equals(value, options[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static bool IsControllable(WidgetState widget)
        {
            if (widget == null) return false;
            var type = (widget.WidgetType ?? string.Empty).Trim().ToLowerInvariant();
            var definition = (widget.DefinitionName ?? string.Empty).Trim().ToLowerInvariant();

            return IsControlType(type) || IsControlType(definition);
        }

        private static bool IsMomentaryButton(WidgetState widget)
        {
            var type = (widget.WidgetType ?? string.Empty).Trim().ToLowerInvariant();
            var definition = (widget.DefinitionName ?? string.Empty).Trim().ToLowerInvariant();
            return IsButtonType(type) || IsButtonType(definition);
        }

        private static bool IsControlType(string type)
        {
            return type == "toggle" || type == "switch" || type == "checkbox" ||
                   type == "inputbool" || type == "button" || type == "action" ||
                   type == "buttonout" || type == "buttonin" ||
                   type == "pushbutton" || type == "vbutton" || type == "vbtn" ||
                   type == "btn";
        }

        private static bool IsButtonType(string type)
        {
            return type == "button" || type == "action" || type == "buttonout" ||
                   type == "buttonin" || type == "pushbutton" ||
                   type == "vbutton" || type == "vbtn" || type == "btn";
        }

        private static int CountMatches(IList<string> source, IList<string> candidates)
        {
            var count = 0;
            foreach (var token in source)
                if (candidates.Contains(token, StringComparer.OrdinalIgnoreCase))
                    count++;
            return count;
        }

        private static IList<string> Tokenize(string text)
        {
            var result = new List<string>();
            foreach (Match match in Regex.Matches(text ?? string.Empty, @"[a-z0-9]+|[а-яё]+",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                result.Add(match.Value.ToLowerInvariant().Replace('ё', 'е'));
            return result;
        }

        private static string NormalizeWord(string word)
        {
            if (string.IsNullOrWhiteSpace(word)) return string.Empty;
            var value = word.ToLowerInvariant().Replace('ё', 'е');

            switch (value)
            {
                case "дверь":
                case "двери":
                case "дверью":
                case "дверям":
                case "дверях":
                    return "двер";
                case "ворота":
                case "воротами":
                case "воротах":
                    return "ворот";
                case "форточка":
                case "форточку":
                case "форточки":
                case "форточкой":
                    return "форточк";
                case "окно":
                case "окна":
                case "окном":
                case "окнами":
                    return "окн";
                case "насос":
                case "насоса":
                case "насосу":
                case "насосом":
                case "насосы":
                    return "насос";
                case "свет":
                case "света":
                case "светом":
                case "свету":
                    return "свет";
            }

            for (var i = 0; i < Suffixes.Length; i++)
            {
                var suffix = Suffixes[i];
                if (value.Length > suffix.Length + 2 && value.EndsWith(suffix, StringComparison.Ordinal))
                    return value.Substring(0, value.Length - suffix.Length);
            }

            return value;
        }
    }
}