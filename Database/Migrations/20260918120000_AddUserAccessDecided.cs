using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAccessDecided : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Третье состояние допуска. «Ждет допуска» и «отозван» до сих пор выглядели в базе
            // одинаково (IsApproved=0), а это разные вещи: по первому администратор еще не
            // решал, по второму решил отказом. В панели они теперь называются разными словами,
            // и отличить их можно только отдельным признаком.
            migrationBuilder.AddColumn<bool>(
                name: "AccessDecided",
                table: "db_users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Бэкфилл: решение точно принимали там, где допуск есть, — его выдал администратор,
            // дал верный пароль или человек завел базу сам. Остальные строки остаются
            // «ожидающими»: именно так панель показывала их и до этой миграции, и придумывать
            // за администратора отказ, которого он не делал, миграция не вправе.
            migrationBuilder.Sql(
                "UPDATE db_users SET AccessDecided = 1 WHERE IsApproved = 1 OR IsAdmin = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "AccessDecided", table: "db_users");
        }
    }
}
