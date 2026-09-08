using KeyPulse.Models;
using KeyPulse.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace KeyPulse.Tests.Data;

/// <summary>
/// Exercises the EF value converters defined in ApplicationDbContext.OnModelCreating by round-tripping
/// entities through the real schema. Save and read use separate context instances so the read-side
/// converter actually runs (rather than a change-tracker cache hit).
/// </summary>
public class DbContextConverterTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();

    public void Dispose() => _db.Dispose();

    // ── DateTime converters (Local<->UTC, second truncation) ───────────────────

    [Fact]
    public void DateTime_LocalInput_TruncatedToSecond_AndRoundTrips()
    {
        var local = new DateTime(2026, 5, 20, 9, 30, 45, DateTimeKind.Local).AddMilliseconds(789);
        int id;
        using (var ctx = _db.CreateContext())
        {
            var e = new DeviceEvent
            {
                DeviceId = "D1",
                EventType = EventTypes.Connected,
                EventTime = local,
            };
            ctx.DeviceEvents.Add(e);
            ctx.SaveChanges();
            id = e.DeviceEventId;
        }

        using (var ctx = _db.CreateContext())
        {
            var e = ctx.DeviceEvents.Single(x => x.DeviceEventId == id);
            e.EventTime.Millisecond.ShouldBe(0); // sub-second dropped
            e.EventTime.ShouldBe(new DateTime(2026, 5, 20, 9, 30, 45, DateTimeKind.Local));
            e.EventTime.Kind.ShouldBe(DateTimeKind.Local); // read side forces Local
        }
    }

    [Fact]
    public void DateTime_UtcInput_RoundTripsToSameInstant()
    {
        var utc = new DateTime(2026, 5, 20, 9, 30, 45, DateTimeKind.Utc);
        int id;
        using (var ctx = _db.CreateContext())
        {
            var e = new DeviceEvent
            {
                DeviceId = "D1",
                EventType = EventTypes.Connected,
                EventTime = utc,
            };
            ctx.DeviceEvents.Add(e);
            ctx.SaveChanges();
            id = e.DeviceEventId;
        }

        using (var ctx = _db.CreateContext())
        {
            var e = ctx.DeviceEvents.Single(x => x.DeviceEventId == id);
            e.EventTime.Kind.ShouldBe(DateTimeKind.Local);
            e.EventTime.ToUniversalTime().ShouldBe(utc); // same instant after the round-trip
        }
    }

    [Fact]
    public void RepeatedDaylightSavingHour_PreservesBothInstantsWhenReadAndSavedAgain()
    {
        var first = new DateTime(2025, 11, 2, 5, 30, 0, DateTimeKind.Utc);
        var second = first.AddHours(1);
        using (var context = _db.CreateContext())
        {
            context.ActivitySnapshots.AddRange(
                new ActivitySnapshot { DeviceId = "D1", Minute = first },
                new ActivitySnapshot { DeviceId = "D1", Minute = second }
            );
            context.SaveChanges();
        }
        using (var context = _db.CreateContext())
        {
            var rows = context.ActivitySnapshots.OrderBy(x => x.ActivitySnapshotId).ToArray();
            rows.Select(x => x.Minute.ToUniversalTime()).ShouldBe(new[] { first, second });
            foreach (var row in rows)
                context.Entry(row).Property(x => x.Minute).IsModified = true;
            context.SaveChanges();
        }
        using (var context = _db.CreateContext())
            context
                .ActivitySnapshots.OrderBy(x => x.ActivitySnapshotId)
                .AsEnumerable()
                .Select(x => x.Minute.ToUniversalTime())
                .ShouldBe(new[] { first, second });
    }

    // ── HourlyInputCount JSON converter (via DailyDeviceStat) ──────────────────

    [Fact]
    public void HourlyInputCount_RoundTrips24Values()
    {
        var hourly = new long[24];
        hourly[9] = 100;
        hourly[23] = 7;
        using (var ctx = _db.CreateContext())
        {
            ctx.DailyDeviceStats.Add(
                new DailyDeviceStat
                {
                    Day = new DateOnly(2026, 5, 20),
                    DeviceId = "D1",
                    HourlyInputCount = hourly,
                }
            );
            ctx.SaveChanges();
        }

        using (var ctx = _db.CreateContext())
        {
            var stat = ctx.DailyDeviceStats.Single();
            stat.HourlyInputCount.Length.ShouldBe(24);
            stat.HourlyInputCount[9].ShouldBe(100);
            stat.HourlyInputCount[23].ShouldBe(7);
        }
    }

    [Fact]
    public void HourlyInputCount_NullOnWrite_ViolatesNotNull()
    {
        // EF bypasses value converters for null CLR values, so SerializeHourlyInputCount's null guard
        // is never reached and the NOT NULL column rejects the row. (The model default is a 24-zero
        // array, so this never happens in practice.)
        using var ctx = _db.CreateContext();
        ctx.DailyDeviceStats.Add(
            new DailyDeviceStat
            {
                Day = new DateOnly(2026, 5, 20),
                DeviceId = "D1",
                HourlyInputCount = null!,
            }
        );

        Should.Throw<DbUpdateException>(() => ctx.SaveChanges());
    }

    [Theory]
    [InlineData("[5,6,7]")] // short -> zero-padded to 24
    [InlineData("[]")] // empty -> 24 zeros
    [InlineData("")] // blank -> 24 zeros
    [InlineData("null")] // json null -> 24 zeros
    public void HourlyInputCount_Deserialize_NormalizesToLength24(string storedJson)
    {
        SeedRawHourlyJson(storedJson);
        using var ctx = _db.CreateContext();
        ctx.DailyDeviceStats.Single().HourlyInputCount.Length.ShouldBe(24);
    }

    [Fact]
    public void HourlyInputCount_Deserialize_ShortJson_PadsWithZeros()
    {
        SeedRawHourlyJson("[5,6,7]");
        using var ctx = _db.CreateContext();
        var stat = ctx.DailyDeviceStats.Single();
        stat.HourlyInputCount[0].ShouldBe(5);
        stat.HourlyInputCount[2].ShouldBe(7);
        stat.HourlyInputCount[3].ShouldBe(0);
    }

    [Fact]
    public void HourlyInputCount_Deserialize_LongJson_TruncatedTo24()
    {
        var json = "[" + string.Join(",", Enumerable.Range(1, 30)) + "]"; // 30 elements
        SeedRawHourlyJson(json);
        using var ctx = _db.CreateContext();
        var stat = ctx.DailyDeviceStats.Single();
        stat.HourlyInputCount.Length.ShouldBe(24);
        stat.HourlyInputCount[0].ShouldBe(1);
        stat.HourlyInputCount[23].ShouldBe(24); // elements 25..30 dropped
    }

    [Fact]
    public void HourlyInputCount_Deserialize_MalformedJson_Throws()
    {
        SeedRawHourlyJson("[1,2,");
        using var ctx = _db.CreateContext();
        Should.Throw<Exception>(() => ctx.DailyDeviceStats.Single());
    }

    private void SeedRawHourlyJson(string json)
    {
        using var ctx = _db.CreateContext();
        ctx.DailyDeviceStats.Add(new DailyDeviceStat { Day = new DateOnly(2026, 5, 20), DeviceId = "D1" });
        ctx.SaveChanges();
        ctx.Database.ExecuteSqlRaw("UPDATE DailyDeviceStats SET HourlyInputCount = {0};", json);
    }
}
