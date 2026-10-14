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

            var stage = "создание распознавателя речи";

            try
            {
                _recognizer = new SpeechRecognizer();

                stage = "настройка распознавания речи";
                _recognizer.Constraints.Add(
                    new SpeechRecognitionTopicConstraint(
                        SpeechRecognitionScenario.Dictation,
                        "dictation"));

                stage = "подготовка распознавания";
                var compilation = await _recognizer.CompileConstraintsAsync();
                if (compilation == null)
                {
                    var failed = RecognitionFailed;
                    if (failed != null)
                        failed(this, "Не удалось подготовить распознавание речи: система не вернула результат проверки. Попробуйте ещё раз.");
                    return;
                }

                if (compilation.Status != SpeechRecognitionResultStatus.Success)
                {
                    var failed = RecognitionFailed;
                    if (failed != null)
                        failed(this, "Не удалось подготовить распознавание речи. Статус системы: " +
                            compilation.Status.ToString() + ". Проверьте языковые данные и поддержку распознавания.");
                    return;
                }

                stage = "запуск прослушивания";
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
                        failed(this, "Речь не распознана. Нажмите на микрофон и повторите фразу.");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Не удалось выполнить этап «" + stage + "». Код ошибки: 0x" +
                    ex.HResult.ToString("X8") + ".", ex);
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