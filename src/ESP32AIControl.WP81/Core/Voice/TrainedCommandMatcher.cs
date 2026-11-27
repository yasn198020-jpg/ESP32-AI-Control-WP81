using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ESP32AIControl.WP81.Core.Voice
{
    public static class TrainedCommandConstants
    {
        public const string ReadValue = "__READ_VALUE__";
    }

    public sealed class TrainedVoiceCommand
    {
        public string Phrase { get; set; }
        public string DeviceId { get; set; }
        public string WidgetId { get; set; }
        public string Value { get; set; }
        public IList<string> Variants { get; private set; }

        public TrainedVoiceCommand()
        {
            Phrase = DeviceId = WidgetId = string.Empty;
            Value = "1";
            Variants = new List<string>();
        }
    }

    /// <summary>Matches saved voice phrases deterministically and rejects close ties.</summary>
    public sealed class TrainedCommandMatcher
    {
        private readonly ITrainedCommandRepository _store;
        public TrainedCommandMatcher(ITrainedCommandRepository store) { _store = store; }

        public TrainedVoiceCommand Match(string text)
        {
            var all = MatchAll(text);
            return all.Count == 0 ? null : all[0];
        }

        public IList<TrainedVoiceCommand> MatchAll(string text)
        {
            var normalized = Normalize(text);
            if (normalized.Length == 0 || _store == null) return new List<TrainedVoiceCommand>();
            var commands = _store.Load() ?? new List<TrainedVoiceCommand>();
            if (commands.Count == 0) return new List<TrainedVoiceCommand>();

            var exact = commands.Where(c => c.Variants.Any(v => Normalize(v) == normalized) ||
                Normalize(c.Phrase) == normalized).Select(c => Normalize(c.Phrase))
                .Where(x => x.Length > 0).Distinct().ToList();
            if (exact.Count == 1) return commands.Where(c => exact.Contains(Normalize(c.Phrase))).ToList();
            if (exact.Count > 1) return new List<TrainedVoiceCommand>();

            var groups = commands.GroupBy(c => Normalize(c.Phrase)).Where(g => g.Key.Length > 0).ToList();
            var contained = new List<KeyValuePair<string, int>>();
            foreach (var group in groups)
            {
                var score = group.SelectMany(c => c.Variants).Select(Normalize)
                    .Where(v => v.Length > 0 && ContainsPhrase(normalized, v))
                    .Select(v => v.Length).DefaultIfEmpty(0).Max();
                if (score > 0) contained.Add(new KeyValuePair<string, int>(group.Key, score));
            }
            if (contained.Count > 0)
            {
                var max = contained.Max(x => x.Value);
                var winners = contained.Where(x => x.Value == max).Select(x => x.Key).Distinct().ToList();
                return winners.Count == 1 ? commands.Where(c => winners.Contains(Normalize(c.Phrase))).ToList() : new List<TrainedVoiceCommand>();
            }

            var partial = new List<KeyValuePair<string, double>>();
            foreach (var group in groups)
            {
                var score = group.SelectMany(c => c.Variants).Select(Normalize)
                    .Where(v => v.Length > 0 && PhraseWordsContained(normalized, v))
                    .Select(v => SimilarityAgainstText(normalized, v)).DefaultIfEmpty(0).Max();
                if (score > 0) partial.Add(new KeyValuePair<string, double>(group.Key, score));
            }
            if (partial.Count > 0)
            {
                var max = partial.Max(x => x.Value);
                var winners = partial.Where(x => x.Value >= max - 0.04).Select(x => x.Key).Distinct().ToList();
                return winners.Count == 1 ? commands.Where(c => winners.Contains(Normalize(c.Phrase))).ToList() : new List<TrainedVoiceCommand>();
            }

            var fuzzy = new List<KeyValuePair<string, double>>();
            foreach (var group in groups)
            {
                var score = group.SelectMany(c => c.Variants.Concat(new[] { c.Phrase }))
                    .Select(Normalize).Where(v => v.Length > 0)
                    .Select(v => SimilarityAgainstText(normalized, v)).DefaultIfEmpty(0).Max();
                if (score >= 0.78) fuzzy.Add(new KeyValuePair<string, double>(group.Key, score));
            }
            if (fuzzy.Count == 0) return new List<TrainedVoiceCommand>();
            var best = fuzzy.Max(x => x.Value);
            var selected = fuzzy.Where(x => x.Value >= best - 0.04).Select(x => x.Key).Distinct().ToList();
            return selected.Count == 1 ? commands.Where(c => selected.Contains(Normalize(c.Phrase))).ToList() : new List<TrainedVoiceCommand>();
        }

        private static string Normalize(string value)
        {
            var text = RussianSemanticNormalizer.Normalize(value ?? string.Empty);
            text = Regex.Replace(text, @"[^a-zа-я0-9]+", " ");
            text = Regex.Replace(text, @"^марф(а|у|е|ой)\s*", string.Empty);
            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        private static bool ContainsPhrase(string text, string phrase)
        {
            return text == phrase || (" " + text + " ").Contains(" " + phrase + " ");
        }

        private static bool PhraseWordsContained(string text, string phrase)
        {
            var spoken = new HashSet<string>(text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(Stem));
            var saved = phrase.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(Stem).ToList();
            return spoken.Count > 0 && spoken.All(x => x.Length >= 3) && spoken.All(x => saved.Contains(x));
        }

        private static double SimilarityAgainstText(string text, string phrase)
        {
            if (text == phrase) return 1.0;
            if (text.Contains(phrase)) return 0.96;
            var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var target = phrase.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || target.Length == 0) return 0;
            var best = 0.0;
            for (var size = Math.Max(1, target.Length - 1); size <= Math.Min(words.Length, target.Length + 1); size++)
                for (var i = 0; i + size <= words.Length; i++)
                {
                    var window = string.Join(" ", words.Skip(i).Take(size).ToArray());
                    best = Math.Max(best, Math.Max(EditSimilarity(window, phrase), TokenSimilarity(window, phrase)));
                }
            return best;
        }

        private static double TokenSimilarity(string a, string b)
        {
            var aa = new HashSet<string>(a.Split(' ').Select(Stem));
            var bb = new HashSet<string>(b.Split(' ').Select(Stem));
            if (aa.Count == 0 || bb.Count == 0) return 0;
            return 2.0 * aa.Intersect(bb).Count() / (aa.Count + bb.Count);
        }

        private static string Stem(string word)
        {
            var endings = new[] { "иями", "ами", "ого", "ему", "ому", "ыми", "ими", "ать", "ить", "еть", "ять", "уй", "ой", "ый", "ий", "ая", "яя", "ое", "ее", "ые", "ие", "ы", "и", "а", "я", "о", "е" };
            foreach (var ending in endings)
                if (word.Length > ending.Length + 2 && word.EndsWith(ending, StringComparison.Ordinal))
                    return word.Substring(0, word.Length - ending.Length);
            return word;
        }

        private static double EditSimilarity(string a, string b)
        {
            var max = Math.Max(a.Length, b.Length);
            return max == 0 ? 1 : 1.0 - (double)Levenshtein(a, b) / max;
        }

        private static int Levenshtein(string a, string b)
        {
            var previous = Enumerable.Range(0, b.Length + 1).ToArray();
            for (var i = 0; i < a.Length; i++)
            {
                var current = new int[b.Length + 1];
                current[0] = i + 1;
                for (var j = 0; j < b.Length; j++)
                {
                    var cost = a[i] == b[j] ? 0 : 1;
                    current[j + 1] = Math.Min(Math.Min(current[j] + 1, previous[j + 1] + 1), previous[j] + cost);
                }
                previous = current;
            }
            return previous[b.Length];
        }
    }
}