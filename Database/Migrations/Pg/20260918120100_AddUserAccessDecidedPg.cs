using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <inheritdoc />
    public partial class AddUserAccessDecidedPg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Пара к AddUserAccessDecided (SQLite): признак «решение по допуску принимали».
            // Логические поля пользователей на сервере хранятся целым числом (как isadmin
            // и isapproved), поэтому и здесь integer, а не boolean.
            migrationBuilder.AddColumn<int>(
                name: "accessdecided",
                table: "db_users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                "UPDATE db_users SET accessdecided = 1 WHERE isapproved = 1 OR isadmin = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "accessdecided", table: "db_users");
        }
    }
}
