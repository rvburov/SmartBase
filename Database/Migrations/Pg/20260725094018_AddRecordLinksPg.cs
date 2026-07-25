using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <inheritdoc />
    public partial class AddRecordLinksPg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "db_record_links",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    fieldid = table.Column<int>(type: "integer", nullable: false),
                    fromrecordid = table.Column<int>(type: "integer", nullable: false),
                    torecordid = table.Column<int>(type: "integer", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_record_links", x => x.id);
                    table.ForeignKey(
                        name: "FK_db_record_links_db_fields_fieldid",
                        column: x => x.fieldid,
                        principalTable: "db_fields",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_db_record_links_db_records_fromrecordid",
                        column: x => x.fromrecordid,
                        principalTable: "db_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_db_record_links_db_records_torecordid",
                        column: x => x.torecordid,
                        principalTable: "db_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_db_record_links_fieldid_fromrecordid_position",
                table: "db_record_links",
                columns: new[] { "fieldid", "fromrecordid", "position" });

            migrationBuilder.CreateIndex(
                name: "IX_db_record_links_fieldid_fromrecordid_torecordid",
                table: "db_record_links",
                columns: new[] { "fieldid", "fromrecordid", "torecordid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_db_record_links_fromrecordid",
                table: "db_record_links",
                column: "fromrecordid");

            migrationBuilder.CreateIndex(
                name: "IX_db_record_links_torecordid",
                table: "db_record_links",
                column: "torecordid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "db_record_links");
        }
    }
}
