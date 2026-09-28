using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core
{
    public sealed class AppSettingsStore
    {
        private const string FileName = "app-settings.json";

        public async Task<AppSettings> LoadAsync()
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName).AsTask();
                var json = await FileIO.ReadTextAsync(file).AsTask();
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var serializer = new DataContractJsonSerializer(typeof(AppSettings));
                    return serializer.ReadObject(stream) as AppSettings ?? new AppSettings();
                }
            }
            catch
            {
                return new AppSettings();
            }
        }

        public async Task SaveAsync(AppSettings settings)
        {
            if (settings == null) return;
            using (var stream = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(AppSettings));
                serializer.WriteObject(stream, settings);
                var json = Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting).AsTask();
                await FileIO.WriteTextAsync(file, json).AsTask();
            }
        }
    }
}