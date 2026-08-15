using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddUserLastSeen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastSeenAt",
                table: "db_users",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // Бэкфилл: про старые строки известно только первое открытие базы — с него и
            // начинаем отсчёт активности (иначе колонка панели была бы сплошь пустой,
            // пока каждый пользователь не откроет базу заново).
            migrationBuilder.Sql("UPDATE db_users SET LastSeenAt = CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSeenAt",
                table: "db_users");
        }
    }
}
