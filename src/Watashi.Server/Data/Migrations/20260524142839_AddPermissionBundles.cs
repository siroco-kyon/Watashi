using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watashi.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPermissionBundles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PermissionBundles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionBundles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermissionBundles_Users_CreatedBy",
                        column: x => x.CreatedBy,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PermissionBundleEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BundleId = table.Column<int>(type: "INTEGER", nullable: false),
                    ShareId = table.Column<int>(type: "INTEGER", nullable: false),
                    TemplateId = table.Column<int>(type: "INTEGER", nullable: false),
                    AllowedPath = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionBundleEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PermissionBundleEntries_CifsShares_ShareId",
                        column: x => x.ShareId,
                        principalTable: "CifsShares",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PermissionBundleEntries_PermissionBundles_BundleId",
                        column: x => x.BundleId,
                        principalTable: "PermissionBundles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PermissionBundleEntries_PermissionTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "PermissionTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PermissionBundleEntries_BundleId",
                table: "PermissionBundleEntries",
                column: "BundleId");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionBundleEntries_ShareId",
                table: "PermissionBundleEntries",
                column: "ShareId");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionBundleEntries_TemplateId",
                table: "PermissionBundleEntries",
                column: "TemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionBundles_CreatedBy",
                table: "PermissionBundles",
                column: "CreatedBy");

            migrationBuilder.CreateIndex(
                name: "IX_PermissionBundles_Name",
                table: "PermissionBundles",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PermissionBundleEntries");

            migrationBuilder.DropTable(
                name: "PermissionBundles");
        }
    }
}
