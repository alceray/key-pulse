using KeyPulse.Models;
using Serilog;

namespace KeyPulse.Services;

/// <summary>Publishes connection and credential references together, without overwriting a source password.</summary>
public sealed class DatabaseConnectionSettingsService(
    AppSettingsService settingsService,
    IDatabaseCredentialStore credentials
)
{
    public static (PostgreSqlConnectionSettings Connection, string? CredentialReference) ResolveRecoveryPostgreSql(
        AppUserSettings settings,
        DatabaseConnectionRole role
    )
    {
        // Recovery must stay accessible even when a pending destination is incomplete.
        if (
            role == DatabaseConnectionRole.Destination
            && settings.PendingDatabaseProvider == DatabaseProvider.PostgreSql
        )
            return (
                settings.PendingPostgreSql ?? settings.PostgreSql,
                settings.PendingPostgreSql == null
                    ? settings.PostgreSqlCredentialReference
                    : settings.PendingPostgreSqlCredentialReference
            );
        return (settings.PostgreSql, settings.PostgreSqlCredentialReference);
    }

    public static (PostgreSqlConnectionSettings Connection, string? CredentialReference) ResolvePendingPostgreSql(
        AppUserSettings settings
    )
    {
        if (settings.PendingDatabaseProvider != DatabaseProvider.PostgreSql)
            throw new InvalidOperationException("There is no pending PostgreSQL destination");
        if (
            settings.PendingPostgreSql != null
            && !string.IsNullOrWhiteSpace(settings.PendingPostgreSqlCredentialReference)
        )
            return (settings.PendingPostgreSql, settings.PendingPostgreSqlCredentialReference);
        if (
            settings.PendingPostgreSql != null
            || settings.PendingPostgreSqlCredentialReference != null
            || settings.DatabaseProvider == DatabaseProvider.PostgreSql
        )
            throw new InvalidOperationException(
                "The pending PostgreSQL destination is incomplete. Cancel the change and select it again."
            );
        // Earlier SQLite-to-PostgreSQL requests stored the destination in the active fields.
        return (settings.PostgreSql, settings.PostgreSqlCredentialReference);
    }

    public void SchedulePostgreSql(
        AppUserSettings settings,
        PostgreSqlConnectionSettings destination,
        string password,
        bool copyHistory,
        bool replaceExisting
    )
    {
        EnsureNoPendingChange(settings);
        EnsureNoPendingChange(settingsService.GetSettings());
        DatabaseConfigurationService.EnsureNotUsedByOtherBuild(destination);
        if (
            settings.DatabaseProvider == DatabaseProvider.PostgreSql
            && (!copyHistory || DatabaseConfigurationService.IsSamePostgreSqlDatabase(settings.PostgreSql, destination))
        )
            throw new InvalidOperationException(
                "Choose a different database to copy history. Edit authentication for the current database without copying."
            );
        if (replaceExisting && !copyHistory)
            throw new InvalidOperationException("Replacing history requires copying the active history");
        settings.PendingDatabaseProvider = DatabaseProvider.PostgreSql;
        settings.PendingDatabaseImport = copyHistory;
        settings.PendingDatabaseReplace = replaceExisting;
        settings.PendingDatabaseSwitchId = Guid.NewGuid().ToString("N");
        SaveCredential(settings, destination, password, destination: true);
    }

    public void ScheduleSqlite(AppUserSettings settings)
    {
        EnsureNoPendingChange(settings);
        EnsureNoPendingChange(settingsService.GetSettings());
        settings.PendingDatabaseProvider = DatabaseProvider.Sqlite;
        settings.PendingDatabaseImport = settings.DatabaseProvider == DatabaseProvider.PostgreSql;
        settings.PendingDatabaseReplace = false;
        settings.PendingDatabaseSwitchId = Guid.NewGuid().ToString("N");
        settingsService.SaveSettings(settings);
    }

    public void UpdateAuthentication(
        AppUserSettings settings,
        PostgreSqlConnectionSettings connection,
        string password,
        bool destination = false
    )
    {
        var existing = destination ? ResolvePendingPostgreSql(settings).Connection : settings.PostgreSql;
        if (!DatabaseConfigurationService.IsSamePostgreSqlDatabase(existing, connection))
            throw new InvalidOperationException("Cancel the pending change before selecting a different database");
        SaveCredential(settings, connection, password, destination);
    }

    public static void EnsureNoPendingChange(AppUserSettings settings)
    {
        if (settings.PendingDatabaseProvider.HasValue)
            throw new InvalidOperationException(
                "Restart to finish the pending change, or cancel it before selecting another database"
            );
    }

    private void SaveCredential(
        AppUserSettings settings,
        PostgreSqlConnectionSettings connection,
        string password,
        bool destination
    )
    {
        var previous = destination
            ? settings.PendingPostgreSqlCredentialReference
            : settings.PostgreSqlCredentialReference;
        var reference = Guid.NewGuid().ToString("N");
        try
        {
            credentials.WritePostgreSqlPassword(password, reference);
            if (destination)
            {
                settings.PendingPostgreSql = connection.Copy();
                settings.PendingPostgreSqlCredentialReference = reference;
            }
            else
            {
                settings.PostgreSql = connection.Copy();
                settings.PostgreSqlCredentialReference = reference;
            }
            settingsService.SaveSettings(settings);
        }
        finally
        {
            // Save can throw after publication, for example from a notification subscriber.
            // Never delete an entry based on the exception or on the caller's in-memory settings.
            CleanupCredentials(previous, reference);
        }
    }

    internal void CleanupCredentials(params string?[] references)
    {
        try
        {
            var durable = settingsService.ReadPersistedSettings();
            foreach (var reference in references.Distinct())
            {
                // The legacy entry is shared by older settings and is never ours to remove here.
                if (
                    reference == null
                    || !Guid.TryParseExact(reference, "N", out _)
                    || reference == durable.PostgreSqlCredentialReference
                    || reference == durable.PendingPostgreSqlCredentialReference
                )
                    continue;
                credentials.DeletePostgreSqlPassword(reference);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Unused database credentials could not be cleaned up");
        }
    }
}
