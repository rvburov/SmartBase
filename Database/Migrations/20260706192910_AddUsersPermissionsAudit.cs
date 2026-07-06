using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddUsersPermissionsAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "change_log",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Ts = table.Column<string>(type: "TEXT", nullable: false),
                    UserKey = table.Column<string>(type: "TEXT", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", nullable: false),
                    Action = table.Column<string>(type: "TEXT", nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", nullable: false),
                    TargetName = table.Column<string>(type: "TEXT", nullable: false),
                    CollectionName = table.Column<string>(type: "TEXT", nullable: true),
                    Count = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_change_log", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "db_meta",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_meta", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "db_users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserKey = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    IsAdmin = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsApproved = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "db_permissions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    CollectionId = table.Column<int>(type: "INTEGER", nullable: true),
                    ActionsMask = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_permissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_db_permissions_db_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "db_collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_db_permissions_db_users_UserId",
                        column: x => x.UserId,
                        principalTable: "db_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_db_permissions_CollectionId",
                table: "db_permissions",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_db_permissions_UserId_CollectionId",
                table: "db_permissions",
                columns: new[] { "UserId", "CollectionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_db_users_UserKey",
                table: "db_users",
                column: "UserKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "change_log");

            migrationBuilder.DropTable(
                name: "db_meta");

            migrationBuilder.DropTable(
                name: "db_permissions");

            migrationBuilder.DropTable(
                name: "db_users");
        }
    }
}
