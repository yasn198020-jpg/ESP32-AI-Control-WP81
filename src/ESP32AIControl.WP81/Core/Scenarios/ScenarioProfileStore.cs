using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core.Scenarios
{
    public sealed class ScenarioProfileStore
    {
        private const string FileName = "scenario-profiles.json";
        private const string TemporaryFileName = "scenario-profiles.json.tmp";
        private readonly SemaphoreSlim _ioGate = new SemaphoreSlim(1, 1);

        public async Task<IList<ScenarioProfile>> LoadAsync()
        {
            await _ioGate.WaitAsync();
            try
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
            finally
            {
                _ioGate.Release();
            }
        }

        public async Task SaveAsync(IList<ScenarioProfile> profiles)
        {
            await _ioGate.WaitAsync();
            try
            {
                var snapshot = new List<ScenarioProfile>();
                if (profiles != null)
                {
                    for (var i = 0; i < profiles.Count; i++)
                        if (profiles[i] != null)
                            snapshot.Add(profiles[i].Clone());
                }

                string json;
                using (var stream = new MemoryStream())
                {
                    var serializer = new DataContractJsonSerializer(typeof(List<ScenarioProfile>));
                    serializer.WriteObject(stream, snapshot);
                    json = Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
                }

                var folder = ApplicationData.Current.LocalFolder;
                var temporary = await folder.CreateFileAsync(
                    TemporaryFileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(temporary, json);

                // Commit only after the complete JSON snapshot has been written.
                // A failed write/rename leaves the previous profile file intact.
                await temporary.RenameAsync(FileName, NameCollisionOption.ReplaceExisting);
            }
            finally
            {
                _ioGate.Release();
            }
        }
    }
}
