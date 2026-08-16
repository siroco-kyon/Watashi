using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watashi.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRemoteTrash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RemoteTrashEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeletedByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    DeletedByUsername = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    HostId = table.Column<int>(type: "INTEGER", nullable: false),
                    ShareId = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    TrashPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    ItemType = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    OriginalModifiedAtUtc = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DeletedAt = table.Column<string>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    RestoredAt = table.Column<string>(type: "TEXT", nullable: true),
                    RestoredByUserId = table.Column<int>(type: "INTEGER", nullable: true),
                    RestoredPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    PurgedAt = table.Column<string>(type: "TEXT", nullable: true),
                    PurgedByUserId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteTrashEntries", x => x.Id);
                    table.CheckConstraint("CK_RemoteTrashEntry_Size", "SizeBytes >= 0");
                    table.CheckConstraint("CK_RemoteTrashEntry_Status", "Status IN ('trashing', 'active', 'restoring', 'restored', 'purging', 'purged', 'failed')");
                    table.ForeignKey(
                        name: "FK_RemoteTrashEntries_CifsShares_ShareId",
                        column: x => x.ShareId,
                        principalTable: "CifsShares",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RemoteTrashEntries_Users_DeletedByUserId",
                        column: x => x.DeletedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RemoteTrashEntries_Users_PurgedByUserId",
                        column: x => x.PurgedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_RemoteTrashEntries_Users_RestoredByUserId",
                        column: x => x.RestoredByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteTrashEntries_DeletedByUserId_Status_DeletedAt",
                table: "RemoteTrashEntries",
                columns: new[] { "DeletedByUserId", "Status", "DeletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteTrashEntries_PurgedByUserId",
                table: "RemoteTrashEntries",
                column: "PurgedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteTrashEntries_RestoredByUserId",
                table: "RemoteTrashEntries",
                column: "RestoredByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteTrashEntries_ShareId_Status_DeletedAt",
                table: "RemoteTrashEntries",
                columns: new[] { "ShareId", "Status", "DeletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteTrashEntries_Status_ExpiresAt",
                table: "RemoteTrashEntries",
                columns: new[] { "Status", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RemoteTrashEntries");
        }
    }
}
