using System.Data;
using System.IO;
using KeyPulse.Configuration;
using KeyPulse.Data;
using KeyPulse.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace KeyPulse.Services;

/// <summary>Completes provider changes while monitoring is stopped during application startup.</summary>
public sealed class DatabaseSwitchService
{
    private const int BatchSize = 1000;
    internal const string ImportSwitchMetaKey = "DatabaseImportSwitchId";
    private readonly AppSettingsService _settingsService;
    private readonly IDatabaseCredentialStore _credentialStore;
    private readonly DatabaseInstanceLock _databaseLock;
    private readonly string _sqlitePath;

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
            var password = AcquirePostgreSql(settings);
            await DatabaseConfigurationService.TestPostgreSqlAsync(settings.PostgreSql, password, cancellationToken);
            await using var target = ConfiguredDbContextFactory.CreatePostgreSqlContext(settings.PostgreSql, password);
            await ValidateKnownMigrationsAsync(target, cancellationToken);
            await target.Database.MigrateAsync(cancellationToken);
            AppMetaStore.EnsureTable(target);
            var populated = await HasApplicationDataAsync(target, cancellationToken);
            if (populated && !settings.PendingDatabaseReplace)
                throw new InvalidOperationException(
                    "The PostgreSQL database already contains KeyPulse data. Confirm replacement in Settings."
                );

            if (settings.PendingDatabaseImport)
            {
                if (settings.DatabaseProvider != DatabaseProvider.Sqlite)
                    throw new InvalidOperationException("The active database is not SQLite");
                await using var source = ConfiguredDbContextFactory.CreateSqliteContext(
                    _sqlitePath,
                    SqliteOpenMode.ReadWrite,
                    pooling: false
                );
                await source.Database.MigrateAsync(cancellationToken);
                DatabaseMigrations.RunAll(source);
                AppMetaStore.EnsureTable(source);
                await CopyHistoryAsync(
                    source,
                    target,
                    settings.PendingDatabaseSwitchId,
                    settings.PendingDatabaseReplace,
                    cancellationToken,
                    _databaseLock.VerifyHeld
                );
            }
            else
            {
                await using var transaction = await target.Database.BeginTransactionAsync(cancellationToken);
                AppMetaStore.Write(target, ImportSwitchMetaKey, settings.PendingDatabaseSwitchId);
                _databaseLock.VerifyHeld();
                await transaction.CommitAsync(cancellationToken);
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

    private async Task<bool> IsPendingSwitchCompleteAsync(AppUserSettings settings, CancellationToken cancellationToken)
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
            var password = AcquirePostgreSql(settings);
            await using var target = ConfiguredDbContextFactory.CreatePostgreSqlContext(settings.PostgreSql, password);
            await target.Database.OpenConnectionAsync(cancellationToken);
            return HasCompletionMarker(target, settings.PendingDatabaseSwitchId);
        }
        throw new InvalidOperationException("The pending database provider is invalid");
    }

    public async Task CancelPendingSwitchAsync(
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
            // Canceling an unavailable target leaves the active SQLite history intact.
            Log.Warning(ex, "The destination could not be checked before canceling the database change");
        }
        if (completed)
        {
            Activate(settings);
            return;
        }
        if (useLocalWithoutCopying)
            settings.DatabaseProvider = DatabaseProvider.Sqlite;
        settings.ClearPendingDatabaseSwitch();
        _settingsService.SaveSettings(settings);
        if (settings.DatabaseProvider == DatabaseProvider.Sqlite)
            _databaseLock.Dispose();
    }

    private string AcquirePostgreSql(AppUserSettings settings)
    {
        var password =
            _credentialStore.ReadPostgreSqlPassword()
            ?? throw new InvalidOperationException("The saved PostgreSQL password is unavailable");
        DatabaseConfigurationService.EnsureNotUsedByOtherBuild(settings.PostgreSql);
        _databaseLock.Acquire(
            DatabaseConfigurationService.BuildPostgreSqlConnectionString(settings.PostgreSql, password)
        );
        return password;
    }

    private void Activate(AppUserSettings settings)
    {
        if (settings.PendingDatabaseProvider == DatabaseProvider.PostgreSql)
            _databaseLock.VerifyHeld();
        settings.DatabaseProvider = settings.PendingDatabaseProvider!.Value;
        settings.ClearPendingDatabaseSwitch();
        _settingsService.SaveSettings(settings);
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
                await ValidateKnownMigrationsAsync(source, cancellationToken);
                var pendingMigrations = await source.Database.GetPendingMigrationsAsync(cancellationToken);
                if (pendingMigrations.Any())
                    throw new InvalidOperationException(
                        "The PostgreSQL history needs a schema update before it can be copied"
                    );
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
