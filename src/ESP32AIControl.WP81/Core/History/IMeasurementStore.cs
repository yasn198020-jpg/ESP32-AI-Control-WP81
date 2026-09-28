using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ESP32AIControl.WP81.Core.History
{
    public interface IMeasurementStore
    {
        Task AppendAsync(MeasurementPoint point);
        Task<IList<MeasurementPoint>> ReadAsync(string deviceId, string widgetId, DateTimeOffset from, DateTimeOffset to);
        Task<IList<MeasurementPoint>> ReadAllAsync(DateTimeOffset from, DateTimeOffset to);
        Task DeleteBeforeAsync(DateTimeOffset timestamp);
    }
}