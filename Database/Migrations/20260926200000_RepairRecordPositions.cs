using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations
{
    /// <summary>
    /// Чинит номера записей (<c>db_records.Position</c>), вышедшие за пределы int. Прежняя
    /// перенумерация коллекции ставила номера «ранг × 1000», а массовый дубль сдвигал хвост на
    /// «(число копий + 1) × 1000»: на коллекции больше 2,1 млн записей номера уходили за int, SQLite
    /// хранил их как есть, а чтение записи падало с переполнением (правка, дубль, растягивание в
    /// Диаграмме). Теперь номера выдает AllocatePositions и всегда в пределах int.
    ///
    /// Затрагиваются только коллекции с такими номерами (проверка — по индексу (CollectionId,
    /// Position), мгновенно): их записи получают номера по прежнему порядку (Position, Id) с шагом
    /// 1000, на больших коллекциях мельче — чтобы уложиться в int. На испорченной коллекции в 10 млн
    /// записей — около минуты один раз под сплэшем. В PostgreSQL столбец int4 — такие номера туда
    /// не записывались (прежние запросы падали), поэтому миграция только для SQLite.
    /// </summary>
    public partial class RepairRecordPositions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                WITH bad AS (
                    SELECT c.Id AS cid FROM db_collections c
                    WHERE (SELECT MAX(Position) FROM db_records WHERE CollectionId = c.Id) > 2147483647
                       OR (SELECT MIN(Position) FROM db_records WHERE CollectionId = c.Id) < -2147483648),
                cnt AS (
                    SELECT CollectionId AS cid, COUNT(*) AS n FROM db_records
                    WHERE CollectionId IN (SELECT cid FROM bad) GROUP BY CollectionId),
                ranked AS (
                    SELECT r.Id AS id,
                           ROW_NUMBER() OVER (PARTITION BY r.CollectionId ORDER BY r.Position, r.Id)
                             * MAX(1, MIN(1000, 2147483647 / (cnt.n + 1))) AS pos
                    FROM db_records r JOIN cnt ON cnt.cid = r.CollectionId)
                UPDATE db_records SET Position = ranked.pos
                FROM ranked WHERE db_records.Id = ranked.id;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Прежние номера не восстанавливаются: они и были поломкой.
        }
    }
}
