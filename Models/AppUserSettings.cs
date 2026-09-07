namespace KeyPulse.Models;

public class AppUserSettings
{
#if DEBUG
    public bool LaunchOnLogin { get; set; }
#else
    public bool LaunchOnLogin { get; set; } = true;
#endif
    public bool IsFirstLaunch { get; set; } = true;
    public bool AutoInstallUpdates { get; set; } = true;

    /// <summary>Use the dark application theme. Existing installations retain the light theme.</summary>
    public bool DarkMode { get; set; }

    /// <summary>When true, closing the window keeps the app running in the tray instead of exiting.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Set when the user opts out of the close-to-tray reminder via its "don't show again" checkbox.</summary>
    public bool SuppressCloseToTrayHint { get; set; }

    /// <summary>Months of per-minute activity detail to keep. Zero keeps everything forever.</summary>
    // Bounded by default. Minute rows dominate the database and nothing reads them past the one-week chart window.
    public int ActivityRetentionMonths { get; set; } = 24;

    /// <summary>The database used by this Debug or Release installation.</summary>
    public DatabaseProvider DatabaseProvider { get; set; } = DatabaseProvider.Sqlite;

    /// <summary>Non-secret PostgreSQL connection settings. The password is stored in Windows Credential Manager.</summary>
    public PostgreSqlConnectionSettings PostgreSql { get; set; } = new();

    /// <summary>A provider switch that will be completed before monitoring starts on the next launch.</summary>
    public DatabaseProvider? PendingDatabaseProvider { get; set; }

    /// <summary>Whether a pending SQLite-to-PostgreSQL switch must copy the current installation's history.</summary>
    public bool PendingDatabaseImport { get; set; }

    /// <summary>Correlates a committed import with activation if settings persistence is interrupted.</summary>
    public string? PendingDatabaseSwitchId { get; set; }
}
