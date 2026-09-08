using System.IO;
using KeyPulse.Data;
using KeyPulse.Models;
using KeyPulse.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KeyPulse.Tests.Infrastructure;

internal sealed class DatabaseSwitchTestScope : IDisposable
{
    internal string DirectoryPath { get; } =
        Path.Combine(Path.GetTempPath(), "keypulse-switch-" + Guid.NewGuid().ToString("N"));
    internal string SqlitePath => Path.Combine(DirectoryPath, "history.db");
    internal string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    internal AppSettingsService Settings { get; }
    internal FakeDatabaseCredentialStore Credentials { get; } = new();
    internal DatabaseInstanceLock DatabaseLock { get; } = new();
    internal DatabaseSwitchService Switch { get; }

    internal DatabaseSwitchTestScope()
    {
        Directory.CreateDirectory(DirectoryPath);
        Settings = new AppSettingsService(SettingsPath);
        Switch = new DatabaseSwitchService(Settings, Credentials, DatabaseLock, SqlitePath);
    }

    internal ApplicationDbContext CreateDatabase(string? path = null)
    {
        var context = ConfiguredDbContextFactory.CreateSqliteContext(path ?? SqlitePath, pooling: false);
        context.Database.Migrate();
        DatabaseMigrations.MarkTimestampMigrationsApplied(context);
        return context;
    }

    internal ApplicationDbContext OpenDatabase(string? path = null) =>
        ConfiguredDbContextFactory.CreateSqliteContext(path ?? SqlitePath, SqliteOpenMode.ReadWrite, pooling: false);

    internal AppUserSettings Schedule(
        DatabaseProvider source,
        DatabaseProvider destination,
        PostgreSqlConnectionSettings? postgres = null,
        bool replace = false
    )
    {
        var settings = new AppUserSettings
        {
            IsFirstLaunch = false,
            DatabaseProvider = source,
            PendingDatabaseProvider = destination,
            PendingDatabaseImport = true,
            PendingDatabaseReplace = replace,
            PendingDatabaseSwitchId = Guid.NewGuid().ToString("N"),
            PostgreSql = postgres ?? new PostgreSqlConnectionSettings(),
        };
        Settings.SaveSettings(settings);
        return settings;
    }

    internal static void Seed(
        ApplicationDbContext context,
        string deviceId = "USB\\VID_1234&PID_5678",
        int eventCount = 4
    )
    {
        var minute = new DateTime(2025, 11, 2, 5, 30, 0, DateTimeKind.Utc);
        context.Devices.Add(
            new Device
            {
                DeviceId = deviceId,
                DeviceName = "Hidden keyboard \u2603",
                DeviceType = DeviceTypes.Keyboard,
                IsHiddenFromDisplay = true,
                TotalConnectionSeconds = 9123,
                TotalInputCount = 12345,
                DaysConnected = 7,
                LastConnectedAt = minute,
                LastSeenAt = minute.AddHours(1),
            }
        );
        context.DeviceEvents.Add(new DeviceEvent { EventType = EventTypes.AppStarted, EventTime = minute });
        for (var i = 0; i < eventCount; i++)
            context.DeviceEvents.Add(
                new DeviceEvent
                {
                    DeviceId = deviceId,
                    EventType = i % 2 == 0 ? EventTypes.Connected : EventTypes.Disconnected,
                    EventTime = minute.AddSeconds(i),
                }
            );
        context.DeviceEvents.Add(new DeviceEvent { EventType = EventTypes.AppEnded, EventTime = minute.AddHours(1) });
        foreach (var instant in new[] { minute, minute.AddHours(1) })
        {
            context.ActivitySnapshots.Add(
                new ActivitySnapshot
                {
                    DeviceId = deviceId,
                    Minute = instant,
                    Keystrokes = 40,
                    MouseClicks = 2,
                    MouseMovementSeconds = 12,
                    ActiveSeconds = 20,
                }
            );
            context.ActivityProjections.Add(new ActivityProjection { DeviceId = deviceId, Minute = instant });
        }
        foreach (var day in new[] { new DateOnly(2020, 1, 1), new DateOnly(2025, 11, 2) })
            context.DailyDeviceStats.Add(
                new DailyDeviceStat
                {
                    DeviceId = deviceId,
                    Day = day,
                    SessionCount = 3,
                    ConnectionSeconds = 9123,
                    Keystrokes = 3000,
                    MouseClicks = 100,
                    MouseMovementSeconds = 60,
                    ActiveSeconds = 120,
                    HourlyInputCount = Enumerable.Range(0, 24).Select(x => (long)x * 10).ToArray(),
                    UpdatedAt = minute,
                }
            );
        context.SaveChanges();
        context.ChangeTracker.Clear();
        context
            .Devices.Where(x => x.DeviceId == deviceId)
            .ExecuteUpdate(set => set.SetProperty(x => x.SessionStartedAt, minute));
        AppMetaStore.EnsureTable(context);
        AppMetaStore.Write(context, "DailyStatsFullBackfillAt", "2025-11-02T05:30:00.0000000Z");
        AppMetaStore.Write(
            context,
            DailyStatsService.CONNECTION_SPAN_RECOMPUTE_META_KEY,
            "2025-11-02T05:30:00.0000000Z"
        );
        AppMetaStore.Write(context, DatabaseSwitchService.ImportSwitchMetaKey, "previous-switch");
    }

    public void Dispose()
    {
        DatabaseLock.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}

internal sealed class FakeDatabaseCredentialStore : IDatabaseCredentialStore
{
    internal string? Password { get; set; }
    internal Dictionary<string, string> Entries { get; } = new();
    internal bool FailDeletion { get; set; }
    internal bool FailWriteAfterCreation { get; set; }

    public string? ReadPostgreSqlPassword(string? credentialReference = null) =>
        credentialReference == null ? Password : Entries.GetValueOrDefault(credentialReference);

    public void WritePostgreSqlPassword(string password, string? credentialReference = null)
    {
        if (credentialReference == null)
            Password = password;
        else
            Entries[credentialReference] = password;
        if (FailWriteAfterCreation)
            throw new IOException("Injected credential write failure");
    }

    public void DeletePostgreSqlPassword(string? credentialReference = null)
    {
        if (FailDeletion)
            throw new IOException("Injected credential cleanup failure");
        if (credentialReference == null)
            Password = null;
        else
            Entries.Remove(credentialReference);
    }
}
