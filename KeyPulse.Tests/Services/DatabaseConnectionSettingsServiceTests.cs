using System.IO;
using System.Text.Json;
using KeyPulse.Data;
using KeyPulse.Models;
using KeyPulse.Services;
using KeyPulse.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace KeyPulse.Tests.Services;

public class DatabaseConnectionSettingsServiceTests
{
    [Fact]
    public void CredentialWriteFailure_DoesNotPublishPendingSettingsOrReplaceSourcePassword()
    {
        using var scope = new DatabaseSwitchTestScope();
        var active = SaveActive(scope);
        scope.Credentials.FailWriteAfterCreation = true;
        Should.Throw<IOException>(
            () =>
                new DatabaseConnectionSettingsService(scope.Settings, scope.Credentials).SchedulePostgreSql(
                    active,
                    new() { Database = "destination" },
                    "new-secret",
                    true,
                    true
                )
        );
        scope.Settings.ReadPersistedSettings().PendingDatabaseProvider.ShouldBeNull();
        scope.Credentials.Entries.ShouldBeEmpty();
        scope.Credentials.Password.ShouldBe("source-secret");
    }

    [Fact]
    public void Scheduling_KeepsTheActiveConnectionAndCredential_AndFactoryUsesTheSource()
    {
        using var scope = new DatabaseSwitchTestScope();
        var active = SaveActive(scope);
        var destination = new PostgreSqlConnectionSettings { Database = "destination", Username = "destination_user" };
        var service = new DatabaseConnectionSettingsService(scope.Settings, scope.Credentials);
        service.SchedulePostgreSql(active, destination, "destination-secret", true, true);
        var durable = scope.Settings.ReadPersistedSettings();
        durable.PostgreSql.Database.ShouldBe("source");
        durable.PendingPostgreSql!.Database.ShouldBe("destination");
        durable.PendingDatabaseProvider.ShouldBe(DatabaseProvider.PostgreSql);
        scope.Credentials.Password.ShouldBe("source-secret");
        scope
            .Credentials.ReadPostgreSqlPassword(durable.PendingPostgreSqlCredentialReference)
            .ShouldBe("destination-secret");
        File.ReadAllText(scope.SettingsPath).ShouldNotContain("secret");
        using var context = new ConfiguredDbContextFactory(scope.Settings, scope.Credentials).CreateDbContext();
        var connection = new NpgsqlConnectionStringBuilder(context.Database.GetDbConnection().ConnectionString);
        connection.Database.ShouldBe("source");
        connection.Password.ShouldBe("source-secret");
        Should.Throw<InvalidOperationException>(
            () => service.SchedulePostgreSql(durable, destination, "another", true, true)
        );
        scope.Settings.ReadPersistedSettings().PendingDatabaseSwitchId.ShouldBe(durable.PendingDatabaseSwitchId);
        scope.Credentials.Entries.Count.ShouldBe(1);
        Should.Throw<InvalidOperationException>(() => service.ScheduleSqlite(new AppUserSettings()));
    }

    [Fact]
    public void FailedSettingsPublication_RemovesOnlyTheUnpublishedCredential()
    {
        using var scope = new DatabaseSwitchTestScope();
        var active = SaveActive(scope);
        var before = File.ReadAllBytes(scope.SettingsPath);
        using (var locked = new FileStream(scope.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Should.Throw<IOException>(
                () =>
                    new DatabaseConnectionSettingsService(scope.Settings, scope.Credentials).SchedulePostgreSql(
                        active,
                        new() { Database = "destination" },
                        "destination-secret",
                        true,
                        true
                    )
            );
        File.ReadAllBytes(scope.SettingsPath).ShouldBe(before);
        scope.Credentials.Password.ShouldBe("source-secret");
        scope.Credentials.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void NotificationFailureAfterPublication_DoesNotDeleteTheDurableDestinationCredential()
    {
        using var scope = new DatabaseSwitchTestScope();
        var active = SaveActive(scope);
        scope.Settings.SettingsChanged += _ => throw new InvalidOperationException("subscriber failed");
        Should.Throw<InvalidOperationException>(
            () =>
                new DatabaseConnectionSettingsService(scope.Settings, scope.Credentials).SchedulePostgreSql(
                    active,
                    new() { Database = "destination" },
                    "destination-secret",
                    true,
                    true
                )
        );
        var durable = scope.Settings.ReadPersistedSettings();
        scope
            .Credentials.ReadPostgreSqlPassword(durable.PendingPostgreSqlCredentialReference)
            .ShouldBe("destination-secret");
        durable.PostgreSql.Database.ShouldBe("source");
        scope.Credentials.Password.ShouldBe("source-secret");
    }

    [Fact]
    public void AuthenticationEdit_UsesAnAtomicReferenceWithoutSchedulingHistoryReplacement()
    {
        using var scope = new DatabaseSwitchTestScope();
        var active = SaveActive(scope);
        var connection = active.PostgreSql.Copy();
        connection.Username = "new_user";
        connection.SslMode = PostgreSqlSslMode.Require;
        var service = new DatabaseConnectionSettingsService(scope.Settings, scope.Credentials);
        service.UpdateAuthentication(active, connection, "new-secret");
        var durable = scope.Settings.ReadPersistedSettings();
        durable.PendingDatabaseProvider.ShouldBeNull();
        durable.PostgreSql.Username.ShouldBe("new_user");
        using var context = new ConfiguredDbContextFactory(scope.Settings, scope.Credentials).CreateDbContext();
        new NpgsqlConnectionStringBuilder(context.Database.GetDbConnection().ConnectionString).Password.ShouldBe(
            "new-secret"
        );
        var previous = durable.PostgreSqlCredentialReference;
        scope.Credentials.FailDeletion = true;
        service.UpdateAuthentication(durable, connection, "latest-secret");
        scope
            .Credentials.ReadPostgreSqlPassword(scope.Settings.ReadPersistedSettings().PostgreSqlCredentialReference)
            .ShouldBe("latest-secret");
        scope.Credentials.ReadPostgreSqlPassword(previous).ShouldBe("new-secret");
        Should.Throw<InvalidOperationException>(
            () => service.SchedulePostgreSql(scope.Settings.GetSettings(), connection, "secret", true, true)
        );
    }

    [Fact]
    public void UnreadableDurableSettings_NeverAuthorizesCredentialDeletion()
    {
        using var scope = new DatabaseSwitchTestScope();
        var reference = Guid.NewGuid().ToString("N");
        scope.Credentials.WritePostgreSqlPassword("keep", reference);
        File.WriteAllText(scope.SettingsPath, "invalid json");
        new DatabaseConnectionSettingsService(scope.Settings, scope.Credentials).CleanupCredentials(reference);
        scope.Credentials.ReadPostgreSqlPassword(reference).ShouldBe("keep");
    }

    [Fact]
    public void LegacyPendingSettingsResolveTheLegacyDestination_ButIncompleteDirectStateIsRejected()
    {
        var legacy = JsonSerializer.Deserialize<AppUserSettings>(
            """{"PendingDatabaseProvider":1,"PostgreSql":{"Database":"legacy"}}"""
        )!;
        var destination = DatabaseConnectionSettingsService.ResolvePendingPostgreSql(legacy);
        destination.Connection.Database.ShouldBe("legacy");
        destination.CredentialReference.ShouldBeNull();
        legacy.DatabaseProvider = DatabaseProvider.PostgreSql;
        Should.Throw<InvalidOperationException>(
            () => DatabaseConnectionSettingsService.ResolvePendingPostgreSql(legacy)
        );
        legacy.PendingPostgreSql = new() { Database = "new" };
        Should.Throw<InvalidOperationException>(
            () => DatabaseConnectionSettingsService.ResolvePendingPostgreSql(legacy)
        );
        legacy.PendingPostgreSqlCredentialReference = Guid.NewGuid().ToString("N");
        var roundTrip = JsonSerializer.Deserialize<AppUserSettings>(JsonSerializer.Serialize(legacy))!;
        destination = DatabaseConnectionSettingsService.ResolvePendingPostgreSql(roundTrip);
        destination.Connection.Database.ShouldBe("new");
        destination.CredentialReference.ShouldBe(legacy.PendingPostgreSqlCredentialReference);
        roundTrip.ClearPendingDatabaseSwitch();
        roundTrip.PendingPostgreSql.ShouldBeNull();
        roundTrip.PendingPostgreSqlCredentialReference.ShouldBeNull();
        Should.Throw<InvalidOperationException>(
            () => DatabaseConnectionSettingsService.ResolvePendingPostgreSql(roundTrip)
        );
    }

    [Fact]
    public void BuildIsolation_ReservesBothActiveAndPendingEndpoints_IncludingLegacySettings()
    {
        var other = new AppUserSettings
        {
            DatabaseProvider = DatabaseProvider.PostgreSql,
            PostgreSql = new() { Database = "active" },
            PendingDatabaseProvider = DatabaseProvider.PostgreSql,
            PendingPostgreSql = new() { Database = "pending" },
        };
        Should.Throw<InvalidOperationException>(
            () => DatabaseConfigurationService.EnsureNotUsedByOtherBuild(other.PostgreSql, other)
        );
        Should.Throw<InvalidOperationException>(
            () => DatabaseConfigurationService.EnsureNotUsedByOtherBuild(other.PendingPostgreSql, other)
        );
        Should.NotThrow(
            () => DatabaseConfigurationService.EnsureNotUsedByOtherBuild(new() { Database = "separate" }, other)
        );
        other.DatabaseProvider = DatabaseProvider.Sqlite;
        other.PendingPostgreSql = null;
        Should.Throw<InvalidOperationException>(
            () => DatabaseConfigurationService.EnsureNotUsedByOtherBuild(other.PostgreSql, other)
        );
        var reference = Guid.NewGuid().ToString("N");
        WindowsDatabaseCredentialStore.TargetName(reference).ShouldEndWith($"/{BuildInfo.EnvironmentName}/{reference}");
        Should.Throw<InvalidOperationException>(() => WindowsDatabaseCredentialStore.TargetName("../Release/password"));
    }

    private static AppUserSettings SaveActive(DatabaseSwitchTestScope scope)
    {
        var active = new AppUserSettings
        {
            IsFirstLaunch = false,
            DatabaseProvider = DatabaseProvider.PostgreSql,
            PostgreSql = new() { Database = "source" },
        };
        scope.Credentials.Password = "source-secret";
        scope.Settings.SaveSettings(active);
        return active;
    }
}
