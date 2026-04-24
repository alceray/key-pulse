using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyPulse.Migrations
{
    /// <inheritdoc />
    public partial class AddActivitySnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivitySnapshots",
                columns: table => new
                {
                    ActivitySnapshotId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: false),
                    Minute = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Keystrokes = table.Column<int>(type: "INTEGER", nullable: false),
                    MouseClicks = table.Column<int>(type: "INTEGER", nullable: false),
                    MouseMoved = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivitySnapshots", x => x.ActivitySnapshotId);
                });

            migrationBuilder.CreateIndex(
                name: "Idx_ActivitySnapshots_DeviceIdMinute",
                table: "ActivitySnapshots",
                columns: new[] { "DeviceId", "Minute" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "Idx_ActivitySnapshots_Minute",
                table: "ActivitySnapshots",
                column: "Minute");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivitySnapshots");
        }
    }
}
