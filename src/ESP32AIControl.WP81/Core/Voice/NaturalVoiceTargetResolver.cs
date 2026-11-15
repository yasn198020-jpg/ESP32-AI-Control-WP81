using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ESP32AIControl.WP81.Core.State;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core.Voice
{
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

        public NaturalVoiceTargetResolver(DeviceRepository devices)
        {
            if (devices == null) throw new ArgumentNullException("devices");
            _devices = devices;
        }

        public string ResolveCommand(string phrase, out string reason)
        {
            reason = null;
            var tokens = Tokenize(phrase);
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
                if (IsActionToken(token) || StopWords.Contains(token) ||
                    Regex.IsMatch(token, @"^\d+$"))
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

            if (candidates.Count > 1 && candidates[0].Score == candidates[1].Score)
            {
                var first = candidates[0].Widget;
                var second = candidates[1].Widget;
                reason = "Нашла несколько подходящих элементов: «" +
                         (first.Description ?? first.Id) + "» (" + first.Page + ") и «" +
                         (second.Description ?? second.Id) + "» (" + second.Page +
                         "). Добавьте название страницы или уточните устройство.";
                return null;
            }

            var selected = candidates[0];
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
                var token = NormalizeWord(tokens[i]);
                if (token.StartsWith("откр", StringComparison.Ordinal) ||
                    token == "open" || token.StartsWith("подним", StringComparison.Ordinal) ||
                    token.StartsWith("отпер", StringComparison.Ordinal))
                    intents.Add("open");
                else if (token.StartsWith("закр", StringComparison.Ordinal) ||
                         token == "close" || token.StartsWith("опуст", StringComparison.Ordinal) ||
                         token.StartsWith("запер", StringComparison.Ordinal))
                    intents.Add("close");
                else if (token.StartsWith("включ", StringComparison.Ordinal) ||
                         token.StartsWith("вруб", StringComparison.Ordinal) ||
                         token.StartsWith("зажг", StringComparison.Ordinal) ||
                         (hasTurn && token == "on"))
                    intents.Add("on");
                else if (token.StartsWith("выключ", StringComparison.Ordinal) ||
                         token.StartsWith("выруб", StringComparison.Ordinal) ||
                         token.StartsWith("погас", StringComparison.Ordinal) ||
                         token.StartsWith("отруб", StringComparison.Ordinal) ||
                         (hasTurn && token == "off"))
                    intents.Add("off");
                else if (token.StartsWith("запуст", StringComparison.Ordinal) ||
                         token.StartsWith("старт", StringComparison.Ordinal) ||
                         token == "start")
                    intents.Add("start");
                else if (token.StartsWith("останов", StringComparison.Ordinal) ||
                         token.StartsWith("прекрат", StringComparison.Ordinal) ||
                         token == "stop")
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
            var word = NormalizeWord(token);
            return word.StartsWith("откр", StringComparison.Ordinal) ||
                   word.StartsWith("закр", StringComparison.Ordinal) ||
                   word.StartsWith("включ", StringComparison.Ordinal) ||
                   word.StartsWith("выключ", StringComparison.Ordinal) ||
                   word.StartsWith("вруб", StringComparison.Ordinal) ||
                   word.StartsWith("выруб", StringComparison.Ordinal) ||
                   word.StartsWith("зажг", StringComparison.Ordinal) ||
                   word.StartsWith("погас", StringComparison.Ordinal) ||
                   word.StartsWith("отруб", StringComparison.Ordinal) ||
                   word.StartsWith("подним", StringComparison.Ordinal) ||
                   word.StartsWith("опуст", StringComparison.Ordinal) ||
                   word.StartsWith("отпер", StringComparison.Ordinal) ||
                   word.StartsWith("запер", StringComparison.Ordinal) ||
                   word.StartsWith("запуст", StringComparison.Ordinal) ||
                   word.StartsWith("останов", StringComparison.Ordinal) ||
                   word.StartsWith("прекрат", StringComparison.Ordinal) ||
                   word == "open" || word == "close" || word == "turn" ||
                   word == "on" || word == "off" || word == "start" || word == "stop";
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