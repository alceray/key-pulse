using System.Data;
using System.IO;
using KeyPulse.Configuration;
using KeyPulse.Data;
using KeyPulse.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace KeyPulse.Services;

public enum DatabaseConnectionRole
{
    Source,
    Destination,
}

/// <summary>Completes database changes while monitoring is stopped during application startup.</summary>
public sealed class DatabaseSwitchService
{
    private const int BatchSize = 1000;
    internal const string ImportSwitchMetaKey = "DatabaseImportSwitchId";
    private readonly AppSettingsService _settingsService;
    private readonly IDatabaseCredentialStore _credentialStore;
    private readonly DatabaseInstanceLock _databaseLock;
    private readonly string _sqlitePath;
    public DatabaseConnectionRole FailureConnection { get; private set; } = DatabaseConnectionRole.Destination;
    private DatabaseConnectionSettingsService ConnectionSettings => new(_settingsService, _credentialStore);

    public DatabaseSwitchService(
        AppSettingsService settingsService,
        IDatabaseCredentialStore credentialStore,
        DatabaseInstanceLock databaseLock
    )
        : this(
            settingsService,
            credentialStore,
            databaseLock,
            AppDataPaths.GetPath(AppConstants.Paths.DatabaseFileName)
        ) { }

    internal DatabaseSwitchService(
        AppSettingsService settingsService,
        IDatabaseCredentialStore credentialStore,
        DatabaseInstanceLock databaseLock,
        string sqlitePath
    )
    {
        _settingsService = settingsService;
        _credentialStore = credentialStore;
        _databaseLock = databaseLock;
        _sqlitePath = Path.GetFullPath(sqlitePath);
    }

    public async Task<bool> PrepareStartupAsync(
        Func<Exception, bool> recover,
        CancellationToken cancellationToken = default
    )
    {
        var ready = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await ProcessPendingSwitchAsync(cancellationToken);
                    var current = _settingsService.GetSettings();
                    if (current.DatabaseProvider == DatabaseProvider.PostgreSql)
                    {
                        var password = AcquirePostgreSql(current);
                        await DatabaseConfigurationService.TestPostgreSqlAsync(
                            current.PostgreSql,
                            password,
                            cancellationToken
                        );
                    }
                    ready = true;
                    return true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Error(ex, "Database startup could not be completed");
                    if (!recover(ex))
                        return false;
                }
            }
        }
        finally
        {
            if (!ready)
                _databaseLock.Dispose();
        }
    }

    public async Task<bool> ProcessPendingSwitchAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await ProcessPendingSwitchCoreAsync(cancellationToken);
        }
        catch
        {
            _databaseLock.Dispose();
            throw;
        }
    }

    private async Task<bool> ProcessPendingSwitchCoreAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsService.GetSettings();
        if (!settings.PendingDatabaseProvider.HasValue)
            return false;
        if (settings.PendingDatabaseReplace && !settings.PendingDatabaseImport)
            throw new InvalidOperationException("Replacing database history requires copying the active history");

        if (string.IsNullOrWhiteSpace(settings.PendingDatabaseSwitchId))
        {
            settings.PendingDatabaseSwitchId = Guid.NewGuid().ToString("N");
            _settingsService.SaveSettings(settings);
        }
        if (await TryCompletePendingSwitchAsync(cancellationToken))
            return true;

        if (settings.PendingDatabaseProvider == DatabaseProvider.Sqlite)
        {
            if (settings.PendingDatabaseImport)
            {
                if (settings.DatabaseProvider != DatabaseProvider.PostgreSql)
                    throw new InvalidOperationException("The active database is not PostgreSQL");
                await ExportPostgreSqlHistoryAsync(settings, cancellationToken);
            }
        }
        else if (settings.PendingDatabaseProvider == DatabaseProvider.PostgreSql)
        {
            var (destination, reference) = DatabaseConnectionSettingsService.ResolvePendingPostgreSql(settings);
            var password = AcquirePostgreSql(destination, reference, _databaseLock, DatabaseConnectionRole.Destination);
            using var sourceLock = new DatabaseInstanceLock();
            ApplicationDbContext? source = null;
            try
            {
                if (settings.DatabaseProvider == DatabaseProvider.PostgreSql)
                {
                    if (!settings.PendingDatabaseImport)
                        throw new InvalidOperationException(
                            "A PostgreSQL database change requires copying its history"
                        );
                    if (DatabaseConfigurationService.IsSamePostgreSqlDatabase(settings.PostgreSql, destination))
                        throw new InvalidOperationException("The source and destination are the same database");
                    var sourcePassword = AcquirePostgreSql(
                        settings.PostgreSql,
                        settings.PostgreSqlCredentialReference,
                        sourceLock,
                        DatabaseConnectionRole.Source
                    );
                    source = ConfiguredDbContextFactory.CreatePostgreSqlContext(settings.PostgreSql, sourcePassword);
                    await ValidateSourceSchemaAsync(source, cancellationToken);
                    await HasApplicationDataAsync(source, cancellationToken);
                    AppMetaStore.ReadExisting(source);
                }
                else if (settings.DatabaseProvider == DatabaseProvider.Sqlite && settings.PendingDatabaseImport)
                {
                    source = ConfiguredDbContextFactory.CreateSqliteContext(
                        _sqlitePath,
                        SqliteOpenMode.ReadWrite,
                        pooling: false
                    );
                    await source.Database.MigrateAsync(cancellationToken);
                    DatabaseMigrations.RunAll(source);
                    AppMetaStore.EnsureTable(source);
                }
                FailureConnection = DatabaseConnectionRole.Destination;
                await DatabaseConfigurationService.TestPostgreSqlAsync(destination, password, cancellationToken);
                await using var target = ConfiguredDbContextFactory.CreatePostgreSqlContext(destination, password);
                await ValidateKnownMigrationsAsync(target, cancellationToken);
                await target.Database.MigrateAsync(cancellationToken);
                AppMetaStore.EnsureTable(target);
                if (await HasApplicationDataAsync(target, cancellationToken) && !settings.PendingDatabaseReplace)
                    throw new InvalidOperationException(
                        "The PostgreSQL database already contains KeyPulse data. Confirm replacement in Settings."
                    );
                void VerifyLocks()
                {
                    if (settings.DatabaseProvider == DatabaseProvider.PostgreSql)
                    {
                        FailureConnection = DatabaseConnectionRole.Source;
                        sourceLock.VerifyHeld();
                    }
                    FailureConnection = DatabaseConnectionRole.Destination;
                    _databaseLock.VerifyHeld();
                }
                if (source != null)
                    await CopyHistoryAsync(
                        source,
                        target,
                        settings.PendingDatabaseSwitchId!,
                        settings.PendingDatabaseReplace,
                        cancellationToken,
                        VerifyLocks
                    );
                else
                {
                    await using var transaction = await target.Database.BeginTransactionAsync(cancellationToken);
                    AppMetaStore.Write(target, ImportSwitchMetaKey, settings.PendingDatabaseSwitchId!);
                    VerifyLocks();
                    await transaction.CommitAsync(cancellationToken);
                }
                // Keep both locks through settings publication; DI inherits the destination lock.
                Activate(settings);
                return true;
            }
            finally
            {
                if (source != null)
                    await source.DisposeAsync();
            }
        }
        else
            throw new InvalidOperationException("The pending database provider is invalid");

        Activate(settings);
        return true;
    }

    public async Task<bool> TryCompletePendingSwitchAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsService.GetSettings();
        if (!await IsPendingSwitchCompleteAsync(settings, cancellationToken))
            return false;
        Activate(settings);
        return true;
    }

    private async Task<bool> IsPendingSwitchCompleteAsync(
        AppUserSettings settings,
        CancellationToken cancellationToken,
        string? destinationPassword = null
    )
    {
        if (!settings.PendingDatabaseProvider.HasValue || string.IsNullOrWhiteSpace(settings.PendingDatabaseSwitchId))
            return false;

        if (settings.PendingDatabaseProvider == DatabaseProvider.Sqlite)
        {
            if (!File.Exists(_sqlitePath))
                return false;
            await using var target = ConfiguredDbContextFactory.CreateSqliteContext(
                _sqlitePath,
                SqliteOpenMode.ReadOnly,
                pooling: false
            );
            return HasCompletionMarker(target, settings.PendingDatabaseSwitchId);
        }
        if (settings.PendingDatabaseProvider == DatabaseProvider.PostgreSql)
        {
            var (destination, reference) = DatabaseConnectionSettingsService.ResolvePendingPostgreSql(settings);
            var password = AcquirePostgreSql(
                destination,
                reference,
                _databaseLock,
                DatabaseConnectionRole.Destination,
                destinationPassword
            );
            await using var target = ConfiguredDbContextFactory.CreatePostgreSqlContext(destination, password);
            await target.Database.OpenConnectionAsync(cancellationToken);
            return HasCompletionMarker(target, settings.PendingDatabaseSwitchId);
        }
        throw new InvalidOperationException("The pending database provider is invalid");
    }

    public async Task<bool> CancelPendingSwitchAsync(
        bool useLocalWithoutCopying = false,
        CancellationToken cancellationToken = default
    )
    {
        var settings = _settingsService.GetSettings();
        var completed = false;
        try
        {
            completed = await IsPendingSwitchCompleteAsync(settings, cancellationToken);
        }
        catch (Exception ex)
            when (settings.PendingDatabaseProvider == DatabaseProvider.PostgreSql
                && ex is not OperationCanceledException
            )
        {
            // Canceling an unavailable target preserves the active source connection and history.
            Log.Warning(ex, "The destination could not be checked before canceling the database change");
        }
        if (completed)
        {
            Activate(settings);
            return true;
        }
        var abandonedCredential = settings.PendingPostgreSqlCredentialReference;
        if (useLocalWithoutCopying)
            settings.DatabaseProvider = DatabaseProvider.Sqlite;
        settings.ClearPendingDatabaseSwitch();
        try
        {
            _settingsService.SaveSettings(settings);
        }
        finally
        {
            _databaseLock.Dispose();
            ConnectionSettings.CleanupCredentials(abandonedCredential);
        }
        return false;
    }

    public async Task UpdatePendingAuthenticationAsync(
        DatabaseConnectionRole role,
        PostgreSqlConnectionSettings connection,
        string password,
        CancellationToken cancellationToken = default
    )
    {
        var settings = _settingsService.GetSettings();
        if (!settings.PendingDatabaseProvider.HasValue)
            throw new InvalidOperationException("There is no pending database change");
        var destination = role == DatabaseConnectionRole.Destination;
        var existing = destination
            ? DatabaseConnectionSettingsService.ResolvePendingPostgreSql(settings).Connection
            : settings.PostgreSql;
        if (!DatabaseConfigurationService.IsSamePostgreSqlDatabase(existing, connection))
            throw new InvalidOperationException("Cancel the pending change before selecting a different database");
        // Probe with proposed destination authentication before any settings or credential writes.
        var probe = _settingsService.GetSettings();
        if (destination)
        {
            if (probe.PendingPostgreSql == null)
                probe.PostgreSql = connection;
            else
                probe.PendingPostgreSql = connection;
        }
        var completed = await IsPendingSwitchCompleteAsync(probe, cancellationToken, destination ? password : null);
        if (!completed)
        {
            FailureConnection = role;
            if (destination)
                await DatabaseConfigurationService.TestPostgreSqlAsync(connection, password, cancellationToken);
            else
                await DatabaseConfigurationService.TestPostgreSqlReadAsync(connection, password, cancellationToken);
        }
        else if (!destination)
        {
            Activate(settings);
            return;
        }
        ConnectionSettings.UpdateAuthentication(settings, connection, password, destination);
        if (completed)
            Activate(settings);
    }

    private string AcquirePostgreSql(AppUserSettings settings) =>
        AcquirePostgreSql(
            settings.PostgreSql,
            settings.PostgreSqlCredentialReference,
            _databaseLock,
            DatabaseConnectionRole.Source
        );

    private string AcquirePostgreSql(
        PostgreSqlConnectionSettings connection,
        string? reference,
        DatabaseInstanceLock databaseLock,
        DatabaseConnectionRole role,
        string? passwordOverride = null
    )
    {
        FailureConnection = role;
        var password =
            passwordOverride
            ?? _credentialStore.ReadPostgreSqlPassword(reference)
            ?? throw new InvalidOperationException(
                $"The saved {role.ToString().ToLowerInvariant()} PostgreSQL password is unavailable"
            );
        DatabaseConfigurationService.EnsureNotUsedByOtherBuild(connection);
        databaseLock.Acquire(DatabaseConfigurationService.BuildPostgreSqlConnectionString(connection, password));
        return password;
    }

    private void Activate(AppUserSettings settings)
    {
        var previousCredential = settings.PostgreSqlCredentialReference;
        if (settings.PendingDatabaseProvider == DatabaseProvider.PostgreSql)
        {
            _databaseLock.VerifyHeld();
            var (destination, reference) = DatabaseConnectionSettingsService.ResolvePendingPostgreSql(settings);
            settings.PostgreSql = destination.Copy();
            settings.PostgreSqlCredentialReference = reference;
        }
        settings.DatabaseProvider = settings.PendingDatabaseProvider!.Value;
        settings.ClearPendingDatabaseSwitch();
        try
        {
            _settingsService.SaveSettings(settings);
        }
        finally
        {
            ConnectionSettings.CleanupCredentials(previousCredential);
        }
        if (settings.DatabaseProvider == DatabaseProvider.Sqlite)
            _databaseLock.Dispose();
        Log.Information("Database provider switched to {Provider}", settings.DatabaseProvider);
    }

    internal static bool HasCompletionMarker(ApplicationDbContext target, string switchId) =>
        AppMetaStore.TableExists(target)
        && AppMetaStore.ReadExisting(target).TryGetValue(ImportSwitchMetaKey, out var completedId)
        && string.Equals(completedId, switchId, StringComparison.Ordinal);

    private async Task ExportPostgreSqlHistoryAsync(AppUserSettings settings, CancellationToken cancellationToken)
    {
        var password = AcquirePostgreSql(settings);
        var stagingPath = SqliteHistoryFile.GetStagingPath(_sqlitePath, settings.PendingDatabaseSwitchId!);
        SqliteHistoryFile.DeleteStaging(stagingPath);
        try
        {
            await using (var source = ConfiguredDbContextFactory.CreatePostgreSqlContext(settings.PostgreSql, password))
            {
                await ValidateSourceSchemaAsync(source, cancellationToken);
                await using var staging = ConfiguredDbContextFactory.CreateSqliteContext(stagingPath, pooling: false);
                await staging.Database.MigrateAsync(cancellationToken);
                AppMetaStore.EnsureTable(staging);
                await CopyHistoryAsync(
                    source,
                    staging,
                    settings.PendingDatabaseSwitchId!,
                    false,
                    cancellationToken,
                    _databaseLock.VerifyHeld
                );
            }
            cancellationToken.ThrowIfCancellationRequested();
            await SqliteHistoryFile.PublishAsync(stagingPath, _sqlitePath, cancellationToken, _databaseLock.VerifyHeld);
            await using var installed = ConfiguredDbContextFactory.CreateSqliteContext(
                _sqlitePath,
                SqliteOpenMode.ReadOnly,
                pooling: false
            );
            if (!HasCompletionMarker(installed, settings.PendingDatabaseSwitchId!))
                throw new InvalidOperationException("The installed SQLite history could not be verified");
        }
        finally
        {
            try
            {
                SqliteHistoryFile.DeleteStaging(stagingPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Temporary database files could not be removed");
            }
        }
    }

    internal static async Task ValidateSourceSchemaAsync(
        ApplicationDbContext source,
        CancellationToken cancellationToken
    )
    {
        await ValidateKnownMigrationsAsync(source, cancellationToken);
        if ((await source.Database.GetPendingMigrationsAsync(cancellationToken)).Any())
            throw new InvalidOperationException("The PostgreSQL history needs a schema update before it can be copied");
    }

    private static async Task ValidateKnownMigrationsAsync(
        ApplicationDbContext context,
        CancellationToken cancellationToken
    )
    {
        var applied = await context.Database.GetAppliedMigrationsAsync(cancellationToken);
        if (applied.Except(context.Database.GetMigrations(), StringComparer.Ordinal).Any())
            throw new InvalidOperationException(
                "The database schema belongs to a different or newer version of KeyPulse"
            );
    }

    internal static async Task CopyHistoryAsync(
        ApplicationDbContext source,
        ApplicationDbContext target,
        string switchId,
        bool replaceExisting,
        CancellationToken cancellationToken = default,
        Action? ensureExclusiveAccess = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(switchId);
        var isolation = source.Database.IsNpgsql() ? IsolationLevel.RepeatableRead : IsolationLevel.Serializable;
        await using var sourceTransaction = await source.Database.BeginTransactionAsync(isolation, cancellationToken);
        var expected = await DatabaseHistoryFingerprint.ReadAsync(source, cancellationToken);
        var expectedMeta = new Dictionary<string, string>(AppMetaStore.ReadExisting(source), StringComparer.Ordinal);
        expectedMeta[ImportSwitchMetaKey] = switchId;
        if (target.Database.IsSqlite())
            DatabaseMigrations.AddTimestampMigrationMarkers(expectedMeta);

        await using var transaction = await target.Database.BeginTransactionAsync(cancellationToken);
        if (!replaceExisting && await HasApplicationDataAsync(target, cancellationToken))
            throw new InvalidOperationException(
                "The destination already contains history and replacement was not confirmed"
            );
        if (replaceExisting)
        {
            await target.ActivityProjections.ExecuteDeleteAsync(cancellationToken);
            await target.DailyDeviceStats.ExecuteDeleteAsync(cancellationToken);
            await target.ActivitySnapshots.ExecuteDeleteAsync(cancellationToken);
            await target.DeviceEvents.ExecuteDeleteAsync(cancellationToken);
            await target.Devices.ExecuteDeleteAsync(cancellationToken);
        }
        await target.Database.ExecuteSqlRawAsync("DELETE FROM AppMeta;", cancellationToken);
        await CopyDevicesAsync(source, target, cancellationToken);
        await CopyDeviceEventsAsync(source, target, cancellationToken);
        await CopyActivitySnapshotsAsync(source, target, cancellationToken);
        await CopyDailyStatsAsync(source, target, cancellationToken);
        await CopyActivityProjectionsAsync(source, target, cancellationToken);
        foreach (var (key, value) in expectedMeta)
            AppMetaStore.Write(target, key, value);

        var actual = await DatabaseHistoryFingerprint.ReadAsync(target, cancellationToken);
        var actualMeta = AppMetaStore.ReadExisting(target);
        if (
            actual != expected
            || actualMeta.Count != expectedMeta.Count
            || expectedMeta.Any(x => !actualMeta.TryGetValue(x.Key, out var value) || value != x.Value)
        )
            throw new InvalidOperationException("The copied database history did not match the source");
        ensureExclusiveAccess?.Invoke();
        await transaction.CommitAsync(cancellationToken);
        Log.Information(
            "Database history copied and verified: {DeviceCount} devices, {EventCount} events, {SnapshotCount} activity minutes",
            actual.Devices.Count,
            actual.Events.Count,
            actual.Snapshots.Count
        );
    }

    internal static async Task<bool> HasApplicationDataAsync(
        ApplicationDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        // Inspect every table, including when an earlier table has data, so a partial schema fails.
        var populated = await context.Devices.AnyAsync(cancellationToken);
        populated |= await context.DeviceEvents.AnyAsync(cancellationToken);
        populated |= await context.ActivitySnapshots.AnyAsync(cancellationToken);
        populated |= await context.DailyDeviceStats.AnyAsync(cancellationToken);
        populated |= await context.ActivityProjections.AnyAsync(cancellationToken);
        return populated;
    }

    private static async Task CopyDevicesAsync(
        ApplicationDbContext source,
        ApplicationDbContext target,
        CancellationToken cancellationToken
    )
    {
        var rows = await source.Devices.AsNoTracking().OrderBy(x => x.DeviceId).ToListAsync(cancellationToken);
        foreach (var row in rows)
            row.SessionStartedAt = null;
        foreach (var batch in rows.Chunk(BatchSize))
        {
            target.Devices.AddRange(
                batch.Select(x => new Device
                {
                    DeviceId = x.DeviceId,
                    DeviceName = x.DeviceName,
                    DeviceType = x.DeviceType,
                    TotalConnectionSeconds = x.TotalConnectionSeconds,
                    SessionStartedAt = null,
                    LastConnectedAt = x.LastConnectedAt,
                    LastSeenAt = x.LastSeenAt,
                    IsHiddenFromDisplay = x.IsHiddenFromDisplay,
                    TotalInputCount = x.TotalInputCount,
                    DaysConnected = x.DaysConnected,
                })
            );
            await target.SaveChangesAsync(cancellationToken);
            target.ChangeTracker.Clear();
        }
    }

    private static async Task CopyDeviceEventsAsync(
        ApplicationDbContext source,
        ApplicationDbContext target,
        CancellationToken cancellationToken
    ) =>
        await CopyInBatchesAsync(
            source.DeviceEvents.AsNoTracking().OrderBy(x => x.DeviceEventId),
            batch =>
                target.DeviceEvents.AddRange(
                    batch.Select(x => new DeviceEvent
                    {
                        DeviceId = x.DeviceId,
                        EventTime = x.EventTime,
                        EventType = x.EventType,
                    })
                ),
            target,
            cancellationToken
        );

    private static async Task CopyActivitySnapshotsAsync(
        ApplicationDbContext source,
        ApplicationDbContext target,
        CancellationToken cancellationToken
    ) =>
        await CopyInBatchesAsync(
            source.ActivitySnapshots.AsNoTracking().OrderBy(x => x.ActivitySnapshotId),
            batch =>
                target.ActivitySnapshots.AddRange(
                    batch.Select(x => new ActivitySnapshot
                    {
                        DeviceId = x.DeviceId,
                        Minute = x.Minute,
                        Keystrokes = x.Keystrokes,
                        MouseClicks = x.MouseClicks,
                        MouseMovementSeconds = x.MouseMovementSeconds,
                        ActiveSeconds = x.ActiveSeconds,
                    })
                ),
            target,
            cancellationToken
        );

    private static async Task CopyDailyStatsAsync(
        ApplicationDbContext source,
        ApplicationDbContext target,
        CancellationToken cancellationToken
    ) =>
        await CopyInBatchesAsync(
            source.DailyDeviceStats.AsNoTracking().OrderBy(x => x.DailyDeviceStatId),
            batch =>
                target.DailyDeviceStats.AddRange(
                    batch.Select(x => new DailyDeviceStat
                    {
                        Day = x.Day,
                        DeviceId = x.DeviceId,
                        SessionCount = x.SessionCount,
                        ConnectionSeconds = x.ConnectionSeconds,
                        Keystrokes = x.Keystrokes,
                        MouseClicks = x.MouseClicks,
                        MouseMovementSeconds = x.MouseMovementSeconds,
                        ActiveSeconds = x.ActiveSeconds,
                        HourlyInputCount = x.HourlyInputCount.ToArray(),
                        UpdatedAt = x.UpdatedAt,
                    })
                ),
            target,
            cancellationToken
        );

    private static async Task CopyActivityProjectionsAsync(
        ApplicationDbContext source,
        ApplicationDbContext target,
        CancellationToken cancellationToken
    ) =>
        await CopyInBatchesAsync(
            source.ActivityProjections.AsNoTracking().OrderBy(x => x.ActivityProjectionId),
            batch =>
                target.ActivityProjections.AddRange(
                    batch.Select(x => new ActivityProjection { DeviceId = x.DeviceId, Minute = x.Minute })
                ),
            target,
            cancellationToken
        );

    private static async Task CopyInBatchesAsync<T>(
        IOrderedQueryable<T> query,
        Action<IReadOnlyList<T>> addBatch,
        ApplicationDbContext target,
        CancellationToken cancellationToken
    )
        where T : class
    {
        var offset = 0;
        while (true)
        {
            var batch = await query.Skip(offset).Take(BatchSize).ToListAsync(cancellationToken);
            if (batch.Count == 0)
                return;
            addBatch(batch);
            await target.SaveChangesAsync(cancellationToken);
            target.ChangeTracker.Clear();
            offset += batch.Count;
        }
    }
}
