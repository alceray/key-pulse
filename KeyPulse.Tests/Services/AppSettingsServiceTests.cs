using System.IO;
using System.Text.Json;
using KeyPulse.Models;
using KeyPulse.Tests.Infrastructure;

namespace KeyPulse.Tests.Services;

public class AppSettingsServiceTests
{
    [Fact]
    public void Save_ReplacesValidJsonBeforeNotifying_AndCleansTemporaryFiles()
    {
        using var scope = new DatabaseSwitchTestScope();
        scope.Settings.SaveSettings(new AppUserSettings { DarkMode = false });
        var notified = 0;
        scope.Settings.SettingsChanged += value =>
        {
            JsonSerializer
                .Deserialize<AppUserSettings>(File.ReadAllText(scope.SettingsPath))!
                .DarkMode.ShouldBe(value.DarkMode);
            notified++;
        };
        scope.Settings.SaveSettings(new AppUserSettings { DarkMode = true });
        scope.Settings.GetSettings().DarkMode.ShouldBeTrue();
        notified.ShouldBe(1);
        Directory.GetFiles(scope.DirectoryPath, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public void FailedReplacement_PreservesExactSettings_AndDoesNotNotify()
    {
        using var scope = new DatabaseSwitchTestScope();
        var settings = scope.Schedule(DatabaseProvider.PostgreSql, DatabaseProvider.Sqlite);
        var before = File.ReadAllBytes(scope.SettingsPath);
        var notified = false;
        scope.Settings.SettingsChanged += _ => notified = true;
        settings.ClearPendingDatabaseSwitch();
        using (var locked = new FileStream(scope.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Should.Throw<IOException>(() => scope.Settings.SaveSettings(settings));
        File.ReadAllBytes(scope.SettingsPath).ShouldBe(before);
        notified.ShouldBeFalse();
        Directory.GetFiles(scope.DirectoryPath, "*.tmp").ShouldBeEmpty();
    }
}
