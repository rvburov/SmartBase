using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataBase.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddFieldValueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_db_field_values_FieldId",
                table: "db_field_values");

            migrationBuilder.CreateIndex(
                name: "IX_db_field_values_FieldId_Value",
                table: "db_field_values",
                columns: new[] { "FieldId", "Value" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_db_field_values_FieldId_Value",
                table: "db_field_values");

            migrationBuilder.CreateIndex(
                name: "IX_db_field_values_FieldId",
                table: "db_field_values",
                column: "FieldId");
        }
    }
}
