using System;

namespace ESP32AIControl.WP81.Core.Voice
{
    /// <summary>Optional diagnostics bridge; logging failures never interrupt Marfa processing.</summary>
    public static class MarfaCoreDiagnostics
    {
        public static Action<string> Sink { get; set; }

        public static void Log(string message)
        {
            try
            {
                var handler = Sink;
                if (handler != null) handler(message ?? string.Empty);
            }
            catch
            {
                // Diagnostics must never break command recognition or planning.
            }
        }
    }
}