using System.IO;
using KeyPulse.Data;
using KeyPulse.Models;
using KeyPulse.Services;
using KeyPulse.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace KeyPulse.Tests.Services;

public class DatabaseSwitchServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryQueuedSwitch_IsProcessedBeforeStartupCompletes(bool alreadyPending)
    {
        using var scope = new DatabaseSwitchTestScope();
        var settings = scope.Schedule(DatabaseProvider.PostgreSql, DatabaseProvider.Sqlite);
        if (!alreadyPending)
        {
            settings.ClearPendingDatabaseSwitch();
            scope.Settings.SaveSettings(settings);
        }
        var recoveries = 0;
        (
            await scope.Switch.PrepareStartupAsync(_ =>
            {
                ++recoveries;
                var pending = scope.Schedule(DatabaseProvider.PostgreSql, DatabaseProvider.Sqlite);
                using var installed = scope.CreateDatabase();
                AppMetaStore.Write(
                    installed,
                    DatabaseSwitchService.ImportSwitchMetaKey,
                    pending.PendingDatabaseSwitchId!
                );
                return recoveries == 1;
            })
        ).ShouldBeTrue();
        recoveries.ShouldBe(1);
        scope.Settings.GetSettings().DatabaseProvider.ShouldBe(DatabaseProvider.Sqlite);
        scope.Settings.GetSettings().PendingDatabaseProvider.ShouldBeNull();
    }

    [Fact]
    public async Task Copy_PreservesAllHistoryAndMaintenanceMarkers_AndClearsSession()
    {
        using var scope = new DatabaseSwitchTestScope();
        using var source = scope.CreateDatabase();
        using var target = scope.CreateDatabase(Path.Combine(scope.DirectoryPath, "target.db"));
        DatabaseSwitchTestScope.Seed(source);
        var expected = await DatabaseHistoryFingerprint.ReadAsync(source);
        var switchId = Guid.NewGuid().ToString("N");

        await DatabaseSwitchService.CopyHistoryAsync(source, target, switchId, false);

        (await DatabaseHistoryFingerprint.ReadAsync(target)).ShouldBe(expected);
        var copied = target.Devices.AsNoTracking().Single();
        copied.SessionStartedAt.ShouldBeNull();
        copied.TotalConnectionSeconds.ShouldBe(9123);
        copied.IsHiddenFromDisplay.ShouldBeTrue();
        copied.DeviceName.ShouldBe("Hidden keyboard \u2603");
        target.DailyDeviceStats.Single(x => x.Day == new DateOnly(2020, 1, 1)).Keystrokes.ShouldBe(3000);
        target
            .DailyDeviceStats.First()
            .HourlyInputCount.ShouldBe(Enumerable.Range(0, 24).Select(x => (long)x * 10).ToArray());
        var sourceMeta = AppMetaStore.ReadExisting(source);
        var targetMeta = AppMetaStore.ReadExisting(target);
        targetMeta["DailyStatsFullBackfillAt"].ShouldBe(sourceMeta["DailyStatsFullBackfillAt"]);
        targetMeta[DatabaseSwitchService.ImportSwitchMetaKey].ShouldBe(switchId);
        sourceMeta[DatabaseSwitchService.ImportSwitchMetaKey].ShouldBe("previous-switch");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Copy_FailureAfterFirstBatch_RestoresAllOriginalDataAndMetadata(bool populated)
    {
        using var scope = new DatabaseSwitchTestScope();
        using var source = scope.CreateDatabase();
        using var target = scope.CreateDatabase(Path.Combine(scope.DirectoryPath, "target.db"));
        DatabaseSwitchTestScope.Seed(source, eventCount: 1005);
        if (populated)
            DatabaseSwitchTestScope.Seed(target, "OLD");
        var expected = await DatabaseHistoryFingerprint.ReadAsync(target);
        var expectedMeta = AppMetaStore.ReadExisting(target);
        target.Database.ExecuteSqlRaw(
            """
            CREATE TRIGGER fail_second_batch BEFORE INSERT ON DeviceEvents
            WHEN (SELECT COUNT(*) FROM DeviceEvents) >= 1000
            BEGIN SELECT RAISE(ABORT, 'simulated write failure'); END;
            """
        );

        await Should.ThrowAsync<DbUpdateException>(
            () => DatabaseSwitchService.CopyHistoryAsync(source, target, Guid.NewGuid().ToString("N"), populated)
        );

        (await DatabaseHistoryFingerprint.ReadAsync(target)).ShouldBe(expected);
        AppMetaStore.ReadExisting(target).ShouldBe(expectedMeta);
    }

    [Fact]
    public async Task Copy_VerificationRejectsChangesThatKeepCountsAndCombinedInputTotalEqual()
    {
        using var scope = new DatabaseSwitchTestScope();
        using var source = scope.CreateDatabase();
        using var target = scope.CreateDatabase(Path.Combine(scope.DirectoryPath, "target.db"));
        DatabaseSwitchTestScope.Seed(source);
        DatabaseSwitchTestScope.Seed(target, "OLD");
        var expected = await DatabaseHistoryFingerprint.ReadAsync(target);
        target.Database.ExecuteSqlRaw(
            """
            CREATE TRIGGER alter_input AFTER INSERT ON ActivitySnapshots
            BEGIN UPDATE ActivitySnapshots SET Keystrokes = Keystrokes - 1, MouseClicks = MouseClicks + 1
            WHERE ActivitySnapshotId = NEW.ActivitySnapshotId; END;
            """
        );

        await Should.ThrowAsync<InvalidOperationException>(
            () => DatabaseSwitchService.CopyHistoryAsync(source, target, Guid.NewGuid().ToString("N"), true)
        );

        (await DatabaseHistoryFingerprint.ReadAsync(target)).ShouldBe(expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Copy_CancellationBeforeCommit_RollsBack(bool populated)
    {
        using var scope = new DatabaseSwitchTestScope();
        using var source = scope.CreateDatabase();
        using var target = scope.CreateDatabase(Path.Combine(scope.DirectoryPath, "target.db"));
        DatabaseSwitchTestScope.Seed(source);
        if (populated)
            DatabaseSwitchTestScope.Seed(target, "OLD");
        var expected = await DatabaseHistoryFingerprint.ReadAsync(target);
        using var cancellation = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(
            () =>
                DatabaseSwitchService.CopyHistoryAsync(
                    source,
                    target,
                    Guid.NewGuid().ToString("N"),
                    populated,
                    cancellation.Token,
                    cancellation.Cancel
                )
        );

        (await DatabaseHistoryFingerprint.ReadAsync(target)).ShouldBe(expected);
    }

    [Fact]
    public async Task Copy_ReplacementLeavesExactlyTheSource_AndRequiresConsent()
    {
        using var scope = new DatabaseSwitchTestScope();
        using var source = scope.CreateDatabase();
        using var target = scope.CreateDatabase(Path.Combine(scope.DirectoryPath, "target.db"));
        DatabaseSwitchTestScope.Seed(source);
        DatabaseSwitchTestScope.Seed(target, "OLD");
        await Should.ThrowAsync<InvalidOperationException>(
            () => DatabaseSwitchService.CopyHistoryAsync(source, target, "new", false)
        );
        await DatabaseSwitchService.CopyHistoryAsync(source, target, "new", true);
        (await DatabaseHistoryFingerprint.ReadAsync(target)).ShouldBe(
            await DatabaseHistoryFingerprint.ReadAsync(source)
        );
        target.Devices.Any(x => x.DeviceId == "OLD").ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstalledSqliteMarker_ResumesWithoutSourceCredentials_EvenWhenEmpty(bool populated)
    {
        using var scope = new DatabaseSwitchTestScope();
        var settings = scope.Schedule(DatabaseProvider.PostgreSql, DatabaseProvider.Sqlite);
        using (var target = scope.CreateDatabase())
        {
            if (populated)
                DatabaseSwitchTestScope.Seed(target);
            AppMetaStore.Write(target, DatabaseSwitchService.ImportSwitchMetaKey, settings.PendingDatabaseSwitchId!);
        }
        await scope.Switch.ProcessPendingSwitchAsync();
        var active = scope.Settings.GetSettings();
        active.DatabaseProvider.ShouldBe(DatabaseProvider.Sqlite);
        active.PendingDatabaseProvider.ShouldBeNull();
        active.PendingDatabaseSwitchId.ShouldBeNull();
        active.PendingDatabaseImport.ShouldBeFalse();
        active.PendingDatabaseReplace.ShouldBeFalse();
        (await scope.Switch.ProcessPendingSwitchAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task UnpublishedStagingMarker_DoesNotActivateSQLite()
    {
        using var scope = new DatabaseSwitchTestScope();
        var settings = scope.Schedule(DatabaseProvider.PostgreSql, DatabaseProvider.Sqlite);
        var stagingPath = SqliteHistoryFile.GetStagingPath(scope.SqlitePath, settings.PendingDatabaseSwitchId!);
        using (var staging = scope.CreateDatabase(stagingPath))
            AppMetaStore.Write(staging, DatabaseSwitchService.ImportSwitchMetaKey, settings.PendingDatabaseSwitchId!);
        await Should.ThrowAsync<InvalidOperationException>(() => scope.Switch.ProcessPendingSwitchAsync());
        scope.Settings.GetSettings().DatabaseProvider.ShouldBe(DatabaseProvider.PostgreSql);
        File.Exists(scope.SqlitePath).ShouldBeFalse();
    }

    [Fact]
    public async Task ActivationSaveFailure_PreservesPendingId_AndRetryOnlyActivates()
    {
        using var scope = new DatabaseSwitchTestScope();
        var settings = scope.Schedule(DatabaseProvider.PostgreSql, DatabaseProvider.Sqlite);
        using (var target = scope.CreateDatabase())
            AppMetaStore.Write(target, DatabaseSwitchService.ImportSwitchMetaKey, settings.PendingDatabaseSwitchId!);
        using (var lockedSettings = new FileStream(scope.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Should.ThrowAsync<IOException>(() => scope.Switch.ProcessPendingSwitchAsync());
            scope.Settings.GetSettings().PendingDatabaseSwitchId.ShouldBe(settings.PendingDatabaseSwitchId);
        }
        await scope.Switch.ProcessPendingSwitchAsync();
        scope.Settings.GetSettings().DatabaseProvider.ShouldBe(DatabaseProvider.Sqlite);
    }

    [Fact]
    public async Task ExplicitFallbackAfterFailedExport_ClearsAllPendingState()
    {
        using var scope = new DatabaseSwitchTestScope();
        scope.Schedule(DatabaseProvider.PostgreSql, DatabaseProvider.Sqlite);
        await Should.ThrowAsync<InvalidOperationException>(() => scope.Switch.ProcessPendingSwitchAsync());
        await scope.Switch.CancelPendingSwitchAsync(useLocalWithoutCopying: true);
        var settings = scope.Settings.GetSettings();
        settings.DatabaseProvider.ShouldBe(DatabaseProvider.Sqlite);
        settings.PendingDatabaseProvider.ShouldBeNull();
        settings.PendingDatabaseSwitchId.ShouldBeNull();
        settings.PendingDatabaseImport.ShouldBeFalse();
        settings.PendingDatabaseReplace.ShouldBeFalse();
    }
}
