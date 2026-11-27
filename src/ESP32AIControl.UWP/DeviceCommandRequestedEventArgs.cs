namespace ESP32AIControl.UWP
{
    public sealed class DeviceCommandRequestedEventArgs : System.EventArgs
    {
        public DeviceModel Device { get; private set; }
        public WidgetModel Widget { get; private set; }
        public string Value { get; private set; }
        public DeviceCommandRequestedEventArgs(DeviceModel device, WidgetModel widget, string value)
        {
            Device = device; Widget = widget; Value = value;
        }
    }
}
