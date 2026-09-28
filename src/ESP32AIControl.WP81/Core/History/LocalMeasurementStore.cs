using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

namespace ESP32AIControl.WP81.Core.History
{
    public sealed class LocalMeasurementStore : IMeasurementStore
    {
        private const string FileName = "measurements.tsv";
        private readonly object _gate = new object();

        public async Task AppendAsync(MeasurementPoint point)
        {
            if (point == null) return;

            var line = Escape(point.Timestamp.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)) + "\t" +
                       Escape(point.DeviceId) + "\t" +
                       Escape(point.WidgetId) + "\t" +
                       Escape(point.Value) + "\r\n";

            var file = await GetFileAsync(true);
            await FileIO.AppendTextAsync(file, line);
        }

        public async Task<IList<MeasurementPoint>> ReadAsync(
            string deviceId, string widgetId, DateTimeOffset from, DateTimeOffset to)
        {
            var result = new List<MeasurementPoint>();
            StorageFile file;

            try
            {
                file = await GetFileAsync(false);
            }
            catch (FileNotFoundException)
            {
                return result;
            }

            var text = await FileIO.ReadTextAsync(file);
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                var parts = line.Split(new[] { '\t' });
                if (parts.Length < 4) continue;

                DateTime timestamp;
                if (!DateTime.TryParseExact(parts[0], "o", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out timestamp))
                    continue;

                var pointTime = new DateTimeOffset(timestamp.ToUniversalTime());
                if (pointTime < from || pointTime > to) continue;
                if (!string.Equals(Unescape(parts[1]), deviceId ?? string.Empty, StringComparison.Ordinal))
                    continue;
                if (!string.Equals(Unescape(parts[2]), widgetId ?? string.Empty, StringComparison.Ordinal))
                    continue;

                result.Add(new MeasurementPoint
                {
                    DeviceId = Unescape(parts[1]),
                    WidgetId = Unescape(parts[2]),
                    Value = Unescape(parts[3]),
                    Timestamp = pointTime
                });
            }

            result.Sort(delegate(MeasurementPoint a, MeasurementPoint b)
            {
                return a.Timestamp.CompareTo(b.Timestamp);
            });
            return result;
        }

        public async Task<IList<MeasurementPoint>> ReadAllAsync(DateTimeOffset from, DateTimeOffset to)
        {
            var result = new List<MeasurementPoint>();
            StorageFile file;
            try { file = await GetFileAsync(false); }
            catch (Exception) { return result; }

            var text = await FileIO.ReadTextAsync(file);
            var lines = text.Split(new[] { '\\r', '\\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var parts = line.Split(new[] { '\\t' });
                if (parts.Length < 4) continue;
                DateTime timestamp;
                if (!DateTime.TryParseExact(parts[0], "o", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out timestamp)) continue;
                var pointTime = new DateTimeOffset(timestamp.ToUniversalTime());
                if (pointTime < from || pointTime > to) continue;
                result.Add(new MeasurementPoint
                {
                    Timestamp = pointTime,
                    DeviceId = Unescape(parts[1]),
                    WidgetId = Unescape(parts[2]),
                    Value = Unescape(parts[3])
                });
            }
            result.Sort(delegate(MeasurementPoint a, MeasurementPoint b)
            {
                return a.Timestamp.CompareTo(b.Timestamp);
            });
            return result;
        }

        public async Task DeleteBeforeAsync(DateTimeOffset timestamp)
        {
            StorageFile file;
            try { file = await GetFileAsync(false); }
            catch (FileNotFoundException) { return; }

            var text = await FileIO.ReadTextAsync(file);
            var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var keep = new List<string>();

            foreach (var line in lines)
            {
                var parts = line.Split(new[] { '\t' });
                if (parts.Length < 4) continue;

                DateTime timestampValue;
                if (!DateTime.TryParseExact(parts[0], "o", CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out timestampValue))
                    continue;

                if (new DateTimeOffset(timestampValue.ToUniversalTime()) >= timestamp)
                    keep.Add(line);
            }

            await FileIO.WriteTextAsync(file, string.Join("\r\n", keep) + (keep.Count == 0 ? string.Empty : "\r\n"));
        }

        private static async Task<StorageFile> GetFileAsync(bool create)
        {
            var options = create ? CreationCollisionOption.OpenIfExists : CreationCollisionOption.FailIfExists;
            return await ApplicationData.Current.LocalFolder.CreateFileAsync(FileName, options);
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
        }

        private static string Unescape(string value)
        {
            return (value ?? string.Empty).Replace("\\\\", "\\");
        }
    }
}