using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyPulse.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceUsageFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<TimeSpan>(
                name: "TotalUsage",
                table: "Devices",
                type: "TEXT",
                nullable: false,
                defaultValue: new TimeSpan(0, 0, 0, 0, 0));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TotalUsage",
                table: "Devices");
        }
    }
}
