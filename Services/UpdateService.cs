using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeyPulse.Configuration;
using Serilog;

namespace KeyPulse.Services;

public class UpdateService : IDisposable
{
    private readonly HttpClient _httpClient;
    private System.Windows.Threading.DispatcherTimer? _checkTimer;
    private string? _latestVersion;
    private bool _updateAvailable;

    public event Action<UpdateAvailableEventArgs>? UpdateStatusChanged;

    public UpdateService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "KeyPulse-Signal-Update-Checker");
    }

    public string CurrentVersion
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return $"{version?.Major}.{version?.Minor}.{version?.Build}";
        }
    }

    public string? LatestVersion => _latestVersion;

    public bool UpdateAvailable => _updateAvailable;

    public void Start()
    {
        Log.Information("Update service started");
        CheckForUpdatesAsync().ConfigureAwait(false);

        _checkTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _checkTimer.Tick += (_, _) => CheckForUpdatesAsync().ConfigureAwait(false);
        _checkTimer.Start();
    }

    public async Task CheckForUpdatesAsync()
    {
        try
        {
            Log.Information("Checking for updates. Current version: v{CurrentVersion}", CurrentVersion);
            using var response = await _httpClient.GetAsync(AppConstants.Updates.GitHubApiUrl);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to check for updates: {StatusCode}", response.StatusCode);
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);

            if (release?.TagName == null)
            {
                Log.Warning("No valid release found from GitHub API");
                return;
            }

            var latestVersion = release.TagName.TrimStart('v');
            _latestVersion = latestVersion;

            var updateAvailable = IsNewerVersion(CurrentVersion, latestVersion);

            Log.Information(
                "Update check result: Current=v{Current}, Latest=v{Latest}, Available={Available}",
                CurrentVersion,
                latestVersion,
                updateAvailable
            );

            if (updateAvailable != _updateAvailable)
            {
                _updateAvailable = updateAvailable;
                UpdateStatusChanged?.Invoke(
                    new UpdateAvailableEventArgs
                    {
                        Available = updateAvailable,
                        CurrentVersion = CurrentVersion,
                        LatestVersion = latestVersion,
                        DownloadUrl = release.HtmlUrl,
                    }
                );
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error checking for updates");
        }
    }

    public void InstallUpdate()
    {
        if (!_updateAvailable || _latestVersion == null)
        {
            Log.Warning("No update available to install");
            return;
        }

        try
        {
            var downloadUrl = AppConstants.Updates.GetGitHubReleaseTagUrl(_latestVersion);
            Process.Start(new ProcessStartInfo { FileName = downloadUrl, UseShellExecute = true });
            Log.Information("Opened update download page for version v{LatestVersion}", _latestVersion);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open update download page for version v{LatestVersion}", _latestVersion);
        }
    }

    private static bool IsNewerVersion(string current, string latest)
    {
        try
        {
            var currentParts = current.Split('.').Select(int.Parse).ToArray();
            var latestParts = latest.Split('.').Select(int.Parse).ToArray();

            for (int i = 0; i < Math.Max(currentParts.Length, latestParts.Length); i++)
            {
                var currPart = i < currentParts.Length ? currentParts[i] : 0;
                var latestPart = i < latestParts.Length ? latestParts[i] : 0;

                if (latestPart > currPart)
                    return true;
                if (latestPart < currPart)
                    return false;
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error comparing versions: {Current} vs {Latest}", current, latest);
            return false;
        }
    }

    public void Dispose()
    {
        _checkTimer?.Stop();
        _httpClient?.Dispose();
        Log.Information("UpdateService disposed");
        GC.SuppressFinalize(this);
    }

    public class UpdateAvailableEventArgs
    {
        public bool Available { get; set; }
        public string? CurrentVersion { get; set; }
        public string? LatestVersion { get; set; }
        public string? DownloadUrl { get; set; }
    }

    private class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }
    }
}
