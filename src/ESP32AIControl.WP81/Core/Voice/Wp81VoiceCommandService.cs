using System;
using System.Threading.Tasks;
using Windows.Media.SpeechRecognition;

namespace ESP32AIControl.WP81.Core.Voice
{
    public sealed class Wp81VoiceCommandService : IVoiceCommandService
    {
        private SpeechRecognizer _recognizer;

        public event EventHandler<string> CommandRecognized;
        public event EventHandler<string> RecognitionFailed;

        public async Task StartListeningAsync()
        {
            if (_recognizer != null) return;

            try
            {
                _recognizer = new SpeechRecognizer();
                await _recognizer.CompileConstraintsAsync();

                var result = await _recognizer.RecognizeAsync();
                if (result != null &&
                    result.Status == SpeechRecognitionResultStatus.Success &&
                    !string.IsNullOrWhiteSpace(result.Text))
                {
                    var handler = CommandRecognized;
                    if (handler != null)
                        handler(this, result.Text);
                }
                else
                {
                    var failed = RecognitionFailed;
                    if (failed != null)
                        failed(this, "Не удалось распознать речь. Нажмите 🎤 и повторите.");
                }
            }
            finally
            {
                await StopListeningAsync();
            }
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
