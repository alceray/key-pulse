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
    private bool _disposed;

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
        if (_checkTimer != null)
            return;

        var startStopwatch = Stopwatch.StartNew();
        Log.Information("Update check started");
        CheckForUpdatesAsync().ConfigureAwait(false);

        _checkTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _checkTimer.Tick += (_, _) => CheckForUpdatesAsync().ConfigureAwait(false);
        _checkTimer.Start();
        startStopwatch.Stop();
        Log.Information("Update check completed in {ElapsedMs}ms", startStopwatch.ElapsedMilliseconds);
    }

    public async Task CheckForUpdatesAsync()
    {
        try
        {
            using var response = await _httpClient.GetAsync(AppConstants.Updates.GitHubApiUrl);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Update check request failed: {StatusCode}", response.StatusCode);
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            var release = JsonSerializer.Deserialize<GitHubRelease>(json);

            if (release?.TagName == null)
            {
                Log.Warning("Update check returned invalid release data");
                return;
            }

            var latestVersion = release.TagName.TrimStart('v');
            _latestVersion = latestVersion;

            var updateAvailable = IsNewerVersion(CurrentVersion, latestVersion);

            Log.Debug(
                "Update check result: Current=v{Current}, Latest=v{Latest}, UpdateAvailable={Available}",
                CurrentVersion,
                latestVersion,
                updateAvailable
            );

            if (updateAvailable != _updateAvailable)
            {
                _updateAvailable = updateAvailable;
                UpdateStatusChanged?.Invoke(
                    new UpdateAvailableEventArgs { Available = updateAvailable, LatestVersion = latestVersion }
                );
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update check failed");
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

            for (var i = 0; i < Math.Max(currentParts.Length, latestParts.Length); i++)
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
            Log.Error(ex, "Version comparison failed: {Current} vs {Latest}", current, latest);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            Log.Debug("Update check dispose skipped because it was already disposed");
            return;
        }

        _disposed = true;
        _checkTimer?.Stop();
        _checkTimer = null;
        _httpClient.Dispose();
        Log.Information("Update check stopped and disposed");
    }

    public class UpdateAvailableEventArgs
    {
        public bool Available { get; set; }
        public string? LatestVersion { get; set; }
    }

    private class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }
    }
}
