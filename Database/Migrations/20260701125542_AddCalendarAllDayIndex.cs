using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarAllDayIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_calendar_index_CollectionId_AllDay_StartIso",
                table: "calendar_index",
                columns: new[] { "CollectionId", "AllDay", "StartIso" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_calendar_index_CollectionId_AllDay_StartIso",
                table: "calendar_index");
        }
    }
}
