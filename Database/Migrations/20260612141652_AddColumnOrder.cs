using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddColumnOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Единый порядок отображения колонок таблицы (Title/Desc + пользовательские
            // поля). Заполняется лениво при первом перемещении колонки в коллекции —
            // существующие коллекции работают по дефолтному порядку без записей здесь.
            // Миграция лишь создаёт пустую таблицу, существующих данных не касается.
            migrationBuilder.CreateTable(
                name: "column_order",
                columns: table => new
                {
                    CollectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_column_order", x => new { x.CollectionId, x.Key });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "column_order");
        }
    }
}
