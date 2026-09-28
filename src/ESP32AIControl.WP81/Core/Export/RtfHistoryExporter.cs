using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using ESP32AIControl.WP81.Core.History;

namespace ESP32AIControl.WP81.Core.Export
{
    public sealed class RtfHistoryExporter : IHistoryExporter
    {
        public async Task ExportAsync(
            IList<MeasurementPoint> points,
            DateTimeOffset from,
            DateTimeOffset to,
            string destinationName)
        {
            var name = string.IsNullOrEmpty(destinationName) ? "ESP32-history.rtf" : destinationName;
            if (!name.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase))
                name += ".rtf";

            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                name, CreationCollisionOption.ReplaceExisting);

            var sb = new StringBuilder();
            sb.AppendLine("{\\rtf1\\ansi\\deff0");
            sb.AppendLine("\\fs28 ESP32 AI Control - Measurement History\\par");
            sb.AppendLine("\\fs20 From: " + Escape(from.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)) +
                          " To: " + Escape(to.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)) + "\\par");
            sb.AppendLine("\\par");
            sb.AppendLine("Timestamp\\tab Device\\tab Widget\\tab Value\\par");

            if (points != null)
            {
                foreach (var p in points)
                {
                    sb.Append(Escape(p.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
                    sb.Append("\\tab ");
                    sb.Append(Escape(p.DeviceId));
                    sb.Append("\\tab ");
                    sb.Append(Escape(p.WidgetId));
                    sb.Append("\\tab ");
                    sb.Append(Escape(p.Value));
                    sb.AppendLine("\\par");
                }
            }

            sb.Append("}");
            await FileIO.WriteTextAsync(file, sb.ToString(), UnicodeEncoding.Utf8);
        }

        private static string Escape(string text)
        {
            return (text ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("{", "\\{")
                .Replace("}", "\\}")
                .Replace(Environment.NewLine, "\\line ");
        }
    }
}