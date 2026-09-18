using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <inheritdoc />
    public partial class DemoteRobotAdminsPg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Пара к DemoteRobotAdmins (SQLite): роль администратора, выданная токену до запрета,
            // снимается. Причина та же — токен лежит файлом настроек на машине обмена.
            migrationBuilder.Sql(
                "UPDATE db_users SET isadmin = 0 WHERE isadmin = 1 AND userkey LIKE 'robot.%'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Обратного хода нет — см. пару для SQLite.
        }
    }
}
