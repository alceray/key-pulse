using System.IO;
using KeyPulse.Data;
using KeyPulse.Helpers;
using KeyPulse.Models;
using KeyPulse.Services;
using KeyPulse.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace KeyPulse.Tests.Services;

[Collection("PostgreSQL switches")]
public class DatabaseSwitchPostgreSqlTests(PostgreSqlTestServer server)
{
    [PostgreSqlFact]
    public async Task RepeatedRoundTrip_PreservesHistory_AndAcceptsNewInputAfterEachMove()
    {
        using var scope = new DatabaseSwitchTestScope();
        var postgres = await server.CreateDatabaseAsync();
        scope.Credentials.Password = "";
        using (var local = scope.CreateDatabase())
            DatabaseSwitchTestScope.Seed(local);
        var provider = DatabaseProvider.Sqlite;

        for (var move = 0; move < 4; move++)
        {
            DatabaseHistoryFingerprint expected;
            using (var source = provider == DatabaseProvider.Sqlite ? scope.OpenDatabase() : server.Open(postgres))
                expected = await DatabaseHistoryFingerprint.ReadAsync(source);
            var destination =
                provider == DatabaseProvider.Sqlite ? DatabaseProvider.PostgreSql : DatabaseProvider.Sqlite;
            var pending = scope.Schedule(
                provider,
                destination,
                postgres,
                replace: destination == DatabaseProvider.PostgreSql
            );
            await scope.Switch.ProcessPendingSwitchAsync();
            using (var target = destination == DatabaseProvider.Sqlite ? scope.OpenDatabase() : server.Open(postgres))
            {
                (await DatabaseHistoryFingerprint.ReadAsync(target)).ShouldBe(expected);
                DatabaseSwitchService.HasCompletionMarker(target, pending.PendingDatabaseSwitchId!).ShouldBeTrue();
                if (destination == DatabaseProvider.Sqlite)
                {
                    DatabaseMigrations.RunAll(target);
                    (await DatabaseHistoryFingerprint.ReadAsync(target)).ShouldBe(expected);
                }
                target.Devices.Single().SessionStartedAt.ShouldBeNull();
                target.ActivitySnapshots.Add(
                    new ActivitySnapshot
                    {
                        DeviceId = "USB\\VID_1234&PID_5678",
                        Minute = new DateTime(2026, 1, 1, 12, move, 0, DateTimeKind.Utc),
                        Keystrokes = move + 1,
                        ActiveSeconds = 1,
                    }
                );
                await target.SaveChangesAsync();
            }
            var factory = new TransferContextFactory(
                () => destination == DatabaseProvider.Sqlite ? scope.OpenDatabase() : server.Open(postgres)
            );
            using (var timer = new AppTimerService())
            using (var daily = new DailyStatsService(factory, timer))
            {
                daily.ProjectClosedActivityMinutes();
                daily.ProjectClosedActivityMinutes();
                using var projected = factory.CreateDbContext();
                var inputDay = TimeFormatter.ToLocalDay(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
                projected
                    .DailyDeviceStats.Single(x => x.Day == inputDay)
                    .Keystrokes.ShouldBe(Enumerable.Range(1, move + 1).Sum());
                projected.DailyDeviceStats.Single(x => x.Day == new DateOnly(2020, 1, 1)).Keystrokes.ShouldBe(3000);
                projected.ActivityProjections.Count().ShouldBe(3 + move);
            }
            (await scope.Switch.ProcessPendingSwitchAsync()).ShouldBeFalse();
            scope.Settings.GetSettings().DatabaseProvider.ShouldBe(destination);
            provider = destination;
        }
    }

    [PostgreSqlFact]
    public async Task PostgreSqlCommitBeforeSettingsFailure_ResumesEvenWithoutSqliteSource()
    {
        using var scope = new DatabaseSwitchTestScope();
        var postgres = await server.CreateDatabaseAsync();
        scope.Credentials.Password = "";
        using (var local = scope.CreateDatabase())
            DatabaseSwitchTestScope.Seed(local);
        var pending = scope.Schedule(DatabaseProvider.Sqlite, DatabaseProvider.PostgreSql, postgres, replace: true);
        using (var locked = new FileStream(scope.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Should.ThrowAsync<IOException>(() => scope.Switch.ProcessPendingSwitchAsync());
        scope.Settings.GetSettings().PendingDatabaseSwitchId.ShouldBe(pending.PendingDatabaseSwitchId);
        DatabaseHistoryFingerprint expected;
        using (var target = server.Open(postgres))
        {
            DatabaseSwitchService.HasCompletionMarker(target, pending.PendingDatabaseSwitchId!).ShouldBeTrue();
            expected = await DatabaseHistoryFingerprint.ReadAsync(target);
        }
        scope.DatabaseLock.Dispose();
        File.Delete(scope.SqlitePath);
        await scope.Switch.ProcessPendingSwitchAsync();
        using var resumed = server.Open(postgres);
        (await DatabaseHistoryFingerprint.ReadAsync(resumed)).ShouldBe(expected);
        scope.Settings.GetSettings().DatabaseProvider.ShouldBe(DatabaseProvider.PostgreSql);
    }

    [PostgreSqlFact]
    public async Task SqlitePublicationBeforeSettingsFailure_ResumesWithoutPostgreSql()
    {
        using var scope = new DatabaseSwitchTestScope();
        var postgres = await SeedPostgreSqlAsync();
        scope.Credentials.Password = "";
        using (var old = scope.CreateDatabase())
            DatabaseSwitchTestScope.Seed(old, "OLD");
        var pending = scope.Schedule(DatabaseProvider.PostgreSql, DatabaseProvider.Sqlite, postgres);
        using (var locked = new FileStream(scope.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Should.ThrowAsync<IOException>(() => scope.Switch.ProcessPendingSwitchAsync());
        scope.DatabaseLock.Dispose();
        scope.Credentials.Password = null;
        using (var installed = scope.OpenDatabase())
            DatabaseSwitchService.HasCompletionMarker(installed, pending.PendingDatabaseSwitchId!).ShouldBeTrue();
        await scope.Switch.ProcessPendingSwitchAsync();
        scope.Settings.GetSettings().DatabaseProvider.ShouldBe(DatabaseProvider.Sqlite);
    }

    [PostgreSqlFact]
    public async Task EmptyPostgreSqlImport_ResumesByMarkerBeforeInspectingRows()
    {
        using var scope = new DatabaseSwitchTestScope();
        var postgres = await server.CreateDatabaseAsync();
        scope.Credentials.Password = "";
        using (scope.CreateDatabase()) { }
        var pending = scope.Schedule(DatabaseProvider.Sqlite, DatabaseProvider.PostgreSql, postgres, replace: true);
        using (var locked = new FileStream(scope.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Should.ThrowAsync<IOException>(() => scope.Switch.ProcessPendingSwitchAsync());
        File.Delete(scope.SqlitePath);
        await scope.Switch.ProcessPendingSwitchAsync();
        using var target = server.Open(postgres);
        DatabaseSwitchService.HasCompletionMarker(target, pending.PendingDatabaseSwitchId!).ShouldBeTrue();
        (await DatabaseSwitchService.HasApplicationDataAsync(target)).ShouldBeFalse();
    }

    [PostgreSqlFact]
    public async Task LockConflict_StopsBeforeTargetSchemaCreation()
    {
        using var scope = new DatabaseSwitchTestScope();
        var postgres = await server.CreateDatabaseAsync();
        scope.Credentials.Password = "";
        using (scope.CreateDatabase()) { }
        scope.Schedule(DatabaseProvider.Sqlite, DatabaseProvider.PostgreSql, postgres, replace: true);
        using var otherWriter = new DatabaseInstanceLock();
        otherWriter.Acquire(DatabaseConfigurationService.BuildPostgreSqlConnectionString(postgres, ""));

        await Should.ThrowAsync<InvalidOperationException>(() => scope.Switch.ProcessPendingSwitchAsync());

        using var target = server.Open(postgres);
        (await target.Database.GetAppliedMigrationsAsync()).ShouldBeEmpty();
        AppMetaStore.TableExists(target).ShouldBeFalse();
    }

    [PostgreSqlFact]
    public async Task Export_ReadOnlyRoleAndNoSqliteMarkers_PreservesInstantsWithoutRunningNormalization()
    {
        using var scope = new DatabaseSwitchTestScope();
        var postgres = await SeedPostgreSqlAsync();
        var role = "reader_" + Guid.NewGuid().ToString("N");
        await PostgreSqlTestServer.ExecuteAsync(
            postgres,
            $"""
            CREATE ROLE "{role}" LOGIN;
            GRANT USAGE ON SCHEMA public TO "{role}";
            GRANT SELECT ON ALL TABLES IN SCHEMA public TO "{role}";
            """
        );
        DatabaseHistoryFingerprint expected;
        using (var source = server.Open(postgres))
        {
            AppMetaStore.ReadExisting(source).ContainsKey("UtcTimestampMigrationV1").ShouldBeFalse();
            expected = await DatabaseHistoryFingerprint.ReadAsync(source);
        }
        postgres.Username = role;
        await Should.ThrowAsync<InvalidOperationException>(
            () => DatabaseConfigurationService.TestPostgreSqlAsync(postgres, "")
        );
        scope.Credentials.Password = "";
        scope.Schedule(DatabaseProvider.PostgreSql, DatabaseProvider.Sqlite, postgres);

        await scope.Switch.ProcessPendingSwitchAsync();

        using var target = scope.OpenDatabase();
        AppMetaStore.ReadExisting(target)["UtcTimestampMigrationV1"].ShouldBe("done");
        AppMetaStore.ReadExisting(target)["TimestampSecondPrecisionMigrationV1"].ShouldBe("done");
        target.Database.ExecuteSqlRaw(
            """
            CREATE TRIGGER forbid_timestamp_update BEFORE UPDATE ON DeviceEvents
            BEGIN SELECT RAISE(ABORT, 'timestamp normalization must not run'); END;
            """
        );
        DatabaseMigrations.RunAll(target);
        (await DatabaseHistoryFingerprint.ReadAsync(target)).ShouldBe(expected);
        using var unchangedSource = server.Open(postgres);
        AppMetaStore.ReadExisting(unchangedSource).ContainsKey("UtcTimestampMigrationV1").ShouldBeFalse();
    }

    [PostgreSqlFact]
    public async Task PopulatedTarget_RequiresConsent_AndReplacementAcceptsDataAddedAfterConfirmation()
    {
        using var scope = new DatabaseSwitchTestScope();
        var postgres = await SeedPostgreSqlAsync();
        scope.Credentials.Password = "";
        using (var local = scope.CreateDatabase())
            DatabaseSwitchTestScope.Seed(local, "NEW");
        scope.Schedule(DatabaseProvider.Sqlite, DatabaseProvider.PostgreSql, postgres);
        await Should.ThrowAsync<InvalidOperationException>(() => scope.Switch.ProcessPendingSwitchAsync());
        scope.Schedule(DatabaseProvider.Sqlite, DatabaseProvider.PostgreSql, postgres, replace: true);
        using (var target = server.Open(postgres))
        {
            target.Devices.Add(
                new Device
                {
                    DeviceId = "ADDED_LATER",
                    DeviceName = "Later",
                    DeviceType = DeviceTypes.Mouse,
                }
            );
            await target.SaveChangesAsync();
        }
        await scope.Switch.ProcessPendingSwitchAsync();
        using var replaced = server.Open(postgres);
        replaced.Devices.Single().DeviceId.ShouldBe("NEW");
    }

    [PostgreSqlFact]
    public async Task MissingSqliteSource_DoesNotErasePopulatedTarget()
    {
        using var scope = new DatabaseSwitchTestScope();
        var postgres = await SeedPostgreSqlAsync();
        scope.Credentials.Password = "";
        scope.Schedule(DatabaseProvider.Sqlite, DatabaseProvider.PostgreSql, postgres, replace: true);
        using var target = server.Open(postgres);
        var expected = await DatabaseHistoryFingerprint.ReadAsync(target);

        await Should.ThrowAsync<SqliteException>(() => scope.Switch.ProcessPendingSwitchAsync());

        (await DatabaseHistoryFingerprint.ReadAsync(target)).ShouldBe(expected);
        File.Exists(scope.SqlitePath).ShouldBeFalse();
    }

    [PostgreSqlFact]
    public async Task PartialTargetSchema_IsAnError_AndRetainsExistingRows()
    {
        using var scope = new DatabaseSwitchTestScope();
        var postgres = await SeedPostgreSqlAsync();
        scope.Credentials.Password = "";
        using (scope.CreateDatabase()) { }
        scope.Schedule(DatabaseProvider.Sqlite, DatabaseProvider.PostgreSql, postgres, replace: true);
        await PostgreSqlTestServer.ExecuteAsync(postgres, "DROP TABLE \"ActivityProjections\";");

        await Should.ThrowAsync<PostgresException>(() => scope.Switch.ProcessPendingSwitchAsync());

        using var target = server.Open(postgres);
        target.Devices.Count().ShouldBe(1);
        target.ActivitySnapshots.Count().ShouldBe(2);
        AppMetaStore.ReadExisting(target)[DatabaseSwitchService.ImportSwitchMetaKey].ShouldBe("previous-switch");
    }

    [PostgreSqlFact]
    public async Task LockRetargeting_ReleasesPreviousDatabase_AndClaimsTheNewOne()
    {
        var first = await server.CreateDatabaseAsync();
        var second = await server.CreateDatabaseAsync();
        using var writer = new DatabaseInstanceLock();
        using var otherWriter = new DatabaseInstanceLock();
        var firstConnection = DatabaseConfigurationService.BuildPostgreSqlConnectionString(first, "");
        var secondConnection = DatabaseConfigurationService.BuildPostgreSqlConnectionString(second, "");
        writer.Acquire(firstConnection);
        writer.Acquire(secondConnection);
        otherWriter.Acquire(firstConnection);
        Should.Throw<InvalidOperationException>(() => otherWriter.Acquire(secondConnection));
        writer.Dispose();
        otherWriter.Acquire(secondConnection);
    }

    [PostgreSqlFact]
    public async Task RecoveryOfActiveProvider_CanQueueAndFinishAnExport()
    {
        using var scope = new DatabaseSwitchTestScope();
        var postgres = await SeedPostgreSqlAsync();
        var active = new AppUserSettings
        {
            IsFirstLaunch = false,
            DatabaseProvider = DatabaseProvider.PostgreSql,
            PostgreSql = postgres,
        };
        scope.Settings.SaveSettings(active);
        var recoveries = 0;
        var ready = await scope.Switch.PrepareStartupAsync(_ =>
        {
            scope.Credentials.Password = "";
            scope.Schedule(DatabaseProvider.PostgreSql, DatabaseProvider.Sqlite, postgres);
            return ++recoveries == 1;
        });
        ready.ShouldBeTrue();
        recoveries.ShouldBe(1);
        scope.Settings.GetSettings().DatabaseProvider.ShouldBe(DatabaseProvider.Sqlite);
        using var exported = scope.OpenDatabase();
        exported.Devices.Count().ShouldBe(1);
    }

    [PostgreSqlFact]
    public async Task LostAdvisoryConnection_AbortsTheCopy_AndNextAttemptCanReconnect()
    {
        using var scope = new DatabaseSwitchTestScope();
        var postgres = await SeedPostgreSqlAsync();
        scope.Credentials.Password = "";
        using (var local = scope.CreateDatabase())
            DatabaseSwitchTestScope.Seed(local, "NEW");
        var pending = scope.Schedule(DatabaseProvider.Sqlite, DatabaseProvider.PostgreSql, postgres, replace: true);
        scope.DatabaseLock.Acquire(DatabaseConfigurationService.BuildPostgreSqlConnectionString(postgres, ""));
        await PostgreSqlTestServer.ExecuteAsync(
            postgres,
            """
            SELECT pg_terminate_backend(pid) FROM pg_locks
            WHERE locktype = 'advisory' AND database = (SELECT oid FROM pg_database WHERE datname = current_database());
            """
        );
        using (var source = scope.OpenDatabase())
        using (var target = server.Open(postgres))
        {
            var original = await DatabaseHistoryFingerprint.ReadAsync(target);
            await Should.ThrowAsync<Exception>(
                () =>
                    DatabaseSwitchService.CopyHistoryAsync(
                        source,
                        target,
                        pending.PendingDatabaseSwitchId!,
                        true,
                        ensureExclusiveAccess: scope.DatabaseLock.VerifyHeld
                    )
            );
            (await DatabaseHistoryFingerprint.ReadAsync(target)).ShouldBe(original);
        }
        await scope.Switch.ProcessPendingSwitchAsync();
        using var resumed = server.Open(postgres);
        resumed.Devices.Single().DeviceId.ShouldBe("NEW");
    }

    [PostgreSqlFact]
    public async Task UncleanSourceSession_IsCopiedWithoutChangingEventsOrInflatingStoredDuration()
    {
        using var scope = new DatabaseSwitchTestScope();
        var postgres = await SeedPostgreSqlAsync();
        using (var source = server.Open(postgres))
            await source.DeviceEvents.Where(x => x.EventType == EventTypes.AppEnded).ExecuteDeleteAsync();
        scope.Credentials.Password = "";
        scope.Schedule(DatabaseProvider.PostgreSql, DatabaseProvider.Sqlite, postgres);
        await scope.Switch.ProcessPendingSwitchAsync();
        using var target = scope.OpenDatabase();
        target.DeviceEvents.Count(x => x.EventType == EventTypes.AppStarted).ShouldBe(1);
        target.DeviceEvents.Any(x => x.EventType == EventTypes.AppEnded).ShouldBeFalse();
        target.Devices.Single().SessionStartedAt.ShouldBeNull();
        target.Devices.Single().TotalConnectionSeconds.ShouldBe(9123);
    }

    private sealed class TransferContextFactory(Func<ApplicationDbContext> create)
        : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => create();
    }

    private async Task<PostgreSqlConnectionSettings> SeedPostgreSqlAsync()
    {
        var settings = await server.CreateDatabaseAsync();
        await server.CreateSchemaAsync(settings);
        using var context = server.Open(settings);
        DatabaseSwitchTestScope.Seed(context);
        return settings;
    }
}
