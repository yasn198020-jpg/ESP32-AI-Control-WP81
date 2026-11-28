using System;

namespace ESP32AIControl.UWP.Core.Marfa
{
    /// <summary>Small platform-independent diagnostics hook for the Marfa core.</summary>
    public static class MarfaCoreDiagnostics
    {
        public static Action<string> Sink { get; set; }

        public static void Log(string message)
        {
            try
            {
                var sink = Sink;
                if (sink != null) sink(message ?? "");
            }
            catch
            {
                // Diagnostics must never interrupt command processing.
            }
        }
    }
}