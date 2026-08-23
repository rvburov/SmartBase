using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
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
    public partial class RecordAndCellColors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Bg",
                table: "record_display",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fg",
                table: "record_display",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "db_records",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Bg",
                table: "column_meta",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Fg",
                table: "column_meta",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cell_style",
                columns: table => new
                {
                    CollectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Bg = table.Column<string>(type: "TEXT", nullable: true),
                    Fg = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cell_style", x => new { x.CollectionId, x.RecordId, x.Key });
                    table.ForeignKey(
                        name: "FK_cell_style_db_records_RecordId",
                        column: x => x.RecordId,
                        principalTable: "db_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cell_style_RecordId",
                table: "cell_style",
                column: "RecordId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cell_style");

            migrationBuilder.DropColumn(
                name: "Bg",
                table: "record_display");

            migrationBuilder.DropColumn(
                name: "Fg",
                table: "record_display");

            migrationBuilder.DropColumn(
                name: "Color",
                table: "db_records");

            migrationBuilder.DropColumn(
                name: "Bg",
                table: "column_meta");

            migrationBuilder.DropColumn(
                name: "Fg",
                table: "column_meta");
        }
    }
}
