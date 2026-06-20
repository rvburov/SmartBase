using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordCollectionPositionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_db_records_CollectionId",
                table: "db_records");

            migrationBuilder.CreateIndex(
                name: "IX_db_records_CollectionId_Position",
                table: "db_records",
                columns: new[] { "CollectionId", "Position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_db_records_CollectionId_Position",
                table: "db_records");

            migrationBuilder.CreateIndex(
                name: "IX_db_records_CollectionId",
                table: "db_records",
                column: "CollectionId");
        }
    }
}
