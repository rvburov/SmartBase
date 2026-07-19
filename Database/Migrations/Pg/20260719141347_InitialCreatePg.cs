using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <inheritdoc />
    public partial class InitialCreatePg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    color = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_categories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "change_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ts = table.Column<string>(type: "text", nullable: false),
                    userkey = table.Column<string>(type: "text", nullable: false),
                    username = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    targettype = table.Column<string>(type: "text", nullable: false),
                    targetname = table.Column<string>(type: "text", nullable: false),
                    collectionname = table.Column<string>(type: "text", nullable: true),
                    count = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_change_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "column_meta",
                columns: table => new
                {
                    collectionid = table.Column<int>(type: "integer", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: true),
                    visible = table.Column<int>(type: "integer", nullable: true),
                    level = table.Column<int>(type: "integer", nullable: true),
                    displayname = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_column_meta", x => new { x.collectionid, x.key });
                });

            migrationBuilder.CreateTable(
                name: "db_groups",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    parentid = table.Column<int>(type: "integer", nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_groups", x => x.id);
                    table.ForeignKey(
                        name: "FK_db_groups_db_groups_parentid",
                        column: x => x.parentid,
                        principalTable: "db_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "db_meta",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_meta", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "db_users",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    userkey = table.Column<string>(type: "text", nullable: false),
                    displayname = table.Column<string>(type: "text", nullable: false),
                    isadmin = table.Column<int>(type: "integer", nullable: false),
                    isapproved = table.Column<int>(type: "integer", nullable: false),
                    createdat = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "record_display",
                columns: table => new
                {
                    collectionid = table.Column<int>(type: "integer", nullable: false),
                    recordid = table.Column<int>(type: "integer", nullable: false),
                    visible = table.Column<int>(type: "integer", nullable: false),
                    level = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_record_display", x => new { x.collectionid, x.recordid });
                });

            migrationBuilder.CreateTable(
                name: "ui_col_widths",
                columns: table => new
                {
                    collectionid = table.Column<int>(type: "integer", nullable: false),
                    fieldid = table.Column<int>(type: "integer", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ui_col_widths", x => new { x.collectionid, x.fieldid });
                });

            migrationBuilder.CreateTable(
                name: "ui_collection_settings",
                columns: table => new
                {
                    collectionid = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    colresize = table.Column<int>(type: "integer", nullable: false),
                    rowresize = table.Column<int>(type: "integer", nullable: false),
                    viewmode = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ui_collection_settings", x => x.collectionid);
                });

            migrationBuilder.CreateTable(
                name: "ui_row_heights",
                columns: table => new
                {
                    collectionid = table.Column<int>(type: "integer", nullable: false),
                    recordid = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ui_row_heights", x => new { x.collectionid, x.recordid });
                });

            migrationBuilder.CreateTable(
                name: "db_collections",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    groupid = table.Column<int>(type: "integer", nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false),
                    icon = table.Column<string>(type: "text", nullable: false),
                    color = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_collections", x => x.id);
                    table.ForeignKey(
                        name: "FK_db_collections_db_groups_groupid",
                        column: x => x.groupid,
                        principalTable: "db_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "db_fields",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    collectionid = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    fieldtype = table.Column<string>(type: "text", nullable: false),
                    configjson = table.Column<string>(type: "text", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    iscalstart = table.Column<int>(type: "integer", nullable: false),
                    iscalend = table.Column<int>(type: "integer", nullable: false),
                    issystem = table.Column<int>(type: "integer", nullable: false),
                    isrequired = table.Column<int>(type: "integer", nullable: false),
                    istaskexec = table.Column<int>(type: "integer", nullable: false),
                    istaskassigner = table.Column<int>(type: "integer", nullable: false),
                    istaskstatus = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_fields", x => x.id);
                    table.ForeignKey(
                        name: "FK_db_fields_db_collections_collectionid",
                        column: x => x.collectionid,
                        principalTable: "db_collections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "db_permissions",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    userid = table.Column<int>(type: "integer", nullable: false),
                    collectionid = table.Column<int>(type: "integer", nullable: true),
                    actionsmask = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_permissions", x => x.id);
                    table.ForeignKey(
                        name: "FK_db_permissions_db_collections_collectionid",
                        column: x => x.collectionid,
                        principalTable: "db_collections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_db_permissions_db_users_userid",
                        column: x => x.userid,
                        principalTable: "db_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "db_records",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    collectionid = table.Column<int>(type: "integer", nullable: false),
                    createdat = table.Column<string>(type: "text", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    startdt = table.Column<string>(type: "text", nullable: true),
                    enddt = table.Column<string>(type: "text", nullable: true),
                    allday = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_records", x => x.id);
                    table.ForeignKey(
                        name: "FK_db_records_db_collections_collectionid",
                        column: x => x.collectionid,
                        principalTable: "db_collections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "calendar_index",
                columns: table => new
                {
                    recordid = table.Column<int>(type: "integer", nullable: false),
                    fieldid = table.Column<int>(type: "integer", nullable: false),
                    collectionid = table.Column<int>(type: "integer", nullable: false),
                    startiso = table.Column<string>(type: "text", nullable: false),
                    endiso = table.Column<string>(type: "text", nullable: false),
                    allday = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calendar_index", x => new { x.recordid, x.fieldid });
                    table.ForeignKey(
                        name: "FK_calendar_index_db_fields_fieldid",
                        column: x => x.fieldid,
                        principalTable: "db_fields",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_calendar_index_db_records_recordid",
                        column: x => x.recordid,
                        principalTable: "db_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "db_field_values",
                columns: table => new
                {
                    recordid = table.Column<int>(type: "integer", nullable: false),
                    fieldid = table.Column<int>(type: "integer", nullable: false),
                    value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_db_field_values", x => new { x.recordid, x.fieldid });
                    table.ForeignKey(
                        name: "FK_db_field_values_db_fields_fieldid",
                        column: x => x.fieldid,
                        principalTable: "db_fields",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_db_field_values_db_records_recordid",
                        column: x => x.recordid,
                        principalTable: "db_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_calendar_index_collectionid_allday_endiso_startiso_recordid~",
                table: "calendar_index",
                columns: new[] { "collectionid", "allday", "endiso", "startiso", "recordid", "fieldid" });

            migrationBuilder.CreateIndex(
                name: "IX_calendar_index_collectionid_allday_startiso_recordid_fieldid",
                table: "calendar_index",
                columns: new[] { "collectionid", "allday", "startiso", "recordid", "fieldid" });

            migrationBuilder.CreateIndex(
                name: "IX_calendar_index_collectionid_startiso",
                table: "calendar_index",
                columns: new[] { "collectionid", "startiso" });

            migrationBuilder.CreateIndex(
                name: "IX_calendar_index_fieldid",
                table: "calendar_index",
                column: "fieldid");

            migrationBuilder.CreateIndex(
                name: "IX_categories_name",
                table: "categories",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_db_collections_groupid",
                table: "db_collections",
                column: "groupid");

            migrationBuilder.CreateIndex(
                name: "IX_db_field_values_fieldid_value",
                table: "db_field_values",
                columns: new[] { "fieldid", "value" });

            migrationBuilder.CreateIndex(
                name: "IX_db_fields_collectionid_name",
                table: "db_fields",
                columns: new[] { "collectionid", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_db_groups_parentid",
                table: "db_groups",
                column: "parentid");

            migrationBuilder.CreateIndex(
                name: "IX_db_permissions_collectionid",
                table: "db_permissions",
                column: "collectionid");

            migrationBuilder.CreateIndex(
                name: "IX_db_permissions_userid_collectionid",
                table: "db_permissions",
                columns: new[] { "userid", "collectionid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_db_records_collectionid_position",
                table: "db_records",
                columns: new[] { "collectionid", "position" });

            migrationBuilder.CreateIndex(
                name: "IX_db_users_userkey",
                table: "db_users",
                column: "userkey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "calendar_index");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "change_log");

            migrationBuilder.DropTable(
                name: "column_meta");

            migrationBuilder.DropTable(
                name: "db_field_values");

            migrationBuilder.DropTable(
                name: "db_meta");

            migrationBuilder.DropTable(
                name: "db_permissions");

            migrationBuilder.DropTable(
                name: "record_display");

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
                name: "db_users");

            migrationBuilder.DropTable(
                name: "db_collections");

            migrationBuilder.DropTable(
                name: "db_groups");
        }
    }
}
