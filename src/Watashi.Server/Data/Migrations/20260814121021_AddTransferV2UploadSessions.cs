using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watashi.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTransferV2UploadSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UploadSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    HostId = table.Column<int>(type: "INTEGER", nullable: false),
                    ShareId = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    TempPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    IdempotencyKeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TotalSize = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpectedSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UploadedOffset = table.Column<long>(type: "INTEGER", nullable: false),
                    Overwrite = table.Column<bool>(type: "INTEGER", nullable: false),
                    TargetExisted = table.Column<bool>(type: "INTEGER", nullable: false),
                    TargetSize = table.Column<long>(type: "INTEGER", nullable: true),
                    TargetModifiedAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CommittedETag = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<string>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UploadSessions", x => x.Id);
                    table.CheckConstraint("CK_UploadSession_Offset", "UploadedOffset >= 0 AND UploadedOffset <= TotalSize");
                    table.CheckConstraint("CK_UploadSession_Size", "TotalSize >= 0");
                    table.CheckConstraint("CK_UploadSession_Status", "Status IN ('active', 'committing', 'completed', 'cancelled', 'expired', 'failed')");
                    table.ForeignKey(
                        name: "FK_UploadSessions_CifsShares_ShareId",
                        column: x => x.ShareId,
                        principalTable: "CifsShares",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UploadSessions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_ShareId",
                table: "UploadSessions",
                column: "ShareId");

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_Status_ExpiresAt",
                table: "UploadSessions",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UploadSessions_UserId_IdempotencyKeyHash",
                table: "UploadSessions",
                columns: new[] { "UserId", "IdempotencyKeyHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UploadSessions");
        }
    }
}
