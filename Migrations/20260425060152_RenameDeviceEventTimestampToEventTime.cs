using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyPulse.Migrations
{
    /// <inheritdoc />
    public partial class RenameDeviceEventTimestampToEventTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Timestamp",
                table: "DeviceEvents",
                newName: "EventTime");

            migrationBuilder.RenameIndex(
                name: "Idx_DeviceEvents_Timestamp",
                table: "DeviceEvents",
                newName: "Idx_DeviceEvents_EventTime");

            migrationBuilder.RenameIndex(
                name: "Idx_DeviceEvents_DeviceIdTimestamp",
                table: "DeviceEvents",
                newName: "Idx_DeviceEvents_DeviceIdEventTime");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "EventTime",
                table: "DeviceEvents",
                newName: "Timestamp");

            migrationBuilder.RenameIndex(
                name: "Idx_DeviceEvents_EventTime",
                table: "DeviceEvents",
                newName: "Idx_DeviceEvents_Timestamp");

            migrationBuilder.RenameIndex(
                name: "Idx_DeviceEvents_DeviceIdEventTime",
                table: "DeviceEvents",
                newName: "Idx_DeviceEvents_DeviceIdTimestamp");
        }
    }
}
