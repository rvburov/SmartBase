using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <summary>
    /// Убираем локальный фон строки. Он выглядел ровно как заливка от цвета записи и
    /// отличался лишь тем, видит ли его календарь, — в меню это читалось как два
    /// одинаковых пункта. Заливка строки теперь одна и живёт в db_records.Color;
    /// в record_display остаётся цвет ТЕКСТА строки (Fg), он ничего не дублирует.
    ///
    /// Дешёвая миграция: record_display — таблица-дельта, строка в ней есть только у
    /// изменённых записей, поэтому перекладка при DROP COLUMN ничего не стоит.
    /// </summary>
    /// <inheritdoc />
    public partial class DropRowBackground : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Bg",
                table: "record_display");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Bg",
                table: "record_display",
                type: "TEXT",
                nullable: true);
        }
    }
}
