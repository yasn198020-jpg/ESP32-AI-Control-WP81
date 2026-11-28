using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ESP32AIControl.UWP.Core.Marfa
{
    public static class TrainedCommandConstants { public const string ReadValue = "__READ_VALUE__"; }

    public sealed class TrainedCommandMatcher
    {
        private readonly ITrainedCommandRepository store;
        public TrainedCommandMatcher(ITrainedCommandRepository repository) { store = repository; }

        public TrainedVoiceCommand Match(string text) { return MatchAll(text).FirstOrDefault(); }

        public IList<TrainedVoiceCommand> MatchAll(string text)
        {
            string normalized = Normalize(text);
            if (normalized.Length == 0 || store == null) return new List<TrainedVoiceCommand>();
            var commands = store.Load() ?? new List<TrainedVoiceCommand>();
            if (commands.Count == 0) return new List<TrainedVoiceCommand>();

            var exactKeys = new HashSet<string>();
            foreach (var command in commands)
                if ((command.Variants ?? new List<string>()).Any(v => Normalize(v) == normalized) || Normalize(command.Phrase) == normalized)
                    if (Normalize(command.Phrase).Length > 0) exactKeys.Add(Normalize(command.Phrase));
            if (exactKeys.Count == 1) return commands.Where(c => exactKeys.Contains(Normalize(c.Phrase))).ToList();
            if (exactKeys.Count > 1) return new List<TrainedVoiceCommand>();

            var contained = new Dictionary<string, int>();
            foreach (var group in commands.GroupBy(c => Normalize(c.Phrase)))
            {
                int specificity = (group.SelectMany(c => c.Variants ?? new List<string>()).Select(Normalize)
                    .Where(v => v.Length > 0 && ContainsPhrase(normalized, v)).Select(v => v.Length).DefaultIfEmpty(0).Max());
                if (specificity > 0) contained[group.Key] = specificity;
            }
            if (contained.Count > 0)
            {
                int best = contained.Values.Max();
                var keys = new HashSet<string>(contained.Where(x => x.Value == best).Select(x => x.Key));
                return keys.Count == 1 ? commands.Where(c => keys.Contains(Normalize(c.Phrase))).ToList() : new List<TrainedVoiceCommand>();
            }

            var partial = new Dictionary<string, double>();
            foreach (var group in commands.GroupBy(c => Normalize(c.Phrase)))
            {
                var scores = group.SelectMany(c => c.Variants ?? new List<string>()).Select(Normalize)
                    .Where(v => v.Length > 0 && PhraseWordsContainedInText(normalized, v))
                    .Select(v => SimilarityAgainstText(normalized, v)).ToList();
                if (scores.Count > 0) partial[group.Key] = scores.Max();
            }
            if (partial.Count > 0)
            {
                double best = partial.Values.Max();
                var keys = new HashSet<string>(partial.Where(x => x.Value >= best - 0.04).Select(x => x.Key));
                return keys.Count == 1 ? commands.Where(c => keys.Contains(Normalize(c.Phrase))).ToList() : new List<TrainedVoiceCommand>();
            }

            var candidates = new Dictionary<string, double>();
            foreach (var group in commands.GroupBy(c => Normalize(c.Phrase)))
            {
                var variants = group.SelectMany(c => (c.Variants ?? new List<string>()).Concat(new[] { c.Phrase }))
                    .Select(Normalize).Where(v => v.Length > 0).Distinct().ToList();
                if (variants.Count > 0)
                {
                    double score = variants.Max(v => SimilarityAgainstText(normalized, v));
                    if (score >= 0.78) candidates[group.Key] = score;
                }
            }
            if (candidates.Count == 0) return new List<TrainedVoiceCommand>();
            double bestScore = candidates.Values.Max();
            var bestGroups = new HashSet<string>(candidates.Where(x => x.Value >= bestScore - 0.04).Select(x => x.Key));
            return bestGroups.Count == 1 ? commands.Where(c => bestGroups.Contains(Normalize(c.Phrase))).ToList() : new List<TrainedVoiceCommand>();
        }

        private static bool ContainsPhrase(string text, string phrase)
        { return text == phrase || (" " + text + " ").Contains(" " + phrase + " "); }

        private static bool PhraseWordsContainedInText(string text, string phrase)
        {
            var textWords = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(Stem).ToList();
            var phraseWords = phrase.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(Stem).ToList();
            if (textWords.Count == 0 || phraseWords.Count == 0 || textWords.Any(w => w.Length < 3)) return false;
            return textWords.All(w => phraseWords.Contains(w));
        }

        private static double SimilarityAgainstText(string text, string phrase)
        {
            if (text == phrase) return 1.0;
            if (text.Contains(phrase)) return 0.96;
            var words = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var phraseWords = phrase.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || phraseWords.Length == 0) return 0.0;
            double best = 0.0;
            for (int size = Math.Max(1, phraseWords.Length - 1); size <= Math.Min(words.Length, phraseWords.Length + 1); size++)
                for (int i = 0; i <= words.Length - size; i++)
                {
                    string window = String.Join(" ", words.Skip(i).Take(size));
                    best = Math.Max(best, Math.Max(EditSimilarity(window, phrase), TokenSimilarity(window, phrase)));
                }
            return best;
        }

        private static double TokenSimilarity(string a, string b)
        {
            var aa = new HashSet<string>(a.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(Stem));
            var bb = new HashSet<string>(b.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(Stem));
            if (aa.Count == 0 || bb.Count == 0) return 0.0;
            return 2.0 * aa.Intersect(bb).Count() / (aa.Count + bb.Count);
        }

        private static string Stem(string word)
        {
            string w = word.ToLowerInvariant();
            string[] endings = { "иями", "ами", "ого", "ему", "ому", "ыми", "ими", "ать", "ить", "еть", "ять",
                "уй", "ой", "ый", "ий", "ая", "яя", "ое", "ее", "ые", "ие", "ы", "и", "а", "я", "о", "е" };
            foreach (var ending in endings)
                if (w.Length > ending.Length + 2 && w.EndsWith(ending, StringComparison.Ordinal))
                    return w.Substring(0, w.Length - ending.Length);
            return w;
        }

        private static double EditSimilarity(string a, string b)
        {
            int max = Math.Max(a.Length, b.Length);
            return max == 0 ? 1.0 : 1.0 - (double)Levenshtein(a, b) / max;
        }

        private static int Levenshtein(string a, string b)
        {
            if (a == b) return 0;
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;
            int[] previous = Enumerable.Range(0, b.Length + 1).ToArray();
            for (int i = 0; i < a.Length; i++)
            {
                var current = new int[b.Length + 1]; current[0] = i + 1;
                for (int j = 0; j < b.Length; j++)
                {
                    int cost = a[i] == b[j] ? 0 : 1;
                    current[j + 1] = Math.Min(Math.Min(current[j] + 1, previous[j + 1] + 1), previous[j] + cost);
                }
                previous = current;
            }
            return previous[b.Length];
        }

        private static string Normalize(string value)
        {
            string text = (value ?? "").ToLowerInvariant().Replace('ё', 'е');
            text = Regex.Replace(text, @"[^a-zа-я0-9]+", " ").Trim();
            text = Regex.Replace(text, @"^марф(а|у|е|ой)\s*", "");
            return Regex.Replace(text, @"\s+", " ").Trim();
        }
    }
}