using System;
using System.Globalization;
using System.Text.RegularExpressions;
using ESP32AIControl.WP81.Core.State;

namespace ESP32AIControl.WP81.Core.Voice
{
    public sealed class ParsedVoiceCommand
    {
        public string Command { get; set; }
        public DateTimeOffset? ExecuteAt { get; set; }
        public bool RequiresConfirmation { get; set; }
        public string ConfirmationText { get; set; }
    }

    public sealed class VoiceCommandParser
    {
        private const int MaximumDelayDays = 30;

        private readonly string _openCommand;
        private readonly string _closeCommand;
        private readonly NaturalVoiceTargetResolver _naturalResolver;

        public string LastReason { get; private set; }

        public VoiceCommandParser(
            string openCommand,
            string closeCommand,
            DeviceRepository devices = null)
        {
            _openCommand = openCommand ?? string.Empty;
            _closeCommand = closeCommand ?? string.Empty;
            _naturalResolver = devices == null ? null : new NaturalVoiceTargetResolver(devices);
        }

        public ParsedVoiceCommand Parse(string text)
        {
            LastReason = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                LastReason = "Пустая голосовая команда.";
                return null;
            }

            var original = text.Trim();
            var normalized = Regex.Replace(original.ToLowerInvariant(), @"[,.!?;:]+", " ");

            // Do not turn a negative or contradictory phrase into a physical action.
            if (ContainsNegativeIntent(normalized))
            {
                LastReason = "В команде есть отрицание. Ничего не отправлено.";
                return null;
            }

            var asksOpen = ContainsAny(normalized,
                "открой теплиц", "открыть теплиц", "open greenhouse");
            var asksClose = ContainsAny(normalized,
                "закрой теплиц", "закрыть теплиц", "close greenhouse");

            if (asksOpen && asksClose)
            {
                LastReason = "Команда одновременно содержит открытие и закрытие. Ничего не отправлено.";
                return null;
            }

            var command = string.Empty;
            var requiresConfirmation = false;
            if (asksOpen && !string.IsNullOrWhiteSpace(_openCommand))
            {
                command = _openCommand;
                requiresConfirmation = true;
            }
            else if (asksClose && !string.IsNullOrWhiteSpace(_closeCommand))
            {
                command = _closeCommand;
                requiresConfirmation = true;
            }
            else
            {
                // Match the original utterance so case-sensitive MQTT IDs and values
                // are not silently converted to lowercase.
                var direct = Regex.Match(original,
                    @"([a-z0-9_-]+/[a-z0-9_.:-]+)\s*=\s*([^\s]+)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (direct.Success)
                    command = direct.Groups[1].Value + "=" + direct.Groups[2].Value;

                if (string.IsNullOrWhiteSpace(command) && _naturalResolver != null)
                {
                    string reason;
                    command = _naturalResolver.ResolveCommand(original, out reason);
                    if (!string.IsNullOrWhiteSpace(reason))
                        LastReason = reason;
                    requiresConfirmation = !string.IsNullOrWhiteSpace(command);
                }
            }

            if (string.IsNullOrWhiteSpace(command))
            {
                if (string.IsNullOrWhiteSpace(LastReason))
                    LastReason = "Не удалось однозначно определить команду или управляемый элемент.";
                return null;
            }

            var hasDelayMarker = Regex.IsMatch(normalized, @"(?:через|in)\s+",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var executeAt = ParseDelay(normalized);

            // A malformed or excessively long delay must not degrade into immediate execution.
            if (hasDelayMarker && !executeAt.HasValue)
            {
                LastReason = "Не удалось разобрать задержку. Команда не будет выполнена сразу.";
                return null;
            }

            return new ParsedVoiceCommand
            {
                Command = command.Trim(),
                ExecuteAt = executeAt,
                RequiresConfirmation = requiresConfirmation,
                ConfirmationText = requiresConfirmation
                    ? "Марфа предлагает выполнить команду:\n" + command.Trim() +
                      (executeAt.HasValue ? "\nВремя: " + executeAt.Value.ToString("g") : string.Empty)
                    : string.Empty
            };
        }

        private static bool ContainsNegativeIntent(string text)
        {
            return ContainsAny(text,
                "не открывай", "не открывать", "не надо открывать", "не нужно открывать",
                "не закрывай", "не закрывать", "не надо закрывать", "не нужно закрывать",
                "не включай", "не включить", "не выключай", "не выключить",
                "не запускай", "не запускать", "не останавливай", "не останавливать",
                "don't open", "do not open", "don't close", "do not close",
                "don't turn on", "don't turn off", "do not turn on", "do not turn off");
        }

        private static bool ContainsAny(string text, params string[] phrases)
        {
            for (var i = 0; i < phrases.Length; i++)
                if (text.IndexOf(phrases[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private static DateTimeOffset? ParseDelay(string text)
        {
            var match = Regex.Match(text,
                @"(?:через|in)\s+([0-9]+)\s*(секунд(?:у|ы)?|сек|минут(?:у|ы)?|мин|час(?:а|ов)?|дн(?:я|ей)?)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (match.Success)
            {
                long amount;
                if (!long.TryParse(match.Groups[1].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out amount) || amount <= 0)
                    return null;

                var unit = match.Groups[2].Value;
                long multiplier = 1L;
                if (unit.StartsWith("мин", StringComparison.OrdinalIgnoreCase)) multiplier = 60L;
                else if (unit.StartsWith("час", StringComparison.OrdinalIgnoreCase)) multiplier = 3600L;
                else if (unit.StartsWith("дн", StringComparison.OrdinalIgnoreCase)) multiplier = 86400L;

                var maximumSeconds = MaximumDelayDays * 86400L;
                if (amount > maximumSeconds / multiplier)
                    return null;

                return CreateDate(amount * multiplier);
            }

            match = Regex.Match(text,
                @"(?:через|in)\s+(один|одну|два|две|три|четыре|пять|десять|двадцать|тридцать|сорок|пятьдесят|шестьдесят)\s*(минут(?:у|ы)?|час(?:а|ов)?)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (match.Success)
            {
                var amount = WordNumber(match.Groups[1].Value);
                if (amount <= 0)
                    return null;

                var seconds = match.Groups[2].Value.StartsWith("час", StringComparison.OrdinalIgnoreCase)
                    ? amount * 3600L
                    : amount * 60L;
                return CreateDate(seconds);
            }

            return null;
        }

        private static DateTimeOffset? CreateDate(long seconds)
        {
            if (seconds <= 0 || seconds > MaximumDelayDays * 86400L)
                return null;
            return DateTimeOffset.Now.AddSeconds(seconds);
        }

        private static int WordNumber(string word)
        {
            switch (word)
            {
                case "один":
                case "одну": return 1;
                case "два":
                case "две": return 2;
                case "три": return 3;
                case "четыре": return 4;
                case "пять": return 5;
                case "десять": return 10;
                case "двадцать": return 20;
                case "тридцать": return 30;
                case "сорок": return 40;
                case "пятьдесят": return 50;
                case "шестьдесят": return 60;
                default: return 0;
            }
        }
    }
}