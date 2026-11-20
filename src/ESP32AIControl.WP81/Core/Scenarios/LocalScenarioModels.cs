using System;
using System.Collections.Generic;

namespace ESP32AIControl.WP81.Core.Scenarios
{
    public sealed class LocalScenarioCondition
    {
        public string WidgetId { get; set; }
        public string Operator { get; set; }
        public double Threshold { get; set; }
        public string Connector { get; set; }

        public LocalScenarioCondition()
        {
            WidgetId = string.Empty;
            Operator = ">";
            Connector = "AND";
        }
    }

    public sealed class LocalScenarioAction
    {
        public string DeviceId { get; set; }
        public string WidgetId { get; set; }
        public string Value { get; set; }

        public LocalScenarioAction()
        {
            DeviceId = string.Empty;
            WidgetId = string.Empty;
            Value = "1";
        }
    }

    public sealed class LocalScenarioDefinition
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Message { get; set; }
        public bool Enabled { get; set; }
        public bool NotificationEnabled { get; set; }
        public IList<LocalScenarioCondition> Conditions { get; private set; }
        public IList<LocalScenarioAction> Actions { get; private set; }

        public LocalScenarioDefinition()
        {
            Id = Guid.NewGuid().ToString("N");
            Title = "Новый сценарий";
            Message = "Условие выполнено";
            Enabled = true;
            NotificationEnabled = true;
            Conditions = new List<LocalScenarioCondition>();
            Actions = new List<LocalScenarioAction>();
        }
    }

    public sealed class LocalScenarioRuntimeState
    {
        public bool Fired { get; set; }
        public bool AwaitingTrueUpdate { get; set; }
        public IDictionary<int, bool> ConditionValues { get; private set; }

        public LocalScenarioRuntimeState()
        {
            ConditionValues = new Dictionary<int, bool>();
        }
    }
}
