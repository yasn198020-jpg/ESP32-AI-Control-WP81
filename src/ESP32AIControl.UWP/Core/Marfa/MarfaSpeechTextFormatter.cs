using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ESP32AIControl.UWP.Core.Marfa
{
    public static class MarfaSpeechTextFormatter
    {
        public static string FormatTemperatureForSpeech(string raw, string unit = "°C", string description = "")
        {
            string source = (raw ?? "").Trim().Replace(',', '.');
            decimal number;
            if (!Decimal.TryParse(source, NumberStyles.Number, CultureInfo.InvariantCulture, out number))
                return String.IsNullOrWhiteSpace(unit) ? (raw ?? "") : (raw ?? "") + " " + unit;

            string value = number.ToString("0.############################", CultureInfo.InvariantCulture).Replace('.', ',');
            string text = (description ?? "").ToLowerInvariant().Replace('ё', 'е');
            bool percent = (unit ?? "").Contains("%") || text.Contains("влажн");
            bool temperature = (unit ?? "").Contains("°") || String.Equals(unit, "c", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("температур") || text.Contains("темп");
            if (percent) return value + " процентов";
            if (!temperature) return String.IsNullOrWhiteSpace(unit) ? value : value + " " + unit;

            string degree;
            if (Decimal.Truncate(Math.Abs(number)) == Math.Abs(number))
            {
                int whole = (int)(Math.Abs(number) % 100);
                int last = whole % 10;
                degree = whole >= 11 && whole <= 14 ? "градусов" :
                    last == 1 ? "градус" : last >= 2 && last <= 4 ? "градуса" : "градусов";
            }
            else degree = "градуса";
            return value + " " + degree;
        }
    }
}