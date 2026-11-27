using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Storage;

namespace ESP32AIControl.WP81.Core.Network
{
    /// <summary>
    /// Small persistent diagnostic journal for reconnect, UDP and WebSocket events.
    /// Kept bounded to fit the Windows Phone LocalSettings quota.
    /// </summary>
    public sealed class ConnectionJournal
    {
        private const string Key = "connection_journal_v1";
        private const int MaxEntries = 25;
        private const int MaxLineLength = 220;
        private readonly object _sync = new object();
        private readonly ApplicationDataContainer _settings = ApplicationData.Current.LocalSettings;

        public IList<string> Load()
        {
            lock (_sync)
            {
                object value;
                if (!_settings.Values.TryGetValue(Key, out value) || !(value is string))
                    return new List<string>();
                return ((string)value).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Take(MaxEntries).ToList();
            }
        }

        public void Append(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (_sync)
            {
                var entries = Load().ToList();
                var safe = line.Replace("\r", " ").Replace("\n", " ");
                if (safe.Length > MaxLineLength)
                    safe = safe.Substring(0, MaxLineLength);
                entries.Insert(0, safe);
                _settings.Values[Key] = string.Join("\n", entries.Take(MaxEntries));
            }
        }

        public void Clear()
        {
            lock (_sync)
                _settings.Values.Remove(Key);
        }
    }
}
