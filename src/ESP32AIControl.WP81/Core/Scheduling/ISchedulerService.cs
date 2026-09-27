using System;
using System.Threading.Tasks;

namespace ESP32AIControl.WP81.Core.Scheduling
{
    public interface ISchedulerService
    {
        Task<string> ScheduleAsync(string command, DateTimeOffset executeAt);
        Task CancelAsync(string taskId);
    }
}
