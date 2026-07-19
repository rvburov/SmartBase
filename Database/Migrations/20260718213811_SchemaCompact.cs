using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <summary>
    /// Уплотнение схемы, два независимых изменения:
    ///
    /// 1) column_order + column_display + system_field_meta → одна таблица column_meta.
    ///    Три таблицы с одинаковым ключом (CollectionId, Key) описывали один объект — колонку
    ///    таблицы. Теперь все аспекты (порядок, видимость/уровень, подпись) в одной строке;
    ///    аспект NULL = дефолт, поэтому прежняя семантика «нет строки = дефолт» сохраняется
    ///    поаспектно. Неиспользуемая колонка system_field_meta.Position не переносится.
    ///
    /// 2) db_field_values и calendar_index пересозданы WITHOUT ROWID с естественным PK
    ///    (RecordId, FieldId); суррогатный Id у db_field_values удалён. Таблица кластеризована
    ///    по PK: значения одной записи лежат физически рядом (чтение строки таблицы —
    ///    последовательное), а отдельное B-дерево уникального индекса пары больше не нужно —
    ///    минус треть объёма и работы на вставке/удалении у самой большой таблицы базы.
    ///
    /// Пересоздание — руками через CREATE…WITHOUT ROWID + INSERT…SELECT (EF не умеет
    /// WITHOUT ROWID). Копирование в порядке PK — последовательная запись B-дерева; источник
    /// читается с NOT INDEXED: без подсказки планировщик шёл бы по индексу пары и на каждую
    /// строку прыгал в таблицу-источник случайным чтением по всему файлу — на миллионах строк
    /// это на порядки медленнее полного скана с внешней сортировкой (оба прохода
    /// последовательные; сортировка спиллится во временные файлы, не в RAM).
    /// Миграции выполняются до включения PRAGMA foreign_keys (см. DatabaseManager), поэтому
    /// FK-проверки при перекладке строк не работают и не замедляют копирование; сами
    /// объявления FK в новых таблицах сохранены 1:1. Кеш/mmap на время миграций поднимает
    /// AppDbContext.ApplyMigrationPragmas (вызывается перед Migrate()).
    ///
    /// ВНИМАНИЕ: на больших базах (миллионы значений) миграция разово переписывает
    /// db_field_values и calendar_index целиком — первое открытие займёт время,
    /// на диске нужен запас ~размера этих таблиц (WAL на время транзакции + temp-файлы
    /// сортировки); после миграций DatabaseManager дополнительно ужимает файл (VACUUM).
    /// </summary>
    public partial class SchemaCompact : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 1) column_meta: создать, слить данные трёх таблиц, старые удалить ──
            migrationBuilder.CreateTable(
                name: "column_meta",
                columns: table => new
                {
                    CollectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: true),
                    Visible = table.Column<bool>(type: "INTEGER", nullable: true),
                    Level = table.Column<int>(type: "INTEGER", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_column_meta", x => new { x.CollectionId, x.Key });
                });

            // Полный набор ключей — объединение трёх таблиц; аспекты подтягиваются LEFT JOIN'ами
            // (отсутствующий аспект остаётся NULL = дефолт). Строки system_field_meta без подписи
            // (DisplayName IS NULL) — мусор от старых сохранений, в объединение не входят.
            migrationBuilder.Sql(@"
                INSERT INTO column_meta (CollectionId, Key, Position, Visible, Level, DisplayName)
                SELECT k.CollectionId, k.Key, co.Position, cd.Visible, cd.Level, sm.DisplayName
                FROM (
                    SELECT CollectionId, Key FROM column_order
                    UNION SELECT CollectionId, Key FROM column_display
                    UNION SELECT CollectionId, Key FROM system_field_meta WHERE DisplayName IS NOT NULL
                ) k
                LEFT JOIN column_order      co ON co.CollectionId = k.CollectionId AND co.Key = k.Key
                LEFT JOIN column_display    cd ON cd.CollectionId = k.CollectionId AND cd.Key = k.Key
                LEFT JOIN system_field_meta sm ON sm.CollectionId = k.CollectionId AND sm.Key = k.Key;
            ");

            migrationBuilder.DropTable(name: "column_display");
            migrationBuilder.DropTable(name: "column_order");
            migrationBuilder.DropTable(name: "system_field_meta");

            // ── 2) db_field_values: WITHOUT ROWID, PK (RecordId, FieldId), без Id ──
            migrationBuilder.Sql(@"
                CREATE TABLE ""db_field_values_new"" (
                    ""RecordId"" INTEGER NOT NULL,
                    ""FieldId""  INTEGER NOT NULL,
                    ""Value""    TEXT NOT NULL,
                    CONSTRAINT ""PK_db_field_values"" PRIMARY KEY (""RecordId"", ""FieldId""),
                    CONSTRAINT ""FK_db_field_values_db_fields_FieldId"" FOREIGN KEY (""FieldId"")
                        REFERENCES ""db_fields"" (""Id"") ON DELETE CASCADE,
                    CONSTRAINT ""FK_db_field_values_db_records_RecordId"" FOREIGN KEY (""RecordId"")
                        REFERENCES ""db_records"" (""Id"") ON DELETE CASCADE
                ) WITHOUT ROWID;

                INSERT INTO ""db_field_values_new"" (""RecordId"", ""FieldId"", ""Value"")
                    SELECT ""RecordId"", ""FieldId"", ""Value"" FROM ""db_field_values"" NOT INDEXED
                    ORDER BY ""RecordId"", ""FieldId"";

                DROP TABLE ""db_field_values"";
                ALTER TABLE ""db_field_values_new"" RENAME TO ""db_field_values"";

                CREATE INDEX ""IX_db_field_values_FieldId_Value""
                    ON ""db_field_values"" (""FieldId"", ""Value"");
            ");

            // ── 3) calendar_index: WITHOUT ROWID (PK (RecordId, FieldId) был и раньше) ──
            migrationBuilder.Sql(@"
                CREATE TABLE ""calendar_index_new"" (
                    ""RecordId""     INTEGER NOT NULL,
                    ""FieldId""      INTEGER NOT NULL,
                    ""CollectionId"" INTEGER NOT NULL,
                    ""StartIso""     TEXT NOT NULL,
                    ""EndIso""       TEXT NOT NULL,
                    ""AllDay""       INTEGER NOT NULL,
                    CONSTRAINT ""PK_calendar_index"" PRIMARY KEY (""RecordId"", ""FieldId""),
                    CONSTRAINT ""FK_calendar_index_db_fields_FieldId"" FOREIGN KEY (""FieldId"")
                        REFERENCES ""db_fields"" (""Id"") ON DELETE CASCADE,
                    CONSTRAINT ""FK_calendar_index_db_records_RecordId"" FOREIGN KEY (""RecordId"")
                        REFERENCES ""db_records"" (""Id"") ON DELETE CASCADE
                ) WITHOUT ROWID;

                INSERT INTO ""calendar_index_new""
                    (""RecordId"", ""FieldId"", ""CollectionId"", ""StartIso"", ""EndIso"", ""AllDay"")
                    SELECT ""RecordId"", ""FieldId"", ""CollectionId"", ""StartIso"", ""EndIso"", ""AllDay""
                    FROM ""calendar_index"" NOT INDEXED
                    ORDER BY ""RecordId"", ""FieldId"";

                DROP TABLE ""calendar_index"";
                ALTER TABLE ""calendar_index_new"" RENAME TO ""calendar_index"";

                CREATE INDEX ""IX_calendar_index_FieldId""
                    ON ""calendar_index"" (""FieldId"");
                CREATE INDEX ""IX_calendar_index_CollectionId_StartIso""
                    ON ""calendar_index"" (""CollectionId"", ""StartIso"");
                CREATE INDEX ""IX_calendar_index_CollectionId_AllDay_StartIso_RecordId_FieldId""
                    ON ""calendar_index"" (""CollectionId"", ""AllDay"", ""StartIso"", ""RecordId"", ""FieldId"");
                CREATE INDEX ""IX_calendar_index_CollectionId_AllDay_EndIso_StartIso_RecordId_FieldId""
                    ON ""calendar_index"" (""CollectionId"", ""AllDay"", ""EndIso"", ""StartIso"", ""RecordId"", ""FieldId"");
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // ── 1) Обратно три таблицы колонок из column_meta ──
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

            migrationBuilder.CreateTable(
                name: "column_display",
                columns: table => new
                {
                    CollectionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Visible = table.Column<bool>(type: "INTEGER", nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_column_display", x => new { x.CollectionId, x.Key });
                });

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

            migrationBuilder.Sql(@"
                INSERT INTO column_order (CollectionId, Key, Position)
                    SELECT CollectionId, Key, Position FROM column_meta WHERE Position IS NOT NULL;
                INSERT INTO column_display (CollectionId, Key, Visible, Level)
                    SELECT CollectionId, Key, Visible, COALESCE(Level, 1) FROM column_meta WHERE Visible IS NOT NULL;
                INSERT INTO system_field_meta (CollectionId, Key, DisplayName, Position)
                    SELECT CollectionId, Key, DisplayName, 0 FROM column_meta WHERE DisplayName IS NOT NULL;
            ");

            migrationBuilder.DropTable(name: "column_meta");

            // ── 2) db_field_values обратно в rowid-таблицу с суррогатным Id ──
            migrationBuilder.Sql(@"
                CREATE TABLE ""db_field_values_old"" (
                    ""Id""       INTEGER NOT NULL CONSTRAINT ""PK_db_field_values"" PRIMARY KEY AUTOINCREMENT,
                    ""RecordId"" INTEGER NOT NULL,
                    ""FieldId""  INTEGER NOT NULL,
                    ""Value""    TEXT NOT NULL,
                    CONSTRAINT ""FK_db_field_values_db_fields_FieldId"" FOREIGN KEY (""FieldId"")
                        REFERENCES ""db_fields"" (""Id"") ON DELETE CASCADE,
                    CONSTRAINT ""FK_db_field_values_db_records_RecordId"" FOREIGN KEY (""RecordId"")
                        REFERENCES ""db_records"" (""Id"") ON DELETE CASCADE
                );

                INSERT INTO ""db_field_values_old"" (""RecordId"", ""FieldId"", ""Value"")
                    SELECT ""RecordId"", ""FieldId"", ""Value"" FROM ""db_field_values""
                    ORDER BY ""RecordId"", ""FieldId"";

                DROP TABLE ""db_field_values"";
                ALTER TABLE ""db_field_values_old"" RENAME TO ""db_field_values"";

                CREATE UNIQUE INDEX ""IX_db_field_values_RecordId_FieldId""
                    ON ""db_field_values"" (""RecordId"", ""FieldId"");
                CREATE INDEX ""IX_db_field_values_FieldId_Value""
                    ON ""db_field_values"" (""FieldId"", ""Value"");
            ");

            // ── 3) calendar_index обратно в rowid-таблицу ──
            migrationBuilder.Sql(@"
                CREATE TABLE ""calendar_index_old"" (
                    ""RecordId""     INTEGER NOT NULL,
                    ""FieldId""      INTEGER NOT NULL,
                    ""CollectionId"" INTEGER NOT NULL,
                    ""StartIso""     TEXT NOT NULL,
                    ""EndIso""       TEXT NOT NULL,
                    ""AllDay""       INTEGER NOT NULL,
                    CONSTRAINT ""PK_calendar_index"" PRIMARY KEY (""RecordId"", ""FieldId""),
                    CONSTRAINT ""FK_calendar_index_db_fields_FieldId"" FOREIGN KEY (""FieldId"")
                        REFERENCES ""db_fields"" (""Id"") ON DELETE CASCADE,
                    CONSTRAINT ""FK_calendar_index_db_records_RecordId"" FOREIGN KEY (""RecordId"")
                        REFERENCES ""db_records"" (""Id"") ON DELETE CASCADE
                );

                INSERT INTO ""calendar_index_old""
                    (""RecordId"", ""FieldId"", ""CollectionId"", ""StartIso"", ""EndIso"", ""AllDay"")
                    SELECT ""RecordId"", ""FieldId"", ""CollectionId"", ""StartIso"", ""EndIso"", ""AllDay""
                    FROM ""calendar_index"";

                DROP TABLE ""calendar_index"";
                ALTER TABLE ""calendar_index_old"" RENAME TO ""calendar_index"";

                CREATE INDEX ""IX_calendar_index_FieldId""
                    ON ""calendar_index"" (""FieldId"");
                CREATE INDEX ""IX_calendar_index_CollectionId_StartIso""
                    ON ""calendar_index"" (""CollectionId"", ""StartIso"");
                CREATE INDEX ""IX_calendar_index_CollectionId_AllDay_StartIso_RecordId_FieldId""
                    ON ""calendar_index"" (""CollectionId"", ""AllDay"", ""StartIso"", ""RecordId"", ""FieldId"");
                CREATE INDEX ""IX_calendar_index_CollectionId_AllDay_EndIso_StartIso_RecordId_FieldId""
                    ON ""calendar_index"" (""CollectionId"", ""AllDay"", ""EndIso"", ""StartIso"", ""RecordId"", ""FieldId"");
            ");
        }
    }
}
