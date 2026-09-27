using System.Threading.Tasks;

namespace ESP32AIControl.WP81.Core.Voice
{
    public interface IVoiceCommandService
    {
        Task StartListeningAsync();
        Task StopListeningAsync();
        event System.EventHandler<string> CommandRecognized;
    }
}
