using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <summary>
    /// Возвращаем локальный фон строки и убираем сквозной цвет записи.
    ///
    /// Прошлая итерация оставила один уровень заливки — цвет самой записи (db_records.Color),
    /// видный ещё и в календаре, задачах, «Списке». В работе это оказалось не тем: заливку
    /// строки пользователь читает как оформление таблицы, а «пометка записи» вылезала
    /// полосками в разделах, где её не ждут. Фон строки снова живёт в record_display рядом
    /// с видимостью, уровнем и цветом текста — и наружу не видён.
    ///
    /// Уже проставленные цвета записей НЕ теряются: они переезжают в фон строки той
    /// коллекции, где запись лежит, а db_records.Color зануляется. Строка record_display
    /// могла уже существовать (скрытая запись, цвет текста) — поэтому upsert, а не INSERT.
    ///
    /// Саму колонку db_records.Color не удаляем: DropColumn на SQLite — перестройка
    /// таблицы, а это самая большая таблица базы (миллионы строк). Пустая колонка стоит
    /// дешевле; см. комментарий в DbRecordEntity.
    ///
    /// Дешёвая миграция: ADD COLUMN + один проход по записям С ЦВЕТОМ (их единицы).
    /// </summary>
    /// <inheritdoc />
    public partial class RowBackgroundBackPg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bg",
                table: "record_display",
                type: "text",
                nullable: true);

            // visible здесь integer (bool→int в PG-маппинге), поэтому 1, а не true.
            migrationBuilder.Sql(@"
                INSERT INTO record_display (collectionid, recordid, visible, level, bg)
                SELECT r.collectionid, r.id, 1, 1, r.color
                FROM db_records r WHERE r.color IS NOT NULL
                ON CONFLICT (collectionid, recordid) DO UPDATE SET bg = excluded.bg");
            migrationBuilder.Sql("UPDATE db_records SET color = NULL WHERE color IS NOT NULL");
        }

        /// <summary>Откат возвращает цвет записям тем же путём (фон строки → db_records.color)
        /// и убирает колонку.</summary>
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE db_records r SET color = d.bg
                FROM record_display d
                WHERE d.recordid = r.id AND d.collectionid = r.collectionid AND d.bg IS NOT NULL");

            migrationBuilder.DropColumn(
                name: "bg",
                table: "record_display");
        }
    }
}
