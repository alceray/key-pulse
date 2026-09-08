using KeyPulse.Configuration;
using KeyPulse.Models;
using KeyPulse.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KeyPulse.Data;

public sealed class ConfiguredDbContextFactory : IDbContextFactory<ApplicationDbContext>
{
    private readonly DatabaseProvider _provider;
    private readonly PostgreSqlConnectionSettings _postgreSql;
    private readonly string? _password;

    public ConfiguredDbContextFactory(AppSettingsService settingsService, IDatabaseCredentialStore credentialStore)
    {
        var settings = settingsService.GetSettings();
        _provider = settings.DatabaseProvider;
        _postgreSql = settings.PostgreSql.Copy();
        _password =
            _provider == DatabaseProvider.PostgreSql
                ? credentialStore.ReadPostgreSqlPassword(settings.PostgreSqlCredentialReference)
                : null;
    }

    public ApplicationDbContext CreateDbContext()
    {
        if (_provider == DatabaseProvider.PostgreSql)
        {
            if (_password == null)
                throw new InvalidOperationException(
                    "The PostgreSQL password is not available in Windows Credential Manager"
                );
            return CreatePostgreSqlContext(_postgreSql, _password);
        }

        return CreateSqliteContext(AppDataPaths.GetPath(AppConstants.Paths.DatabaseFileName));
    }

    internal static ApplicationDbContext CreateSqliteContext(
        string databasePath,
        SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate,
        bool pooling = true
    )
    {
        var builder = new DbContextOptionsBuilder<ApplicationDbContext>();
        builder
            .UseLazyLoadingProxies()
            .UseSqlite(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = mode,
                    Pooling = pooling,
                }.ConnectionString
            );
        return new ApplicationDbContext(builder.Options);
    }

    internal static PostgreSqlApplicationDbContext CreatePostgreSqlContext(
        PostgreSqlConnectionSettings settings,
        string password
    )
    {
        var builder = new DbContextOptionsBuilder<ApplicationDbContext>();
        builder
            .UseLazyLoadingProxies()
            .UseNpgsql(DatabaseConfigurationService.BuildPostgreSqlConnectionString(settings, password));
        return new PostgreSqlApplicationDbContext(builder.Options);
    }
}
