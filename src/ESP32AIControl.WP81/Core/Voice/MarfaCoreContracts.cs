using System;
using System.Collections.Generic;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core.Voice
{
    public enum LocalCommandAction { Control, ReadValue, SmartRule, Clarify, Stop, NotFound }

    public sealed class LocalCommandActionItem
    {
        public string DeviceId { get; set; }
        public string WidgetId { get; set; }
        public string Value { get; set; }

        public LocalCommandActionItem()
        {
            DeviceId = string.Empty;
            WidgetId = string.Empty;
            Value = "1";
        }
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
        public IList<LocalCommandActionItem> ActionItems { get; private set; }
        public bool NeedsConfirmation { get; set; }
        public object ScenarioPlan { get; set; }
        public IList<LocalCommandResult> SubResults { get; private set; }

        public LocalCommandResult()
        {
            DeviceId = WidgetId = Value = Reply = string.Empty;
            ConditionDeviceId = ConditionWidgetId = string.Empty;
            ConditionOperator = ">";
            ActionDeviceId = ActionWidgetId = string.Empty;
            ActionValue = "1";
            ActionItems = new List<LocalCommandActionItem>();
            SubResults = new List<LocalCommandResult>();
        }
    }

    public interface IMarfaScenarioAdvisor
    {
        IList<string> RefineControlCandidatesBeforeClarification(
            IList<string> candidateIds, string desiredValue, IList<WidgetState> widgets);
    }

    public interface ITrainedCommandRepository
    {
        IList<TrainedVoiceCommand> Load();
    }
}