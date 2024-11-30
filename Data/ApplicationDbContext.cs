using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KeyPulse.Models;
using KeyPulse.Services;
using Microsoft.EntityFrameworkCore;

namespace KeyPulse.Data
{
    public class ApplicationDbContext : DbContext
    {
        public DbSet<DeviceInfo> Devices { get; set; }
        public DbSet<DeviceEvent> DeviceEvents { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            var databasePath = DataService.GetDatabasePath();
            optionsBuilder
                .UseLazyLoadingProxies()
                .UseSqlite($"Data Source={databasePath}");
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<DeviceInfo>()
                .ToTable("Devices")
                .HasIndex(e => e.DeviceId)
                .HasDatabaseName("Idx_Devices_DeviceId");

            modelBuilder.Entity<DeviceEvent>()
                .ToTable("DeviceEvents");

            modelBuilder.Entity<DeviceEvent>()
                .HasIndex(e => e.Timestamp)
                .HasDatabaseName("Idx_DeviceEvents_Timestamp");

            modelBuilder.Entity<DeviceEvent>()
                .HasIndex(e => new { e.DeviceId, e.Timestamp })
                .HasDatabaseName("Idx_DeviceEvents_DeviceIdTimestamp");

            modelBuilder.Entity<DeviceEvent>()
                .HasIndex(e => new { e.DeviceId, e.Timestamp, e.EventType })
                .IsUnique()
                .HasDatabaseName("Idx_DeviceEvents_Unique");
        }
    }
}
