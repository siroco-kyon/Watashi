using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watashi.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMultipleTrustedDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TrustedDevices_UserId",
                table: "TrustedDevices");

            migrationBuilder.CreateIndex(
                name: "IX_TrustedDevices_UserId",
                table: "TrustedDevices",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TrustedDevices_UserId_MachineName_WindowsUsername_IsRevoked",
                table: "TrustedDevices",
                columns: new[] { "UserId", "MachineName", "WindowsUsername", "IsRevoked" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TrustedDevices_UserId",
                table: "TrustedDevices");

            migrationBuilder.DropIndex(
                name: "IX_TrustedDevices_UserId_MachineName_WindowsUsername_IsRevoked",
                table: "TrustedDevices");

            migrationBuilder.CreateIndex(
                name: "IX_TrustedDevices_UserId",
                table: "TrustedDevices",
                column: "UserId",
                unique: true);
        }
    }
}
