using System.Text.Json;
using KeyPulse.Models;

namespace KeyPulse.Tests.Models;

public class AppUserSettingsTests
{
    [Fact]
    public void Defaults()
    {
        var settings = new AppUserSettings();

        settings.IsFirstLaunch.ShouldBeTrue();
        settings.AutoInstallUpdates.ShouldBeTrue();
        settings.DarkMode.ShouldBeFalse();
        settings.CloseToTray.ShouldBeTrue();
        settings.SuppressCloseToTrayHint.ShouldBeFalse(); // reminder shows until the user opts out
        settings.ActivityRetentionMonths.ShouldBe(24); // bounded by default; 0 would keep forever
        settings.DatabaseProvider.ShouldBe(DatabaseProvider.Sqlite);
        settings.PendingDatabaseProvider.ShouldBeNull();
        settings.PendingDatabaseReplace.ShouldBeFalse();
        settings.PostgreSql.Port.ShouldBe(5432);
#if DEBUG
        settings.PostgreSql.Database.ShouldBe("keypulse_signal_test");
#else
        settings.PostgreSql.Database.ShouldBe("keypulse_signal");
#endif

        // LaunchOnLogin default is build-config dependent (off in Debug so dev runs don't register autostart).
#if DEBUG
        settings.LaunchOnLogin.ShouldBeFalse();
#else
        settings.LaunchOnLogin.ShouldBeTrue();
#endif
    }

    [Fact]
    public void Deserialize_LegacyJsonWithoutRetentionKey_UsesDefaultRetention()
    {
        // A settings.json written before ActivityRetentionMonths existed.
        const string legacyJson = """{"LaunchOnLogin":true,"IsFirstLaunch":false,"AutoInstallUpdates":false}""";

        var settings = JsonSerializer.Deserialize<AppUserSettings>(legacyJson)!;

        settings.LaunchOnLogin.ShouldBeTrue();
        settings.IsFirstLaunch.ShouldBeFalse();
        settings.AutoInstallUpdates.ShouldBeFalse();
        settings.ActivityRetentionMonths.ShouldBe(24);
        settings.DarkMode.ShouldBeFalse();
    }

    [Fact]
    public void Deserialize_JsonWithoutCloseToTrayKey_DefaultsToTrue()
    {
        // A settings.json written before CloseToTray existed should keep the run-in-background default.
        const string legacyJson = """{"LaunchOnLogin":true,"IsFirstLaunch":false,"AutoInstallUpdates":true}""";

        var settings = JsonSerializer.Deserialize<AppUserSettings>(legacyJson)!;

        settings.CloseToTray.ShouldBeTrue();
    }

    [Fact]
    public void SerializeRoundTrip_PreservesAllFields()
    {
        var settings = new AppUserSettings
        {
            LaunchOnLogin = true,
            IsFirstLaunch = false,
            AutoInstallUpdates = false,
            DarkMode = true,
            CloseToTray = false,
            SuppressCloseToTrayHint = true,
            ActivityRetentionMonths = 6,
            DatabaseProvider = DatabaseProvider.PostgreSql,
            PendingDatabaseProvider = DatabaseProvider.Sqlite,
            PendingDatabaseImport = true,
            PendingDatabaseReplace = true,
            PendingDatabaseSwitchId = "switch-123",
            PostgreSql = new PostgreSqlConnectionSettings
            {
                Host = "db.local",
                Port = 5544,
                Database = "keypulse_custom",
                Username = "keypulse_user",
                SslMode = PostgreSqlSslMode.VerifyFull,
            },
        };

        var roundTripped = JsonSerializer.Deserialize<AppUserSettings>(JsonSerializer.Serialize(settings))!;

        roundTripped.LaunchOnLogin.ShouldBeTrue();
        roundTripped.IsFirstLaunch.ShouldBeFalse();
        roundTripped.AutoInstallUpdates.ShouldBeFalse();
        roundTripped.DarkMode.ShouldBeTrue();
        roundTripped.CloseToTray.ShouldBeFalse();
        roundTripped.SuppressCloseToTrayHint.ShouldBeTrue();
        roundTripped.ActivityRetentionMonths.ShouldBe(6);
        roundTripped.DatabaseProvider.ShouldBe(DatabaseProvider.PostgreSql);
        roundTripped.PendingDatabaseProvider.ShouldBe(DatabaseProvider.Sqlite);
        roundTripped.PendingDatabaseImport.ShouldBeTrue();
        roundTripped.PendingDatabaseReplace.ShouldBeTrue();
        roundTripped.PendingDatabaseSwitchId.ShouldBe("switch-123");
        roundTripped.PostgreSql.Host.ShouldBe("db.local");
        roundTripped.PostgreSql.Port.ShouldBe(5544);
        roundTripped.PostgreSql.Database.ShouldBe("keypulse_custom");
        roundTripped.PostgreSql.Username.ShouldBe("keypulse_user");
        roundTripped.PostgreSql.SslMode.ShouldBe(PostgreSqlSslMode.VerifyFull);
    }
}
