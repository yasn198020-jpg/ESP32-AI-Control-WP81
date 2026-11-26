using System;
using System.Globalization;

namespace ESP32AIControl.WP81.Core.Voice
{
    /// <summary>Produces natural Russian numeric replies for Marfa speech output.</summary>
    public static class MarfaSpeechTextFormatter
    {
        public static string FormatValue(string raw, string unit, string description)
        {
            var value = (raw ?? string.Empty).Trim();
            var suffix = (unit ?? string.Empty).Trim();
            double number;
            if (!double.TryParse(value.Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out number) ||
                double.IsNaN(number) || double.IsInfinity(number))
                return suffix.Length == 0 ? value : value + " " + suffix;

            var formatted = number.ToString("0.################", CultureInfo.InvariantCulture).Replace('.', ',');
            var label = (description ?? string.Empty).ToLowerInvariant().Replace('ё', 'е');
            if (suffix.Contains("%") || label.Contains("влажн"))
                return formatted + " процентов";

            var isTemperature = suffix.Contains("°") ||
                string.Equals(suffix, "c", StringComparison.OrdinalIgnoreCase) ||
                label.Contains("температур") || label.Contains("темп");
            if (!isTemperature)
                return suffix.Length == 0 ? formatted : formatted + " " + suffix;

            var absolute = Math.Abs(number);
            var whole = Math.Abs(number - Math.Round(number)) < 0.0000001;
            string degree;
            if (!whole) degree = "градуса";
            else
            {
                var integer = (long)Math.Min(absolute, 1000000000);
                var lastTwo = integer % 100;
                var last = integer % 10;
                if (lastTwo >= 11 && lastTwo <= 14) degree = "градусов";
                else if (last == 1) degree = "градус";
                else if (last >= 2 && last <= 4) degree = "градуса";
                else degree = "градусов";
            }
            return formatted + " " + degree;
        }
    }
}