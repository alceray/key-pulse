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
        public const string TrayArgument = "--tray";
        public const string ActivationEventSuffix = ".ACTIVATE";
        public const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        public const string TrayIconRelativePath = @"Assets\keypulse-signal-icon.ico";
        public const int StartupWarningBalloonTimeoutMs = 5000;
        public const string PostgreSqlCredentialPrefix = "KeyPulse Signal/Database/PostgreSQL";
        public const long PostgreSqlAdvisoryLockKey = 0x4B50554C53454442;
    }

    public static class Paths
    {
        public const string TestDataDirectoryName = "Test";
        public const string LogsDirectoryName = "Logs";
        public const string SettingsFileName = "settings.json";
        public const string DatabaseFileName = "keypulse-data.db";
        public const string DatabaseBackupsDirectoryName = "DbBackups";
        public const int DatabaseBackupRetentionFileCountLimit = 3;
        public const string PreMigrationBackupSuffix = ".pre-migration";
        public const string HeartbeatFileName = "heartbeat.txt";
        public const string LogFilePattern = "*.log";
        public const string RollingLogFileTemplate = "keypulse-logs-.log";
        public const int LogRetentionFileCountLimit = 14;
    }

    public static class Troubleshooting
    {
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

    public static class Date
    {
        public const string DateFormat = "yyyy-MM-dd HH:mm:ss";
        public const string LogFileDateFormat = "yyyyMMdd";
        public const string LogDisplayDateFormat = "yyyy-MM-dd";
    }
}
