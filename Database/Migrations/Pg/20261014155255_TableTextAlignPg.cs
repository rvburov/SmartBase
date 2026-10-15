using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <inheritdoc />
    public partial class TableTextAlignPg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "halign",
                table: "record_display",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "valign",
                table: "record_display",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "halign",
                table: "column_meta",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "valign",
                table: "column_meta",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "halign",
                table: "cell_style",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "valign",
                table: "cell_style",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "halign",
                table: "record_display");

            migrationBuilder.DropColumn(
                name: "valign",
                table: "record_display");

            migrationBuilder.DropColumn(
                name: "halign",
                table: "column_meta");

            migrationBuilder.DropColumn(
                name: "valign",
                table: "column_meta");

            migrationBuilder.DropColumn(
                name: "halign",
                table: "cell_style");

            migrationBuilder.DropColumn(
                name: "valign",
                table: "cell_style");
        }
    }
}
