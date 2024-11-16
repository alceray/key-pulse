using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyPulse.Migrations
{
    /// <inheritdoc />
    public partial class AddConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsConnected",
                table: "Devices");

            migrationBuilder.CreateTable(
                name: "Connections",
                columns: table => new
                {
                    ConnectionID = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConnectedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DisconnectedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeviceID = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Connections", x => x.ConnectionID);
                    table.ForeignKey(
                        name: "FK_Connections_Devices_DeviceID",
                        column: x => x.DeviceID,
                        principalTable: "Devices",
                        principalColumn: "DeviceID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Connections_DeviceID",
                table: "Connections",
                column: "DeviceID");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Connections");

            migrationBuilder.AddColumn<bool>(
                name: "IsConnected",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }
    }
}
