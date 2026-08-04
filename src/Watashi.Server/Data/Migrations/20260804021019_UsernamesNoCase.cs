using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watashi.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class UsernamesNoCase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 既存 DB に大小文字だけが異なるユーザーがいる場合、どちらかを暗黙に
            // 採用せず移行を止める。管理者が衝突を解消してから再実行する必要がある。
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX "IX_Users_Username_NoCase_MigrationGuard"
                ON "Users" ("Username" COLLATE NOCASE);
                """);
            migrationBuilder.Sql("DROP INDEX \"IX_Users_Username_NoCase_MigrationGuard\";");

            migrationBuilder.AlterColumn<string>(
                name: "Username",
                table: "Users",
                type: "TEXT",
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Username",
                table: "Users",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldCollation: "NOCASE");
        }
    }
}
