using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddCrossDbLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_db_record_links_db_records_ToRecordId",
                table: "db_record_links");

            migrationBuilder.DropIndex(
                name: "IX_db_record_links_FieldId_FromRecordId_ToRecordId",
                table: "db_record_links");

            migrationBuilder.AddColumn<int>(
                name: "TargetDbId",
                table: "db_record_links",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_db_record_links_FieldId_FromRecordId_TargetDbId_ToRecordId",
                table: "db_record_links",
                columns: new[] { "FieldId", "FromRecordId", "TargetDbId", "ToRecordId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_db_record_links_FieldId_FromRecordId_TargetDbId_ToRecordId",
                table: "db_record_links");

            migrationBuilder.DropColumn(
                name: "TargetDbId",
                table: "db_record_links");

            migrationBuilder.CreateIndex(
                name: "IX_db_record_links_FieldId_FromRecordId_ToRecordId",
                table: "db_record_links",
                columns: new[] { "FieldId", "FromRecordId", "ToRecordId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_db_record_links_db_records_ToRecordId",
                table: "db_record_links",
                column: "ToRecordId",
                principalTable: "db_records",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
