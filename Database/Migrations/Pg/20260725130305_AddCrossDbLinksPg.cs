using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <inheritdoc />
    public partial class AddCrossDbLinksPg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_db_record_links_db_records_torecordid",
                table: "db_record_links");

            migrationBuilder.DropIndex(
                name: "IX_db_record_links_fieldid_fromrecordid_torecordid",
                table: "db_record_links");

            migrationBuilder.AddColumn<int>(
                name: "targetdbid",
                table: "db_record_links",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_db_record_links_fieldid_fromrecordid_targetdbid_torecordid",
                table: "db_record_links",
                columns: new[] { "fieldid", "fromrecordid", "targetdbid", "torecordid" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_db_record_links_fieldid_fromrecordid_targetdbid_torecordid",
                table: "db_record_links");

            migrationBuilder.DropColumn(
                name: "targetdbid",
                table: "db_record_links");

            migrationBuilder.CreateIndex(
                name: "IX_db_record_links_fieldid_fromrecordid_torecordid",
                table: "db_record_links",
                columns: new[] { "fieldid", "fromrecordid", "torecordid" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_db_record_links_db_records_torecordid",
                table: "db_record_links",
                column: "torecordid",
                principalTable: "db_records",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
