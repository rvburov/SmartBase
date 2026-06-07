using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MyPlanner.Database.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Color = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_categories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "db_groups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ParentId = table.Column<int>(type: "INTEGER", nullable: true),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_db_groups_db_groups_ParentId",
                        column: x => x.ParentId,
                        principalTable: "db_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ui_col_widths",
                columns: table => new
                {
                    CollectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    FieldId = table.Column<int>(type: "INTEGER", nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ui_col_widths", x => new { x.CollectionId, x.FieldId });
                });

            migrationBuilder.CreateTable(
                name: "ui_collection_settings",
                columns: table => new
                {
                    CollectionId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ColResize = table.Column<bool>(type: "INTEGER", nullable: false),
                    RowResize = table.Column<bool>(type: "INTEGER", nullable: false),
                    ViewMode = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ui_collection_settings", x => x.CollectionId);
                });

            migrationBuilder.CreateTable(
                name: "ui_row_heights",
                columns: table => new
                {
                    CollectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordId = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ui_row_heights", x => new { x.CollectionId, x.RecordId });
                });

            migrationBuilder.CreateTable(
                name: "db_collections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    GroupId = table.Column<int>(type: "INTEGER", nullable: true),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    Icon = table.Column<string>(type: "TEXT", nullable: false),
                    Color = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_collections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_db_collections_db_groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "db_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "db_fields",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CollectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    FieldType = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    IsCalStart = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsCalEnd = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsSystem = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsRequired = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_fields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_db_fields_db_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "db_collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "db_records",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CollectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    StartDt = table.Column<string>(type: "TEXT", nullable: true),
                    EndDt = table.Column<string>(type: "TEXT", nullable: true),
                    AllDay = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_records", x => x.Id);
                    table.ForeignKey(
                        name: "FK_db_records_db_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "db_collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "db_field_values",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecordId = table.Column<int>(type: "INTEGER", nullable: false),
                    FieldId = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_field_values", x => x.Id);
                    table.ForeignKey(
                        name: "FK_db_field_values_db_fields_FieldId",
                        column: x => x.FieldId,
                        principalTable: "db_fields",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_db_field_values_db_records_RecordId",
                        column: x => x.RecordId,
                        principalTable: "db_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_categories_Name",
                table: "categories",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_db_collections_GroupId",
                table: "db_collections",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_db_field_values_FieldId",
                table: "db_field_values",
                column: "FieldId");

            migrationBuilder.CreateIndex(
                name: "IX_db_field_values_RecordId_FieldId",
                table: "db_field_values",
                columns: new[] { "RecordId", "FieldId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_db_fields_CollectionId",
                table: "db_fields",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_db_groups_ParentId",
                table: "db_groups",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_db_records_CollectionId",
                table: "db_records",
                column: "CollectionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "db_field_values");

            migrationBuilder.DropTable(
                name: "ui_col_widths");

            migrationBuilder.DropTable(
                name: "ui_collection_settings");

            migrationBuilder.DropTable(
                name: "ui_row_heights");

            migrationBuilder.DropTable(
                name: "db_fields");

            migrationBuilder.DropTable(
                name: "db_records");

            migrationBuilder.DropTable(
                name: "db_collections");

            migrationBuilder.DropTable(
                name: "db_groups");
        }
    }
}
