using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class ExtendCalendarAllDayIndexTiebreak : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_calendar_index_CollectionId_AllDay_StartIso",
                table: "calendar_index");

            migrationBuilder.CreateIndex(
                name: "IX_calendar_index_CollectionId_AllDay_StartIso_RecordId_FieldId",
                table: "calendar_index",
                columns: new[] { "CollectionId", "AllDay", "StartIso", "RecordId", "FieldId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_calendar_index_CollectionId_AllDay_StartIso_RecordId_FieldId",
                table: "calendar_index");

            migrationBuilder.CreateIndex(
                name: "IX_calendar_index_CollectionId_AllDay_StartIso",
                table: "calendar_index",
                columns: new[] { "CollectionId", "AllDay", "StartIso" });
        }
    }
}
