using System.Reflection;

namespace KeyPulse.Configuration;

public static class AppConstants
{
    public static class App
    {
        public static string ProductName => Assembly.GetExecutingAssembly().GetName().Name ?? "KeyPulse Signal";
#if DEBUG
        public static string DefaultName => ProductName + " (Test)";
#else
        public static string DefaultName => ProductName;
#endif
        public const string StartupArgument = "--startup";
        public const string ActivationEventSuffix = ".ACTIVATE";
    }

    public static class Paths
    {
        public const string TestDataDirectoryName = "Test";
        public const string LogsDirectoryName = "Logs";
        public const string SettingsFileName = "settings.json";
        public const string DatabaseFileName = "keypulse-data.db";
        public const string DatabaseBackupsDirectoryName = "DbBackups";
        public const string PreMigrationBackupSuffix = ".pre-migration";
        public const string HeartbeatFileName = "heartbeat.txt";
        public const string LogFilePattern = "*.log";
        public const string RollingLogFileTemplate = "keypulse-logs-.log";
    }

    public static class Troubleshooting
    {
        public const int RetainedFileCountLimit = 14;
        public const int StartupWarningBalloonTimeoutMs = 5000;
        public const string AllLabel = "All";
        public const string FatalToken = "[FTL]";
        public const string ErrorToken = "[ERR]";
        public const string WarningToken = "[WRN]";
        public const string InformationToken = "[INF]";
        public const string DebugToken = "[DBG]";
        public const string TimestampPatternRegex = @"^\d{4}-\d{2}-\d{2}";
        public const double DatePickerWidth = 140;
        public const double SearchPanelWidth = 360;
        public const string SessionStartMarker = "Application startup started";
        public const char DividerChar = '─';
        public const double DividerCharWidth = 7.1;
        public const int DividerMinChars = 48;

        public static readonly IReadOnlyList<string> FilterNames =
        [
            AllLabel,
            "Fatal",
            "Error",
            "Warning",
            "Information",
            "Debug",
        ];
    }

    public static class Registry
    {
        public const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    }

    public static class UsbMonitoring
    {
        public const int SignalAggregationSeconds = 1;
        public const int HeartbeatIntervalSeconds = 30;
        public const string UnknownDeviceName = "Unknown Device";
    }

    public static class Updates
    {
        private const string GITHUB_OWNER = "alceray";
        private const string GITHUB_REPO = "keypulse-signal";
        public const string GitHubApiUrl = $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases/latest";

        public static string GetGitHubReleaseTagUrl(string version) =>
            $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}/releases/tag/v{version}";
    }

    public static class Tray
    {
        public const string TrayIconRelativePath = @"Assets\keyboard_mouse_icon.ico";
    }

    public static class Date
    {
        public const string DateFormat = "yyyy-MM-dd HH:mm:ss";
        public const string LogFileDateFormat = "yyyyMMdd";
        public const string LogDisplayDateFormat = "yyyy-MM-dd";
    }
}
