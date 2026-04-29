using KeyPulse.Configuration;
using KeyPulse.Models;
using Microsoft.EntityFrameworkCore;

namespace KeyPulse.Data;

public class ApplicationDbContext : DbContext
{
    public DbSet<Device> Devices { get; set; }
    public DbSet<DeviceEvent> DeviceEvents { get; set; }
    public DbSet<ActivitySnapshot> ActivitySnapshots { get; set; }

    private static string GetDatabasePath()
    {
        return AppDataPaths.GetPath(AppConstants.Paths.DatabaseFileName);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseLazyLoadingProxies().UseSqlite($"Data Source={GetDatabasePath()}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder
            .Entity<Device>()
            .ToTable("Devices")
            .HasIndex(e => e.DeviceId)
            .HasDatabaseName("Idx_Devices_DeviceId");

        modelBuilder.Entity<Device>().Property(e => e.DeviceType).HasConversion<string>();

        modelBuilder.Entity<DeviceEvent>().ToTable("DeviceEvents");

        modelBuilder.Entity<DeviceEvent>().Property(e => e.EventType).HasConversion<string>();

        modelBuilder.Entity<DeviceEvent>().HasIndex(e => e.EventTime).HasDatabaseName("Idx_DeviceEvents_EventTime");

        modelBuilder
            .Entity<DeviceEvent>()
            .HasIndex(e => new { e.DeviceId, e.EventTime })
            .HasDatabaseName("Idx_DeviceEvents_DeviceIdEventTime");

        modelBuilder
            .Entity<DeviceEvent>()
            .HasIndex(e => new
            {
                e.DeviceId,
                e.EventTime,
                e.EventType,
            })
            .IsUnique()
            .HasDatabaseName("Idx_DeviceEvents_Unique");

        modelBuilder.Entity<ActivitySnapshot>().ToTable("ActivitySnapshots");

        modelBuilder
            .Entity<ActivitySnapshot>()
            .HasIndex(e => new { e.DeviceId, e.Minute })
            .HasDatabaseName("Idx_ActivitySnapshots_DeviceIdMinute")
            .IsUnique();

        modelBuilder.Entity<ActivitySnapshot>().HasIndex(e => e.Minute).HasDatabaseName("Idx_ActivitySnapshots_Minute");
    }
}
