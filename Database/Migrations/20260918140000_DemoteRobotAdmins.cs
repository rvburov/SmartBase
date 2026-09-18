using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class DemoteRobotAdmins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Запрет «робот не бывает администратором» появился в службе прав позже самих
            // роботов: до него панель позволяла выдать токену роль администратора. Запрет
            // защищает от новых случаев, но уже выданную роль не снимает — а она опаснее прочих:
            // токен лежит файлом настроек на машине обмена, и администратор из него означает
            // базу, отданную всякому, кто этот файл прочитает.
            //
            // База, где единственным администратором был робот, и так неуправляема: роботы не
            // открывают окон. После разжалования первый открывший ее человек станет
            // администратором — обычное правило базы без админов.
            migrationBuilder.Sql(
                "UPDATE db_users SET IsAdmin = 0 WHERE IsAdmin = 1 AND UserKey LIKE 'robot.%'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Обратного хода нет: вернуть роль означало бы вернуть дыру, а кому именно она была
            // выдана, миграция не знает — снятие ролей не журналируется отдельной таблицей.
        }
    }
}
