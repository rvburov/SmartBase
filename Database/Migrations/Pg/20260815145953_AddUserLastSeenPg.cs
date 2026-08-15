using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <inheritdoc />
    public partial class AddUserLastSeenPg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "lastseenat",
                table: "db_users",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Бэкфилл: про старые строки известно только первое открытие базы — с него и
            // начинаем отсчёт активности (см. парную SQLite-миграцию AddUserLastSeen).
            migrationBuilder.Sql("UPDATE db_users SET lastseenat = createdat");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "lastseenat",
                table: "db_users");
        }
    }
}
