using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core.History
{
    public sealed class MeasurementRecorder
    {
        private readonly IMeasurementStore _store;
        private readonly Func<int> _intervalProvider;
        private readonly Dictionary<string, DateTimeOffset> _lastSamples =
            new Dictionary<string, DateTimeOffset>();
        private readonly Dictionary<string, string> _lastValues =
            new Dictionary<string, string>();

        public MeasurementRecorder(IMeasurementStore store, Func<int> intervalProvider)
        {
            _store = store;
            _intervalProvider = intervalProvider;
        }

        public async Task ObserveAsync(WidgetState state)
        {
            if (state == null || string.IsNullOrEmpty(state.Id) || string.IsNullOrEmpty(state.Value))
                return;

            double numeric;
            if (!double.TryParse(state.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out numeric))
                return;

            var key = state.Key;
            var now = DateTimeOffset.Now;
            DateTimeOffset previous;
            var interval = Math.Max(1, _intervalProvider());

            if (_lastSamples.TryGetValue(key, out previous) &&
                (now - previous).TotalSeconds < interval)
                return;

            string oldValue;
            if (_lastValues.TryGetValue(key, out oldValue) && oldValue == state.Value &&
                (now - previous).TotalSeconds < interval * 2)
                return;

            _lastSamples[key] = now;
            _lastValues[key] = state.Value;

            await _store.AppendAsync(new MeasurementPoint
            {
                DeviceId = state.DeviceId,
                WidgetId = state.Id,
                Value = state.Value,
                Timestamp = now
            });
        }
    }
}