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
            await FileIO.WriteTextAsync(file, sb.ToString());
        }

        public async Task ExportComparisonAsync(
            IList<Tuple<string, IList<MeasurementPoint>>> traces,
            DateTimeOffset from,
            DateTimeOffset to,
            string destinationName)
        {
            var name = string.IsNullOrEmpty(destinationName) ? "ESP32-comparison.doc" : destinationName;
            if (!name.EndsWith(".doc", StringComparison.OrdinalIgnoreCase)) name += ".doc";
            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                name, CreationCollisionOption.ReplaceExisting);

            var timestamps = new SortedDictionary<DateTimeOffset, Dictionary<string, string>>();
            var keys = new List<string>();
            if (traces != null)
            {
                foreach (var trace in traces)
                {
                    if (trace == null || string.IsNullOrWhiteSpace(trace.Item1)) continue;
                    keys.Add(trace.Item1);
                    if (trace.Item2 == null) continue;
                    foreach (var point in trace.Item2)
                    {
                        Dictionary<string, string> values;
                        if (!timestamps.TryGetValue(point.Timestamp, out values))
                        {
                            values = new Dictionary<string, string>();
                            timestamps.Add(point.Timestamp, values);
                        }
                        values[trace.Item1] = point.Value ?? string.Empty;
                    }
                }
            }

            var sb = new StringBuilder();
            sb.Append("<html><head><meta charset=\"utf-8\"><title>ESP32 comparison</title>");
            sb.Append("<style>body{font-family:Arial}table{border-collapse:collapse}th,td{border:1px solid #777;padding:5px}th{background:#ddd}</style>");
            sb.Append("</head><body><h2>ESP32 AI Control — Sensor comparison</h2><p>Period: ");
            sb.Append(Escape(from.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            sb.Append(" — ");
            sb.Append(Escape(to.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            sb.Append("</p><table><tr><th>Date and time</th>");
            foreach (var key in keys)
            {
                sb.Append("<th>");
                sb.Append(Escape(key));
                sb.Append("</th>");
            }
            sb.Append("</tr>");
            foreach (var row in timestamps)
            {
                sb.Append("<tr><td>");
                sb.Append(Escape(row.Key.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
                sb.Append("</td>");
                foreach (var key in keys)
                {
                    string value;
                    sb.Append("<td>");
                    sb.Append(Escape(row.Value.TryGetValue(key, out value) ? value : string.Empty));
                    sb.Append("</td>");
                }
                sb.Append("</tr>");
            }
            sb.Append("</table><p>Rows: ");
            sb.Append(timestamps.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append("; sensors: ");
            sb.Append(keys.Count.ToString(CultureInfo.InvariantCulture));
            sb.Append("</p></body></html>");
            await FileIO.WriteTextAsync(file, sb.ToString());
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