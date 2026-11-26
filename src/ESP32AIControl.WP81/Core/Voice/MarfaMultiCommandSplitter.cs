using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core.Voice
{
    /// <summary>Splits coordinated spoken actions while preserving page context.</summary>
    public static class MarfaMultiCommandSplitter
    {
        private static readonly Dictionary<string, string> Actions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "открой", "открыть" }, { "открыть", "открыть" }, { "открывай", "открыть" },
            { "закрой", "закрыть" }, { "закрыть", "закрыть" }, { "закрывай", "закрыть" },
            { "включи", "включить" }, { "включить", "включить" }, { "включай", "включить" },
            { "выключи", "выключить" }, { "выключить", "выключить" }, { "выключай", "выключить" },
            { "запусти", "запустить" }, { "запустить", "запустить" },
            { "останови", "остановить" }, { "остановить", "остановить" }
        };
        private static readonly HashSet<string> Entities = new HashSet<string>(new[]
        {
            "дверь", "форточка", "окно", "ворота", "насос", "вентилятор", "обогрев",
            "отопление", "нагрев", "клапан", "кран", "свет", "лампа", "полив",
            "орошение", "реле", "выход", "кнопка", "gpio", "канал"
        }, StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Ignored = new HashSet<string>(new[]
        {
            "и", "а", "но", "в", "во", "на", "для", "по", "из", "с", "со", "к", "ко",
            "у", "от", "до", "под", "над", "при", "про", "там", "здесь"
        }, StringComparer.OrdinalIgnoreCase);

        public static IList<string> Split(string text, IList<WidgetState> widgets)
        {
            var normalized = RussianSemanticNormalizer.Normalize(text ?? string.Empty);
            if (normalized.Length == 0) return new List<string>();
            if (Regex.IsMatch(normalized, @"^\s*(если|когда)\b"))
                return new List<string> { normalized };

            var pageWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var widget in widgets ?? new List<WidgetState>())
                foreach (var word in RussianSemanticNormalizer.Tokens(widget.Page ?? string.Empty))
                    if (!Ignored.Contains(word)) pageWords.Add(Stem(word));

            var clauses = SplitClauses(normalized);
            var result = new List<string>();
            foreach (var clause in clauses)
                result.AddRange(SplitClause(clause, pageWords));
            return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IList<string> SplitClauses(string text)
        {
            var tokens = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var starts = Enumerable.Range(0, tokens.Length).Where(i => Actions.ContainsKey(tokens[i])).ToList();
            if (starts.Count <= 1) return new List<string> { text };
            var result = new List<string>();
            for (var i = 0; i < starts.Count; i++)
            {
                var start = i == 0 ? 0 : starts[i];
                var end = i + 1 < starts.Count ? starts[i + 1] : tokens.Length;
                var part = string.Join(" ", tokens.Skip(start).Take(end - start).ToArray()).Trim(' ', ',', ';');
                if (part.Length > 0) result.Add(part);
            }
            return result;
        }

        private static IList<string> SplitClause(string clause, ISet<string> pageWords)
        {
            var explicitPage = Regex.Match(clause, @"(?:на\s+странице|на\s+вкладке|во\s+вкладке|в\s+вкладке)\s+(.+)$");
            if (explicitPage.Success) return new List<string> { clause };

            var tokens = clause.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var actionIndex = Array.FindIndex(tokens, t => Actions.ContainsKey(t));
            if (actionIndex < 0) return new List<string> { clause };
            var objects = Enumerable.Range(actionIndex + 1, Math.Max(0, tokens.Length - actionIndex - 1))
                .Where(i => Entities.Contains(RussianSemanticNormalizer.NormalizeToken(tokens[i]))).ToList();
            if (objects.Count < 2) return new List<string> { clause };

            var action = Actions[tokens[actionIndex]];
            var output = new List<string>();
            for (var n = 0; n < objects.Count; n++)
            {
                var index = objects[n];
                var end = n + 1 < objects.Count ? objects[n + 1] : tokens.Length;
                var context = new List<string>();
                for (var i = index + 1; i < end; i++)
                    if (pageWords.Contains(Stem(tokens[i]))) context.Add(tokens[i]);
                if (context.Count == 0)
                    for (var i = 0; i < index; i++)
                        if (pageWords.Contains(Stem(tokens[i]))) context.Add(tokens[i]);

                var command = action + " " + tokens[index];
                if (context.Count > 0) command += " " + string.Join(" ", context.Distinct().ToArray());
                output.Add(command);
            }
            return output;
        }

        private static string Stem(string word)
        {
            var endings = new[] { "ов", "ев", "ами", "ями", "ого", "ему", "ому", "ыми", "ими", "ая", "яя", "ое", "ее", "ые", "ие", "ой", "ый", "ий", "ам", "ям", "ах", "ях", "ы", "и", "а", "я", "о", "е" };
            foreach (var ending in endings)
                if (word.Length > ending.Length + 2 && word.EndsWith(ending, StringComparison.Ordinal))
                    return word.Substring(0, word.Length - ending.Length);
            return word;
        }
    }
}