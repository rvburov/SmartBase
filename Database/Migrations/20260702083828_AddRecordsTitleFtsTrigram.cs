using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <summary>
    /// ОТМЕНЕНА (пустая). Здесь строился триграммный FTS5-индекс заголовков (records_fts +
    /// триггеры) для мгновенного поиска подстроки, но на практике решение не подошло:
    /// разовый бэкафилл на больших БД занимал минуты при первом старте, а на ЧАСТЫХ подстроках
    /// (совпадающих с миллионами записей) FTS-путь оказывался медленнее LIKE-скана. Поиск
    /// остаётся фоновым LIKE-сканом (SearchCalendarEventsAsync). Базам, успевшим применить
    /// эту миграцию, объекты сносит RemoveRecordsTitleFtsTrigram; остальным Up() ничего не делает.
    /// </summary>
    public partial class AddRecordsTitleFtsTrigram : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
