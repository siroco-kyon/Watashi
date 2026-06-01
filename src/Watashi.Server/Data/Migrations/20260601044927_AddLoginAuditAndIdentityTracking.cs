using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watashi.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLoginAuditAndIdentityTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AuditLog_Result",
                table: "AuditLogs");

            migrationBuilder.AddColumn<string>(
                name: "LastMachineName",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastWindowsUsername",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AuditLog_Result",
                table: "AuditLogs",
                sql: "Result IN ('success', 'failure', 'warning')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AuditLog_Result",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "LastMachineName",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastWindowsUsername",
                table: "Users");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AuditLog_Result",
                table: "AuditLogs",
                sql: "Result IN ('success', 'failure')");
        }
    }
}
