using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <summary>
    /// Сводка календаря «коллекция, день начала, поле-дата → число событий» для PostgreSQL —
    /// то же, что <c>AddCalendarDayCounts</c> для SQLite (почему и как — там). Триггер здесь —
    /// функция plpgsql на вставку, удаление и смену дня, поля или коллекции строки проекции;
    /// имена в нижнем регистре, как во всей схеме PostgreSQL (ApplyLowercaseNames).
    /// </summary>
    public partial class AddCalendarDayCountsPg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE TABLE calendar_day_counts (
                    collectionid integer NOT NULL,
                    startday     text    NOT NULL,
                    fieldid      integer NOT NULL,
                    events       integer NOT NULL,
                    CONSTRAINT pk_calendar_day_counts PRIMARY KEY (collectionid, startday, fieldid)
                );");

            migrationBuilder.Sql(@"
                INSERT INTO calendar_day_counts (collectionid, startday, fieldid, events)
                SELECT collectionid, substr(startiso, 1, 10), fieldid, COUNT(*)
                FROM calendar_index
                GROUP BY collectionid, substr(startiso, 1, 10), fieldid;");

            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION calendar_day_counts_sync() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' OR TG_OP = 'UPDATE' THEN
                        UPDATE calendar_day_counts SET events = events - 1
                        WHERE collectionid = OLD.collectionid
                          AND startday     = substr(OLD.startiso, 1, 10)
                          AND fieldid      = OLD.fieldid;
                    END IF;
                    IF TG_OP = 'INSERT' OR TG_OP = 'UPDATE' THEN
                        INSERT INTO calendar_day_counts (collectionid, startday, fieldid, events)
                        VALUES (NEW.collectionid, substr(NEW.startiso, 1, 10), NEW.fieldid, 1)
                        ON CONFLICT (collectionid, startday, fieldid)
                        DO UPDATE SET events = calendar_day_counts.events + 1;
                    END IF;
                    RETURN NULL;
                END
                $$;");

            migrationBuilder.Sql(@"
                CREATE TRIGGER tr_calendar_index_day_counts
                AFTER INSERT OR DELETE ON calendar_index
                FOR EACH ROW EXECUTE PROCEDURE calendar_day_counts_sync();");

            migrationBuilder.Sql(@"
                CREATE TRIGGER tr_calendar_index_day_counts_update
                AFTER UPDATE OF collectionid, startiso, fieldid ON calendar_index
                FOR EACH ROW
                WHEN (OLD.collectionid IS DISTINCT FROM NEW.collectionid
                   OR OLD.fieldid IS DISTINCT FROM NEW.fieldid
                   OR substr(OLD.startiso, 1, 10) IS DISTINCT FROM substr(NEW.startiso, 1, 10))
                EXECUTE PROCEDURE calendar_day_counts_sync();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_calendar_index_day_counts_update ON calendar_index;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_calendar_index_day_counts ON calendar_index;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS calendar_day_counts_sync();");
            migrationBuilder.Sql("DROP TABLE IF EXISTS calendar_day_counts;");
        }
    }
}
