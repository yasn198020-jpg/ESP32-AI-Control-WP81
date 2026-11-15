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

            var recognizer = new SpeechRecognizer();
            _recognizer = recognizer;
            try
            {
                await recognizer.CompileConstraintsAsync();

                if (!object.ReferenceEquals(_recognizer, recognizer))
                    return;

                var result = await recognizer.RecognizeAsync();
                if (!object.ReferenceEquals(_recognizer, recognizer))
                    return;

                var text = result == null ? string.Empty : result.Text;
                var handler = CommandRecognized;
                if (handler != null && !string.IsNullOrWhiteSpace(text))
                    handler(this, text);
            }
            finally
            {
                if (object.ReferenceEquals(_recognizer, recognizer))
                    _recognizer = null;
                try { recognizer.Dispose(); } catch { }
            }
        }

        public Task StopListeningAsync()
        {
            var recognizer = _recognizer;
            _recognizer = null;
            if (recognizer != null)
            {
                try { recognizer.Dispose(); } catch { }
            }
            return Task.FromResult(true);
        }
    }
}