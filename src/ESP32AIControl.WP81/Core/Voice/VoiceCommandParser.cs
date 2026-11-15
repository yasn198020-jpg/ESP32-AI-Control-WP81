using System;
using System.Globalization;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ESP32AIControl.WP81.Core.State;

namespace ESP32AIControl.WP81.Core.Voice
{
    public sealed class ParsedVoiceCommand
    {
        public string Command { get; set; }
        public IList<string> Commands { get; set; }
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

            var commands = new List<string>();
            var requiresConfirmation = false;
            if (asksOpen && !string.IsNullOrWhiteSpace(_openCommand))
            {
                commands.Add(_openCommand);
                requiresConfirmation = true;
            }
            else if (asksClose && !string.IsNullOrWhiteSpace(_closeCommand))
            {
                commands.Add(_closeCommand);
                requiresConfirmation = true;
            }
            else
            {
                // Match the original utterance so case-sensitive MQTT IDs and values
                // are not silently converted to lowercase.
                var directMatches = Regex.Matches(original,
                    @"([a-z0-9_-]+/[a-z0-9_.:-]+)\s*=\s*([^\s,;]+)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (directMatches.Count > 0)
                {
                    foreach (Match direct in directMatches)
                        commands.Add(direct.Groups[1].Value + "=" + direct.Groups[2].Value);
                }

                if (commands.Count == 0 && _naturalResolver != null)
                {
                    string reason;
                    var resolved = _naturalResolver.ResolveCommands(original, out reason);
                    if (!string.IsNullOrWhiteSpace(reason))
                        LastReason = reason;
                    if (resolved != null)
                        commands.AddRange(resolved);
                    requiresConfirmation = commands.Count > 0;
                }
            }

            if (commands.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(LastReason))
                    LastReason = "Не удалось однозначно определить команду или управляемый элемент.";
                return null;
            }

            // Never execute two contradictory values for the same MQTT control.
            var targetValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var uniqueCommands = new List<string>();
            foreach (var item in commands)
            {
                var separator = item.IndexOf('=');
                if (separator <= 0 || separator == item.Length - 1)
                {
                    LastReason = "Одна из команд имеет неверный формат. Ничего не отправлено.";
                    return null;
                }

                var target = item.Substring(0, separator).Trim();
                var value = item.Substring(separator + 1).Trim();
                string existing;
                if (targetValues.TryGetValue(target, out existing) &&
                    !string.Equals(existing, value, StringComparison.OrdinalIgnoreCase))
                {
                    LastReason = "В одной фразе обнаружены противоречивые команды для одного устройства. Ничего не отправлено.";
                    return null;
                }

                targetValues[target] = value;
                if (!uniqueCommands.Contains(item, StringComparer.OrdinalIgnoreCase))
                    uniqueCommands.Add(item);
            }
            commands = uniqueCommands;

            if (commands.Count > 5)
            {
                LastReason = "За одну фразу можно выполнить не более пяти действий.";
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

            if (executeAt.HasValue && commands.Count > 1)
            {
                LastReason = "Пока нельзя безопасно запланировать несколько команд одной фразой. Скажите их отдельно.";
                return null;
            }

            return new ParsedVoiceCommand
            {
                Command = commands[0].Trim(),
                Commands = commands,
                ExecuteAt = executeAt,
                RequiresConfirmation = requiresConfirmation,
                ConfirmationText = requiresConfirmation
                    ? "Марфа предлагает выполнить команды:\n" + string.Join("\n", commands.ToArray()) +
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