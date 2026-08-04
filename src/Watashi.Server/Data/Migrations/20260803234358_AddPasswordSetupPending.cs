using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watashi.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordSetupPending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPasswordSetupPending",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PasswordSetupExpiresAt",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WindowsAccountName",
                table: "Users",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPasswordSetupPending",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PasswordSetupExpiresAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "WindowsAccountName",
                table: "Users");
        }
    }
}
