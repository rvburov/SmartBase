using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <summary>
    /// «Описание» перестаёт быть системной колонкой записи и становится ОБЫЧНЫМ полем коллекции
    /// с ролью <c>IsDescription</c>: его можно переименовать, сменить тип и удалить, а заводят его
    /// только календарь и задачи. Миграция переносит уже накопленные описания в db_field_values,
    /// после чего колонка db_records.Description удаляется.
    ///
    /// Порядок шагов обязателен: роль → перенос данных → удаление колонки. Перенос идёт только
    /// для коллекций, где есть хотя бы одно НЕПУСТОЕ описание — пустые колонки в чужих коллекциях
    /// не плодим.
    /// </summary>
    public partial class DescriptionAsField : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDescription",
                table: "db_fields",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // 1) Поле «Описание» в каждой коллекции с непустыми описаниями. Имя — подпись
            //    системной колонки, если пользователь её переименовал, иначе «Описание»;
            //    занятое имя разводим суффиксом (индекс (CollectionId, Name) уникален).
            migrationBuilder.Sql(@"
                INSERT INTO db_fields (CollectionId, Name, FieldType, ConfigJson, Position,
                                       IsCalStart, IsCalEnd, IsSystem, IsRequired,
                                       IsTaskExec, IsTaskAssigner, IsTaskStatus, IsDescription)
                SELECT cid,
                       CASE
                         WHEN NOT EXISTS (SELECT 1 FROM db_fields f
                                           WHERE f.CollectionId = cand.cid AND f.Name = cand.base)
                              THEN cand.base
                         WHEN NOT EXISTS (SELECT 1 FROM db_fields f
                                           WHERE f.CollectionId = cand.cid AND f.Name = cand.base || ' 2')
                              THEN cand.base || ' 2'
                         ELSE cand.base || ' ' || cand.cid
                       END,
                       'text', '{}',
                       COALESCE((SELECT MAX(f.Position) FROM db_fields f WHERE f.CollectionId = cand.cid), 0) + 1000,
                       0, 0, 0, 0, 0, 0, 0, 1
                FROM (
                    SELECT r.CollectionId AS cid,
                           COALESCE((SELECT m.DisplayName FROM column_meta m
                                      WHERE m.CollectionId = r.CollectionId AND m.Key = '__desc__'
                                        AND m.DisplayName IS NOT NULL AND m.DisplayName <> ''),
                                    'Описание') AS base
                    FROM db_records r
                    WHERE r.Description IS NOT NULL AND r.Description <> ''
                    GROUP BY r.CollectionId
                ) AS cand");

            // 2) Значения описаний — в db_field_values (пустые не переносим: отсутствие строки
            //    и пустая строка для поля равнозначны).
            migrationBuilder.Sql(@"
                INSERT INTO db_field_values (RecordId, FieldId, Value)
                SELECT r.Id, f.Id, r.Description
                FROM db_records r
                JOIN db_fields f ON f.CollectionId = r.CollectionId AND f.IsDescription = 1
                WHERE r.Description IS NOT NULL AND r.Description <> ''
                  AND NOT EXISTS (SELECT 1 FROM db_field_values v
                                   WHERE v.RecordId = r.Id AND v.FieldId = f.Id)");

            // 3) Метаданные колонки (порядок, видимость, уровень) переезжают с ключа '__desc__'
            //    на числовой ключ нового поля. Подпись больше не нужна — имя живёт в db_fields.
            migrationBuilder.Sql(@"
                INSERT INTO column_meta (CollectionId, Key, Position, Visible, Level, DisplayName)
                SELECT m.CollectionId, CAST(f.Id AS TEXT), m.Position, m.Visible, m.Level, NULL
                FROM column_meta m
                JOIN db_fields f ON f.CollectionId = m.CollectionId AND f.IsDescription = 1
                WHERE m.Key = '__desc__'
                  AND (m.Position IS NOT NULL OR m.Visible IS NOT NULL OR m.Level IS NOT NULL)
                  AND NOT EXISTS (SELECT 1 FROM column_meta x
                                   WHERE x.CollectionId = m.CollectionId AND x.Key = CAST(f.Id AS TEXT))");

            migrationBuilder.Sql("DELETE FROM column_meta WHERE Key = '__desc__'");

            // 4) Правила авто-привязки файлов, ссылавшиеся на системное «Описание», переводим на
            //    id нового поля. Правило коллекции без поля-описания останется нечитаемым и будет
            //    отброшено при разборе (FileAutoRule.FromConfig) — подставлять «Название» нельзя.
            migrationBuilder.Sql(@"
                UPDATE db_fields
                SET ConfigJson = REPLACE(ConfigJson, '""source"":""__desc__""',
                                         '""source"":""' ||
                                         (SELECT d.Id FROM db_fields d
                                           WHERE d.CollectionId = db_fields.CollectionId AND d.IsDescription = 1
                                           LIMIT 1) || '""')
                WHERE FieldType = 'file'
                  AND ConfigJson LIKE '%__desc__%'
                  AND EXISTS (SELECT 1 FROM db_fields d
                               WHERE d.CollectionId = db_fields.CollectionId AND d.IsDescription = 1)");

            // Колонку сносим НАТИВНЫМ ALTER TABLE … DROP COLUMN (SQLite ≥ 3.35, в поставке
            // Microsoft.Data.Sqlite он свежее), а не migrationBuilder.DropColumn: тот на SQLite
            // пересоздаёт db_records через ef_temp — на базах в миллионы записей это копирование
            // всей таблицы и пересборка индексов на ровном месте. Условия нативного дропа
            // выполнены: Description не входит ни в PK, ни в единственный индекс
            // (CollectionId, Position), ни в FK/CHECK.
            migrationBuilder.Sql("ALTER TABLE db_records DROP COLUMN Description");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "db_records",
                type: "TEXT",
                nullable: true);

            // Возвращаем значения в колонку. Сами поля-описания НЕ удаляем: после отката они
            // останутся обычными текстовыми полями со своими значениями — это безопаснее, чем
            // снести колонку данных, которую пользователь мог уже править как свойство.
            migrationBuilder.Sql(@"
                UPDATE db_records
                SET Description = (SELECT v.Value FROM db_field_values v
                                     JOIN db_fields f ON f.Id = v.FieldId
                                    WHERE v.RecordId = db_records.Id
                                      AND f.CollectionId = db_records.CollectionId
                                      AND f.IsDescription = 1
                                    LIMIT 1)
                WHERE EXISTS (SELECT 1 FROM db_field_values v
                                JOIN db_fields f ON f.Id = v.FieldId
                               WHERE v.RecordId = db_records.Id
                                 AND f.CollectionId = db_records.CollectionId
                                 AND f.IsDescription = 1)");

            migrationBuilder.DropColumn(
                name: "IsDescription",
                table: "db_fields");
        }
    }
}
