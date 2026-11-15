using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core.Scenarios
{
    public sealed class ScenarioProfileStore
    {
        private const string FileName = "scenario-profiles.json";

        public async Task<IList<ScenarioProfile>> LoadAsync()
        {
            StorageFile file;
            try
            {
                file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
            }
            catch (FileNotFoundException)
            {
                return new List<ScenarioProfile>();
            }

            var json = await FileIO.ReadTextAsync(file);
            if (string.IsNullOrWhiteSpace(json))
                return new List<ScenarioProfile>();

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                var serializer = new DataContractJsonSerializer(typeof(List<ScenarioProfile>));
                var profiles = serializer.ReadObject(stream) as List<ScenarioProfile>;
                return profiles ?? new List<ScenarioProfile>();
            }
        }

        public async Task SaveAsync(IList<ScenarioProfile> profiles)
        {
            var snapshot = new List<ScenarioProfile>();
            if (profiles != null)
            {
                for (var i = 0; i < profiles.Count; i++)
                    if (profiles[i] != null)
                        snapshot.Add(profiles[i].Clone());
            }

            using (var stream = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(List<ScenarioProfile>));
                serializer.WriteObject(stream, snapshot);
                var json = Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, json);
            }
        }
    }
}