using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KeyPulse.Migrations
{
    /// <inheritdoc />
    public partial class TrackMouseActiveSeconds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "MouseMoved",
                table: "ActivitySnapshots",
                newName: "MouseActiveSeconds");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "MouseActiveSeconds",
                table: "ActivitySnapshots",
                newName: "MouseMoved");
        }
    }
}
