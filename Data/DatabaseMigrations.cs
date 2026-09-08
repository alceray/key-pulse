using Microsoft.EntityFrameworkCore;
using Serilog;

namespace KeyPulse.Data;

/// <summary>
/// One-time data migrations run at startup after schema migrations.
/// Each migration is guarded by a marker in the AppMeta table.
/// WARNING: These migrations are not safe to rerun against already-migrated data.
///          Do not delete AppMeta marker rows unless you fully understand the consequences.
/// </summary>
internal static class DatabaseMigrations
{
    private const string MetaTableName = "AppMeta";
    private const string UtcTimestampMigrationMarkerKey = "UtcTimestampMigrationV1";
    private const string TimestampSecondPrecisionMigrationMarkerKey = "TimestampSecondPrecisionMigrationV1";

    /// <summary>
    /// Runs all pending one-time data migrations in order.
    /// </summary>
    public static void RunAll(ApplicationDbContext ctx)
    {
        MigratePersistedTimesToUtcIfNeeded(ctx);
        MigratePersistedTimesToSecondPrecisionIfNeeded(ctx);
    }

    internal static void AddTimestampMigrationMarkers(IDictionary<string, string> metadata)
    {
        metadata[UtcTimestampMigrationMarkerKey] = "done";
        metadata[TimestampSecondPrecisionMigrationMarkerKey] = "done";
    }

    public static void MarkTimestampMigrationsApplied(ApplicationDbContext ctx)
    {
        AppMetaStore.EnsureTable(ctx);
        var markers = new Dictionary<string, string>();
        AddTimestampMigrationMarkers(markers);
        foreach (var (key, value) in markers)
            AppMetaStore.Write(ctx, key, value);
    }

    private static void MigratePersistedTimesToUtcIfNeeded(ApplicationDbContext ctx)
    {
        RunOneTimeSqlMigration(
            ctx,
            UtcTimestampMigrationMarkerKey,
            "Persisted timestamp migration to UTC completed",
            "Persisted timestamp migration to UTC failed",
            (connection, transaction) =>
            {
                // Remove rows that would become duplicates after the UTC shift (keep lowest PK per group).
                ExecuteUpdate(
                    connection,
                    transaction,
                    """
                    DELETE FROM DeviceEvents
                    WHERE DeviceEventId NOT IN (
                        SELECT MIN(DeviceEventId)
                        FROM DeviceEvents
                        GROUP BY DeviceId, datetime(EventTime, 'utc'), EventType
                    );
                    """
                );
                ExecuteUpdate(
                    connection,
                    transaction,
                    "UPDATE DeviceEvents SET EventTime = datetime(EventTime, 'utc') WHERE EventTime IS NOT NULL;"
                );

                // Rebuild snapshot rows after UTC conversion to avoid in-place unique collisions.
                // If multiple local rows map to the same UTC minute, merge them deterministically.
                ExecuteUpdate(
                    connection,
                    transaction,
                    """
                    CREATE TEMP TABLE TempActivitySnapshotsUtc AS
                    SELECT
                        MIN(ActivitySnapshotId) AS ActivitySnapshotId,
                        DeviceId,
                        datetime(Minute, 'utc') AS Minute,
                        SUM(Keystrokes) AS Keystrokes,
                        SUM(MouseClicks) AS MouseClicks,
                        MAX(MouseMovementSeconds) AS MouseMovementSeconds,
                        MAX(ActiveSeconds) AS ActiveSeconds
                    FROM ActivitySnapshots
                    WHERE Minute IS NOT NULL
                    GROUP BY DeviceId, datetime(Minute, 'utc');
                    """
                );
                ExecuteUpdate(connection, transaction, "DELETE FROM ActivitySnapshots;");
                ExecuteUpdate(
                    connection,
                    transaction,
                    """
                    INSERT INTO ActivitySnapshots
                        (ActivitySnapshotId, DeviceId, Minute, Keystrokes, MouseClicks, MouseMovementSeconds, ActiveSeconds)
                    SELECT
                        ActivitySnapshotId,
                        DeviceId,
                        Minute,
                        Keystrokes,
                        MouseClicks,
                        MouseMovementSeconds,
                        ActiveSeconds
                    FROM TempActivitySnapshotsUtc;
                    """
                );
                ExecuteUpdate(connection, transaction, "DROP TABLE TempActivitySnapshotsUtc;");

                ExecuteUpdate(
                    connection,
                    transaction,
                    "UPDATE Devices SET SessionStartedAt = datetime(SessionStartedAt, 'utc') WHERE SessionStartedAt IS NOT NULL;"
                );
                ExecuteUpdate(
                    connection,
                    transaction,
                    "UPDATE Devices SET LastConnectedAt = datetime(LastConnectedAt, 'utc') WHERE LastConnectedAt IS NOT NULL;"
                );
                ExecuteUpdate(
                    connection,
                    transaction,
                    "UPDATE Devices SET LastSeenAt = datetime(LastSeenAt, 'utc') WHERE LastSeenAt IS NOT NULL;"
                );
            }
        );
    }

    private static void MigratePersistedTimesToSecondPrecisionIfNeeded(ApplicationDbContext ctx)
    {
        RunOneTimeSqlMigration(
            ctx,
            TimestampSecondPrecisionMigrationMarkerKey,
            "Persisted timestamp precision migration to seconds completed",
            "Persisted timestamp precision migration to seconds failed",
            (connection, transaction) =>
            {
                // Rebuild event rows with second precision to avoid in-place unique collisions.
                ExecuteUpdate(
                    connection,
                    transaction,
                    """
                    CREATE TEMP TABLE TempDeviceEventsSecond AS
                    SELECT
                        MIN(DeviceEventId) AS DeviceEventId,
                        DeviceId,
                        strftime('%Y-%m-%d %H:%M:%S', EventTime) AS EventTime,
                        EventType
                    FROM DeviceEvents
                    GROUP BY DeviceId, strftime('%Y-%m-%d %H:%M:%S', EventTime), EventType;
                    """
                );
                ExecuteUpdate(connection, transaction, "DELETE FROM DeviceEvents;");
                ExecuteUpdate(
                    connection,
                    transaction,
                    """
                    INSERT INTO DeviceEvents (DeviceEventId, DeviceId, EventTime, EventType)
                    SELECT DeviceEventId, DeviceId, EventTime, EventType
                    FROM TempDeviceEventsSecond;
                    """
                );
                ExecuteUpdate(connection, transaction, "DROP TABLE TempDeviceEventsSecond;");

                // Rebuild snapshot rows with second precision to avoid in-place unique collisions.
                ExecuteUpdate(
                    connection,
                    transaction,
                    """
                    CREATE TEMP TABLE TempActivitySnapshotsSecond AS
                    SELECT
                        MIN(ActivitySnapshotId) AS ActivitySnapshotId,
                        DeviceId,
                        strftime('%Y-%m-%d %H:%M:%S', Minute) AS Minute,
                        SUM(Keystrokes) AS Keystrokes,
                        SUM(MouseClicks) AS MouseClicks,
                        MAX(MouseMovementSeconds) AS MouseMovementSeconds,
                        MAX(ActiveSeconds) AS ActiveSeconds
                    FROM ActivitySnapshots
                    GROUP BY DeviceId, strftime('%Y-%m-%d %H:%M:%S', Minute);
                    """
                );
                ExecuteUpdate(connection, transaction, "DELETE FROM ActivitySnapshots;");
                ExecuteUpdate(
                    connection,
                    transaction,
                    """
                    INSERT INTO ActivitySnapshots
                        (ActivitySnapshotId, DeviceId, Minute, Keystrokes, MouseClicks, MouseMovementSeconds, ActiveSeconds)
                    SELECT
                        ActivitySnapshotId,
                        DeviceId,
                        Minute,
                        Keystrokes,
                        MouseClicks,
                        MouseMovementSeconds,
                        ActiveSeconds
                    FROM TempActivitySnapshotsSecond;
                    """
                );
                ExecuteUpdate(connection, transaction, "DROP TABLE TempActivitySnapshotsSecond;");

                ExecuteUpdate(
                    connection,
                    transaction,
                    "UPDATE Devices SET SessionStartedAt = strftime('%Y-%m-%d %H:%M:%S', SessionStartedAt) WHERE SessionStartedAt IS NOT NULL;"
                );
                ExecuteUpdate(
                    connection,
                    transaction,
                    "UPDATE Devices SET LastConnectedAt = strftime('%Y-%m-%d %H:%M:%S', LastConnectedAt) WHERE LastConnectedAt IS NOT NULL;"
                );
                ExecuteUpdate(
                    connection,
                    transaction,
                    "UPDATE Devices SET LastSeenAt = strftime('%Y-%m-%d %H:%M:%S', LastSeenAt) WHERE LastSeenAt IS NOT NULL;"
                );
            }
        );
    }

    private static void RunOneTimeSqlMigration(
        ApplicationDbContext ctx,
        string markerKey,
        string successLog,
        string failureLog,
        Action<System.Data.Common.DbConnection, System.Data.Common.DbTransaction> migrationBody
    )
    {
        using var connection = ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            connection.Open();

        EnsureAppMetaTable(connection);
        if (IsMarkerDone(connection, markerKey))
            return;

        using var transaction = connection.BeginTransaction();
        try
        {
            migrationBody(connection, transaction);
            WriteMarker(connection, transaction, markerKey);

            transaction.Commit();
            Log.Information(successLog);
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            Log.Error(ex, failureLog);
            throw;
        }
    }

    private static void EnsureAppMetaTable(System.Data.Common.DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS AppMeta (MetaKey TEXT PRIMARY KEY NOT NULL, MetaValue TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }

    private static bool IsMarkerDone(System.Data.Common.DbConnection connection, string markerKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT MetaValue FROM {MetaTableName} WHERE MetaKey = $key LIMIT 1;";

        var keyParam = command.CreateParameter();
        keyParam.ParameterName = "$key";
        keyParam.Value = markerKey;
        command.Parameters.Add(keyParam);

        var value = command.ExecuteScalar() as string;
        return string.Equals(value, "done", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteMarker(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string markerKey
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"INSERT INTO {MetaTableName} (MetaKey, MetaValue) VALUES ($key, $value) ON CONFLICT(MetaKey) DO UPDATE SET MetaValue = excluded.MetaValue;";

        var keyParam = command.CreateParameter();
        keyParam.ParameterName = "$key";
        keyParam.Value = markerKey;
        command.Parameters.Add(keyParam);

        var valueParam = command.CreateParameter();
        valueParam.ParameterName = "$value";
        valueParam.Value = "done";
        command.Parameters.Add(valueParam);
        command.ExecuteNonQuery();
    }

    private static void ExecuteUpdate(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string sql
    )
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
