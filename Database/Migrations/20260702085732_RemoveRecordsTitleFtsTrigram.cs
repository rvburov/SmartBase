using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <summary>
    /// Сносит триграммный FTS5-индекс заголовков (records_fts + триггеры синхронизации),
    /// если база успела его получить от отменённой AddRecordsTitleFtsTrigram (см. её комментарий:
    /// минуты бэкафилла при первом старте больших БД и деградация на частых подстроках).
    /// IF EXISTS — безопасно для баз, где индекса не было. Поиск работает LIKE-сканом.
    /// </summary>
    public partial class RemoveRecordsTitleFtsTrigram : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS db_records_fts_ai;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS db_records_fts_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS db_records_fts_au;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS records_fts;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
