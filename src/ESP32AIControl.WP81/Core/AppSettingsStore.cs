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

        private static async Task SaveToFolderAsync(StorageFolder folder, string json)
        {
            var file = await CreateFileAsync(folder, FileName, CreationCollisionOption.ReplaceExisting);
            await WriteTextAsync(file, json);
        }

        public async Task<AppSettings> LoadAsync()
        {
            try
            {
                return await LoadFromFolderAsync(ApplicationData.Current.LocalFolder);
            }
            catch
            {
                // LocalFolder is cleared on uninstall. Try the user's roaming
                // application data so settings can be restored after reinstall
                // or on another phone using the same app package and Microsoft account.
                try
                {
                    var settings = await LoadFromFolderAsync(ApplicationData.Current.RoamingFolder);
                    await SaveAsync(settings); // restore local copy for normal startup
                    return settings;
                }
                catch
                {
                    return new AppSettings();
                }
            }
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
                }
                catch
                {
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
