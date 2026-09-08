using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using KeyPulse.Data;
using KeyPulse.Models;
using KeyPulse.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace KeyPulse.Tests.Infrastructure;

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KEYPULSE_TEST_POSTGRES_BIN")))
            Skip = "Set KEYPULSE_TEST_POSTGRES_BIN to run against a disposable PostgreSQL cluster.";
    }
}

[CollectionDefinition("PostgreSQL switches")]
public sealed class PostgreSqlSwitchCollection : ICollectionFixture<PostgreSqlTestServer> { }

public sealed class PostgreSqlTestServer : IAsyncLifetime
{
    private readonly string? _binPath = Environment.GetEnvironmentVariable("KEYPULSE_TEST_POSTGRES_BIN");
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "keypulse-postgres-tests-" + Guid.NewGuid().ToString("N")
    );
    private bool _started;
    private int _port;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_binPath))
            return;
        Directory.CreateDirectory(_directory);
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        await RunAsync(
            "initdb.exe",
            "-D",
            _directory,
            "-U",
            "keypulse_switch_test",
            "-A",
            "trust",
            "--no-locale",
            "-E",
            "UTF8"
        );
        await RunAsync(
            "pg_ctl.exe",
            "-D",
            _directory,
            "-l",
            Path.Combine(_directory, "server.log"),
            "-o",
            $"-p {_port} -h 127.0.0.1",
            "-w",
            "-t",
            "30",
            "start"
        );
        _started = true;
    }

    internal async Task<PostgreSqlConnectionSettings> CreateDatabaseAsync()
    {
        var settings = SettingsFor("keypulse_switch_" + Guid.NewGuid().ToString("N"));
        await ExecuteAsync(SettingsFor("postgres"), $"CREATE DATABASE \"{settings.Database}\";");
        return settings;
    }

    internal PostgreSqlApplicationDbContext Open(PostgreSqlConnectionSettings settings, string password = "") =>
        ConfiguredDbContextFactory.CreatePostgreSqlContext(settings, password);

    internal async Task CreateSchemaAsync(PostgreSqlConnectionSettings settings, string password = "")
    {
        await using var context = Open(settings, password);
        await context.Database.MigrateAsync();
        AppMetaStore.EnsureTable(context);
    }

    internal static async Task ExecuteAsync(PostgreSqlConnectionSettings settings, string sql, string password = "")
    {
        await using var connection = new NpgsqlConnection(
            DatabaseConfigurationService.BuildPostgreSqlConnectionString(settings, password)
        );
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private PostgreSqlConnectionSettings SettingsFor(string database) =>
        new()
        {
            Host = "127.0.0.1",
            Port = _port,
            Database = database,
            Username = "keypulse_switch_test",
            SslMode = PostgreSqlSslMode.Disable,
        };

    internal async Task<PostgreSqlConnectionSettings> CreatePasswordDatabaseAsync(string password)
    {
        var settings = await CreateDatabaseAsync();
        var role = "owner_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(
            SettingsFor("postgres"),
            $"CREATE ROLE \"{role}\" LOGIN PASSWORD '{password.Replace("'", "''")}'; ALTER DATABASE \"{settings.Database}\" OWNER TO \"{role}\";"
        );
        // Only this fixture's generated role and cluster are affected; admin fixture connections keep trust.
        var hba = Path.Combine(_directory, "pg_hba.conf");
        await File.WriteAllTextAsync(
            hba,
            $"host all {role} 127.0.0.1/32 scram-sha-256\nhost all {role} ::1/128 scram-sha-256\n"
                + await File.ReadAllTextAsync(hba)
        );
        await ExecuteAsync(SettingsFor("postgres"), "SELECT pg_reload_conf();");
        settings.Username = role;
        return settings;
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_binPath))
            return;
        NpgsqlConnection.ClearAllPools();
        if (_started)
            await RunAsync("pg_ctl.exe", "-D", _directory, "-m", "immediate", "-w", "stop");
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private async Task RunAsync(string executable, params string[] arguments)
    {
        var captureOutput = executable == "initdb.exe";
        var start = new ProcessStartInfo(Path.Combine(_binPath!, executable))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = captureOutput,
            RedirectStandardError = captureOutput,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process =
            Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the isolated database fixture");
        var output = captureOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult("");
        var errors = captureOutput ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{executable} failed: {await output}\n{await errors}");
        await Task.WhenAll(output, errors);
    }
}
