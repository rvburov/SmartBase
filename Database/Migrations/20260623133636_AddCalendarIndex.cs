using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddCalendarIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "calendar_index",
                columns: table => new
                {
                    RecordId = table.Column<int>(type: "INTEGER", nullable: false),
                    FieldId = table.Column<int>(type: "INTEGER", nullable: false),
                    CollectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    StartIso = table.Column<string>(type: "TEXT", nullable: false),
                    EndIso = table.Column<string>(type: "TEXT", nullable: false),
                    AllDay = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calendar_index", x => new { x.RecordId, x.FieldId });
                    table.ForeignKey(
                        name: "FK_calendar_index_db_fields_FieldId",
                        column: x => x.FieldId,
                        principalTable: "db_fields",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_calendar_index_db_records_RecordId",
                        column: x => x.RecordId,
                        principalTable: "db_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_calendar_index_CollectionId_StartIso",
                table: "calendar_index",
                columns: new[] { "CollectionId", "StartIso" });

            migrationBuilder.CreateIndex(
                name: "IX_calendar_index_FieldId",
                table: "calendar_index",
                column: "FieldId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_index");
        }
    }
}
