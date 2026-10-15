using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class TableTextAlign : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "HAlign",
                table: "record_display",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VAlign",
                table: "record_display",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HAlign",
                table: "column_meta",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VAlign",
                table: "column_meta",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HAlign",
                table: "cell_style",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VAlign",
                table: "cell_style",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HAlign",
                table: "record_display");

            migrationBuilder.DropColumn(
                name: "VAlign",
                table: "record_display");

            migrationBuilder.DropColumn(
                name: "HAlign",
                table: "column_meta");

            migrationBuilder.DropColumn(
                name: "VAlign",
                table: "column_meta");

            migrationBuilder.DropColumn(
                name: "HAlign",
                table: "cell_style");

            migrationBuilder.DropColumn(
                name: "VAlign",
                table: "cell_style");
        }
    }
}
