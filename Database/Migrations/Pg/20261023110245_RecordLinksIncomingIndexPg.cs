using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <inheritdoc />
    public partial class RecordLinksIncomingIndexPg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Сначала новый индекс, потом прежний: запросы «кто ссылается» не остаются без индекса.
            // Строки не трогаются. Прежний (ToRecordId) — префикс нового, он больше не нужен.
            migrationBuilder.CreateIndex(
                name: "IX_db_record_links_torecordid_targetdbid_fieldid_fromrecordid",
                table: "db_record_links",
                columns: new[] { "torecordid", "targetdbid", "fieldid", "fromrecordid" });

            migrationBuilder.DropIndex(
                name: "IX_db_record_links_torecordid",
                table: "db_record_links");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_db_record_links_torecordid_targetdbid_fieldid_fromrecordid",
                table: "db_record_links");

            migrationBuilder.CreateIndex(
                name: "IX_db_record_links_torecordid",
                table: "db_record_links",
                column: "torecordid");
        }
    }
}
