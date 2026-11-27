using System;
using System.Collections.Generic;
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

        public void SetScenarioScript(string script)
        {
            _planner.SetScenarioScript(script);
        }

        public int GetScenarioRuleCount()
        {
            return _planner.GetScenarioRuleCount();
        }

        public IList<ScenarioVariableDiagnostic> GetScenarioDiagnostics()
        {
            return _planner.GetScenarioDiagnostics();
        }

        public ScenarioPlan BuildPlan(string command)
        {
            return _planner.BuildPlan(command);
        }

        public async Task ExecuteAsync(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("Пустая команда.", "command");

            var plan = _planner.BuildPlan(command);
            ValidateExecutionPlan(plan);

            for (var i = 0; i < plan.Actions.Count; i++)
            {
                var action = plan.Actions[i];
                var current = _devices.Get(action.DeviceId, action.WidgetId);

                // Dependencies execute sequentially. A failed or unconfirmed
                // step throws and prevents every later action from being sent.
                if (current != null && AreValuesEqual(current.Value, action.Value))
                    continue;

                await ExecuteActionAsync(action, current == null
                    ? DateTimeOffset.MinValue
                    : current.UpdatedAt);
            }
        }

        private void ValidateExecutionPlan(ScenarioPlan plan)
        {
            if (plan == null || plan.Actions == null || plan.Actions.Count == 0)
                throw new InvalidOperationException("План команд пуст или не сформирован.");

            if (plan.Actions.Count > 64)
                throw new InvalidOperationException(
                    "План содержит более 64 действий. Команды не отправлены.");

            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < plan.Actions.Count; i++)
            {
                var action = plan.Actions[i];
                if (action == null || string.IsNullOrWhiteSpace(action.DeviceId) ||
                    string.IsNullOrWhiteSpace(action.WidgetId) ||
                    string.IsNullOrWhiteSpace(action.Value))
                    throw new InvalidOperationException(
                        "План содержит неполное действие. Команды не отправлены.");

                var key = action.DeviceId.Trim() + "/" + action.WidgetId.Trim();
                if (!targets.Add(key))
                    throw new InvalidOperationException(
                        "План содержит повторное действие для " + key + ". Команды не отправлены.");

                var matches = _devices.GetWidgetsById(action.WidgetId);
                if (matches.Count != 1 ||
                    !string.Equals(matches[0].DeviceId, action.DeviceId,
                        StringComparison.OrdinalIgnoreCase) ||
                    _devices.Get(action.DeviceId, action.WidgetId) == null)
                    throw new InvalidOperationException(
                        "Нельзя однозначно найти устройство и виджет " + key +
                        ". Команды не отправлены.");
            }
        }

        public async Task ExecuteLocalActionAsync(string deviceId, string widgetId, string value)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(widgetId) ||
                string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Не заданы устройство, виджет или значение локальной команды.");

            var matches = _devices.GetWidgetsById(widgetId);
            var current = _devices.Get(deviceId, widgetId);
            if (matches.Count != 1 || current == null ||
                !string.Equals(matches[0].DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Нельзя однозначно найти виджет " + deviceId + "/" + widgetId + ".");

            if (AreValuesEqual(current.Value, value))
                return;

            await ExecuteActionAsync(new ScenarioAction
            {
                DeviceId = deviceId,
                WidgetId = widgetId,
                Value = value,
                Command = deviceId + "/" + widgetId + "=" + value
            }, current.UpdatedAt);
        }

        private async Task ExecuteActionAsync(
            ScenarioAction action,
            DateTimeOffset baselineUpdatedAt)
        {
            var before = _devices.Get(action.DeviceId, action.WidgetId);
            if (before == null)
                throw new InvalidOperationException(
                    "Виджет " + action.DeviceId + "/" + action.WidgetId +
                    " исчез до отправки команды.");

            if (before.UpdatedAt > baselineUpdatedAt)
                baselineUpdatedAt = before.UpdatedAt;

            // Esp32MqttService selects MQTT or its local WebSocket fallback.
            // Do not reject a command solely because the broker is disconnected.
            await _mqtt.PublishControlAsync(
                action.DeviceId,
                action.WidgetId,
                action.Value);

            var confirmationTimeout = _mqtt.State == MqttConnectionState.Connected
                ? StateTimeoutMilliseconds
                : 10000;

            var confirmed = await WaitForStateAsync(
                action.DeviceId,
                action.WidgetId,
                action.Value,
                baselineUpdatedAt,
                confirmationTimeout);

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
            DateTimeOffset baselineUpdatedAt,
            int timeoutMilliseconds)
        {
            var elapsed = 0;

            while (elapsed < timeoutMilliseconds)
            {
                var state = _devices.Get(deviceId, widgetId);

                // The cached value before Publish is not an acknowledgement.
                // Require a newer device status/event carrying the requested value.
                if (state != null &&
                    state.UpdatedAt > baselineUpdatedAt &&
                    AreValuesEqual(state.Value, expectedValue))
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
