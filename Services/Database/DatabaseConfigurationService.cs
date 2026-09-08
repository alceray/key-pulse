using System.IO;
using System.Text.Json;
using KeyPulse.Configuration;
using KeyPulse.Data;
using KeyPulse.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace KeyPulse.Services;

public sealed class DatabaseConfigurationService
{
    public static string BuildPostgreSqlConnectionString(PostgreSqlConnectionSettings settings, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Database);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Username);
        if (settings.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(settings.Port), "Port must be between 1 and 65535");

        return new NpgsqlConnectionStringBuilder
        {
            Host = settings.Host.Trim(),
            Port = settings.Port,
            Database = settings.Database.Trim(),
            Username = settings.Username.Trim(),
            Password = password,
            SslMode = settings.SslMode switch
            {
                PostgreSqlSslMode.Disable => SslMode.Disable,
                PostgreSqlSslMode.Require => SslMode.Require,
                PostgreSqlSslMode.VerifyFull => SslMode.VerifyFull,
                _ => SslMode.Prefer,
            },
            ApplicationName = AppConstants.App.DefaultName,
            Timeout = 5,
            CommandTimeout = 30,
            IncludeErrorDetail = false,
            Pooling = true,
        }.ConnectionString;
    }

    public static async Task TestPostgreSqlAsync(
        PostgreSqlConnectionSettings settings,
        string password,
        CancellationToken cancellationToken = default
    )
    {
        EnsureNotUsedByOtherBuild(settings);
        await using var connection = new NpgsqlConnection(BuildPostgreSqlConnectionString(settings, password));
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT has_schema_privilege(current_user, 'public', 'CREATE');";
        // Signing in is not enough, and catching a missing CREATE here beats failing after the switch commits.
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
        {
            var role = settings.Username.Trim();
            throw new InvalidOperationException(
                $"The role \"{role}\" can sign in but cannot create tables in the public schema. "
                    + $"Make it the owner of the database, or grant it with GRANT CREATE, USAGE ON SCHEMA public TO \"{role}\"."
            );
        }
    }

    public static bool HasSqliteHistory()
    {
        var path = AppDataPaths.GetPath(AppConstants.Paths.DatabaseFileName);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            return false;

        try
        {
            using var ctx = ConfiguredDbContextFactory.CreateSqliteContext(path);
            return ctx.Devices.Any()
                || ctx.DeviceEvents.Any()
                || ctx.ActivitySnapshots.Any()
                || ctx.DailyDeviceStats.Any()
                || ctx.ActivityProjections.Any();
        }
        catch
        {
            // A database that cannot yet be inspected must not be treated as disposable.
            return true;
        }
    }

    internal static bool IsSamePostgreSqlDatabase(
        PostgreSqlConnectionSettings left,
        PostgreSqlConnectionSettings right
    ) =>
        left.Port == right.Port
        && string.Equals(left.Host.Trim(), right.Host.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Database.Trim(), right.Database.Trim(), StringComparison.Ordinal);

    internal static async Task TestPostgreSqlReadAsync(
        PostgreSqlConnectionSettings settings,
        string password,
        CancellationToken cancellationToken = default
    )
    {
        EnsureNotUsedByOtherBuild(settings);
        await using var source = ConfiguredDbContextFactory.CreatePostgreSqlContext(settings, password);
        await DatabaseSwitchService.ValidateSourceSchemaAsync(source, cancellationToken);
        await DatabaseSwitchService.HasApplicationDataAsync(source, cancellationToken);
        AppMetaStore.ReadExisting(source);
    }

    internal static void EnsureNotUsedByOtherBuild(PostgreSqlConnectionSettings candidate)
    {
        var otherSettingsPath = AppDataPaths.GetOtherBuildSettingsPath();
        if (!File.Exists(otherSettingsPath))
            return;

        try
        {
            var other = JsonSerializer.Deserialize<AppUserSettings>(File.ReadAllText(otherSettingsPath));
            if (other == null)
                return;

            EnsureNotUsedByOtherBuild(candidate, other);
        }
        catch (JsonException)
        {
            // The other build will handle its own malformed settings; it cannot establish a known collision here.
        }
    }

    internal static void EnsureNotUsedByOtherBuild(PostgreSqlConnectionSettings candidate, AppUserSettings other)
    {
        var activeCollision =
            other.DatabaseProvider == DatabaseProvider.PostgreSql
            && IsSamePostgreSqlDatabase(candidate, other.PostgreSql);
        var pendingCollision =
            other.PendingDatabaseProvider == DatabaseProvider.PostgreSql
            && IsSamePostgreSqlDatabase(candidate, other.PendingPostgreSql ?? other.PostgreSql);
        if (activeCollision || pendingCollision)
            throw new InvalidOperationException(
                $"That database is already configured for the other KeyPulse build. Use a separate {BuildInfo.EnvironmentName} database."
            );
    }
}
