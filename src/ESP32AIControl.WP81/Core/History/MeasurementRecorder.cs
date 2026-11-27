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
        private readonly Func<WidgetState, int> _intervalProvider;
        private readonly Dictionary<string, DateTimeOffset> _lastSamples =
            new Dictionary<string, DateTimeOffset>();
        private readonly Dictionary<string, string> _lastValues =
            new Dictionary<string, string>();

        public MeasurementRecorder(IMeasurementStore store, Func<WidgetState, int> intervalProvider)
        {
            _store = store;
            _intervalProvider = intervalProvider;
        }

        public async Task ObserveAsync(WidgetState state)
        {
            if (state == null || string.IsNullOrEmpty(state.Id) ||
                string.IsNullOrWhiteSpace(state.Value))
                return;

            double numeric;
            if (!double.TryParse(state.Value.Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out numeric))
                return;

            var key = state.Key;
            var now = DateTimeOffset.Now;
            var intervalSeconds = Math.Max(1, _intervalProvider(state));
            var intervalElapsed = true;
            DateTimeOffset previousTime;
            if (_lastSamples.TryGetValue(key, out previousTime))
                intervalElapsed = (now - previousTime).TotalSeconds >= intervalSeconds;

            string previousValue;
            var valueChanged = !_lastValues.TryGetValue(key, out previousValue) ||
                !string.Equals(previousValue, state.Value, StringComparison.Ordinal);

            // Android change_interval mode: save immediately on a value change,
            // and also save periodically when the value remains unchanged.
            if (!valueChanged && !intervalElapsed)
                return;

            await _store.AppendAsync(new MeasurementPoint
            {
                DeviceId = state.DeviceId,
                WidgetId = state.Id,
                Value = state.Value,
                Timestamp = now
            });

            _lastSamples[key] = now;
            _lastValues[key] = state.Value;
        }
    }
}
