using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watashi.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPermissionValidity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExpiresAt",
                table: "UserPermissions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "UserPermissions",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TicketNumber",
                table: "UserPermissions",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ValidFrom",
                table: "UserPermissions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "UserPermissions");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "UserPermissions");

            migrationBuilder.DropColumn(
                name: "TicketNumber",
                table: "UserPermissions");

            migrationBuilder.DropColumn(
                name: "ValidFrom",
                table: "UserPermissions");
        }
    }
}
