using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddSystemFieldMeta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Метаданные системных колонок «Название»/«Описание»: подпись (DisplayName)
            // и резерв под порядок (Position). Сами колонки Title/Description в
            // db_records не трогаем — это только наложение поверх них. Миграция
            // лишь создаёт новую пустую таблицу, существующих данных не касается.
            migrationBuilder.CreateTable(
                name: "system_field_meta",
                columns: table => new
                {
                    CollectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_system_field_meta", x => new { x.CollectionId, x.Key });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "system_field_meta");
        }
    }
}
