namespace ESP32AIControl.WP81.Core.Diagnostics
{
    public interface ILogSink
    {
        void Info(string message);
        void Error(string message);
    }
}
