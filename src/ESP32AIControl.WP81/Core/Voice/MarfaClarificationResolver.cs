using System;
using System.Collections.Generic;
using System.Linq;

namespace ESP32AIControl.WP81.Core.Voice
{
    public sealed class MarfaClarificationCandidate
    {
        public string DeviceId { get; set; }
        public string WidgetId { get; set; }
        public string Title { get; set; }
        public string Page { get; set; }
        public string DeviceName { get; set; }
    }

    /// <summary>Resolves ordinal, semantic and multi-selection answers against a presented candidate list.</summary>
    public static class MarfaClarificationResolver
    {
        private static readonly string[] StopWords = { "а", "и", "на", "во", "в", "по", "к", "ко", "у", "из", "для", "это", "эта", "этот", "там", "здесь", "нет", "да", "пожалуйста", "марфа" };
        private static readonly string[] AllWords = { "все", "всех", "всем", "всеми", "везде", "всюду", "повсюду", "повсеместно", "каждый", "каждая", "каждое", "каждую", "каждого", "каждому", "каждым", "каждой", "каждые", "каждых", "целиком", "полностью" };
        private static readonly string[] BothWords = { "оба", "обе", "обоих", "обеих", "обоим", "обеим" };
        private static readonly string[][] Ordinals = {
            new[] { "первый", "первая", "первое", "первую", "первого", "первом", "первых" },
            new[] { "второй", "вторая", "второе", "вторую", "второго", "втором", "вторых" },
            new[] { "третий", "третья", "третье", "третью", "третьего", "третьем", "третьих" },
            new[] { "четвертый", "четвертая", "четвертую", "четвертого", "четвертом", "четвертых" },
            new[] { "пятый", "пятая", "пятое", "пятую", "пятого", "пятом", "пятых" }
        };

        public static IList<int> Resolve(string text, IList<MarfaClarificationCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0) return null;
            var tokens = RussianSemanticNormalizer.Tokens(text ?? string.Empty);
            if (tokens.Count == 0) return null;

            for (var i = 0; i < candidates.Count; i++)
                if (tokens.Any(t => string.Equals(t, (candidates[i].WidgetId ?? string.Empty).ToLowerInvariant(), StringComparison.Ordinal)))
                    return new List<int> { i };

            int ordinal = -1;
            for (var n = 0; n < Ordinals.Length && ordinal < 0; n++)
                if (tokens.Any(t => Ordinals[n].Contains(t))) ordinal = n;
            if (ordinal < 0)
            {
                var number = tokens.FirstOrDefault(t => t.Length == 1 && t[0] >= '1' && t[0] <= '9');
                if (number != null && tokens.Contains("номер")) ordinal = int.Parse(number) - 1;
            }

            var scores = candidates.Select(c => Score(tokens, c)).ToList();
            var positive = Enumerable.Range(0, scores.Count).Where(i => scores[i] > 0).ToList();
            if (tokens.Any(t => t == "и" || t == "а") && positive.Count >= 2) return positive;

            if (ordinal >= 0 && ordinal < candidates.Count)
            {
                var collision = candidates.Any(c =>
                    RussianSemanticNormalizer.Tokens((c.Title ?? "") + " " + (c.Page ?? "") + " " + (c.DeviceName ?? ""))
                    .Any(t => Ordinals[ordinal].Contains(t)));
                if (!collision) return new List<int> { ordinal };
            }

            if (tokens.Any(t => BothWords.Contains(t)))
                return candidates.Count == 2 ? (IList<int>)new List<int> { 0, 1 } : null;

            if (positive.Count > 0)
            {
                var max = positive.Max(i => scores[i]);
                var best = positive.Where(i => scores[i] == max).ToList();
                if (best.Count == 1) return best;
            }
            if (tokens.Any(t => AllWords.Contains(t)))
                return Enumerable.Range(0, candidates.Count).ToList();
            return null;
        }

        private static int Score(IList<string> query, MarfaClarificationCandidate candidate)
        {
            var title = RussianSemanticNormalizer.Tokens(candidate.Title ?? string.Empty);
            var page = RussianSemanticNormalizer.Tokens(candidate.Page ?? string.Empty);
            var device = RussianSemanticNormalizer.Tokens(candidate.DeviceName ?? string.Empty);
            var score = 0;
            foreach (var token in query)
            {
                if (token.Length < 3 || StopWords.Contains(token) || AllWords.Contains(token) || BothWords.Contains(token)) continue;
                if (title.Contains(token)) score += 8;
                if (page.Contains(token)) score += 10;
                if (device.Contains(token)) score += 4;
            }
            return score;
        }
    }
}