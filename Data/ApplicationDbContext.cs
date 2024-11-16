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
        public DbSet<Connection> Connections { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            var databasePath = DataService.GetDatabasePath();
            optionsBuilder
                .UseLazyLoadingProxies()
                .UseSqlite($"Data Source={databasePath}");
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<DeviceInfo>().ToTable("Devices");
            modelBuilder.Entity<Connection>().ToTable("Connections");
        }
    }
}
