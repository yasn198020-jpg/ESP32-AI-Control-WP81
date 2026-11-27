using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core
{
    public sealed class AppSettingsStore
    {
        private const string FileName = "app-settings.json";
        private static readonly SemaphoreSlim SaveLock = new SemaphoreSlim(1, 1);

        private static Task<StorageFile> GetFileAsync(StorageFolder folder, string fileName)
        {
            var tcs = new TaskCompletionSource<StorageFile>();
            var operation = folder.GetFileAsync(fileName);
            operation.Completed = (op, status) =>
            {
                try
                {
                    if (status == AsyncStatus.Completed)
                        tcs.SetResult(op.GetResults());
                    else if (status == AsyncStatus.Canceled)
                        tcs.SetCanceled();
                    else
                        tcs.SetException(new InvalidOperationException("WinRT GetFileAsync failed."));
                }
                catch (Exception ex) { tcs.SetException(ex); }
            };
            return tcs.Task;
        }

        private static Task<StorageFile> CreateFileAsync(StorageFolder folder, string fileName, CreationCollisionOption option)
        {
            var tcs = new TaskCompletionSource<StorageFile>();
            var operation = folder.CreateFileAsync(fileName, option);
            operation.Completed = (op, status) =>
            {
                try
                {
                    if (status == AsyncStatus.Completed)
                        tcs.SetResult(op.GetResults());
                    else if (status == AsyncStatus.Canceled)
                        tcs.SetCanceled();
                    else
                        tcs.SetException(new InvalidOperationException("WinRT CreateFileAsync failed."));
                }
                catch (Exception ex) { tcs.SetException(ex); }
            };
            return tcs.Task;
        }

        private static Task<string> ReadTextAsync(StorageFile file)
        {
            var tcs = new TaskCompletionSource<string>();
            var operation = FileIO.ReadTextAsync(file);
            operation.Completed = (op, status) =>
            {
                try
                {
                    if (status == AsyncStatus.Completed)
                        tcs.SetResult(op.GetResults());
                    else if (status == AsyncStatus.Canceled)
                        tcs.SetCanceled();
                    else
                        tcs.SetException(new InvalidOperationException("WinRT ReadTextAsync failed."));
                }
                catch (Exception ex) { tcs.SetException(ex); }
            };
            return tcs.Task;
        }

        private static Task WriteTextAsync(StorageFile file, string text)
        {
            var tcs = new TaskCompletionSource<object>();
            var operation = FileIO.WriteTextAsync(file, text);
            operation.Completed = (op, status) =>
            {
                try
                {
                    if (status == AsyncStatus.Completed)
                        tcs.SetResult(null);
                    else if (status == AsyncStatus.Canceled)
                        tcs.SetCanceled();
                    else
                        tcs.SetException(new InvalidOperationException("WinRT FileIO.WriteTextAsync failed."));
                }
                catch (Exception ex) { tcs.SetException(ex); }
            };
            return tcs.Task;
        }

        private static async Task<AppSettings> LoadFromFolderAsync(StorageFolder folder)
        {
            var file = await GetFileAsync(folder, FileName);
            var json = await ReadTextAsync(file);
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                var serializer = new DataContractJsonSerializer(typeof(AppSettings));
                var settings = serializer.ReadObject(stream) as AppSettings;
                if (settings == null)
                    throw new InvalidOperationException("Settings file is empty.");
                return settings;
            }
        }

        private static string Serialize(AppSettings settings)
        {
            using (var stream = new MemoryStream())
            {
                var serializer = new DataContractJsonSerializer(typeof(AppSettings));
                serializer.WriteObject(stream, settings);
                return Encoding.UTF8.GetString(stream.ToArray(), 0, (int)stream.Length);
            }
        }

        public async Task ExportToFileAsync(StorageFile file, AppSettings settings)
        {
            if (file == null) throw new ArgumentNullException("file");
            if (settings == null) throw new ArgumentNullException("settings");
            await WriteTextAsync(file, Serialize(settings));
        }

        public async Task<AppSettings> ImportFromFileAsync(StorageFile file)
        {
            if (file == null) throw new ArgumentNullException("file");
            var json = await ReadTextAsync(file);
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                var serializer = new DataContractJsonSerializer(typeof(AppSettings));
                var settings = serializer.ReadObject(stream) as AppSettings;
                if (settings == null)
                    throw new InvalidOperationException("Файл резервной копии не содержит настроек.");
                if (settings.SavedMqttProfiles == null)
                    settings.SavedMqttProfiles = new System.Collections.Generic.List<SavedMqttProfile>();
                return settings;
            }
        }

        private static async Task SaveToFolderAsync(StorageFolder folder, string json)
        {
            var file = await CreateFileAsync(folder, FileName, CreationCollisionOption.ReplaceExisting);
            await WriteTextAsync(file, json);
        }

        public string LastLoadSource { get; private set; }
        public string LastRoamingSaveStatus { get; private set; }

        private static bool HasConfiguredMqtt(AppSettings settings)
        {
            if (settings == null) return false;
            return !string.IsNullOrWhiteSpace(settings.UserName)
                || !string.IsNullOrWhiteSpace(settings.Password)
                || !string.IsNullOrWhiteSpace(settings.Host) && settings.Host != "m4.wqtt.ru"
                || !string.IsNullOrWhiteSpace(settings.Port) && settings.Port != "2815"
                || !string.IsNullOrWhiteSpace(settings.Prefix) && settings.Prefix != "dghjko"
                || !string.IsNullOrWhiteSpace(settings.ClientId) && settings.ClientId != "ESP32-AI-Control-WP81"
                || settings.UseTls;
        }

        private static void CopyMqttSettings(AppSettings source, AppSettings target)
        {
            target.Host = source.Host;
            target.Port = source.Port;
            target.Prefix = source.Prefix;
            target.UserName = source.UserName;
            target.Password = source.Password;
            target.ClientId = source.ClientId;
            target.UseTls = source.UseTls;
        }

        private static bool HasSavedProfiles(AppSettings settings)
        {
            return settings != null && settings.SavedMqttProfiles != null
                && settings.SavedMqttProfiles.Count > 0;
        }

        private static bool RestoreSavedProfiles(AppSettings source, AppSettings target)
        {
            if (!HasSavedProfiles(source)) return false;

            var changed = false;
            if (!HasSavedProfiles(target))
            {
                target.SavedMqttProfiles = source.SavedMqttProfiles;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(target.DefaultMqttProfileName)
                && !string.IsNullOrWhiteSpace(source.DefaultMqttProfileName))
            {
                target.DefaultMqttProfileName = source.DefaultMqttProfileName;
                changed = true;
            }

            return changed;
        }

        public async Task<AppSettings> LoadAsync()
        {
            AppSettings local = null;
            AppSettings roaming = null;

            try { local = await LoadFromFolderAsync(ApplicationData.Current.LocalFolder); }
            catch { }

            try { roaming = await LoadFromFolderAsync(ApplicationData.Current.RoamingFolder); }
            catch { }

            // A local file may exist but contain factory/default MQTT values.
            // In that case, prefer a configured roaming MQTT profile while
            // retaining unrelated local preferences and scenario settings.
            if (local != null)
            {
                var restored = false;

                // A newly installed app can create a local file with factory
                // values before the roaming copy becomes available. Restore
                // the connection and the saved-profile list independently.
                if (!HasConfiguredMqtt(local) && HasConfiguredMqtt(roaming))
                {
                    CopyMqttSettings(roaming, local);
                    restored = true;
                    LastLoadSource = "RoamingFolder (MQTT restored over default local values)";
                }

                if (RestoreSavedProfiles(roaming, local))
                {
                    restored = true;
                    LastLoadSource = "RoamingFolder (saved MQTT profiles restored)";
                }

                if (restored)
                    await SaveAsync(local);
                else
                    LastLoadSource = "LocalFolder";

                return local;
            }

            if (roaming != null)
            {
                LastLoadSource = "RoamingFolder";
                await SaveAsync(roaming); // recreate local copy for normal startup
                return roaming;
            }

            LastLoadSource = "Defaults (no readable settings file)";
            return new AppSettings();
        }

        public async Task SaveAsync(AppSettings settings)
        {
            if (settings == null) return;

            await SaveLock.WaitAsync();
            try
            {
                var json = Serialize(settings);

                // Local copy is the primary source while the app is installed.
                await SaveToFolderAsync(ApplicationData.Current.LocalFolder, json);

                // Roaming data is a second copy intended to survive reinstall
                // and follow the same user to another compatible installation.
                // Keep the app usable if roaming is unavailable or over quota.
                try
                {
                    await SaveToFolderAsync(ApplicationData.Current.RoamingFolder, json);
                    LastRoamingSaveStatus = "OK";
                }
                catch (Exception ex)
                {
                    LastRoamingSaveStatus = "ERROR: " + ex.Message;
                    // Local save succeeded; roaming backup is best-effort.
                }
            }
            finally
            {
                SaveLock.Release();
            }
        }
    }
}
