using System;
using System.Runtime.Serialization;

namespace ESP32AIControl.WP81.Models
{
    [DataContract]
    public sealed class ScheduledCommand
    {
        [DataMember] public string Id { get; set; }
        [DataMember] public string Command { get; set; }
        [DataMember] public DateTimeOffset ExecuteAt { get; set; }
        [DataMember] public string Status { get; set; }
        [DataMember] public string LastError { get; set; }
        [DataMember] public DateTimeOffset CreatedAt { get; set; }
        [DataMember] public DateTimeOffset? CompletedAt { get; set; }

        public ScheduledCommand Clone()
        {
            return new ScheduledCommand
            {
                Id = Id,
                Command = Command,
                ExecuteAt = ExecuteAt,
                Status = Status,
                LastError = LastError,
                CreatedAt = CreatedAt,
                CompletedAt = CompletedAt
            };
        }
    }
}