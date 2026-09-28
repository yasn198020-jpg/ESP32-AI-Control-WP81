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
            var name = string.IsNullOrEmpty(destinationName) ? "ESP32-history.doc" : destinationName;
            if (!name.EndsWith(".doc", StringComparison.OrdinalIgnoreCase))
                name += ".doc";

            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                name, CreationCollisionOption.ReplaceExisting);

            var sb = new StringBuilder();
            sb.Append("<html><head><meta charset=\"utf-8\"><title>ESP32 AI Control</title></head><body>");
            sb.Append("<h2>ESP32 AI Control - Measurement History</h2>");
            sb.Append("<p>From: ");
            sb.Append(Escape(from.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            sb.Append(" &nbsp; To: ");
            sb.Append(Escape(to.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            sb.Append("</p><table border=\"1\" cellspacing=\"0\" cellpadding=\"4\">");
            sb.Append("<tr><th>Timestamp</th><th>Device</th><th>Widget</th><th>Value</th></tr>");

            if (points != null)
            {
                foreach (var p in points)
                {
                    sb.Append("<tr><td>");
                    sb.Append(Escape(p.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
                    sb.Append("</td><td>");
                    sb.Append(Escape(p.DeviceId));
                    sb.Append("</td><td>");
                    sb.Append(Escape(p.WidgetId));
                    sb.Append("</td><td>");
                    sb.Append(Escape(p.Value));
                    sb.Append("</td></tr>");
                }
            }

            sb.Append("</table></body></html>");
            await FileIO.WriteTextAsync(file, sb.ToString(), UnicodeEncoding.Utf8);
        }

        private static string Escape(string text)
        {
            return (text ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
        }
    }
}