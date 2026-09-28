using System;
using System.Threading.Tasks;
using Windows.Media.SpeechRecognition;

namespace ESP32AIControl.WP81.Core.Voice
{
    public sealed class Wp81VoiceCommandService : IVoiceCommandService
    {
        private SpeechRecognizer _recognizer;

        public event EventHandler<string> CommandRecognized;

        public async Task StartListeningAsync()
        {
            if (_recognizer != null) return;

            _recognizer = new SpeechRecognizer();
            await _recognizer.CompileConstraintsAsync();

            var result = await _recognizer.RecognizeAsync();
            var text = result == null ? string.Empty : result.Text;

            var handler = CommandRecognized;
            if (handler != null && !string.IsNullOrWhiteSpace(text))
                handler(this, text);

            await StopListeningAsync();
        }

        public Task StopListeningAsync()
        {
            if (_recognizer != null)
            {
                _recognizer.Dispose();
                _recognizer = null;
            }
            return Task.FromResult(true);
        }
    }
}