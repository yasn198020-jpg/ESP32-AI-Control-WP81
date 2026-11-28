using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ESP32AIControl.UWP.Core.Marfa
{
    /// <summary>Splits coordinated speech into ordinary single-action phrases. Never executes commands.</summary>
    public static class MarfaMultiCommandSplitter
    {
        private static readonly IDictionary<string, string> ActionForms = new Dictionary<string, string>
        {
            { "открой", "открыть" }, { "открыть", "открыть" }, { "открывай", "открыть" },
            { "закрой", "закрыть" }, { "закрыть", "закрыть" }, { "закрывай", "закрыть" },
            { "включи", "включить" }, { "включить", "включить" }, { "включай", "включить" },
            { "выключи", "выключить" }, { "выключить", "выключить" }, { "выключай", "выключить" },
            { "запусти", "запустить" }, { "запустить", "запустить" },
            { "останови", "остановить" }, { "остановить", "остановить" }
        };

        private static readonly HashSet<string> EntityLemmas = new HashSet<string>(new[]
        {
            "дверь", "форточка", "форточк", "окно", "ворота", "насос", "вентилятор",
            "обогрев", "отопление", "нагрев", "клапан", "кран", "свет", "лампа",
            "полив", "орошение", "реле", "выход", "кнопка", "gpio", "канал"
        });
        private static readonly HashSet<string> IgnoredContextWords = new HashSet<string>(new[]
        {
            "и", "а", "но", "в", "во", "на", "для", "по", "из", "с", "со", "к", "ко",
            "у", "от", "до", "под", "над", "при", "про", "там", "здесь"
        });

        private static string CanonicalEntity(string token) { return token == "форточк" ? "форточка" : token; }

        private static string Stem(string token)
        {
            string[] endings = { "ов", "ев", "ами", "ями", "ого", "ему", "ому", "ыми", "ими",
                "ая", "яя", "ое", "ее", "ые", "ие", "ой", "ый", "ий", "ам", "ям", "ах", "ях",
                "ы", "и", "а", "я", "о", "е" };
            foreach (var ending in endings)
                if (token.Length > ending.Length + 2 && token.EndsWith(ending, StringComparison.Ordinal))
                    return token.Substring(0, token.Length - ending.Length);
            return token;
        }

        public static IList<string> Split(string text, IList<MarfaDevice> devices)
        {
            var normalized = RussianSemanticNormalizer.Normalize(text ?? "");
            if (String.IsNullOrWhiteSpace(normalized)) return new List<string>();
            if (Regex.IsMatch(normalized, @"^\s*(если|когда)\b")) return new List<string> { normalized };

            var pageContextStems = new HashSet<string>();
            foreach (var device in devices ?? new List<MarfaDevice>())
                foreach (var widget in device.Widgets ?? new List<MarfaWidgetState>())
                    foreach (var token in RussianSemanticNormalizer.Tokens(widget.Page ?? ""))
                        if (!IgnoredContextWords.Contains(token)) pageContextStems.Add(Stem(token));

            return SplitActionClauses(normalized)
                .SelectMany(clause => SplitClause(clause, pageContextStems))
                .Distinct().ToList();
        }

        private static IList<string> SplitActionClauses(string text)
        {
            var tokens = Regex.Split(text, @"\s+").Where(x => x.Length > 0).ToList();
            var starts = Enumerable.Range(0, tokens.Count).Where(i => ActionForms.ContainsKey(tokens[i])).ToList();
            if (starts.Count <= 1) return new List<string> { text };
            var result = new List<string>();
            for (int i = 0; i < starts.Count; i++)
            {
                int start = i == 0 ? 0 : starts[i];
                int end = i + 1 < starts.Count ? starts[i + 1] : tokens.Count;
                result.Add(String.Join(" ", tokens.Skip(start).Take(end - start)).Trim(' ', ',', ';'));
            }
            return result.Where(x => !String.IsNullOrWhiteSpace(x)).ToList();
        }

        private static IList<string> SplitClause(string clause, ISet<string> pageContextStems)
        {
            var tokens = Regex.Split(clause, @"\s+").Where(x => x.Length > 0).ToList();
            var explicitPage = Regex.Match(clause, @"(?:на\s+страниц(?:е|у)|на\s+вкладк(?:е|у)|во\s+вкладк(?:е|у)|в\s+вкладк(?:е|у)|страниц(?:а|у)|вкладк(?:а|у))\s+");
            if (explicitPage.Success && Regex.IsMatch(clause.Substring(explicitPage.Index + explicitPage.Length), @"\d"))
                return new List<string> { clause };

            int actionIndex = tokens.FindIndex(ActionForms.ContainsKey);
            if (actionIndex < 0) return new List<string> { clause };
            string action = ActionForms[tokens[actionIndex]];
            var objectIndices = Enumerable.Range(0, tokens.Count).Where(i => i != actionIndex && EntityLemmas.Contains(tokens[i])).ToList();
            if (objectIndices.Count == 0) return new List<string> { clause };

            var contextIndices = Enumerable.Range(0, tokens.Count).Where(i =>
                i != actionIndex && !EntityLemmas.Contains(tokens[i]) &&
                pageContextStems.Contains(Stem(tokens[i])) && !IgnoredContextWords.Contains(tokens[i])).ToList();
            var sharedLeading = contextIndices.Where(i => i < objectIndices[0]).Select(i => CanonicalEntity(tokens[i])).Distinct().ToList();
            var localContexts = new List<IList<string>>();
            for (int i = 0; i < objectIndices.Count; i++)
            {
                int objectIndex = objectIndices[i];
                int nextObject = i + 1 < objectIndices.Count ? objectIndices[i + 1] : tokens.Count;
                localContexts.Add(contextIndices.Where(x => x > objectIndex && x < nextObject)
                    .Select(x => CanonicalEntity(tokens[x])).Distinct().ToList());
            }

            var trailing = localContexts.LastOrDefault() ?? new List<string>();
            bool earlierExplicit = localContexts.Take(Math.Max(0, localContexts.Count - 1)).Any(x => x.Count > 0);
            bool shareTrailing = trailing.Count > 0 && !earlierExplicit && sharedLeading.Count == 0;
            var output = new List<string>();
            for (int i = 0; i < objectIndices.Count; i++)
            {
                var own = localContexts[i];
                var contexts = shareTrailing ? trailing : own.Count > 0 ? own : sharedLeading;
                if (contexts.Count == 0) contexts = new List<string> { "" };
                foreach (var context in contexts)
                {
                    var words = new List<string> { CanonicalEntity(tokens[objectIndices[i]]) };
                    if (!String.IsNullOrWhiteSpace(context) && !words.Contains(context)) words.Add(context);
                    output.Add(action + " " + String.Join(" ", words));
                }
            }
            return output;
        }
    }
}