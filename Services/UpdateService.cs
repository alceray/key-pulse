using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeyPulse.Helpers;
using Serilog;

namespace KeyPulse.Services;

public class UpdateService : IDisposable
{
    private const string GITHUB_OWNER = "alceray";
    private const string GITHUB_REPO = "keypulse-signal";
    private const string GITHUB_API_URL = $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases/latest";

    private const string InstallerNamePrefix = "KeyPulse-Signal-Setup-";
    private const string InstallerNameSuffix = ".exe";
    private const string ChecksumSuffix = ".sha256";

    // Inno Setup silent install: no UI, no message boxes, no reboot. CloseApplications/RestartApplications
    // use the Restart Manager (coordinated with the app's AppMutex) to close this running app and relaunch it.
    private const string SilentInstallArguments =
        "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS";

    private readonly HttpClient _httpClient;
    private readonly AppTimerService _appTimerService;
    private readonly CancellationTokenSource _lifetimeCts = new();

    private static readonly TimeSpan UpdateCheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan InstallerDownloadTimeout = TimeSpan.FromMinutes(5);

    private string? _latestVersion;
    private IReadOnlyList<GitHubAsset> _latestAssets = Array.Empty<GitHubAsset>();
    private bool _updateAvailable;
    private bool _started;
    private bool _disposed;
    private bool _networkFailureLogged;
    private int _checkInFlight;
    private int _installInFlight;

    private static string GetGitHubReleaseTagUrl(string version) =>
        $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}/releases/tag/v{version}";

    public event Action<UpdateAvailableEventArgs>? UpdateStatusChanged;

    public UpdateService(AppTimerService appTimerService)
    {
        _appTimerService = appTimerService;
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
        if (_started)
            return;

        _started = true;
        Log.Information("Update checker started");

        _ = CheckForUpdatesAsync();
        _appTimerService.DailyTick += OnDailyTick;
    }

    private void OnDailyTick(object? sender, EventArgs e) => _ = CheckForUpdatesAsync();

    public async Task CheckForUpdatesAsync()
    {
        if (_disposed)
            return;

        if (Interlocked.CompareExchange(ref _checkInFlight, 1, 0) != 0)
            return;

        try
        {
            using var timeoutCts = new CancellationTokenSource(UpdateCheckTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, timeoutCts.Token);

            using var response = await _httpClient.GetAsync(GITHUB_API_URL, linkedCts.Token);

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
            var updateAvailable = IsNewerVersion(CurrentVersion, latestVersion);
            var shouldNotify = ShouldNotifyUpdateStatus(
                _updateAvailable,
                _latestVersion,
                updateAvailable,
                latestVersion
            );
            _latestVersion = latestVersion;
            _latestAssets = release.Assets ?? new List<GitHubAsset>();
            if (_networkFailureLogged)
            {
                _networkFailureLogged = false;
                Log.Information("Update check recovered after transient network failure");
            }

            if (updateAvailable)
                Log.Debug("Update available from v{Current:l} to v{Latest:l}", CurrentVersion, latestVersion);
            else
                Log.Debug("No update available for v{Current:l}", CurrentVersion);

            if (shouldNotify)
            {
                _updateAvailable = updateAvailable;
                UpdateStatusChanged?.Invoke(
                    new UpdateAvailableEventArgs { Available = updateAvailable, LatestVersion = latestVersion }
                );
            }
        }
        catch (OperationCanceledException) when (!_lifetimeCts.IsCancellationRequested)
        {
            LogTransientNetworkFailure(
                "Update check timed out after {TimeoutSeconds} seconds",
                UpdateCheckTimeout.TotalSeconds
            );
        }
        catch (HttpRequestException ex) when (IsTransientNetworkFailure(ex))
        {
            LogTransientNetworkFailure("Update check skipped due to transient network issue: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update check failed");
        }
        finally
        {
            Interlocked.Exchange(ref _checkInFlight, 0);
        }
    }

    public void InstallUpdate() => _ = DownloadAndInstallAsync();

    /// <summary>
    /// Downloads the installer for the latest release, verifies its SHA-256 against the published checksum,
    /// then launches it silently and shuts the app down so it can be replaced in place. Falls back to opening
    /// the release page in a browser when the installer or its checksum asset is unavailable. Returns true only
    /// when a verified installer was launched.
    /// </summary>
    public async Task<bool> DownloadAndInstallAsync()
    {
        if (_disposed)
            return false;

        if (!_updateAvailable || _latestVersion == null)
        {
            Log.Warning("No update available to install");
            return false;
        }

        if (Interlocked.CompareExchange(ref _installInFlight, 1, 0) != 0)
        {
            Log.Debug("Update install already in progress");
            return false;
        }

        string? installerPath = null;
        try
        {
            var assets = _latestAssets
                .Where(a => !string.IsNullOrEmpty(a.Name) && !string.IsNullOrEmpty(a.BrowserDownloadUrl))
                .Select(a => (a.Name!, a.BrowserDownloadUrl!))
                .ToList();

            var (installerUrl, installerName, sha256Url) = SelectInstallerAssets(assets);

            if (installerUrl == null || installerName == null)
            {
                Log.Warning("Installer asset unavailable for v{Version}; opening release page", _latestVersion);
                OpenReleasePage();
                return false;
            }

            if (sha256Url == null)
            {
                Log.Warning(
                    "Update checksum unavailable for v{Version}; opening release page for manual install",
                    _latestVersion
                );
                OpenReleasePage();
                return false;
            }

            CleanStaleInstallers();
            installerPath = Path.Combine(Path.GetTempPath(), installerName);

            using var downloadCts = new CancellationTokenSource(InstallerDownloadTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCts.Token,
                downloadCts.Token
            );

            await DownloadFileAsync(installerUrl, installerPath, linkedCts.Token);

            var checksumContents = await _httpClient.GetStringAsync(sha256Url, linkedCts.Token);
            var expectedHash = ParseSha256(checksumContents);
            if (expectedHash == null)
            {
                Log.Warning("Update checksum could not be parsed for v{Version}; opening release page", _latestVersion);
                TryDeleteFile(installerPath);
                OpenReleasePage();
                return false;
            }

            var installerBytes = await File.ReadAllBytesAsync(installerPath, linkedCts.Token);
            if (!VerifyFileHash(installerBytes, expectedHash))
            {
                Log.Error("Downloaded update failed integrity verification for v{Version}", _latestVersion);
                TryDeleteFile(installerPath);
                return false;
            }

            Log.Information("Update v{Version} verified; launching installer", _latestVersion);
            LaunchInstaller(installerPath);
            RequestShutdown();
            return true;
        }
        catch (OperationCanceledException) when (!_lifetimeCts.IsCancellationRequested)
        {
            Log.Warning("Update download timed out for v{Version}", _latestVersion);
            TryDeleteFile(installerPath);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Update download or install failed for v{Version}", _latestVersion);
            TryDeleteFile(installerPath);
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _installInFlight, 0);
        }
    }

    private void OpenReleasePage()
    {
        if (_latestVersion == null)
        {
            Log.Warning("No update available to install");
            return;
        }

        try
        {
            var downloadUrl = GetGitHubReleaseTagUrl(_latestVersion);
            Process.Start(new ProcessStartInfo { FileName = downloadUrl, UseShellExecute = true });
            Log.Information("Opened update download page for version v{LatestVersion}", _latestVersion);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open update download page for version v{LatestVersion}", _latestVersion);
        }
    }

    private async Task DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken
        );
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static void LaunchInstaller(string installerPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = installerPath,
            Arguments = SilentInstallArguments,
            // ShellExecute lets Windows surface the UAC consent dialog if a per-machine install needs elevation.
            UseShellExecute = true,
        };
        Process.Start(psi);
    }

    private static void RequestShutdown()
    {
        var app = System.Windows.Application.Current;
        var dispatcher = app?.Dispatcher;
        if (!ShutdownDispose.IsDispatcherUsable(dispatcher))
            return;

        // Shut down on the UI thread so OnExit releases the single-instance mutex and unlocks the app
        // files, letting the installer (waiting on the AppMutex) replace them.
        dispatcher!.BeginInvoke(new Action(() => app!.Shutdown()));
    }

    private static void CleanStaleInstallers()
    {
        try
        {
            foreach (
                var file in Directory.EnumerateFiles(Path.GetTempPath(), $"{InstallerNamePrefix}*{InstallerNameSuffix}")
            )
                TryDeleteFile(file);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to clean stale update installers");
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to delete temporary update file");
        }
    }

    /// <summary>
    /// Selects the installer asset (name starting with the setup prefix and ending in .exe) and its companion
    /// .sha256 checksum asset from a release's assets. Returns nulls for anything not present.
    /// </summary>
    internal static (string? InstallerUrl, string? InstallerName, string? Sha256Url) SelectInstallerAssets(
        IReadOnlyList<(string Name, string Url)> assets
    )
    {
        string? installerUrl = null;
        string? installerName = null;

        foreach (var (name, url) in assets)
        {
            if (
                name.StartsWith(InstallerNamePrefix, StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(InstallerNameSuffix, StringComparison.OrdinalIgnoreCase)
            )
            {
                installerUrl = url;
                installerName = name;
                break;
            }
        }

        if (installerName == null)
            return (null, null, null);

        var companionName = installerName + ChecksumSuffix;
        string? sha256Url = null;

        foreach (var (name, url) in assets)
        {
            if (name.Equals(companionName, StringComparison.OrdinalIgnoreCase))
            {
                sha256Url = url;
                break;
            }
        }

        // Fall back to any .sha256 asset when the exact companion name is missing.
        if (sha256Url == null)
        {
            foreach (var (name, url) in assets)
            {
                if (name.EndsWith(ChecksumSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    sha256Url = url;
                    break;
                }
            }
        }

        return (installerUrl, installerName, sha256Url);
    }

    /// <summary>
    /// Extracts a SHA-256 hash from a checksum file. Accepts a raw 64-char hex string or the
    /// "&lt;hash&gt; *&lt;filename&gt;" coreutils format. Returns the upper-case hash, or null if malformed.
    /// </summary>
    internal static string? ParseSha256(string? hashFileContents)
    {
        if (string.IsNullOrWhiteSpace(hashFileContents))
            return null;

        foreach (var rawLine in hashFileContents.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            var token = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0].TrimStart('*');
            return IsSha256Hex(token) ? token.ToUpperInvariant() : null;
        }

        return null;
    }

    /// <summary>
    /// Verifies that <paramref name="fileBytes"/> hashes to the expected SHA-256 (case-insensitive).
    /// </summary>
    internal static bool VerifyFileHash(byte[] fileBytes, string? expectedHexSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedHexSha256))
            return false;

        var expected = expectedHexSha256.Trim().TrimStart('*');
        if (!IsSha256Hex(expected))
            return false;

        var actual = Convert.ToHexString(SHA256.HashData(fileBytes));
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256Hex(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    internal static bool IsNewerVersion(string current, string latest)
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

    internal static bool ShouldNotifyUpdateStatus(
        bool previousAvailable,
        string? previousLatestVersion,
        bool updateAvailable,
        string latestVersion
    )
    {
        return previousAvailable != updateAvailable
            || updateAvailable
                && !string.Equals(previousLatestVersion, latestVersion, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            Log.Debug("Update checker dispose skipped because it was already disposed");
            return;
        }

        _disposed = true;
        _appTimerService.DailyTick -= OnDailyTick;
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
        _httpClient.Dispose();
        Log.Information("Update checker disposed");
    }

    private void LogTransientNetworkFailure(string messageTemplate, object detail)
    {
        if (!_networkFailureLogged)
        {
            _networkFailureLogged = true;
            Log.Warning(messageTemplate, detail);
            return;
        }

        Log.Debug(messageTemplate, detail);
    }

    private static bool IsTransientNetworkFailure(HttpRequestException ex)
    {
        if (ex.HttpRequestError == HttpRequestError.NameResolutionError)
            return true;

        if (ex.InnerException is SocketException socketEx)
            return socketEx.SocketErrorCode == SocketError.HostNotFound
                || socketEx.SocketErrorCode == SocketError.TryAgain;

        return false;
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

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
