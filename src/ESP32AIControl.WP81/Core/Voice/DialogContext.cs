using System;
using System.Collections.Generic;
using System.Linq;

namespace ESP32AIControl.WP81.Core.Voice
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

    /// <summary>Short-lived conversational memory; never persisted as command history.</summary>
    public sealed class DialogContext
    {
        private readonly Queue<DialogTurn> _turns = new Queue<DialogTurn>();
        private readonly int _maxTurns;

        public LocalCommandActionItem LastTarget { get; private set; }
        public string LastTargetTitle { get; private set; }
        public LocalCommandAction? LastIntent { get; private set; }
        public string LastActionValue { get; private set; }
        public string PendingOriginalText { get; private set; }
        public string LastUserText { get; private set; }

        public DialogContext(int maxTurns = 6)
        {
            _maxTurns = Math.Max(1, maxTurns);
            LastTargetTitle = LastActionValue = PendingOriginalText = LastUserText = string.Empty;
        }

        public void BeginTurn(string userText)
        {
            PendingOriginalText = string.Empty;
            LastUserText = userText ?? string.Empty;
        }

        public void SetPending(string originalText) { PendingOriginalText = originalText ?? string.Empty; }
        public void ClearPending() { PendingOriginalText = string.Empty; }

        public void Remember(string userText, LocalCommandResult result)
        {
            if (result == null) return;
            LastIntent = result.Action;
            if (result.Action == LocalCommandAction.Control && result.ActionItems.Count > 0)
            {
                LastTarget = result.ActionItems[0];
                LastTargetTitle = result.Reply ?? string.Empty;
                LastActionValue = LastTarget.Value ?? string.Empty;
            }
            else if (result.Action == LocalCommandAction.ReadValue)
            {
                LastTarget = new LocalCommandActionItem { DeviceId = result.DeviceId, WidgetId = result.WidgetId, Value = result.Value };
                LastTargetTitle = result.Reply ?? string.Empty;
                LastActionValue = string.Empty;
            }
            else if (result.Action == LocalCommandAction.SmartRule)
            {
                LastTarget = new LocalCommandActionItem { DeviceId = result.ActionDeviceId, WidgetId = result.ActionWidgetId, Value = result.ActionValue };
                LastTargetTitle = result.Reply ?? string.Empty;
                LastActionValue = result.ActionValue ?? string.Empty;
            }

            _turns.Enqueue(new DialogTurn
            {
                UserText = userText ?? string.Empty, Action = result.Action,
                DeviceId = result.DeviceId ?? string.Empty, WidgetId = result.WidgetId ?? string.Empty,
                Value = result.Value ?? string.Empty
            });
            while (_turns.Count > _maxTurns) _turns.Dequeue();
        }

        public IList<DialogTurn> RecentTurns() { return _turns.ToList(); }

        public DialogTargetContext Snapshot()
        {
            if (LastTarget == null) return null;
            return new DialogTargetContext
            {
                DeviceId = LastTarget.DeviceId, WidgetId = LastTarget.WidgetId,
                Title = LastTargetTitle, ActionValue = LastActionValue,
                Intent = LastIntent ?? LocalCommandAction.NotFound
            };
        }

        public void Clear()
        {
            _turns.Clear();
            LastTarget = null;
            LastTargetTitle = LastActionValue = PendingOriginalText = LastUserText = string.Empty;
            LastIntent = null;
        }
    }
}