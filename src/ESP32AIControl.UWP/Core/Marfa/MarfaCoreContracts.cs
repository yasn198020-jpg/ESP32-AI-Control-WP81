using System;
using System.Collections.Generic;

namespace ESP32AIControl.UWP.Core.Marfa
{
    public enum LocalCommandAction { CONTROL, READ_VALUE, SMART_RULE, CLARIFY, STOP, NOT_FOUND }
    public enum MarfaWidgetType { TOGGLE, BUTTON, INPUT, VALUE, STATUS }
    public enum WidgetCommandState { IDLE, PENDING, CONFIRMED, TIMED_OUT, SEND_FAILED, BUSY }

    public sealed class MarfaDevice
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool Online { get; set; }
        public IList<MarfaWidgetState> Widgets { get; set; }
        public MarfaDevice() { Widgets = new List<MarfaWidgetState>(); }
    }
    public sealed class MarfaWidgetState
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public MarfaWidgetType Type { get; set; }
        public string Value { get; set; }
        public long LastUpdated { get; set; }
        public string Page { get; set; }
        public string Topic { get; set; }
        public int Order { get; set; }
        public string Unit { get; set; }
        public string DefinitionName { get; set; }
        public string ConfigJson { get; set; }
        public WidgetCommandState CommandState { get; set; }
        public string ExpectedCommandValue { get; set; }
        public string CommandId { get; set; }
    }
    public sealed class LocalCommandActionItem
    {
        public string DeviceId { get; set; }
        public string WidgetId { get; set; }
        public string Value { get; set; }
        public LocalCommandActionItem() { Value = "1"; }
    }
    public sealed class LocalCommandResult
    {
        public LocalCommandAction Action { get; set; }
        public string DeviceId { get; set; }
        public string WidgetId { get; set; }
        public string Value { get; set; }
        public string Reply { get; set; }
        public long DelayMs { get; set; }
        public string ConditionDeviceId { get; set; }
        public string ConditionWidgetId { get; set; }
        public string ConditionOperator { get; set; }
        public double ConditionThreshold { get; set; }
        public string ActionDeviceId { get; set; }
        public string ActionWidgetId { get; set; }
        public string ActionValue { get; set; }
        public IList<LocalCommandActionItem> ActionItems { get; set; }
        public bool NeedsConfirmation { get; set; }
        public object ScenarioPlan { get; set; }
        public IList<LocalCommandResult> SubResults { get; set; }
        public LocalCommandResult()
        {
            DeviceId = WidgetId = Value = ConditionDeviceId = ConditionWidgetId = "";
            Reply = ""; ConditionOperator = ">"; ActionDeviceId = ActionWidgetId = "";
            ActionValue = "1"; ActionItems = new List<LocalCommandActionItem>();
            SubResults = new List<LocalCommandResult>();
        }
    }
    public interface ITrainedCommandRepository { IList<TrainedVoiceCommand> Load(); }
    public sealed class TrainedVoiceCommand
    {
        public string Phrase { get; set; }
        public string DeviceId { get; set; }
        public string WidgetId { get; set; }
        public string Value { get; set; }
        public IList<string> Variants { get; set; }
    }
    public interface IMarfaScenarioAdvisor
    {
        IList<string> RefineControlCandidatesBeforeClarification(IList<string> candidateIds, string desiredValue, IList<MarfaDevice> devices);
    }
    public static class MarfaCoreDiagnostics
    {
        public static Action<string> Sink { get; set; }
        public static void Log(string message) { try { var sink = Sink; if (sink != null) sink(message); } catch { } }
    }
}