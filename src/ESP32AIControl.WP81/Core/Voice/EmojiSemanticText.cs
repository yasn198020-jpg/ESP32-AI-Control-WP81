using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ESP32AIControl.WP81.Core.Voice
{
    /// <summary>Maps common IoT and interface emoji to Russian semantic words for matching and speech.</summary>
    public static class EmojiSemanticText
    {
        private static readonly Dictionary<string, string> Replacements = new Dictionary<string, string>
        {
            { "⚙️", "настройки" }, { "⚙", "настройки" }, { "🍅", "помидоры" }, { "🥒", "огурцы" },
            { "🥕", "морковь" }, { "🌶️", "перец" }, { "🌶", "перец" }, { "🍆", "баклажаны" },
            { "🌱", "растения" }, { "🌿", "зелень" }, { "🌳", "дерево" }, { "🌾", "урожай" },
            { "🔨", "инструменты" }, { "🔧", "инструменты" }, { "🛠", "инструменты" },
            { "🌡️", "температура" }, { "🌡", "температура" }, { "💧", "вода влажность" },
            { "💦", "вода влажность" }, { "🚿", "полив" }, { "🌧", "дождь вода" },
            { "☔", "дождь" }, { "🌊", "вода" }, { "💨", "ветер воздух" }, { "☀", "солнце свет" },
            { "🌤", "погода солнце" }, { "⛅", "погода облако" }, { "☁", "облако" },
            { "❄", "холод" }, { "🔥", "нагрев тепло" }, { "💡", "свет лампа" },
            { "🚪", "дверь" }, { "🪟", "окно форточка" }, { "🏠", "дом" },
            { "🔌", "питание" }, { "🔋", "батарея" }, { "⚡", "электричество" },
            { "📡", "связь сигнал" }, { "📶", "связь сигнал" }, { "🌐", "сеть интернет" },
            { "📈", "графики рост" }, { "📉", "графики снижение" }, { "📊", "статистика графики" },
            { "🔔", "уведомления" }, { "🔕", "уведомления без звука" }, { "⏰", "время будильник" },
            { "📅", "дата календарь" }, { "▶", "запуск выполнить" }, { "⏸", "пауза" },
            { "⏹", "стоп остановить" }, { "🔄", "обновить" }, { "✅", "готово включено" },
            { "❌", "ошибка выключено" }, { "⚠", "предупреждение" }, { "ℹ", "информация" },
            { "🔒", "закрыто блокировка" }, { "🔓", "открыто разблокировка" }, { "🔑", "ключ доступ" },
            { "🌀", "вентилятор" }, { "🔍", "поиск" }, { "🗑", "удалить очистка" },
            { "✏", "изменить" }, { "➕", "добавить" }, { "➖", "убрать" },
            { "⬆", "вверх" }, { "⬇", "вниз" }, { "⬅", "назад влево" }, { "➡", "вперед вправо" }
        };

        public static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            var result = value;
            foreach (var pair in Replacements) result = result.Replace(pair.Key, " " + pair.Value + " ");
            return RussianSemanticNormalizer.Normalize(result);
        }

        public static string ForSpeech(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var result = value;
            foreach (var pair in Replacements) result = result.Replace(pair.Key, " " + pair.Value + " ");
            var output = new StringBuilder(result.Length);
            foreach (var ch in result)
            {
                if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) || ",.!?;:-()".IndexOf(ch) >= 0)
                    output.Append(ch);
                else if (char.IsSurrogate(ch))
                    continue;
                else
                    output.Append(' ');
            }
            return Regex.Replace(output.ToString(), @"\s+", " ").Trim();
        }

        public static bool IsEmojiOnly(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            foreach (var ch in value)
                if (char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch)) return false;
            return true;
        }
    }
}