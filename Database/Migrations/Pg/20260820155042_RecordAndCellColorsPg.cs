using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <summary>
    /// Цвет строк, ячеек и текста в таблице. Пять ADD COLUMN и одна новая таблица —
    /// класс «дешевых» миграций (мгновенны на базе любого размера, table-rebuild нет).
    ///
    /// Два уровня по смыслу (см. RowStyle.Resolve):
    ///   • db_records.Color — СКВОЗНОЙ цвет записи; его показывают таблица, календарь,
    ///     задачи, «Список» и панель записи, он едет с записью при дубле и переносе;
    ///   • record_display.Bg/Fg, column_meta.Bg/Fg и cell_style — ЛОКАЛЬНОЕ оформление
    ///     конкретной таблицы, дельты рядом с ui_*: нет строки = дефолт.
    ///
    /// cell_style ключуется строковым Key («__title__» либо id поля) — как column_meta:
    /// числовым FieldId системную колонку «Название» покрасить было бы нельзя. FK на
    /// запись с каскадом снимает чистку при удалении записей; чистку по полю делает
    /// прикладной код там же, где чистит column_meta.
    /// </summary>
    /// <inheritdoc />
    public partial class RecordAndCellColorsPg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "bg",
                table: "record_display",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fg",
                table: "record_display",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "color",
                table: "db_records",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bg",
                table: "column_meta",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fg",
                table: "column_meta",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cell_style",
                columns: table => new
                {
                    collectionid = table.Column<int>(type: "integer", nullable: false),
                    recordid = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    bg = table.Column<string>(type: "text", nullable: true),
                    fg = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cell_style", x => new { x.collectionid, x.recordid, x.key });
                    table.ForeignKey(
                        name: "FK_cell_style_db_records_recordid",
                        column: x => x.recordid,
                        principalTable: "db_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cell_style_recordid",
                table: "cell_style",
                column: "recordid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cell_style");

            migrationBuilder.DropColumn(
                name: "bg",
                table: "record_display");

            migrationBuilder.DropColumn(
                name: "fg",
                table: "record_display");

            migrationBuilder.DropColumn(
                name: "color",
                table: "db_records");

            migrationBuilder.DropColumn(
                name: "bg",
                table: "column_meta");

            migrationBuilder.DropColumn(
                name: "fg",
                table: "column_meta");
        }
    }
}
