using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
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
    public partial class RowBackgroundBack : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Bg",
                table: "record_display",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql(@"
                INSERT INTO record_display (CollectionId, RecordId, Visible, Level, Bg)
                SELECT r.CollectionId, r.Id, 1, 1, r.Color
                FROM db_records r WHERE r.Color IS NOT NULL
                ON CONFLICT (CollectionId, RecordId) DO UPDATE SET Bg = excluded.Bg");
            migrationBuilder.Sql("UPDATE db_records SET Color = NULL WHERE Color IS NOT NULL");
        }

        /// <summary>Откат возвращает цвет записям тем же путём (фон строки → db_records.Color)
        /// и убирает колонку.</summary>
        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE db_records SET Color = (
                    SELECT d.Bg FROM record_display d
                    WHERE d.RecordId = db_records.Id AND d.CollectionId = db_records.CollectionId)
                WHERE EXISTS (
                    SELECT 1 FROM record_display d
                    WHERE d.RecordId = db_records.Id AND d.CollectionId = db_records.CollectionId
                      AND d.Bg IS NOT NULL)");

            migrationBuilder.DropColumn(
                name: "Bg",
                table: "record_display");
        }
    }
}
