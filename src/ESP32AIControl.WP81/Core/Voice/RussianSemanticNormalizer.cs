using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ESP32AIControl.WP81.Core.Voice
{
    /// <summary>
    /// Deterministic Russian normalization shared by speech and widget names.
    /// Kept dependency-free for the Windows Phone 8.1 runtime.
    /// </summary>
    public static class RussianSemanticNormalizer
    {
        private static readonly Dictionary<string, string> Irregular =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "двери", "дверь" }, { "дверью", "дверь" }, { "дверям", "дверь" },
            { "дверями", "дверь" }, { "дверях", "дверь" }, { "дверей", "дверь" },
            { "огурцы", "огурец" }, { "огурца", "огурец" }, { "огурцу", "огурец" },
            { "огурцом", "огурец" }, { "огурцами", "огурец" }, { "огурцов", "огурец" },
            { "помидоры", "помидор" }, { "помидора", "помидор" }, { "помидору", "помидор" },
            { "помидором", "помидор" }, { "помидорами", "помидор" }, { "помидоров", "помидор" },
            { "открой", "открыть" }, { "откройте", "открыть" }, { "открывай", "открыть" },
            { "открывал", "открыть" }, { "открывала", "открыть" }, { "открыли", "открыть" },
            { "открывает", "открыть" }, { "открывают", "открыть" }, { "открою", "открыть" },
            { "закрой", "закрыть" }, { "закройте", "закрыть" }, { "закрывай", "закрыть" },
            { "закрывал", "закрыть" }, { "закрывала", "закрыть" }, { "закрыли", "закрыть" },
            { "закрывает", "закрыть" }, { "закрывают", "закрыть" }, { "закрою", "закрыть" },
            { "включи", "включить" }, { "включите", "включить" }, { "включай", "включить" },
            { "вруби", "включить" }, { "врубите", "включить" }, { "зажги", "включить" },
            { "зажгите", "включить" }, { "включал", "включить" }, { "включала", "включить" },
            { "выключи", "выключить" }, { "выключите", "выключить" }, { "выключай", "выключить" },
            { "выруби", "выключить" }, { "вырубите", "выключить" }, { "погаси", "выключить" },
            { "погасите", "выключить" }, { "отруби", "выключить" },
            { "запусти", "запустить" }, { "запустите", "запустить" },
            { "останови", "остановить" }, { "остановите", "остановить" }, { "прекрати", "остановить" },
            { "покажи", "показать" }, { "покажите", "показать" }, { "скажи", "сказать" }, { "скажите", "сказать" },
            { "температуры", "температура" }, { "температурой", "температура" },
            { "влажности", "влажность" }, { "влажностью", "влажность" },
            { "давления", "давление" }, { "давлением", "давление" },
            { "форточки", "форточка" }, { "форточку", "форточка" }, { "форточкой", "форточка" },
            { "форточками", "форточка" }, { "окна", "окно" }, { "окном", "окно" }, { "окнами", "окно" },
            { "насоса", "насос" }, { "насосу", "насос" }, { "насосом", "насос" }, { "насосы", "насос" },
            { "света", "свет" }, { "светом", "свет" }, { "свету", "свет" },
            { "лампу", "лампа" }, { "лампы", "лампа" }, { "лампе", "лампа" }, { "лампой", "лампа" },
            { "обогреватель", "обогрев" }, { "обогревателя", "обогрев" }, { "обогревателем", "обогрев" },
            { "обогреватели", "обогрев" }, { "отопление", "обогрев" }, { "отопления", "обогрев" },
            { "градуса", "градус" }, { "градусов", "градус" }, { "градусами", "градус" },
            { "минуты", "минута" }, { "минуту", "минута" }, { "минут", "минута" },
            { "секунды", "секунда" }, { "секунду", "секунда" }, { "секунд", "секунда" },
            { "часа", "час" }, { "часов", "час" }
        };

        private static readonly string[] Endings =
        {
            "иями", "ями", "ами", "ого", "ему", "ому", "ыми", "ими",
            "ией", "ией", "иях", "ами", "ями", "ах", "ях", "ом", "ем",
            "ым", "им", "ую", "юю", "ая", "яя", "ое", "ее", "ые", "ие",
            "ой", "ый", "ий", "ов", "ев", "ам", "ям", "ей", "ью", "ию",
            "ы", "и", "а", "я", "у", "ю", "о", "е", "ь", "й"
        };

        public static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var text = value.ToLowerInvariant().Replace('ё', 'е');
            text = Regex.Replace(text, @"[^а-яa-z0-9_./=\-\s]", " ");
            var tokens = Regex.Split(text, @"\s+").Where(t => t.Length > 0);
            return string.Join(" ", tokens.Select(NormalizeToken).Where(t => t.Length > 0).ToArray()).Trim();
        }

        public static string NormalizeToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return string.Empty;
            var word = token.Trim().Trim('.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '"', '\'');
            if (word.Length == 0) return string.Empty;
            string canonical;
            if (Irregular.TryGetValue(word, out canonical)) return canonical;
            var hasDigitOrLatin = false;
            for (var i = 0; i < word.Length; i++)
            {
                var ch = word[i];
                if (char.IsDigit(ch) || (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z'))
                {
                    hasDigitOrLatin = true;
                    break;
                }
            }
            if (hasDigitOrLatin || word.Contains("/") || word.Contains("_") || word.Contains("=")) return word;
            if (word.EndsWith("ся", StringComparison.Ordinal) || word.EndsWith("сь", StringComparison.Ordinal))
                return word;
            foreach (var ending in Endings)
                if (word.Length > ending.Length + 2 && word.EndsWith(ending, StringComparison.Ordinal))
                    return word.Substring(0, word.Length - ending.Length);
            return word;
        }

        public static IList<string> Tokens(string value)
        {
            return Normalize(value).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}