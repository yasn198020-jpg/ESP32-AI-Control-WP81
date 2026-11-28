using System.Collections.Generic;
using System.Linq;

namespace ESP32AIControl.UWP.Core.Marfa
{
    public sealed class DialogTargetContext
    {
        public string DeviceId { get; set; }
        public string WidgetId { get; set; }
        public string Title { get; set; }
        public string ActionValue { get; set; }
        public LocalCommandAction Intent { get; set; }
    }

    public sealed class DialogTurn
    {
        public string UserText { get; set; }
        public LocalCommandAction Action { get; set; }
        public string DeviceId { get; set; }
        public string WidgetId { get; set; }
        public string Value { get; set; }
    }

    public sealed class DialogContext
    {
        private readonly int maxTurns;
        private readonly Queue<DialogTurn> turns = new Queue<DialogTurn>();

        public LocalCommandActionItem LastTarget { get; private set; }
        public string LastTargetTitle { get; private set; } = "";
        public LocalCommandAction? LastIntent { get; private set; }
        public string LastActionValue { get; private set; } = "";
        public string PendingOriginalText { get; private set; } = "";
        public string LastUserText { get; private set; } = "";

        public DialogContext(int maxTurns = 6)
        {
            this.maxTurns = maxTurns < 1 ? 1 : maxTurns;
        }

        public void BeginTurn(string userText)
        {
            PendingOriginalText = "";
            LastUserText = userText ?? "";
        }

        public void SetPending(string originalText) { PendingOriginalText = originalText ?? ""; }
        public void ClearPending() { PendingOriginalText = ""; }

        public void Remember(string userText, LocalCommandResult result)
        {
            if (result == null) return;
            LastIntent = result.Action;
            switch (result.Action)
            {
                case LocalCommandAction.CONTROL:
                    if (result.ActionItems != null && result.ActionItems.Count > 0)
                    {
                        LastTarget = result.ActionItems[0];
                        var reply = result.Reply ?? "";
                        var marker = reply.IndexOf(": ");
                        LastTargetTitle = marker >= 0 ? reply.Substring(marker + 2).Split(new[] { " через " }, System.StringSplitOptions.None)[0].Trim() : reply.Trim();
                        LastActionValue = LastTarget.Value ?? "";
                    }
                    break;
                case LocalCommandAction.READ_VALUE:
                    LastTarget = new LocalCommandActionItem { DeviceId = result.DeviceId, WidgetId = result.WidgetId, Value = result.Value };
                    LastTargetTitle = (result.Reply ?? "").Split(':')[0].Trim();
                    LastActionValue = "";
                    break;
                case LocalCommandAction.SMART_RULE:
                    LastTarget = new LocalCommandActionItem { DeviceId = result.ActionDeviceId, WidgetId = result.ActionWidgetId, Value = result.ActionValue };
                    LastTargetTitle = result.Reply ?? "";
                    LastActionValue = result.ActionValue ?? "";
                    break;
            }

            turns.Enqueue(new DialogTurn
            {
                UserText = userText ?? "", Action = result.Action, DeviceId = result.DeviceId ?? "",
                WidgetId = result.WidgetId ?? "", Value = result.Value ?? ""
            });
            while (turns.Count > maxTurns) turns.Dequeue();
        }

        public IList<DialogTurn> RecentTurns() { return turns.ToList(); }

        public DialogTargetContext Snapshot()
        {
            if (LastTarget == null) return null;
            return new DialogTargetContext
            {
                DeviceId = LastTarget.DeviceId, WidgetId = LastTarget.WidgetId, Title = LastTargetTitle,
                ActionValue = LastActionValue, Intent = LastIntent ?? LocalCommandAction.NOT_FOUND
            };
        }

        public void Clear()
        {
            turns.Clear(); LastTarget = null; LastTargetTitle = ""; LastIntent = null;
            LastActionValue = ""; PendingOriginalText = ""; LastUserText = "";
        }
    }
}