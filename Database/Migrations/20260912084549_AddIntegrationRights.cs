using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrationRights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Схема не меняется: два новых права — это два новых бита маски
            // (IntegrationsSetup = 8192, IntegrationsOutward = 16384).
            //
            // Бэкфилл ставит ОБА всем, у кого маска уже урезана: до этой правки настройка
            // обмена не спрашивала никакого права вовсе, и любой допущенный пользователь мог
            // и завести соединение, и подтвердить исходящее направление. Миграция не должна
            // менять то, что человек может делать, — она заводит ручку, которой у
            // администратора раньше не было. Сузить круг он сузит сам, сняв галки.
            migrationBuilder.Sql("UPDATE db_permissions SET ActionsMask = ActionsMask | 24576");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE db_permissions SET ActionsMask = ActionsMask & ~24576");
        }
    }
}
