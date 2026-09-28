using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ESP32AIControl.WP81.Core.Voice
{
    public sealed class ParsedVoiceCommand
    {
        public string Command { get; set; }
        public DateTimeOffset? ExecuteAt { get; set; }
    }

    public sealed class VoiceCommandParser
    {
        private readonly string _openCommand;
        private readonly string _closeCommand;

        public VoiceCommandParser(string openCommand, string closeCommand)
        {
            _openCommand = openCommand ?? string.Empty;
            _closeCommand = closeCommand ?? string.Empty;
        }

        public ParsedVoiceCommand Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var normalized = text.Trim().ToLowerInvariant();
            var command = string.Empty;

            if (normalized.Contains("открой теплиц") || normalized.Contains("открыть теплиц") ||
                normalized.Contains("open greenhouse"))
                command = _openCommand;
            else if (normalized.Contains("закрой теплиц") || normalized.Contains("закрыть теплиц") ||
                     normalized.Contains("close greenhouse"))
                command = _closeCommand;
            else
            {
                var direct = Regex.Match(normalized, @"([a-z0-9_-]+/[a-z0-9_.:-]+)\s*=\s*([^\s]+)",
                    RegexOptions.IgnoreCase);
                if (direct.Success)
                    command = direct.Groups[1].Value + "=" + direct.Groups[2].Value;
            }

            if (string.IsNullOrEmpty(command))
                return null;

            var executeAt = ParseDelay(normalized);
            return new ParsedVoiceCommand
            {
                Command = command,
                ExecuteAt = executeAt
            };
        }

        private static DateTimeOffset? ParseDelay(string text)
        {
            var match = Regex.Match(text, @"(?:через|in)\s+([0-9]+)\s*(секунд(?:у|ы)?|сек|минут(?:у|ы)?|мин|час(?:а|ов)?|дн(?:я|ей)?)",
                RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var amount = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var unit = match.Groups[2].Value;
                var seconds = amount;

                if (unit.StartsWith("мин")) seconds = amount * 60;
                else if (unit.StartsWith("час")) seconds = amount * 3600;
                else if (unit.StartsWith("дн")) seconds = amount * 86400;

                return DateTimeOffset.Now.AddSeconds(seconds);
            }

            match = Regex.Match(text, @"(?:через|in)\s+(один|одну|два|две|три|четыре|пять|десять|двадцать|тридцать|сорок|пятьдесят|шестьдесят)\s*(минут(?:у|ы)?|час(?:а|ов)?)",
                RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var amount = WordNumber(match.Groups[1].Value);
                var seconds = match.Groups[2].Value.StartsWith("час") ? amount * 3600 : amount * 60;
                return DateTimeOffset.Now.AddSeconds(seconds);
            }

            return null;
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