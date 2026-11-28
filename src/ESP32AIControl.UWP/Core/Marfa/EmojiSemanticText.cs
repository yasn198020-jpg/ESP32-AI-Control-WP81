using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ESP32AIControl.UWP.Core.Marfa
{
    // Port of Android marfa-core EmojiSemanticText. Original UI labels are not changed.
    public static class EmojiSemanticText
    {
        private static readonly Dictionary<string, string> Replacements = new Dictionary<string, string>
        {
            ["⚙️"] = "настройки",
            ["⚙"] = "настройки",
            ["🍅"] = "помидоры",
            ["🥒"] = "огурцы",
            ["🥕"] = "морковь",
            ["🌶️"] = "перец",
            ["🌶"] = "перец",
            ["🍆"] = "баклажаны",
            ["🥬"] = "зелень",
            ["🥦"] = "капуста",
            ["🌽"] = "кукуруза",
            ["🍓"] = "клубника",
            ["🍇"] = "виноград",
            ["🍎"] = "яблоки",
            ["🌱"] = "растения",
            ["🌿"] = "зелень",
            ["☘️"] = "растения",
            ["☘"] = "растения",
            ["🌳"] = "дерево",
            ["🌲"] = "дерево",
            ["🪴"] = "растения горшок",
            ["🌾"] = "урожай",
            ["🔨"] = "инструменты",
            ["⚒️"] = "инструменты",
            ["⚒"] = "инструменты",
            ["🛠️"] = "инструменты",
            ["🛠"] = "инструменты",
            ["🔧"] = "инструменты",
            ["🔩"] = "крепеж",
            ["🪛"] = "отвертка",
            ["🪚"] = "пила",
            ["🌡️"] = "температура",
            ["🌡"] = "температура",
            ["💧"] = "вода влажность",
            ["💦"] = "вода влажность",
            ["🚿"] = "полив душ",
            ["🌧️"] = "дождь вода",
            ["🌧"] = "дождь вода",
            ["☔"] = "дождь",
            ["🌊"] = "вода поток",
            ["💨"] = "ветер воздух",
            ["🌬️"] = "ветер воздух",
            ["🌬"] = "ветер воздух",
            ["☀️"] = "солнце свет",
            ["☀"] = "солнце свет",
            ["🌤️"] = "погода солнце",
            ["🌤"] = "погода солнце",
            ["⛅"] = "погода облако",
            ["☁️"] = "облако",
            ["☁"] = "облако",
            ["❄️"] = "холод",
            ["❄"] = "холод",
            ["🥶"] = "холод",
            ["🔥"] = "нагрев тепло",
            ["♨️"] = "нагрев тепло",
            ["♨"] = "нагрев тепло",
            ["💡"] = "свет лампа",
            ["🔆"] = "свет яркость",
            ["🔅"] = "свет яркость",
            ["🚪"] = "дверь",
            ["🪟"] = "окно форточка",
            ["🏠"] = "дом",
            ["🔌"] = "питание электричество",
            ["🔋"] = "питание батарея",
            ["⚡"] = "электричество питание",
            ["🔦"] = "свет фонарь",
            ["📡"] = "связь сигнал",
            ["📶"] = "связь сигнал",
            ["🌐"] = "сеть интернет",
            ["📈"] = "графики рост",
            ["📉"] = "графики снижение",
            ["📊"] = "статистика графики",
            ["🔔"] = "уведомления звонок",
            ["🔕"] = "уведомления без звука",
            ["⏰"] = "время будильник",
            ["⏱️"] = "таймер время",
            ["⏱"] = "таймер время",
            ["🕒"] = "время часы",
            ["📅"] = "дата календарь",
            ["▶️"] = "запуск выполнить",
            ["▶"] = "запуск выполнить",
            ["⏸️"] = "пауза",
            ["⏸"] = "пауза",
            ["⏹️"] = "стоп остановить",
            ["⏹"] = "стоп остановить",
            ["🔄"] = "обновить перезапуск",
            ["🔃"] = "обновить",
            ["🔁"] = "повтор",
            ["✅"] = "готово да включено",
            ["☑️"] = "выбрано готово",
            ["☑"] = "выбрано готово",
            ["❌"] = "ошибка нет выключено",
            ["⛔"] = "запрет остановка",
            ["⚠️"] = "предупреждение",
            ["⚠"] = "предупреждение",
            ["ℹ️"] = "информация",
            ["ℹ"] = "информация",
            ["❓"] = "вопрос",
            ["❔"] = "вопрос",
            ["🔒"] = "закрыто блокировка",
            ["🔓"] = "открыто разблокировка",
            ["🔑"] = "ключ доступ",
            ["🛡️"] = "защита",
            ["🛡"] = "защита",
            ["🌀"] = "вентилятор вращение",
            ["🪭"] = "вентилятор",
            ["🧭"] = "направление",
            ["👁️"] = "просмотр",
            ["👁"] = "просмотр",
            ["🔍"] = "поиск",
            ["🔎"] = "поиск",
            ["🗑️"] = "удалить очистка",
            ["🗑"] = "удалить очистка",
            ["✏️"] = "изменить редактировать",
            ["✏"] = "изменить редактировать",
            ["➕"] = "добавить",
            ["➖"] = "убрать",
            ["➗"] = "разделить",
            ["⬆️"] = "вверх",
            ["⬆"] = "вверх",
            ["⬇️"] = "вниз",
            ["⬇"] = "вниз",
            ["⬅️"] = "назад влево",
            ["⬅"] = "назад влево",
            ["➡️"] = "вперед вправо",
            ["➡"] = "вперед вправо",
            ["🏡"] = "дом теплица"
        };
        private static readonly Dictionary<string, string> UnicodeNames = new Dictionary<string, string>
        {
            ["gear"] = "настройки", ["tomato"] = "помидоры", ["cucumber"] = "огурцы",
            ["hammer"] = "инструменты", ["door"] = "дверь", ["window"] = "окно",
            ["house"] = "дом", ["seedling"] = "растения", ["herb"] = "зелень",
            ["thermometer"] = "температура", ["droplet"] = "вода", ["light bulb"] = "свет",
            ["plug"] = "питание", ["satellite antenna"] = "связь", ["chart"] = "графики",
            ["bell"] = "уведомления", ["sun"] = "солнце", ["cloud"] = "облако",
            ["snowflake"] = "холод", ["fire"] = "нагрев", ["robot face"] = "робот"
        };
        public static string Normalize(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return value ?? "";
            string text = ReplaceKnown(value);
            var output = new StringBuilder(text.Length + 16);
            for (int i = 0; i < text.Length;)
            {
                int cp = Char.ConvertToUtf32(text, i);
                int width = Char.IsSurrogatePair(text, i) ? 2 : 1;
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(text, i);
                if (Char.IsLetterOrDigit(text, i) || Char.IsWhiteSpace(text, i)) output.Append(Char.ConvertFromUtf32(cp));
                else if (cp == 0x200D || cp == 0xFE0E || cp == 0xFE0F || (cp >= 0x1F3FB && cp <= 0x1F3FF) || cp == 0x20E3 || (cp >= 0xE0020 && cp <= 0xE007F)) { }
                else if (category == UnicodeCategory.OtherSymbol || category == UnicodeCategory.ModifierSymbol || category == UnicodeCategory.MathSymbol || (cp >= 0x1F000 && cp <= 0x1FAFF) || (cp >= 0x2600 && cp <= 0x27BF))
                {
                    output.Append(' ').Append("эмодзи").Append(' ');
                }
                else output.Append(' ');
                i += width;
            }
            return Regex.Replace(output.ToString().ToLowerInvariant().Replace('ё','е'), @"\s+", " ").Trim();
        }
        public static string ForSpeech(string value) { return Normalize(value); }
        public static bool IsEmojiOnly(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return false;
            return Normalize(value).Length == 0 || !value.Any(Char.IsLetterOrDigit) && !value.Any(Char.IsWhiteSpace);
        }
        private static string ReplaceKnown(string value)
        {
            string text = value;
            foreach (var pair in Replacements) text = text.Replace(pair.Key, " " + pair.Value + " ");
            return text;
        }
    }
}