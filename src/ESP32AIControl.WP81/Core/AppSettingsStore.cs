using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Storage;
using ESP32AIControl.WP81.Models;

namespace ESP32AIControl.WP81.Core
{
    public sealed class AppSettingsStore
    {
        private const string FileName = "app-settings.json";

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
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
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
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
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
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
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
                        tcs.SetException(new InvalidOperationException("WinRT WriteTextAsync failed."));
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            };
            return tcs.Task;
        }

        public async Task<AppSettings> LoadAsync()
        {
            try
            {
                var file = await GetFileAsync(ApplicationData.Current.LocalFolder, FileName);
                var json = await ReadTextAsync(file);
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
                var file = await CreateFileAsync(
                    ApplicationData.Current.LocalFolder,
                    FileName,
                    CreationCollisionOption.ReplaceExisting);
                await WriteTextAsync(file, json);
            }
        }
    }
}