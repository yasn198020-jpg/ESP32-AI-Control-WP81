using System;
using System.Threading.Tasks;
using ESP32AIControl.WP81.Core.Protocol;

namespace ESP32AIControl.WP81.Core.Scenarios
{
    public sealed class Esp32CommandExecutor : ICommandExecutor
    {
        private readonly Esp32MqttService _mqtt;

        public Esp32CommandExecutor(Esp32MqttService mqtt)
        {
            _mqtt = mqtt;
        }

        public async Task ExecuteAsync(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("Пустая команда.", "command");

            var parts = command.Split(new[] { '=' }, 2);
            if (parts.Length != 2)
                throw new ArgumentException("Формат команды: device/widget=value", "command");

            var target = parts[0].Trim().Trim('/');
            var value = parts[1].Trim();
            var slash = target.IndexOf('/');

            if (slash <= 0 || slash >= target.Length - 1)
                throw new ArgumentException("Формат команды: device/widget=value", "command");

            var device = target.Substring(0, slash);
            var widget = target.Substring(slash + 1);

            if (_mqtt.State != MqttConnectionState.Connected)
                throw new InvalidOperationException("MQTT не подключён.");

            await _mqtt.PublishControlAsync(device, widget, value);
        }
    }
}