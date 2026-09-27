using System.Threading.Tasks;

namespace ESP32AIControl.WP81.Core.Scenarios
{
    public interface ICommandExecutor
    {
        Task ExecuteAsync(string command);
    }
}
