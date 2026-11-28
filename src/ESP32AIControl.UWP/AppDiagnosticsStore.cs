using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Storage;

namespace ESP32AIControl.UWP
{
    public sealed class DiagnosticEntry
    {
        public DateTimeOffset Time { get; set; }
        public string Level { get; set; }
        public string Source { get; set; }
        public string Message { get; set; }
    }

    public sealed class AppDiagnosticsStore
    {
        private const string Key = "app_diagnostics_v1";
        private const int MaxEntries = 500;
        private readonly object _sync = new object();

        public void Add(string source, string message, string level = "INFO")
        {
            if (String.IsNullOrWhiteSpace(message)) return;
            lock (_sync)
            {
                var entries = ReadLocked();
                entries.Add(new DiagnosticEntry {
                    Time = DateTimeOffset.Now, Level = level ?? "INFO",
                    Source = source ?? "App", Message = message
                });
                if (entries.Count > MaxEntries) entries = entries.Skip(entries.Count - MaxEntries).ToList();
                WriteLocked(entries);
            }
        }

        public IList<DiagnosticEntry> GetAll()
        {
            lock (_sync) return ReadLocked().OrderByDescending(x => x.Time).ToList();
        }

        public void Clear()
        {
            lock (_sync) ApplicationData.Current.LocalSettings.Values.Remove(Key);
        }

        private List<DiagnosticEntry> ReadLocked()
        {
            try
            {
                object raw;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(Key, out raw) || !(raw is string))
                    return new List<DiagnosticEntry>();
                var result = new List<DiagnosticEntry>();
                foreach (var line in ((string)raw).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = line.Split(new[] { '\t' }, 4);
                    if (p.Length != 4) continue;
                    long ticks;
                    if (!Int64.TryParse(p[0], out ticks)) continue;
                    result.Add(new DiagnosticEntry {
                        Time = new DateTimeOffset(ticks, TimeSpan.Zero).ToLocalTime(),
                        Level = p[1], Source = p[2], Message = p[3]
                    });
                }
                return result;
            }
            catch { return new List<DiagnosticEntry>(); }
        }

        private void WriteLocked(IList<DiagnosticEntry> entries)
        {
            var lines = entries.Select(x => x.Time.UtcDateTime.Ticks + "\t" +
                Clean(x.Level) + "\t" + Clean(x.Source) + "\t" + Clean(x.Message));
            ApplicationData.Current.LocalSettings.Values[Key] = String.Join("\n", lines);
        }

        private static string Clean(string value)
        {
            return (value ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " ");
        }
    }
}