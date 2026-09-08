using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Serilog;

namespace KeyPulse.Data;

internal static class AppMetaStore
{
    public static void EnsureTable(ApplicationDbContext ctx)
    {
        using var command = CreateCommand(
            ctx,
            "CREATE TABLE IF NOT EXISTS AppMeta (MetaKey TEXT PRIMARY KEY NOT NULL, MetaValue TEXT NOT NULL);"
        );
        command.ExecuteNonQuery();
    }

    public static DateTime? ReadUtc(ApplicationDbContext ctx, string key)
    {
        try
        {
            using var command = CreateCommand(ctx, "SELECT MetaValue FROM AppMeta WHERE MetaKey = @key LIMIT 1;");
            AddParameter(command, "@key", key);

            var value = command.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(value))
                return null;

            if (DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                return parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read AppMeta key {MetaKey}", key);
        }

        return null;
    }

    public static void WriteUtc(ApplicationDbContext ctx, string key, DateTime value)
    {
        Write(ctx, key, value.ToString("O"));
    }

    public static void Delete(ApplicationDbContext ctx, string key)
    {
        try
        {
            using var command = CreateCommand(ctx, "DELETE FROM AppMeta WHERE MetaKey = @key;");
            AddParameter(command, "@key", key);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to clear AppMeta key {MetaKey}", key);
        }
    }

    public static IReadOnlyDictionary<string, string> ReadAll(ApplicationDbContext ctx)
    {
        EnsureTable(ctx);
        return ReadExisting(ctx);
    }

    public static IReadOnlyDictionary<string, string> ReadExisting(ApplicationDbContext ctx)
    {
        using var command = CreateCommand(ctx, "SELECT MetaKey, MetaValue FROM AppMeta;");
        using var reader = command.ExecuteReader();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            values[reader.GetString(0)] = reader.GetString(1);
        return values;
    }

    public static bool TableExists(ApplicationDbContext ctx)
    {
        using var command = CreateCommand(
            ctx,
            ctx.Database.IsSqlite()
                ? "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'AppMeta';"
                : "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'appmeta';"
        );
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    public static void Write(ApplicationDbContext ctx, string key, string value)
    {
        using var command = CreateCommand(
            ctx,
            "INSERT INTO AppMeta (MetaKey, MetaValue) VALUES (@key, @value) "
                + "ON CONFLICT(MetaKey) DO UPDATE SET MetaValue = excluded.MetaValue;"
        );
        AddParameter(command, "@key", key);
        AddParameter(command, "@value", value);
        command.ExecuteNonQuery();
    }

    private static DbCommand CreateCommand(ApplicationDbContext ctx, string sql)
    {
        // The connection belongs to the context and is only borrowed here. Closing it would abort any
        // transaction in progress, and PostgreSQL connections cannot be reopened once disposed.
        var connection = ctx.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            ctx.Database.OpenConnection();

        var command = connection.CreateCommand();
        command.Transaction = ctx.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        return command;
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
