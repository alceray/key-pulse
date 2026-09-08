using KeyPulse.Configuration;
using KeyPulse.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serilog;

namespace KeyPulse.Services;

/// <summary>Prevents two KeyPulse processes from writing to the same PostgreSQL database.</summary>
public sealed class DatabaseInstanceLock : IDisposable
{
    private readonly object _gate = new();
    private NpgsqlConnection? _connection;
    private string? _connectionString;

    public void Acquire(ApplicationDbContext context)
    {
        if (!context.Database.IsNpgsql())
            return;

        Acquire(context.Database.GetConnectionString()!);
    }

    /// <summary>Takes the lock from a connection string, before any database-backed service exists.</summary>
    public void Acquire(string connectionString)
    {
        lock (_gate)
        {
            if (_connection != null && _connectionString == connectionString)
            {
                try
                {
                    VerifyHeld();
                    return;
                }
                catch
                {
                    // A new preflight attempt may reconnect. Verification during a transfer still
                    // fails without reacquiring, so losing the lock cannot commit an unprotected copy.
                    Dispose();
                }
            }

            Dispose();
            var options = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
            var connection = new NpgsqlConnection(options.ConnectionString);
            try
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT pg_try_advisory_lock(@lock_key);";
                command.Parameters.AddWithValue("lock_key", AppConstants.App.PostgreSqlAdvisoryLockKey);
                if (command.ExecuteScalar() is not true)
                    throw new InvalidOperationException(
                        "This PostgreSQL database is already in use by another KeyPulse process"
                    );
            }
            catch
            {
                connection.Dispose();
                throw;
            }

            _connection = connection;
            _connectionString = connectionString;
            Log.Debug("PostgreSQL lock acquired for single-instance access");
        }
    }

    internal void VerifyHeld()
    {
        lock (_gate)
        {
            if (_connection == null)
                throw new InvalidOperationException("Exclusive database access is no longer available");
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            command.ExecuteScalar();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_connection == null)
                return;

            try
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock(@lock_key);";
                command.Parameters.AddWithValue("lock_key", AppConstants.App.PostgreSqlAdvisoryLockKey);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to release the PostgreSQL instance lock");
            }
            finally
            {
                _connection.Dispose();
                _connection = null;
                _connectionString = null;
            }
        }
    }
}
