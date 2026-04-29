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
        public const string RollingLogFileTemplate = "keypulse-.log";
    }

    public static class Logging
    {
        public const int RetainedFileCountLimit = 14;
        public const int StartupWarningBalloonTimeoutMs = 5000;
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
}
