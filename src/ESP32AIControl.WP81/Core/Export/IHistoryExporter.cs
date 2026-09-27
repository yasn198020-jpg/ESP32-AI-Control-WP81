using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ESP32AIControl.WP81.Core.History;

namespace ESP32AIControl.WP81.Core.Export
{
    public interface IHistoryExporter
    {
        Task ExportAsync(IList<MeasurementPoint> points, DateTimeOffset from, DateTimeOffset to, string destinationName);
    }
}
