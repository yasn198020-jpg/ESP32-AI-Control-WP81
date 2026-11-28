using System;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Windows.ApplicationModel;
using Windows.Phone.Management.Deployment;
using Windows.Storage;

namespace ESP32AIControl.UWP
{
    public sealed class UpdateManifest
    {
        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonProperty("channel")] public string Channel { get; set; }
        [JsonProperty("platform")] public string Platform { get; set; }
        [JsonProperty("packageIdentity")] public string PackageIdentity { get; set; }
        [JsonProperty("publisher")] public string Publisher { get; set; }
        [JsonProperty("minOsVersion")] public string MinOsVersion { get; set; }
        [JsonProperty("architecture")] public string Architecture { get; set; }
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
        private const string PackageBaseUrl = "https://raw.githubusercontent.com/yasn198020-jpg/ESP32-AI-Control-Updates/windows-10-mobile/";
        private static readonly HttpClient Client = new HttpClient();

        public static async Task<UpdateCheckResult> CheckAsync()
        {
            string json = await Client.GetStringAsync(ManifestUrl);
            var manifest = JsonConvert.DeserializeObject<UpdateManifest>(json);
            if (manifest == null || manifest.SchemaVersion != 1 ||
                manifest.Channel != "windows-10-mobile" ||
                manifest.Platform != "Windows 10 Mobile" ||
                manifest.PackageIdentity != Package.Current.Id.Name ||
                manifest.Publisher != "CN=ESP32AIControl" ||
                manifest.Architecture != "ARM" ||
                manifest.MinOsVersion != "10.0.10586.0" ||
                string.IsNullOrWhiteSpace(manifest.Version))
                throw new InvalidDataException("Манифест обновления не соответствует этому приложению или устройству.");

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

        public static async Task<StorageFile> DownloadAndVerifyAsync(
            UpdateManifest manifest, Action<long, long> progress = null)
        {
            if (manifest == null || !manifest.Enabled ||
                manifest.Package != "app.appxbundle" ||
                !IsSha256(manifest.Sha256))
                throw new InvalidDataException("Пакет ещё не опубликован или данные манифеста неверны.");

            var uri = new Uri(PackageBaseUrl + manifest.Package);
            using (var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? -1;
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    "ESP32AIControl-update.appxbundle", CreationCollisionOption.ReplaceExisting);

                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = await file.OpenStreamForWriteAsync())
                {
                    var buffer = new byte[64 * 1024];
                    long received = 0;
                    int count;
                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await output.WriteAsync(buffer, 0, count);
                        received += count;
                        if (progress != null) progress(received, total);
                    }
                    await output.FlushAsync();
                }

                string actual;
                using (var input = await file.OpenStreamForReadAsync())
                using (var sha = SHA256.Create())
                {
                    actual = await Task.Run(() =>
                        BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant());
                }

                if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    await file.DeleteAsync();
                    throw new InvalidDataException("SHA-256 пакета не совпадает. Файл удалён.");
                }
                return file;
            }
        }

        public static async Task<PackageInstallResult> InstallAsync(
            StorageFile packageFile, Action<uint> progress = null)
        {
            if (packageFile == null) throw new ArgumentNullException("packageFile");

            string packagePath = packageFile.Path;
            string packageUriText = "(не создан)";
            ulong packageSize = 0;

            try
            {
                if (string.IsNullOrWhiteSpace(packagePath))
                    throw new InvalidDataException("У загруженного пакета отсутствует локальный путь.");

                var properties = await packageFile.GetBasicPropertiesAsync();
                packageSize = properties.Size;
                if (packageSize == 0)
                    throw new InvalidDataException("Загруженный пакет имеет нулевой размер.");

                var packageUri = new Uri(packagePath, UriKind.Absolute);
                packageUriText = packageUri.AbsoluteUri;

                if (!packageUri.IsAbsoluteUri || packageUri.Scheme != Uri.UriSchemeFile)
                    throw new InvalidDataException("Для локального пакета ожидался абсолютный file:// URI.");

                Debug.WriteLine("OTA install preflight: path=" + packagePath +
                    "; uri=" + packageUriText + "; size=" + packageSize);

                var operation = InstallationManager.AddPackageAsync(
                    "ESP32-AI-Control", packageUri);

                if (progress != null)
                    operation.Progress = (op, value) => progress(value);

                var result = await operation;
                string state = result.InstallState.ToString();
                Debug.WriteLine("OTA install result: state=" + state +
                    "; productId=" + result.ProductId + "; error=" + result.ErrorText);

                if (state != "Installed")
                {
                    throw new InvalidOperationException(
                        "Системный установщик не подтвердил установку. " +
                        "InstallState=" + state +
                        "; ProductId=" + result.ProductId +
                        "; ErrorText=" + (result.ErrorText ?? "(пусто)") +
                        "; PackagePath=" + packagePath +
                        "; PackageUri=" + packageUriText +
                        "; PackageSize=" + packageSize + " байт.");
                }

                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("OTA install exception: " + ex);
                throw new InvalidOperationException(
                    "Ошибка этапа системной установки. " +
                    "PackagePath=" + (packagePath ?? "(пусто)") +
                    "; PackageUri=" + packageUriText +
                    "; PackageSize=" + packageSize + " байт. " +
                    "Причина: " + ex.Message, ex);
            }
        }

        private static bool IsSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') ||
                      (c >= 'A' && c <= 'F'))) return false;
            }
            return true;
        }
    }
}