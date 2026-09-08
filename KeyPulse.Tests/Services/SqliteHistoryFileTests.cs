using System.IO;
using KeyPulse.Configuration;
using KeyPulse.Data;
using KeyPulse.Models;
using KeyPulse.Services;
using KeyPulse.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KeyPulse.Tests.Services;

public class SqliteHistoryFileTests
{
    [Fact]
    public async Task Backup_PreservesCommittedWalData_AndCanBeRestoredWithoutSidecars()
    {
        using var scope = new DatabaseSwitchTestScope();
        string backupPath;
        DatabaseHistoryFingerprint expected;
        using (var source = scope.CreateDatabase())
        {
            source.Database.OpenConnection();
            source.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            source.Database.ExecuteSqlRaw("PRAGMA wal_autocheckpoint=0;");
            DatabaseSwitchTestScope.Seed(source);
            File.Exists(scope.SqlitePath + "-wal").ShouldBeTrue();
            new FileInfo(scope.SqlitePath + "-wal").Length.ShouldBeGreaterThan(0);
            expected = await DatabaseHistoryFingerprint.ReadAsync(source);
            backupPath = SqliteHistoryFile.CreateVerifiedBackup(scope.SqlitePath);
        }
        File.Exists(backupPath + "-wal").ShouldBeFalse();
        File.Exists(backupPath + "-shm").ShouldBeFalse();
        var restoredPath = Path.Combine(scope.DirectoryPath, "restored.db");
        File.Copy(backupPath, restoredPath);
        using var restored = scope.OpenDatabase(restoredPath);
        (await DatabaseHistoryFingerprint.ReadAsync(restored)).ShouldBe(expected);
    }

    [Fact]
    public void Backup_AcceptsAnOlderSchemaWithoutMigratingIt()
    {
        using var scope = new DatabaseSwitchTestScope();
        using (var connection = new SqliteConnection($"Data Source={scope.SqlitePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE OldHistory (Id INTEGER PRIMARY KEY, Value TEXT); INSERT INTO OldHistory VALUES (1, 'preserve');";
            command.ExecuteNonQuery();
        }
        var backupPath = SqliteHistoryFile.CreateVerifiedBackup(scope.SqlitePath);
        using var backup = new SqliteConnection($"Data Source={backupPath};Pooling=False");
        backup.Open();
        using var read = backup.CreateCommand();
        read.CommandText = "SELECT Value FROM OldHistory;";
        read.ExecuteScalar().ShouldBe("preserve");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_InstallsStaging_AndPreservesOldHistoryWhenPresent(bool hasDestination)
    {
        using var scope = new DatabaseSwitchTestScope();
        if (hasDestination)
            using (var old = scope.CreateDatabase())
                DatabaseSwitchTestScope.Seed(old, "OLD");
        var id = Guid.NewGuid().ToString("N");
        var stagingPath = SqliteHistoryFile.GetStagingPath(scope.SqlitePath, id);
        using (var staging = scope.CreateDatabase(stagingPath))
        {
            DatabaseSwitchTestScope.Seed(staging, "NEW");
            AppMetaStore.Write(staging, DatabaseSwitchService.ImportSwitchMetaKey, id);
        }
        await SqliteHistoryFile.PublishAsync(stagingPath, scope.SqlitePath);
        using var installed = scope.OpenDatabase();
        installed.Devices.Single().DeviceId.ShouldBe("NEW");
        DatabaseSwitchService.HasCompletionMarker(installed, id).ShouldBeTrue();
        File.Exists(stagingPath).ShouldBeFalse();
        if (hasDestination)
        {
            var backupPath = Directory
                .GetFiles(Path.Combine(scope.DirectoryPath, AppConstants.Paths.DatabaseBackupsDirectoryName))
                .Single();
            using var backup = scope.OpenDatabase(backupPath);
            backup.Devices.Single().DeviceId.ShouldBe("OLD");
        }
    }

    [Fact]
    public async Task BackupFailure_LeavesDestinationAndStagingIntact()
    {
        using var scope = new DatabaseSwitchTestScope();
        using (var old = scope.CreateDatabase())
            DatabaseSwitchTestScope.Seed(old, "OLD");
        var stagingPath = SqliteHistoryFile.GetStagingPath(scope.SqlitePath, Guid.NewGuid().ToString("N"));
        using (var staging = scope.CreateDatabase(stagingPath))
            DatabaseSwitchTestScope.Seed(staging, "NEW");
        File.WriteAllText(
            Path.Combine(scope.DirectoryPath, AppConstants.Paths.DatabaseBackupsDirectoryName),
            "block backup directory"
        );

        await Should.ThrowAsync<IOException>(() => SqliteHistoryFile.PublishAsync(stagingPath, scope.SqlitePath));

        using var original = scope.OpenDatabase();
        original.Devices.Single().DeviceId.ShouldBe("OLD");
        File.Exists(stagingPath).ShouldBeTrue();
    }

    [Fact]
    public async Task LockedDestination_CannotBeReplacedOrDeleted()
    {
        using var scope = new DatabaseSwitchTestScope();
        using (var old = scope.CreateDatabase())
            DatabaseSwitchTestScope.Seed(old, "OLD");
        var stagingPath = SqliteHistoryFile.GetStagingPath(scope.SqlitePath, Guid.NewGuid().ToString("N"));
        using (var staging = scope.CreateDatabase(stagingPath))
            DatabaseSwitchTestScope.Seed(staging, "NEW");
        using (var locked = new FileStream(scope.SqlitePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Should.ThrowAsync<Exception>(() => SqliteHistoryFile.PublishAsync(stagingPath, scope.SqlitePath));
        using var original = scope.OpenDatabase();
        original.Devices.Single().DeviceId.ShouldBe("OLD");
        File.Exists(stagingPath).ShouldBeTrue();
    }

    [Fact]
    public async Task OpenWalConnection_ExplainsLock_AndPublicationCanBeRetriedAfterDisconnecting()
    {
        using var scope = new DatabaseSwitchTestScope();
        using (var old = scope.CreateDatabase())
            DatabaseSwitchTestScope.Seed(old, "OLD");
        var switchId = Guid.NewGuid().ToString("N");
        var stagingPath = SqliteHistoryFile.GetStagingPath(scope.SqlitePath, switchId);
        using (var staging = scope.CreateDatabase(stagingPath))
        {
            DatabaseSwitchTestScope.Seed(staging, "NEW");
            AppMetaStore.Write(staging, DatabaseSwitchService.ImportSwitchMetaKey, switchId);
        }

        // An idle database browser connection can prevent leaving WAL mode even after a complete
        // checkpoint. It does not need an active transaction or an uncommitted write.
        using (var browser = new SqliteConnection($"Data Source={scope.SqlitePath};Pooling=False"))
        {
            browser.Open();
            using var command = browser.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; SELECT COUNT(*) FROM Devices;";
            command.ExecuteScalar();

            var error = await Should.ThrowAsync<IOException>(
                () => SqliteHistoryFile.PublishAsync(stagingPath, scope.SqlitePath)
            );
            error.Message.ShouldContain(scope.SqlitePath);
            error.Message.ShouldContain("Disconnect");
            error.Message.ShouldContain("Retry");
            error.InnerException.ShouldBeOfType<SqliteException>().SqliteErrorCode.ShouldBe(5);
            using var original = scope.OpenDatabase();
            original.Devices.Single().DeviceId.ShouldBe("OLD");
            DatabaseSwitchService.HasCompletionMarker(original, switchId).ShouldBeFalse();
            File.Exists(stagingPath).ShouldBeTrue();
        }

        await SqliteHistoryFile.PublishAsync(stagingPath, scope.SqlitePath);

        using var installed = scope.OpenDatabase();
        installed.Devices.Single().DeviceId.ShouldBe("NEW");
        DatabaseSwitchService.HasCompletionMarker(installed, switchId).ShouldBeTrue();
        File.Exists(stagingPath).ShouldBeFalse();
    }

    [Fact]
    public async Task BusyWalCheckpoint_AbortsPublication()
    {
        using var scope = new DatabaseSwitchTestScope();
        using var old = scope.CreateDatabase();
        old.Database.OpenConnection();
        old.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        DatabaseSwitchTestScope.Seed(old, "OLD");
        using var readConnection = new SqliteConnection($"Data Source={scope.SqlitePath};Pooling=False");
        readConnection.Open();
        using var readerSnapshot = readConnection.BeginTransaction(deferred: true);
        using (var read = readConnection.CreateCommand())
        {
            read.Transaction = readerSnapshot;
            read.CommandText = "SELECT COUNT(*) FROM Devices;";
            read.ExecuteScalar();
        }
        old.Database.ExecuteSqlRaw("UPDATE Devices SET DeviceName = 'latest committed name';");
        var stagingPath = SqliteHistoryFile.GetStagingPath(scope.SqlitePath, Guid.NewGuid().ToString("N"));
        using (var staging = scope.CreateDatabase(stagingPath))
            DatabaseSwitchTestScope.Seed(staging, "NEW");

        await Should.ThrowAsync<Exception>(() => SqliteHistoryFile.PublishAsync(stagingPath, scope.SqlitePath));

        old.Devices.AsNoTracking().Single().DeviceName.ShouldBe("latest committed name");
        File.Exists(stagingPath).ShouldBeTrue();
    }
}
