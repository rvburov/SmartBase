using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <summary>
    /// PG-двойник <c>DescriptionAsField</c>: «Описание» переезжает из колонки db_records в обычное
    /// поле коллекции с ролью <c>isdescription</c> (значения — в db_field_values), после чего
    /// колонка удаляется. Имена колонок в PG-модели строчные, флаги хранятся integer 0/1
    /// (см. AppDbContext.ApplyLowercaseNames), поэтому SQL отличается от SQLite только регистром.
    /// </summary>
    public partial class DescriptionAsFieldPg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "isdescription",
                table: "db_fields",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // 1) Поле «Описание» в каждой коллекции с непустыми описаниями (имя — подпись
            //    системной колонки, если она переименована; занятое разводим суффиксом).
            migrationBuilder.Sql(@"
                INSERT INTO db_fields (collectionid, name, fieldtype, configjson, position,
                                       iscalstart, iscalend, issystem, isrequired,
                                       istaskexec, istaskassigner, istaskstatus, isdescription)
                SELECT cand.cid,
                       CASE
                         WHEN NOT EXISTS (SELECT 1 FROM db_fields f
                                           WHERE f.collectionid = cand.cid AND f.name = cand.base)
                              THEN cand.base
                         WHEN NOT EXISTS (SELECT 1 FROM db_fields f
                                           WHERE f.collectionid = cand.cid AND f.name = cand.base || ' 2')
                              THEN cand.base || ' 2'
                         ELSE cand.base || ' ' || cand.cid
                       END,
                       'text', '{}',
                       COALESCE((SELECT MAX(f.position) FROM db_fields f WHERE f.collectionid = cand.cid), 0) + 1000,
                       0, 0, 0, 0, 0, 0, 0, 1
                FROM (
                    SELECT r.collectionid AS cid,
                           COALESCE((SELECT m.displayname FROM column_meta m
                                      WHERE m.collectionid = r.collectionid AND m.key = '__desc__'
                                        AND m.displayname IS NOT NULL AND m.displayname <> ''),
                                    'Описание') AS base
                    FROM db_records r
                    WHERE r.description IS NOT NULL AND r.description <> ''
                    GROUP BY r.collectionid
                ) AS cand");

            // 2) Значения описаний — в db_field_values.
            migrationBuilder.Sql(@"
                INSERT INTO db_field_values (recordid, fieldid, value)
                SELECT r.id, f.id, r.description
                FROM db_records r
                JOIN db_fields f ON f.collectionid = r.collectionid AND f.isdescription = 1
                WHERE r.description IS NOT NULL AND r.description <> ''
                  AND NOT EXISTS (SELECT 1 FROM db_field_values v
                                   WHERE v.recordid = r.id AND v.fieldid = f.id)");

            // 3) Метаданные колонки '__desc__' → числовой ключ нового поля.
            migrationBuilder.Sql(@"
                INSERT INTO column_meta (collectionid, key, position, visible, level, displayname)
                SELECT m.collectionid, CAST(f.id AS TEXT), m.position, m.visible, m.level, NULL
                FROM column_meta m
                JOIN db_fields f ON f.collectionid = m.collectionid AND f.isdescription = 1
                WHERE m.key = '__desc__'
                  AND (m.position IS NOT NULL OR m.visible IS NOT NULL OR m.level IS NOT NULL)
                  AND NOT EXISTS (SELECT 1 FROM column_meta x
                                   WHERE x.collectionid = m.collectionid AND x.key = CAST(f.id AS TEXT))");

            migrationBuilder.Sql("DELETE FROM column_meta WHERE key = '__desc__'");

            // 4) Правила авто-привязки файлов со ссылкой на системное «Описание» → id нового поля.
            migrationBuilder.Sql(@"
                UPDATE db_fields
                SET configjson = REPLACE(configjson, '""source"":""__desc__""',
                                         '""source"":""' ||
                                         (SELECT d.id FROM db_fields d
                                           WHERE d.collectionid = db_fields.collectionid AND d.isdescription = 1
                                           LIMIT 1) || '""')
                WHERE fieldtype = 'file'
                  AND configjson LIKE '%__desc__%'
                  AND EXISTS (SELECT 1 FROM db_fields d
                               WHERE d.collectionid = db_fields.collectionid AND d.isdescription = 1)");

            migrationBuilder.DropColumn(
                name: "description",
                table: "db_records");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "description",
                table: "db_records",
                type: "text",
                nullable: true);

            // Значения возвращаем в колонку; сами поля-описания оставляем обычными текстовыми
            // (сносить колонку данных, которую могли править как свойство, опаснее отката схемы).
            migrationBuilder.Sql(@"
                UPDATE db_records
                SET description = (SELECT v.value FROM db_field_values v
                                     JOIN db_fields f ON f.id = v.fieldid
                                    WHERE v.recordid = db_records.id
                                      AND f.collectionid = db_records.collectionid
                                      AND f.isdescription = 1
                                    LIMIT 1)
                WHERE EXISTS (SELECT 1 FROM db_field_values v
                                JOIN db_fields f ON f.id = v.fieldid
                               WHERE v.recordid = db_records.id
                                 AND f.collectionid = db_records.collectionid
                                 AND f.isdescription = 1)");

            migrationBuilder.DropColumn(
                name: "isdescription",
                table: "db_fields");
        }
    }
}
