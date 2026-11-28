using System;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.Security.Credentials;
using Windows.Security.Cryptography.Certificates;
using Windows.Web.Http.Filters;
using WdpHttpClient = Windows.Web.Http.HttpClient;
using WdpHttpMethod = Windows.Web.Http.HttpMethod;
using WdpHttpRequestMessage = Windows.Web.Http.HttpRequestMessage;
using WdpHttpMultipartFormDataContent = Windows.Web.Http.HttpMultipartFormDataContent;
using WdpHttpStreamContent = Windows.Web.Http.HttpStreamContent;
using WdpHttpMediaTypeHeaderValue = Windows.Web.Http.Headers.HttpMediaTypeHeaderValue;
using WdpHttpCredentialsHeaderValue = Windows.Web.Http.Headers.HttpCredentialsHeaderValue;

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

    public sealed class DevicePortalCredentials
    {
        public string Address { get; set; }
        public string UserName { get; set; }
        public string Password { get; set; }
    }

    public static class UpdateService
    {
        private const string PortalCredentialResource = "ESP32AIControl.DevicePortal";
        private const string PortalAddressKey = "DevicePortalAddress";
        private const string PortalUserKey = "DevicePortalUser";
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

        public static string GetDevicePortalAddress()
        {
            object value = ApplicationData.Current.LocalSettings.Values[PortalAddressKey];
            return value == null ? "192.168.1.58" : value.ToString();
        }

        public static string GetDevicePortalUserName()
        {
            object value = ApplicationData.Current.LocalSettings.Values[PortalUserKey];
            return value == null ? "" : value.ToString();
        }

        public static string GetDevicePortalPassword()
        {
            string userName = GetDevicePortalUserName();
            if (string.IsNullOrWhiteSpace(userName)) return "";

            try
            {
                var vault = new PasswordVault();
                foreach (var credential in vault.FindAllByResource(PortalCredentialResource))
                {
                    if (string.Equals(credential.UserName, userName, StringComparison.Ordinal))
                    {
                        credential.RetrievePassword();
                        return credential.Password;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Device Portal credential lookup failed: " + ex.Message);
            }
            return "";
        }

        public static void SaveDevicePortalCredentials(string address, string userName, string password)
        {
            if (string.IsNullOrWhiteSpace(address) ||
                string.IsNullOrWhiteSpace(userName) ||
                string.IsNullOrEmpty(password))
                throw new ArgumentException("Укажите адрес Device Portal, логин и пароль.");

            ApplicationData.Current.LocalSettings.Values[PortalAddressKey] = address.Trim();
            ApplicationData.Current.LocalSettings.Values[PortalUserKey] = userName.Trim();

            var vault = new PasswordVault();
            try
            {
                foreach (var credential in vault.FindAllByResource(PortalCredentialResource))
                    vault.Remove(credential);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Device Portal credential cleanup: " + ex.Message);
            }

            vault.Add(new PasswordCredential(
                PortalCredentialResource, userName.Trim(), password));
        }

        public static async Task<string> InstallViaDevicePortalAsync(
            StorageFile packageFile, string address, string userName, string password)
        {
            if (packageFile == null) throw new ArgumentNullException("packageFile");
            if (string.IsNullOrWhiteSpace(address) ||
                string.IsNullOrWhiteSpace(userName) ||
                string.IsNullOrEmpty(password))
                throw new ArgumentException("Не заданы параметры подключения к Device Portal.");

            string endpoint = address.Trim();
            if (!endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                endpoint = "https://" + endpoint;
            if (!endpoint.EndsWith("/", StringComparison.Ordinal))
                endpoint += "/";

            Uri rootUri;
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out rootUri) ||
                !string.Equals(rootUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(rootUri.UserInfo))
                throw new InvalidDataException("Укажите адрес Device Portal в формате https://IP[:порт].");

            var properties = await packageFile.GetBasicPropertiesAsync();
            if (properties.Size == 0)
                throw new InvalidDataException("Загруженный пакет имеет нулевой размер.");

            var filter = new HttpBaseProtocolFilter();
            // Lumia Device Portal uses a device-local self-signed HTTPS certificate.
            // Ignore only certificate-chain/name errors for this explicitly configured endpoint.
            filter.IgnorableServerCertificateErrors.Add(ChainValidationResult.Untrusted);
            filter.IgnorableServerCertificateErrors.Add(ChainValidationResult.InvalidName);
            filter.IgnorableServerCertificateErrors.Add(ChainValidationResult.Expired);
            string requestUrl = new Uri(rootUri,
                "api/app/packagemanager/package?package=" +
                Uri.EscapeDataString(packageFile.Name)).AbsoluteUri;

            Debug.WriteLine("OTA Device Portal install: url=" + requestUrl +
                "; file=" + packageFile.Path + "; size=" + properties.Size);

            using (var client = new WdpHttpClient(filter))
            using (var request = new WdpHttpRequestMessage(WdpHttpMethod.Post, new Uri(requestUrl)))
            using (var multipart = new WdpHttpMultipartFormDataContent())
            using (var fileStream = await packageFile.OpenAsync(FileAccessMode.Read))
            using (var fileContent = new WdpHttpStreamContent(fileStream))
            {
                fileContent.Headers.ContentType =
                    new WdpHttpMediaTypeHeaderValue("application/octet-stream");
                string basicToken = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    "auto-" + userName.Trim() + ":" + password));
                request.Headers.Authorization =
                    new WdpHttpCredentialsHeaderValue("Basic", basicToken);
                multipart.Add(fileContent, "package", packageFile.Name);
                request.Content = multipart;

                using (var response = await client.SendRequestAsync(request))
                {
                    string responseBody = response.Content == null
                        ? ""
                        : await response.Content.ReadAsStringAsync();

                    Debug.WriteLine("OTA Device Portal response: HTTP " +
                        (int)response.StatusCode + " " + response.ReasonPhrase +
                        "; body=" + responseBody);

                    if ((int)response.StatusCode != 200)
                    {
                        throw new InvalidOperationException(
                            "Device Portal отклонил установку: HTTP " +
                            (int)response.StatusCode + " " + response.ReasonPhrase +
                            ". " + (string.IsNullOrWhiteSpace(responseBody)
                                ? "Ответ сервера пуст."
                                : responseBody));
                    }

                    return "Device Portal принял пакет (HTTP 200). Windows выполняет установку. " +
                        "Приложение может автоматически закрыться или перезапуститься.";
                }
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