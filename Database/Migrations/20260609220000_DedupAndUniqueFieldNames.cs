using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class DedupAndUniqueFieldNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Дедуплицируем поля с одинаковым (CollectionId, Name).
            //    Меньший Id сохраняет исходное имя — это поле, которое было создано
            //    первым и, как правило, уже связано с db_field_values. Остальные
            //    дубли получают суффикс " (2)", " (3)" и т.д., чтобы их значения
            //    в db_field_values остались доступны (записи не удаляются).
            migrationBuilder.Sql(@"
                WITH ranked AS (
                    SELECT Id, CollectionId, Name,
                           ROW_NUMBER() OVER (PARTITION BY CollectionId, Name ORDER BY Id) AS rn
                    FROM db_fields
                )
                UPDATE db_fields
                SET Name = (
                    SELECT db_fields.Name || ' (' || r.rn || ')'
                    FROM ranked r WHERE r.Id = db_fields.Id
                )
                WHERE Id IN (SELECT Id FROM ranked WHERE rn > 1);
            ");

            // 2) Меняем индекс CollectionId на составной уникальный (CollectionId, Name).
            migrationBuilder.DropIndex(
                name: "IX_db_fields_CollectionId",
                table: "db_fields");

            migrationBuilder.CreateIndex(
                name: "IX_db_fields_CollectionId_Name",
                table: "db_fields",
                columns: new[] { "CollectionId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down восстанавливает только структуру индексов. Переименованные
            // дубли не возвращаются к исходному имени — это потребовало бы
            // парсинга суффикса и могло бы конфликтовать с пользовательскими
            // правками, сделанными уже после применения миграции.
            migrationBuilder.DropIndex(
                name: "IX_db_fields_CollectionId_Name",
                table: "db_fields");

            migrationBuilder.CreateIndex(
                name: "IX_db_fields_CollectionId",
                table: "db_fields",
                column: "CollectionId");
        }
    }
}
