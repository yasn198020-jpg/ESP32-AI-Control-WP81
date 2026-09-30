using System;
using System.Collections.Generic;
using ESP32AIControl.WP81.Core.Protocol;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core.State
{
    public sealed class DeviceRepository
    {
        private readonly Dictionary<string, WidgetState> _widgets =
            new Dictionary<string, WidgetState>();

        private readonly Dictionary<string, DateTimeOffset> _lastUpdates =
            new Dictionary<string, DateTimeOffset>();

        // IoTManager element IDs are globally unique across ESPs. The index
        // lets scenario code resolve "btn43" to the real widget/device without
        // putting a device ID into the exported scenario.
        private readonly Dictionary<string, string> _widgetIdIndex =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public event EventHandler<WidgetState> WidgetChanged;

        public IEnumerable<WidgetState> Widgets
        {
            get { return _widgets.Values; }
        }

        public WidgetState Get(string deviceId, string widgetId)
        {
            var key = (deviceId ?? string.Empty) + "/" + (widgetId ?? string.Empty);

            lock (_widgets)
            {
                WidgetState state;
                if (_widgets.TryGetValue(key, out state))
                    return state.Clone();
            }

            return null;
        }

        public WidgetState GetByWidgetId(string widgetId)
        {
            if (string.IsNullOrWhiteSpace(widgetId))
                return null;

            lock (_widgets)
            {
                string key;
                if (!_widgetIdIndex.TryGetValue(widgetId, out key) || string.IsNullOrEmpty(key))
                    return null;

                WidgetState state;
                if (_widgets.TryGetValue(key, out state))
                    return state.Clone();
            }

            return null;
        }

        public IList<WidgetState> GetWidgetsSnapshot()
        {
            var result = new List<WidgetState>();

            lock (_widgets)
            {
                foreach (var state in _widgets.Values)
                    result.Add(state.Clone());
            }

            return result;
        }

        public void Apply(Esp32Message message)
        {
            if (message == null || string.IsNullOrEmpty(message.WidgetId))
                return;

            var key = (message.DeviceId ?? string.Empty) + "/" + message.WidgetId;
            WidgetState state;

            lock (_widgets)
            {
                if (!_widgets.TryGetValue(key, out state))
                {
                    state = new WidgetState
                    {
                        DeviceId = message.DeviceId,
                        Id = message.WidgetId,
                        Description = message.Description,
                        WidgetType = message.WidgetType,
                        Page = message.Page,
                        Order = message.Order
                    };
                    _widgets[key] = state;
                }

                UpdateWidgetIdIndex(state.Id, key);

                if (!string.IsNullOrEmpty(message.DeviceId))
                    state.DeviceId = message.DeviceId;
                if (!string.IsNullOrEmpty(message.Description))
                    state.Description = message.Description;
                if (!string.IsNullOrEmpty(message.WidgetType))
                    state.WidgetType = message.WidgetType;
                if (!string.IsNullOrEmpty(message.Page))
                    state.Page = message.Page;
                if (message.Order != 0)
                    state.Order = message.Order;

                if (message.Kind == Esp32MessageKind.Status ||
                    message.Kind == Esp32MessageKind.Event)
                {
                    if (IsDuplicate(key, message.Value))
                        return;

                    state.Value = message.Value;
                    state.UpdatedAt = DateTimeOffset.Now;
                }
            }

            var handler = WidgetChanged;
            if (handler != null)
                handler(this, state.Clone());
        }

        private void UpdateWidgetIdIndex(string widgetId, string key)
        {
            if (string.IsNullOrWhiteSpace(widgetId))
                return;

            string existing;
            if (!_widgetIdIndex.TryGetValue(widgetId, out existing))
            {
                _widgetIdIndex[widgetId] = key;
                return;
            }

            if (string.Equals(existing, key, StringComparison.OrdinalIgnoreCase))
                return;

            // Same element ID appeared on two devices. Treat it as ambiguous
            // instead of silently selecting the wrong ESP.
            _widgetIdIndex[widgetId] = string.Empty;
        }

        private bool IsDuplicate(string key, string value)
        {
            var stampKey = key + "|" + (value ?? string.Empty);
            DateTimeOffset previous;

            if (_lastUpdates.TryGetValue(stampKey, out previous))
            {
                if ((DateTimeOffset.Now - previous).TotalMilliseconds < 750)
                    return true;
            }

            _lastUpdates[stampKey] = DateTimeOffset.Now;
            return false;
        }
    }
}
