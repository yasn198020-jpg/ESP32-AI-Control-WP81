using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Windows.ApplicationModel;
using Windows.Storage;

namespace ESP32AIControl.UWP
{
    public sealed class UpdateManifest
    {
        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonProperty("channel")] public string Channel { get; set; }
        [JsonProperty("packageIdentity")] public string PackageIdentity { get; set; }
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("package")] public string Package { get; set; }
        [JsonProperty("sha256")] public string Sha256 { get; set; }
        [JsonProperty("enabled")] public bool Enabled { get; set; }
        [JsonProperty("releaseNotes")] public string ReleaseNotes { get; set; }
    }

    public sealed class UpdateCheckResult
    {
        public UpdateManifest Manifest { get; set; }
        public Version CurrentVersion { get; set; }
        public Version AvailableVersion { get; set; }
        public bool IsUpdateAvailable { get; set; }
    }

    public static class UpdateService
    {
        public const string ManifestUrl = "https://raw.githubusercontent.com/yasn198020-jpg/ESP32-AI-Control-Updates/windows-10-mobile/version.json";
        private static readonly HttpClient Client = new HttpClient();

        public static async Task<UpdateCheckResult> CheckAsync()
        {
            string json = await Client.GetStringAsync(ManifestUrl);
            var manifest = JsonConvert.DeserializeObject<UpdateManifest>(json);
            if (manifest == null || manifest.SchemaVersion != 1 ||
                manifest.Channel != "windows-10-mobile" ||
                manifest.PackageIdentity != Package.Current.Id.Name ||
                string.IsNullOrWhiteSpace(manifest.Version))
                throw new InvalidDataException("Манифест обновления неверен или относится к другому приложению.");

            Version available;
            if (!Version.TryParse(manifest.Version, out available))
                throw new InvalidDataException("Некорректная версия в манифесте.");

            var v = Package.Current.Id.Version;
            var current = new Version(v.Major, v.Minor, v.Build, v.Revision);
            return new UpdateCheckResult
            {
                Manifest = manifest,
                CurrentVersion = current,
                AvailableVersion = available,
                IsUpdateAvailable = manifest.Enabled && available > current
            };
        }

        public static async Task<StorageFile> DownloadAndVerifyAsync(UpdateManifest manifest)
        {
            if (manifest == null || !manifest.Enabled ||
                string.IsNullOrWhiteSpace(manifest.Package) ||
                string.IsNullOrWhiteSpace(manifest.Sha256) || manifest.Sha256.Length != 64)
                throw new InvalidDataException("Пакет ещё не опубликован или SHA-256 не задан.");
            if (manifest.Package.Contains("..") || manifest.Package.Contains("/") ||
                manifest.Package.Contains("\\"))
                throw new InvalidDataException("Недопустимое имя пакета.");

            var uri = new Uri("https://raw.githubusercontent.com/yasn198020-jpg/ESP32-AI-Control-Updates/windows-10-mobile/" + manifest.Package);
            byte[] data = await Client.GetByteArrayAsync(uri);
            using (var sha = SHA256.Create())
            {
                string actual = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
                if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 не совпадает. Пакет отклонён.");
            }
            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                "ESP32AIControl-update.appxbundle", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBytesAsync(file, data);
            return file;
        }
    }
}