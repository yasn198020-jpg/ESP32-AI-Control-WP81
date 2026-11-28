using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ESP32AIControl.UWP.Core.Marfa
{
    public sealed class MarfaClarificationCandidate
    {
        public string DeviceId { get; set; }
        public string WidgetId { get; set; }
        public string Title { get; set; }
        public string Page { get; set; }
        public string DeviceName { get; set; }
    }

    /// <summary>Resolves an answer against the exact ordered alternatives previously presented by Marfa.</summary>
    public static class MarfaClarificationResolver
    {
        private sealed class Ordinal
        {
            public int Index;
            public ISet<string> Markers;
        }

        private static readonly HashSet<string> StopWords = new HashSet<string>(new[]
        {
            "а","и","на","во","в","по","к","ко","у","из","для","это","эта","этот","там","здесь",
            "нет","да","пожалуйста","марфа"
        });
        private static readonly HashSet<string> AllWords = new HashSet<string>(new[]
        {
            "все","всех","всем","всеми","везде","всюду","повсюду","повсеместно",
            "каждый","каждая","каждое","каждую","каждого","каждому","каждым","каждой","каждые","каждых",
            "целиком","полностью"
        }.Select(RussianSemanticNormalizer.Lemma));
        private static readonly HashSet<string> BothWords = new HashSet<string>(
            new[] { "оба","обе","обоих","обеих","обоим","обеим" }.Select(RussianSemanticNormalizer.Lemma));
        private static readonly string[][] OrdinalForms =
        {
            new[] { "первый","первая","первое","первую","первого","первом","первых" },
            new[] { "второй","вторая","второе","вторую","второго","втором","вторых" },
            new[] { "третий","третья","третье","третью","третьего","третьем","третьих" },
            new[] { "четвертый","четвертая","четвертую","четвертого","четвертом","четвертых" },
            new[] { "пятый","пятая","пятое","пятую","пятого","пятом","пятых" }
        };
        private static readonly string[] NumberWords = { "один","два","три","четыре","пять","шесть","семь","восемь","девять" };

        public static IList<int> Resolve(string text, IList<MarfaClarificationCandidate> candidates)
        {
            if (candidates == null || candidates.Count == 0) return null;
            var tokens = RussianSemanticNormalizer.Tokens(text ?? "").Where(x => x.Length > 0).ToList();
            if (tokens.Count == 0) return null;

            for (int i = 0; i < candidates.Count; i++)
            {
                string id = candidates[i].WidgetId ?? "";
                if (tokens.Any(t => String.Equals(t, id.ToLowerInvariant(), StringComparison.Ordinal)))
                    return new List<int> { i };
            }

            var ordinal = FindOrdinal(tokens);
            var scores = candidates.Select(c => ScoreCandidate(tokens, c)).ToList();
            bool ordinalCollision = ordinal != null && candidates.Any(c => CandidateContainsOrdinal(c, ordinal));
            var positive = Enumerable.Range(0, scores.Count).Where(i => scores[i] > 0).ToList();
            bool coordinated = tokens.Any(t => t == "и" || t == "а");
            if (coordinated && positive.Count >= 2) return positive;

            if (ordinal != null && !ordinalCollision && ordinal.Index >= 0 && ordinal.Index < candidates.Count)
                return new List<int> { ordinal.Index };

            if (tokens.Any(t => BothWords.Contains(t)))
                return candidates.Count == 2 ? Enumerable.Range(0, 2).ToList() : null;

            if (positive.Count > 0)
            {
                int best = positive.Max(i => scores[i]);
                var tied = positive.Where(i => scores[i] == best).ToList();
                if (tied.Count == 1) return new List<int> { tied[0] };
            }

            if (tokens.Any(t => AllWords.Contains(t))) return Enumerable.Range(0, candidates.Count).ToList();
            return null;
        }

        private static int ScoreCandidate(IList<string> query, MarfaClarificationCandidate candidate)
        {
            var title = RussianSemanticNormalizer.Tokens(candidate.Title ?? "");
            var page = RussianSemanticNormalizer.Tokens(candidate.Page ?? "");
            var device = RussianSemanticNormalizer.Tokens(candidate.DeviceName ?? "");
            int score = 0;
            foreach (var token in query.Where(t => t.Length >= 3 && !StopWords.Contains(t) && !AllWords.Contains(t) && !BothWords.Contains(t)))
            {
                if (title.Contains(token)) score += 8;
                if (page.Contains(token)) score += 10;
                if (device.Contains(token)) score += 4;
            }
            return score;
        }

        private static bool CandidateContainsOrdinal(MarfaClarificationCandidate candidate, Ordinal ordinal)
        {
            var all = new HashSet<string>(RussianSemanticNormalizer.Tokens(candidate.Title ?? "")
                .Concat(RussianSemanticNormalizer.Tokens(candidate.Page ?? ""))
                .Concat(RussianSemanticNormalizer.Tokens(candidate.DeviceName ?? "")));
            return ordinal.Markers.Any(all.Contains);
        }

        private static Ordinal FindOrdinal(IList<string> tokens)
        {
            for (int i = 0; i < OrdinalForms.Length; i++)
            {
                var forms = new HashSet<string>(OrdinalForms[i].Select(RussianSemanticNormalizer.Lemma));
                if (tokens.Any(forms.Contains)) return new Ordinal { Index = i, Markers = forms };
            }
            int numberIndex = -1;
            for (int i = 0; i < NumberWords.Length; i++)
                if (tokens.Contains(NumberWords[i])) { numberIndex = tokens.IndexOf(NumberWords[i]); break; }
            if (numberIndex > 0 && tokens[numberIndex - 1] == "номер")
                return new Ordinal { Index = NumberWords.ToList().IndexOf(tokens[numberIndex]), Markers = new HashSet<string> { "номер", tokens[numberIndex] } };

            foreach (var token in tokens)
            {
                int number;
                if (Regex.IsMatch(token, "^[1-9]$") && Int32.TryParse(token, out number))
                    return new Ordinal { Index = number - 1, Markers = new HashSet<string> { token } };
            }
            return null;
        }
    }
}