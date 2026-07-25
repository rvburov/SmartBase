using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddRecordLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "db_record_links",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FieldId = table.Column<int>(type: "INTEGER", nullable: false),
                    FromRecordId = table.Column<int>(type: "INTEGER", nullable: false),
                    ToRecordId = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_record_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_db_record_links_db_fields_FieldId",
                        column: x => x.FieldId,
                        principalTable: "db_fields",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_db_record_links_db_records_FromRecordId",
                        column: x => x.FromRecordId,
                        principalTable: "db_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_db_record_links_db_records_ToRecordId",
                        column: x => x.ToRecordId,
                        principalTable: "db_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_db_record_links_FieldId_FromRecordId_Position",
                table: "db_record_links",
                columns: new[] { "FieldId", "FromRecordId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_db_record_links_FieldId_FromRecordId_ToRecordId",
                table: "db_record_links",
                columns: new[] { "FieldId", "FromRecordId", "ToRecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_db_record_links_FromRecordId",
                table: "db_record_links",
                column: "FromRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_db_record_links_ToRecordId",
                table: "db_record_links",
                column: "ToRecordId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "db_record_links");
        }
    }
}
