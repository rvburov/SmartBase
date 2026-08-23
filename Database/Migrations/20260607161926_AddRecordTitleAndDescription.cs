using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordTitleAndDescription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Добавляем новые колонки на уровне таблицы записей.
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "db_records",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "db_records",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            // 2) Переносим существующие значения системных полей
            //    «Название» / «Описание» из db_field_values в новые колонки.
            //    Берем первое совпадение на запись, чтобы исключить риск дублей.
            migrationBuilder.Sql(@"
                UPDATE db_records
                SET Title = COALESCE((
                    SELECT fv.Value
                    FROM db_field_values fv
                    INNER JOIN db_fields f ON f.Id = fv.FieldId
                    WHERE fv.RecordId = db_records.Id
                      AND f.Name = 'Название'
                    LIMIT 1
                ), '')
                WHERE Title = '';
            ");

            migrationBuilder.Sql(@"
                UPDATE db_records
                SET Description = (
                    SELECT fv.Value
                    FROM db_field_values fv
                    INNER JOIN db_fields f ON f.Id = fv.FieldId
                    WHERE fv.RecordId = db_records.Id
                      AND f.Name = 'Описание'
                    LIMIT 1
                )
                WHERE Description IS NULL;
            ");

            // 3) Удаляем значения этих системных полей из db_field_values.
            migrationBuilder.Sql(@"
                DELETE FROM db_field_values
                WHERE FieldId IN (
                    SELECT Id FROM db_fields WHERE Name IN ('Название', 'Описание')
                );
            ");

            // 4) Удаляем сами системные поля — данные теперь живут в колонках.
            migrationBuilder.Sql(@"
                DELETE FROM db_fields WHERE Name IN ('Название', 'Описание');
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down не восстанавливает системные поля и не возвращает значения
            // обратно в db_field_values — откат поддерживается только на уровне
            // схемы (откатить миграцию имеет смысл лишь до начала ее применения
            // на проде; для отката с данными нужен бэкап).
            migrationBuilder.DropColumn(
                name: "Description",
                table: "db_records");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "db_records");
        }
    }
}
