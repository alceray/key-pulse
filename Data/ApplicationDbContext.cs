using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KeyPulse.Models;
using KeyPulse.Services;
using Microsoft.EntityFrameworkCore;

namespace KeyPulse.Data
{
    internal class ApplicationDbContext : DbContext
    {
        public DbSet<USBDeviceInfo> Devices { get; set; }

        private string _databasePath;

        public ApplicationDbContext()
        {
            _databasePath = DataService.GetDatabasePath();
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                optionsBuilder.UseSqlite($"Data Source={_databasePath}");
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<USBDeviceInfo>().ToTable("Devices");
        }
    }
}
