using System.IO;
using KeyPulse.Configuration;
using Microsoft.Data.Sqlite;
using Serilog;

namespace KeyPulse.Services;

internal static class SqliteHistoryFile
{
    internal static string GetStagingPath(string databasePath, string switchId)
    {
        if (!Guid.TryParse(switchId, out var id))
            throw new InvalidOperationException("The pending database change has an invalid identifier");
        return databasePath + "." + id.ToString("N") + ".import";
    }

    internal static void DeleteStaging(string stagingPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            File.Delete(stagingPath + suffix);
    }

    internal static Task PublishAsync(
        string stagingPath,
        string databasePath,
        CancellationToken cancellationToken = default,
        Action? ensureExclusiveAccess = null
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Preflight may have inspected the local database with a pooled context. No capture services
        // exist yet, so releasing these handles cannot interrupt a running writer.
        SqliteConnection.ClearAllPools();
        try
        {
            PrepareStandalone(stagingPath);
            if (File.Exists(databasePath))
            {
                CreateVerifiedBackup(databasePath);
                PrepareStandalone(databasePath);
                cancellationToken.ThrowIfCancellationRequested();
                ensureExclusiveAccess?.Invoke();
                File.Replace(stagingPath, databasePath, null);
            }
            else
            {
                if (File.Exists(databasePath + "-wal") || File.Exists(databasePath + "-shm"))
                    throw new IOException(
                        "The local database has orphaned journal files and cannot be replaced safely"
                    );
                cancellationToken.ThrowIfCancellationRequested();
                ensureExclusiveAccess?.Invoke();
                File.Move(stagingPath, databasePath);
            }
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        {
            throw DatabaseInUse(databasePath, ex);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
        {
            throw DatabaseInUse(databasePath, ex);
        }
        return Task.CompletedTask;
    }

    internal static string CreateVerifiedBackup(string databasePath)
    {
        var directory = Path.Combine(
            Path.GetDirectoryName(databasePath)!,
            AppConstants.Paths.DatabaseBackupsDirectoryName
        );
        Directory.CreateDirectory(directory);
        var backupPath = Path.Combine(
            directory,
            $"{Path.GetFileNameWithoutExtension(databasePath)}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.pre-import.db"
        );

        using (var source = Open(databasePath))
        {
            using var snapshot = source.BeginTransaction(deferred: true);
            using (var start = source.CreateCommand())
            {
                start.Transaction = snapshot;
                start.CommandText = "SELECT COUNT(*) FROM sqlite_schema;";
                start.ExecuteScalar();
            }
            using (var backup = Open(backupPath, SqliteOpenMode.ReadWriteCreate))
                source.BackupDatabase(backup);

            using var command = source.CreateCommand();
            command.Transaction = snapshot;
            command.CommandText = "ATTACH DATABASE $path AS history_backup;";
            command.Parameters.AddWithValue("$path", backupPath);
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = "PRAGMA history_backup.integrity_check;";
            if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
                throw new IOException("The SQLite backup failed its integrity check");

            // Compare the original schema and every table, including older frozen databases whose
            // columns may predate the current EF model. Both reads use the backed-up snapshot.
            VerifyTable(command, "sqlite_schema");
            command.CommandText = "SELECT name FROM main.sqlite_schema WHERE type = 'table';";
            var tables = new List<string>();
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    tables.Add(reader.GetString(0));
            foreach (var table in tables)
                VerifyTable(command, table);
            snapshot.Commit();
        }
        PrepareStandalone(backupPath);
        Log.Information("Local database backup verified at {BackupPath}", backupPath);
        return backupPath;
    }

    private static void VerifyTable(SqliteCommand command, string table)
    {
        var quoted = '"' + table.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
        command.CommandText = $"""
            SELECT (SELECT COUNT(*) FROM main.{quoted}) != (SELECT COUNT(*) FROM history_backup.{quoted})
                OR EXISTS (SELECT * FROM main.{quoted} EXCEPT SELECT * FROM history_backup.{quoted})
                OR EXISTS (SELECT * FROM history_backup.{quoted} EXCEPT SELECT * FROM main.{quoted});
            """;
        if (Convert.ToInt64(command.ExecuteScalar()) != 0)
            throw new IOException("The SQLite backup did not match the local history");
    }

    private static void PrepareStandalone(string path)
    {
        using (var connection = Open(path))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read() || reader.GetInt32(0) != 0 || reader.GetInt32(1) != reader.GetInt32(2))
                    throw DatabaseInUse(path);
            }
            command.CommandText = "PRAGMA journal_mode=DELETE;";
            if (!string.Equals(command.ExecuteScalar() as string, "delete", StringComparison.OrdinalIgnoreCase))
                throw DatabaseInUse(path);
        }
        if (File.Exists(path + "-wal") || File.Exists(path + "-shm"))
            throw DatabaseInUse(path);
    }

    private static IOException DatabaseInUse(string path, Exception? innerException = null) =>
        new(
            $"The local SQLite database is in use: {path}. "
                + "Disconnect it in database tools such as Rider or DB Browser for SQLite, then choose Retry. "
                + "The local history has not been replaced.",
            innerException
        );

    private static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = mode,
                Pooling = false,
                DefaultTimeout = 5,
            }.ConnectionString
        );
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}
