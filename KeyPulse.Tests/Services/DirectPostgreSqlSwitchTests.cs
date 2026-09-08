using System.Diagnostics;
using System.IO;
using KeyPulse.Data;
using KeyPulse.Helpers;
using KeyPulse.Models;
using KeyPulse.Services;
using KeyPulse.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace KeyPulse.Tests.Services;

[Collection("PostgreSQL switches")]
public class DirectPostgreSqlSwitchTests(PostgreSqlTestServer server)
{
    [PostgreSqlFact]
    public async Task ReplacementRequiresConsent_AndCopiesTheHistoryPresentAtRestart()
    {
        using var scope = new DatabaseSwitchTestScope();
        var source = await SeedAsync();
        var target = await SeedAsync("OLD");
        SaveActive(scope, source);
        var service = new DatabaseConnectionSettingsService(scope.Settings, scope.Credentials);
        service.SchedulePostgreSql(scope.Settings.GetSettings(), target, "", true, false);
        await Should.ThrowAsync<InvalidOperationException>(() => scope.Switch.ProcessPendingSwitchAsync());
        using (var unchanged = server.Open(target))
            unchanged.Devices.Single().DeviceId.ShouldBe("OLD");
        await scope.Switch.CancelPendingSwitchAsync();
        var pending = Schedule(scope, target);
        using (var later = server.Open(target))
        {
            later.Devices.Add(
                new Device
                {
                    DeviceId = "LATER",
                    DeviceName = "Added after confirmation",
                    DeviceType = DeviceTypes.Other,
                }
            );
            await later.SaveChangesAsync();
        }
        DatabaseHistoryFingerprint expected;
        using (var laterSource = server.Open(source))
        {
            await laterSource.Devices.ExecuteUpdateAsync(update =>
                update.SetProperty(x => x.DeviceName, "Changed after confirmation")
            );
            expected = await DatabaseHistoryFingerprint.ReadAsync(laterSource);
        }
        await scope.Switch.ProcessPendingSwitchAsync();
        using var copied = server.Open(target);
        (await DatabaseHistoryFingerprint.ReadAsync(copied)).ShouldBe(expected);
        DatabaseSwitchService.HasCompletionMarker(copied, pending.PendingDatabaseSwitchId!).ShouldBeTrue();
    }

    [PostgreSqlFact]
    public async Task ActivationNotificationAndCredentialCleanupFailures_LeaveDestinationUsable()
    {
        using var scope = new DatabaseSwitchTestScope();
        var source = await SeedAsync();
        var target = await server.CreateDatabaseAsync();
        var active = SaveActive(scope, source);
        new DatabaseConnectionSettingsService(scope.Settings, scope.Credentials).UpdateAuthentication(
            active,
            source,
            ""
        );
        var sourceReference = scope.Settings.ReadPersistedSettings().PostgreSqlCredentialReference;
        var pending = Schedule(scope, target);
        void FailedNotification(AppUserSettings settings)
        {
            if (!settings.PendingDatabaseProvider.HasValue)
                throw new InvalidOperationException("Injected notification failure after activation");
        }
        scope.Settings.SettingsChanged += FailedNotification;
        scope.Credentials.FailDeletion = true;
        await Should.ThrowAsync<InvalidOperationException>(() => scope.Switch.ProcessPendingSwitchAsync());
        var durable = scope.Settings.ReadPersistedSettings();
        durable.PostgreSql.Database.ShouldBe(target.Database);
        durable.PostgreSqlCredentialReference.ShouldBe(pending.PendingPostgreSqlCredentialReference);
        durable.PendingDatabaseProvider.ShouldBeNull();
        scope.Credentials.ReadPostgreSqlPassword(sourceReference).ShouldBe("");
        scope.Credentials.ReadPostgreSqlPassword(durable.PostgreSqlCredentialReference).ShouldBe("");
        scope.Settings.SettingsChanged -= FailedNotification;
        (await scope.Switch.PrepareStartupAsync(_ => false)).ShouldBeTrue();
        using var copied = new ConfiguredDbContextFactory(scope.Settings, scope.Credentials).CreateDbContext();
        copied.Devices.Count().ShouldBe(1);
        DatabaseSwitchService.HasCompletionMarker(copied, pending.PendingDatabaseSwitchId!).ShouldBeTrue();
    }

    [PostgreSqlFact]
    public async Task RoundTrip_PreservesAllHistoryAndMetadata_ProjectsNewInputOnce_AndDoesNotTouchSqlite()
    {
        using var scope = new DatabaseSwitchTestScope();
        var first = await SeedAsync();
        var second = await SeedAsync("OLD");
        File.WriteAllText(scope.SqlitePath, "untouched local backup");
        var active = SaveActive(scope, first);
        string? lastId = null;
        foreach (var destination in new[] { second, first })
        {
            DatabaseHistoryFingerprint expected;
            using (var source = server.Open(active.PostgreSql))
                expected = await DatabaseHistoryFingerprint.ReadAsync(source);
            var pending = Schedule(scope, destination);
            pending.PendingDatabaseSwitchId.ShouldNotBe(lastId);
            lastId = pending.PendingDatabaseSwitchId;
            await scope.Switch.ProcessPendingSwitchAsync();
            active = scope.Settings.ReadPersistedSettings();
            active.PostgreSql.Database.ShouldBe(destination.Database);
            active.PendingDatabaseProvider.ShouldBeNull();
            using (var target = server.Open(destination))
            {
                (await DatabaseHistoryFingerprint.ReadAsync(target)).ShouldBe(expected);
                DatabaseSwitchService.HasCompletionMarker(target, lastId!).ShouldBeTrue();
                AppMetaStore.ReadExisting(target)["DailyStatsFullBackfillAt"].ShouldBe("2025-11-02T05:30:00.0000000Z");
                target.ActivitySnapshots.Add(
                    new ActivitySnapshot
                    {
                        DeviceId = "USB\\VID_1234&PID_5678",
                        Minute = new DateTime(2026, 1, 1, 12, destination == first ? 1 : 0, 0, DateTimeKind.Utc),
                        Keystrokes = 9,
                        ActiveSeconds = 1,
                    }
                );
                await target.SaveChangesAsync();
            }
            using var timer = new AppTimerService();
            using var daily = new DailyStatsService(
                new ConfiguredDbContextFactory(scope.Settings, scope.Credentials),
                timer
            );
            daily.ProjectClosedActivityMinutes();
            daily.ProjectClosedActivityMinutes();
            using var projected = server.Open(destination);
            projected
                .DailyDeviceStats.Single(x =>
                    x.Day == TimeFormatter.ToLocalDay(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc))
                )
                .Keystrokes.ShouldBe(destination == first ? 18 : 9);
            projected.DailyDeviceStats.Single(x => x.Day == new DateOnly(2020, 1, 1)).Keystrokes.ShouldBe(3000);
            (await scope.Switch.ProcessPendingSwitchAsync()).ShouldBeFalse();
            File.ReadAllText(scope.SqlitePath).ShouldBe("untouched local backup");
            scope.DatabaseLock.Dispose();
        }
    }

    [PostgreSqlFact]
    public async Task DifferentServersAndPasswords_KeepSourceAuthenticationUntilActivation()
    {
        var otherServer = new PostgreSqlTestServer();
        await otherServer.InitializeAsync();
        try
        {
            using var scope = new DatabaseSwitchTestScope();
            var source = await server.CreatePasswordDatabaseAsync("source-password");
            var destination = await otherServer.CreatePasswordDatabaseAsync("destination-password");
            await server.CreateSchemaAsync(source, "source-password");
            using (var context = server.Open(source, "source-password"))
                DatabaseSwitchTestScope.Seed(context);
            await Should.ThrowAsync<PostgresException>(
                () => DatabaseConfigurationService.TestPostgreSqlAsync(destination, "source-password")
            );
            SaveActive(scope, source, "source-password");
            Schedule(scope, destination, "destination-password");
            scope.Credentials.Password.ShouldBe("source-password");
            await scope.Switch.ProcessPendingSwitchAsync();
            using var active = new ConfiguredDbContextFactory(scope.Settings, scope.Credentials).CreateDbContext();
            active.Devices.Count().ShouldBe(1);
            using var original = server.Open(source, "source-password");
            original.Devices.Count().ShouldBe(1);
            var durable = scope.Settings.ReadPersistedSettings();
            durable.PostgreSql.Port.ShouldBe(destination.Port);
            scope
                .Credentials.ReadPostgreSqlPassword(durable.PostgreSqlCredentialReference)
                .ShouldBe("destination-password");
        }
        finally
        {
            await otherServer.DisposeAsync();
        }
    }

    [PostgreSqlFact]
    public async Task CommittedTransfer_ResumesOrCancelsIntoActivationWithoutSource_IncludingEmptyHistory()
    {
        foreach (var empty in new[] { false, true })
        {
            using var scope = new DatabaseSwitchTestScope();
            var source = await server.CreateDatabaseAsync();
            await server.CreateSchemaAsync(source);
            if (!empty)
                using (var context = server.Open(source))
                    DatabaseSwitchTestScope.Seed(context);
            var destination = await SeedAsync("REPLACE_ME");
            SaveActive(scope, source);
            var pending = Schedule(scope, destination);
            using (var locked = new FileStream(scope.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await Should.ThrowAsync<IOException>(() => scope.Switch.ProcessPendingSwitchAsync());
            scope.Settings.ReadPersistedSettings().PostgreSql.Database.ShouldBe(source.Database);
            scope.Credentials.ReadPostgreSqlPassword(pending.PendingPostgreSqlCredentialReference).ShouldBe("");
            await PostgreSqlTestServer.ExecuteAsync(destination, $"DROP DATABASE \"{source.Database}\" WITH (FORCE);");
            scope.Credentials.Password = null;
            if (empty)
                (await scope.Switch.CancelPendingSwitchAsync()).ShouldBeTrue();
            else
                await scope.Switch.ProcessPendingSwitchAsync();
            scope.Settings.ReadPersistedSettings().PostgreSql.Database.ShouldBe(destination.Database);
            using var target = server.Open(destination);
            target.Devices.Count().ShouldBe(empty ? 0 : 1);
            DatabaseSwitchService.HasCompletionMarker(target, pending.PendingDatabaseSwitchId!).ShouldBeTrue();
        }
    }

    [PostgreSqlFact]
    public async Task CancelUnavailableDestination_PreservesSourceAndCleansOnlyPendingCredential()
    {
        using var scope = new DatabaseSwitchTestScope();
        var source = await SeedAsync();
        var missing = source.Copy();
        missing.Database = "missing_" + Guid.NewGuid().ToString("N");
        SaveActive(scope, source, "source-password");
        var pending = Schedule(scope, missing, "destination-password");
        (await scope.Switch.CancelPendingSwitchAsync()).ShouldBeFalse();
        var durable = scope.Settings.ReadPersistedSettings();
        durable.PostgreSql.Database.ShouldBe(source.Database);
        durable.DatabaseProvider.ShouldBe(DatabaseProvider.PostgreSql);
        durable.PendingPostgreSql.ShouldBeNull();
        scope.Credentials.Password.ShouldBe("source-password");
        scope.Credentials.ReadPostgreSqlPassword(pending.PendingPostgreSqlCredentialReference).ShouldBeNull();
    }

    [PostgreSqlFact]
    public async Task SourceAndDestinationLockConflicts_FailBeforeTargetMigrations_AndReleaseAcquiredLocks()
    {
        foreach (var blockSource in new[] { false, true })
        {
            using var scope = new DatabaseSwitchTestScope();
            var source = await SeedAsync();
            var target = await server.CreateDatabaseAsync();
            SaveActive(scope, source);
            Schedule(scope, target);
            using (var competing = new DatabaseInstanceLock())
            {
                competing.Acquire(
                    DatabaseConfigurationService.BuildPostgreSqlConnectionString(blockSource ? source : target, "")
                );
                await Should.ThrowAsync<InvalidOperationException>(() => scope.Switch.ProcessPendingSwitchAsync());
                scope.Switch.FailureConnection.ShouldBe(
                    blockSource ? DatabaseConnectionRole.Source : DatabaseConnectionRole.Destination
                );
            }
            using var targetContext = server.Open(target);
            (await targetContext.Database.GetAppliedMigrationsAsync()).ShouldBeEmpty();
            using var sourceProbe = new DatabaseInstanceLock();
            using var targetProbe = new DatabaseInstanceLock();
            sourceProbe.Acquire(DatabaseConfigurationService.BuildPostgreSqlConnectionString(source, ""));
            targetProbe.Acquire(DatabaseConfigurationService.BuildPostgreSqlConnectionString(target, ""));
        }
    }

    [PostgreSqlFact]
    public async Task SameDatabaseThroughHostAlias_CannotAcquireBothLocksOrReplaceHistory()
    {
        using var scope = new DatabaseSwitchTestScope();
        var source = await SeedAsync();
        var alias = source.Copy();
        alias.Host = "localhost";
        SaveActive(scope, source);
        Schedule(scope, alias);
        using var context = server.Open(source);
        var expected = await DatabaseHistoryFingerprint.ReadAsync(context);
        await Should.ThrowAsync<InvalidOperationException>(() => scope.Switch.ProcessPendingSwitchAsync());
        (await DatabaseHistoryFingerprint.ReadAsync(context)).ShouldBe(expected);
        AppMetaStore.ReadExisting(context)[DatabaseSwitchService.ImportSwitchMetaKey].ShouldBe("previous-switch");
    }

    [PostgreSqlFact]
    public async Task OppositeTransfers_WithBothDatabasesInUse_FailPromptly()
    {
        using var firstScope = new DatabaseSwitchTestScope();
        using var secondScope = new DatabaseSwitchTestScope();
        var first = await SeedAsync();
        var second = await SeedAsync("SECOND");
        SaveActive(firstScope, first);
        SaveActive(secondScope, second);
        Schedule(firstScope, second);
        Schedule(secondScope, first);
        using var firstWriter = new DatabaseInstanceLock();
        using var secondWriter = new DatabaseInstanceLock();
        firstWriter.Acquire(DatabaseConfigurationService.BuildPostgreSqlConnectionString(first, ""));
        secondWriter.Acquire(DatabaseConfigurationService.BuildPostgreSqlConnectionString(second, ""));
        var watch = Stopwatch.StartNew();
        await Task.WhenAll(
                Task.Run(
                    () =>
                        Should.ThrowAsync<InvalidOperationException>(
                            () => firstScope.Switch.ProcessPendingSwitchAsync()
                        )
                ),
                Task.Run(
                    () =>
                        Should.ThrowAsync<InvalidOperationException>(
                            () => secondScope.Switch.ProcessPendingSwitchAsync()
                        )
                )
            )
            .WaitAsync(TimeSpan.FromSeconds(10));
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        using var unchanged = server.Open(second);
        unchanged.Devices.Single().DeviceId.ShouldBe("SECOND");
    }

    [PostgreSqlFact]
    public async Task ReadOnlySourceRole_DoesNotNeedCreatePermissionOrReceiveSchemaWrites()
    {
        using var scope = new DatabaseSwitchTestScope();
        var source = await SeedAsync();
        var role = "reader_" + Guid.NewGuid().ToString("N");
        await PostgreSqlTestServer.ExecuteAsync(
            source,
            $"CREATE ROLE \"{role}\" LOGIN; GRANT USAGE ON SCHEMA public TO \"{role}\"; GRANT SELECT ON ALL TABLES IN SCHEMA public TO \"{role}\";"
        );
        source.Username = role;
        await DatabaseConfigurationService.TestPostgreSqlReadAsync(source, "");
        await Should.ThrowAsync<InvalidOperationException>(
            () => DatabaseConfigurationService.TestPostgreSqlAsync(source, "")
        );
        SaveActive(scope, source);
        var target = await server.CreateDatabaseAsync();
        Schedule(scope, target);
        await scope.Switch.ProcessPendingSwitchAsync();
        using var original = server.Open(source);
        AppMetaStore.ReadExisting(original)[DatabaseSwitchService.ImportSwitchMetaKey].ShouldBe("previous-switch");
        using var copied = server.Open(target);
        (await DatabaseHistoryFingerprint.ReadAsync(copied)).ShouldBe(
            await DatabaseHistoryFingerprint.ReadAsync(original)
        );
    }

    [PostgreSqlFact]
    public async Task UnsupportedOrPartialSource_LeavesDestinationDataUnchanged()
    {
        foreach (var partial in new[] { false, true })
        {
            using var scope = new DatabaseSwitchTestScope();
            var source = await SeedAsync();
            var target = await SeedAsync("OLD");
            await PostgreSqlTestServer.ExecuteAsync(
                source,
                partial
                    ? "DROP TABLE \"ActivityProjections\";"
                    : "INSERT INTO \"__EFMigrationsHistory\" VALUES ('99999999999999_Unknown', '9.0.0');"
            );
            SaveActive(scope, source);
            Schedule(scope, target);
            using var unchanged = server.Open(target);
            var expected = await DatabaseHistoryFingerprint.ReadAsync(unchanged);
            await Should.ThrowAsync<Exception>(() => scope.Switch.ProcessPendingSwitchAsync());
            (await DatabaseHistoryFingerprint.ReadAsync(unchanged)).ShouldBe(expected);
        }
    }

    [PostgreSqlFact]
    public async Task CopyAndVerificationFailures_RollBackDestinationAndPreservePendingState()
    {
        foreach (var corrupt in new[] { false, true })
        {
            using var scope = new DatabaseSwitchTestScope();
            var source = await SeedAsync();
            var target = await SeedAsync("OLD");
            var body = corrupt
                ? "NEW.\"DeviceName\" := 'corrupted'; RETURN NEW;"
                : "RAISE EXCEPTION 'injected copy failure';";
            await PostgreSqlTestServer.ExecuteAsync(
                target,
                $"CREATE FUNCTION fail_copy() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN {body} END $$; CREATE TRIGGER fail_copy BEFORE INSERT ON \"Devices\" FOR EACH ROW EXECUTE FUNCTION fail_copy();"
            );
            SaveActive(scope, source);
            var pending = Schedule(scope, target);
            using var unchanged = server.Open(target);
            var expected = await DatabaseHistoryFingerprint.ReadAsync(unchanged);
            await Should.ThrowAsync<Exception>(() => scope.Switch.ProcessPendingSwitchAsync());
            (await DatabaseHistoryFingerprint.ReadAsync(unchanged)).ShouldBe(expected);
            scope.Settings.ReadPersistedSettings().PendingDatabaseSwitchId.ShouldBe(pending.PendingDatabaseSwitchId);
            scope.Settings.ReadPersistedSettings().PostgreSql.Database.ShouldBe(source.Database);
        }
    }

    [PostgreSqlFact]
    public async Task LosingEitherAdvisoryConnectionDuringCopy_PreventsCommit()
    {
        foreach (var killSource in new[] { false, true })
        {
            using var scope = new DatabaseSwitchTestScope();
            var source = await SeedAsync();
            var target = await SeedAsync("OLD");
            var database = killSource ? source.Database : target.Database;
            await PostgreSqlTestServer.ExecuteAsync(
                target,
                $"CREATE FUNCTION break_lock() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN PERFORM pg_terminate_backend(pid) FROM pg_locks WHERE locktype = 'advisory' AND database = (SELECT oid FROM pg_database WHERE datname = '{database}'); RETURN NEW; END $$; CREATE TRIGGER break_lock BEFORE INSERT ON \"Devices\" FOR EACH ROW EXECUTE FUNCTION break_lock();"
            );
            SaveActive(scope, source);
            Schedule(scope, target);
            using var unchanged = server.Open(target);
            var expected = await DatabaseHistoryFingerprint.ReadAsync(unchanged);
            await Should.ThrowAsync<Exception>(() => scope.Switch.ProcessPendingSwitchAsync());
            (await DatabaseHistoryFingerprint.ReadAsync(unchanged)).ShouldBe(expected);
            scope.Settings.ReadPersistedSettings().PostgreSql.Database.ShouldBe(source.Database);
        }
    }

    [PostgreSqlFact]
    public async Task AuthenticationRecovery_PreservesEndpointsAndOperationId_AndChecksCompletionFirst()
    {
        using var scope = new DatabaseSwitchTestScope();
        var source = await SeedAsync();
        var target = await server.CreatePasswordDatabaseAsync("target-password");
        SaveActive(scope, source);
        var pending = Schedule(scope, target, "wrong-password");
        await Should.ThrowAsync<PostgresException>(() => scope.Switch.ProcessPendingSwitchAsync());
        await scope.Switch.UpdatePendingAuthenticationAsync(
            DatabaseConnectionRole.Destination,
            target,
            "target-password"
        );
        scope.Settings.ReadPersistedSettings().PendingDatabaseSwitchId.ShouldBe(pending.PendingDatabaseSwitchId);
        var changedSource = source.Copy();
        changedSource.SslMode = PostgreSqlSslMode.Prefer;
        await scope.Switch.UpdatePendingAuthenticationAsync(DatabaseConnectionRole.Source, changedSource, "");
        var updated = scope.Settings.ReadPersistedSettings();
        updated.PendingDatabaseSwitchId.ShouldBe(pending.PendingDatabaseSwitchId);
        updated.PostgreSql.Database.ShouldBe(source.Database);
        using (var locked = new FileStream(scope.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Should.ThrowAsync<IOException>(() => scope.Switch.ProcessPendingSwitchAsync());
        scope.Credentials.DeletePostgreSqlPassword(updated.PostgreSqlCredentialReference);
        await scope.Switch.UpdatePendingAuthenticationAsync(DatabaseConnectionRole.Source, source, "not-used");
        scope.Settings.ReadPersistedSettings().PostgreSql.Database.ShouldBe(target.Database);
        scope.Settings.ReadPersistedSettings().PendingDatabaseProvider.ShouldBeNull();
    }

    private async Task<PostgreSqlConnectionSettings> SeedAsync(string deviceId = "USB\\VID_1234&PID_5678")
    {
        var settings = await server.CreateDatabaseAsync();
        await server.CreateSchemaAsync(settings);
        using var context = server.Open(settings);
        DatabaseSwitchTestScope.Seed(context, deviceId);
        return settings;
    }

    private static AppUserSettings SaveActive(
        DatabaseSwitchTestScope scope,
        PostgreSqlConnectionSettings source,
        string password = ""
    )
    {
        var settings = new AppUserSettings
        {
            IsFirstLaunch = false,
            DatabaseProvider = DatabaseProvider.PostgreSql,
            PostgreSql = source.Copy(),
        };
        scope.Credentials.Password = password;
        scope.Settings.SaveSettings(settings);
        return settings;
    }

    private static AppUserSettings Schedule(
        DatabaseSwitchTestScope scope,
        PostgreSqlConnectionSettings destination,
        string password = ""
    )
    {
        new DatabaseConnectionSettingsService(scope.Settings, scope.Credentials).SchedulePostgreSql(
            scope.Settings.GetSettings(),
            destination,
            password,
            true,
            true
        );
        return scope.Settings.ReadPersistedSettings();
    }
}
