using System;
using System.Globalization;
using System.Threading.Tasks;
using ESP32AIControl.WP81.Core.Protocol;
using ESP32AIControl.WP81.Core.Mqtt;
using ESP32AIControl.WP81.Core.State;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core.Scenarios
{
    public sealed class Esp32CommandExecutor : ICommandExecutor
    {
        private const int StateTimeoutMilliseconds = 2500;
        private readonly Esp32MqttService _mqtt;
        private readonly DeviceRepository _devices;
        private readonly ScenarioPlanner _planner;

        public Esp32CommandExecutor(Esp32MqttService mqtt, DeviceRepository devices)
        {
            if (mqtt == null) throw new ArgumentNullException("mqtt");
            if (devices == null) throw new ArgumentNullException("devices");

            _mqtt = mqtt;
            _devices = devices;
            _planner = new ScenarioPlanner(devices);
        }

        public async Task ExecuteAsync(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("Пустая команда.", "command");

            var plan = _planner.BuildPlan(command);

            for (var i = 0; i < plan.Actions.Count; i++)
            {
                var action = plan.Actions[i];
                var current = _devices.Get(action.DeviceId, action.WidgetId);

                if (current != null && AreValuesEqual(current.Value, action.Value))
                    continue;

                await ExecuteActionAsync(action);
            }
        }

        private async Task ExecuteActionAsync(ScenarioAction action)
        {
            if (_mqtt.State != MqttConnectionState.Connected)
                throw new InvalidOperationException("MQTT не подключён.");

            await _mqtt.PublishControlAsync(
                action.DeviceId,
                action.WidgetId,
                action.Value);

            var confirmed = await WaitForStateAsync(
                action.DeviceId,
                action.WidgetId,
                action.Value,
                StateTimeoutMilliseconds);

            if (!confirmed)
            {
                var description = GetDescription(action);
                var prefix = action.IsDependency
                    ? "Не удалось выполнить зависимость"
                    : "Не подтверждено действие";

                throw new InvalidOperationException(
                    prefix + ": " + description + " (" + action.Command + ")");
            }
        }

        private async Task<bool> WaitForStateAsync(
            string deviceId,
            string widgetId,
            string expectedValue,
            int timeoutMilliseconds)
        {
            var elapsed = 0;

            while (elapsed < timeoutMilliseconds)
            {
                var state = _devices.Get(deviceId, widgetId);

                if (state != null && AreValuesEqual(state.Value, expectedValue))
                    return true;

                await Task.Delay(100);
                elapsed += 100;
            }

            return false;
        }

        private string GetDescription(ScenarioAction action)
        {
            var state = _devices.Get(action.DeviceId, action.WidgetId);

            if (state != null && !string.IsNullOrWhiteSpace(state.Description))
                return state.Description;

            return action.WidgetId;
        }

        private static bool AreValuesEqual(string first, string second)
        {
            var a = (first ?? string.Empty).Trim();
            var b = (second ?? string.Empty).Trim();

            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return true;

            double da;
            double db;

            if (double.TryParse(
                    a.Replace(',', '.'),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out da) &&
                double.TryParse(
                    b.Replace(',', '.'),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out db))
                return Math.Abs(da - db) < 0.000001;

            return false;
        }
    }
}
