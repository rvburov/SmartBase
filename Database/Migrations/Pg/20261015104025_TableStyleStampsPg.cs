using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <inheritdoc />
    public partial class TableStyleStampsPg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "bgat",
                table: "record_display",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "fgat",
                table: "record_display",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "halignat",
                table: "record_display",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "valignat",
                table: "record_display",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "bgat",
                table: "column_meta",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "fgat",
                table: "column_meta",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "halignat",
                table: "column_meta",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "valignat",
                table: "column_meta",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "bgat",
                table: "record_display");

            migrationBuilder.DropColumn(
                name: "fgat",
                table: "record_display");

            migrationBuilder.DropColumn(
                name: "halignat",
                table: "record_display");

            migrationBuilder.DropColumn(
                name: "valignat",
                table: "record_display");

            migrationBuilder.DropColumn(
                name: "bgat",
                table: "column_meta");

            migrationBuilder.DropColumn(
                name: "fgat",
                table: "column_meta");

            migrationBuilder.DropColumn(
                name: "halignat",
                table: "column_meta");

            migrationBuilder.DropColumn(
                name: "valignat",
                table: "column_meta");
        }
    }
}
