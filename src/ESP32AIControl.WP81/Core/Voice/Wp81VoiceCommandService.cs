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

            var stage = "создание SpeechRecognizer";

            try
            {
                _recognizer = new SpeechRecognizer();

                stage = "добавление ограничения распознавания";
                _recognizer.Constraints.Add(
                    new SpeechRecognitionTopicConstraint(
                        SpeechRecognitionScenario.Dictation,
                        "dictation"));

                stage = "CompileConstraintsAsync";
                var compilation = await _recognizer.CompileConstraintsAsync();
                if (compilation == null)
                {
                    var failed = RecognitionFailed;
                    if (failed != null)
                        failed(this, "Не удалось подготовить распознавание речи (результат подготовки пустой).");
                    return;
                }

                if (compilation.Status != SpeechRecognitionResultStatus.Success)
                {
                    var failed = RecognitionFailed;
                    if (failed != null)
                        failed(this, "Подготовка распознавания не пройдена: " + compilation.Status);
                    return;
                }

                stage = "RecognizeAsync";
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
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Ошибка голосового этапа [" + stage + "]: " + ex.Message, ex);
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