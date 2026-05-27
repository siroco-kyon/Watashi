using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watashi.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionNodeGateway : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GatewayNodeId",
                table: "ExecutionNodes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExecutionNodes_GatewayNodeId",
                table: "ExecutionNodes",
                column: "GatewayNodeId");

            migrationBuilder.AddForeignKey(
                name: "FK_ExecutionNodes_ExecutionNodes_GatewayNodeId",
                table: "ExecutionNodes",
                column: "GatewayNodeId",
                principalTable: "ExecutionNodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ExecutionNodes_ExecutionNodes_GatewayNodeId",
                table: "ExecutionNodes");

            migrationBuilder.DropIndex(
                name: "IX_ExecutionNodes_GatewayNodeId",
                table: "ExecutionNodes");

            migrationBuilder.DropColumn(
                name: "GatewayNodeId",
                table: "ExecutionNodes");
        }
    }
}
