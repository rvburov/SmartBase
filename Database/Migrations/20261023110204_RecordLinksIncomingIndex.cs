using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class RecordLinksIncomingIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Сначала новый индекс, потом прежний: запросы «кто ссылается» не остаются без индекса.
            // Строки не трогаются. Прежний (ToRecordId) — префикс нового, он больше не нужен.
            migrationBuilder.CreateIndex(
                name: "IX_db_record_links_ToRecordId_TargetDbId_FieldId_FromRecordId",
                table: "db_record_links",
                columns: new[] { "ToRecordId", "TargetDbId", "FieldId", "FromRecordId" });

            migrationBuilder.DropIndex(
                name: "IX_db_record_links_ToRecordId",
                table: "db_record_links");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_db_record_links_ToRecordId_TargetDbId_FieldId_FromRecordId",
                table: "db_record_links");

            migrationBuilder.CreateIndex(
                name: "IX_db_record_links_ToRecordId",
                table: "db_record_links",
                column: "ToRecordId");
        }
    }
}
