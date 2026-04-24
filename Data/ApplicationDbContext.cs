using System.IO;
using System.Reflection;
using KeyPulse.Models;
using Microsoft.EntityFrameworkCore;

namespace KeyPulse.Data;

public class ApplicationDbContext : DbContext
{
    public DbSet<DeviceInfo> Devices { get; set; }
    public DbSet<DeviceEvent> DeviceEvents { get; set; }

    private static string GetDatabasePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var appName = Assembly.GetExecutingAssembly().GetName().Name ?? "KeyPulse";
        var appFolder = Path.Combine(appData, appName);
        if (!Directory.Exists(appFolder))
            Directory.CreateDirectory(appFolder);
        return Path.Combine(appFolder, "devices.db");
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseLazyLoadingProxies().UseSqlite($"Data Source={GetDatabasePath()}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder
            .Entity<DeviceInfo>()
            .ToTable("Devices")
            .HasIndex(e => e.DeviceId)
            .HasDatabaseName("Idx_Devices_DeviceId");

        modelBuilder.Entity<DeviceEvent>().ToTable("DeviceEvents");

        modelBuilder.Entity<DeviceEvent>().Property(e => e.EventType).HasConversion<string>();

        modelBuilder.Entity<DeviceEvent>().HasIndex(e => e.Timestamp).HasDatabaseName("Idx_DeviceEvents_Timestamp");

        modelBuilder
            .Entity<DeviceEvent>()
            .HasIndex(e => new { e.DeviceId, e.Timestamp })
            .HasDatabaseName("Idx_DeviceEvents_DeviceIdTimestamp");

        modelBuilder
            .Entity<DeviceEvent>()
            .HasIndex(e => new
            {
                e.DeviceId,
                e.Timestamp,
                e.EventType,
            })
            .IsUnique()
            .HasDatabaseName("Idx_DeviceEvents_Unique");
    }
}
