using System.Security.Cryptography;
using System.Text.Json;
using KeyPulse.Data;
using Microsoft.EntityFrameworkCore;

namespace KeyPulse.Services;

internal sealed record DatabaseHistoryFingerprint(
    HistoryTableFingerprint Devices,
    HistoryTableFingerprint Events,
    HistoryTableFingerprint Snapshots,
    HistoryTableFingerprint DailyStats,
    HistoryTableFingerprint Projections
)
{
    internal static async Task<DatabaseHistoryFingerprint> ReadAsync(
        ApplicationDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        // Device ids must compare identically across database collations. The history tables are
        // copied in primary-key order, so their generated ids preserve the source sequence.
        var devices = await context.Devices.AsNoTracking().ToListAsync(cancellationToken);
        var deviceRows = devices
            .OrderBy(x => x.DeviceId, StringComparer.Ordinal)
            .Select(x =>
            {
                x.SessionStartedAt = null;
                return new
                {
                    x.DeviceId,
                    x.DeviceName,
                    x.DeviceType,
                    x.IsHiddenFromDisplay,
                    x.TotalConnectionSeconds,
                    x.TotalInputCount,
                    x.DaysConnected,
                    LastConnectedAt = UtcTicks(x.LastConnectedAt),
                    LastSeenAt = UtcTicks(x.LastSeenAt),
                };
            });
        using var deviceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var row in deviceRows)
            deviceHash.AppendData(JsonSerializer.SerializeToUtf8Bytes(row));

        return new DatabaseHistoryFingerprint(
            new HistoryTableFingerprint(devices.Count, Convert.ToHexString(deviceHash.GetHashAndReset())),
            await ReadTableAsync(
                context.DeviceEvents.AsNoTracking().OrderBy(x => x.DeviceEventId),
                x => new
                {
                    x.DeviceId,
                    EventTime = UtcTicks(x.EventTime),
                    x.EventType,
                },
                cancellationToken
            ),
            await ReadTableAsync(
                context.ActivitySnapshots.AsNoTracking().OrderBy(x => x.ActivitySnapshotId),
                x => new
                {
                    x.DeviceId,
                    Minute = UtcTicks(x.Minute),
                    x.Keystrokes,
                    x.MouseClicks,
                    x.MouseMovementSeconds,
                    x.ActiveSeconds,
                },
                cancellationToken
            ),
            await ReadTableAsync(
                context.DailyDeviceStats.AsNoTracking().OrderBy(x => x.DailyDeviceStatId),
                x => new
                {
                    x.DeviceId,
                    Day = x.Day.DayNumber,
                    x.SessionCount,
                    x.ConnectionSeconds,
                    x.Keystrokes,
                    x.MouseClicks,
                    x.MouseMovementSeconds,
                    x.ActiveSeconds,
                    x.HourlyInputCount,
                    UpdatedAt = UtcTicks(x.UpdatedAt),
                },
                cancellationToken
            ),
            await ReadTableAsync(
                context.ActivityProjections.AsNoTracking().OrderBy(x => x.ActivityProjectionId),
                x => new { x.DeviceId, Minute = UtcTicks(x.Minute) },
                cancellationToken
            )
        );
    }

    private static async Task<HistoryTableFingerprint> ReadTableAsync<T>(
        IQueryable<T> query,
        Func<T, object> persistedValues,
        CancellationToken cancellationToken
    )
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long count = 0;
        await foreach (var row in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(persistedValues(row)));
            count++;
        }
        return new HistoryTableFingerprint(count, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static long? UtcTicks(DateTime? value) => value?.ToUniversalTime().Ticks;
}

internal sealed record HistoryTableFingerprint(long Count, string Hash);
