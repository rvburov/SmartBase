using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmartBase.Database.Migrations.Pg
{
    /// <summary>
    /// Триггеры сводок календаря на PostgreSQL (calendar_day_counts — AddCalendarDayCountsPg,
    /// calendar_spans — AddCalendarSpansPg) — на целый запрос вместо каждой строки. Построчная
    /// функция plpgsql на массовых путях записи (дубль и удаление коллекции пакетами, перенос между
    /// базами, удаление поля каскадом) вызывалась на каждую строку проекции: на миллионе строк —
    /// минута на вставку и минута на удаление сверх самой записи. Теперь триггер видит все строки
    /// запроса таблицей переходов и правит сводку одним запросом по сгруппированным строкам (замер на
    /// 1 млн строк: вставка 75 → 10 с, удаление 57 → 1,6 с, перенос дня у 200 тыс. — 33 → 2,4 с).
    ///
    /// Таблица переходов допускает у триггера одно событие и не допускает списка столбцов, поэтому
    /// триггеров пять, а триггер правки срабатывает на любую правку calendar_index. Правка дня
    /// считает чистое изменение — разность мультимножеств (коллекция, день, поле) до и после:
    /// строки, у которых они не менялись, взаимно сокращаются. Граница длительности считается без
    /// функции calendar_span_seconds — у той блок EXCEPTION, то есть подтранзакция на каждую строку;
    /// если хоть одна дата не разбирается, запрос повторяется построчной функцией (неразобранная
    /// дата дает «границы нет», как раньше). Схема таблиц не меняется — пары для SQLite нет: там
    /// триггеры (AddCalendarDayCounts, AddCalendarSpans) встроены в сам запрос и на строку стоят мало.
    /// </summary>
    public partial class CalendarStatementTriggersPg : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS tr_calendar_index_day_counts_update ON calendar_index;
                DROP TRIGGER IF EXISTS tr_calendar_index_day_counts ON calendar_index;
                DROP TRIGGER IF EXISTS tr_calendar_index_spans ON calendar_index;
                DROP FUNCTION IF EXISTS calendar_day_counts_sync();");

            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION calendar_day_counts_add() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    INSERT INTO calendar_day_counts (collectionid, startday, fieldid, events)
                    SELECT collectionid, substr(startiso, 1, 10), fieldid, COUNT(*)
                    FROM new_rows
                    GROUP BY collectionid, substr(startiso, 1, 10), fieldid
                    ON CONFLICT (collectionid, startday, fieldid)
                    DO UPDATE SET events = calendar_day_counts.events + EXCLUDED.events;
                    RETURN NULL;
                END
                $$;");

            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION calendar_day_counts_remove() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    UPDATE calendar_day_counts d SET events = d.events - o.n
                    FROM (SELECT collectionid, substr(startiso, 1, 10) AS startday, fieldid, COUNT(*) AS n
                          FROM old_rows
                          GROUP BY collectionid, substr(startiso, 1, 10), fieldid) o
                    WHERE d.collectionid = o.collectionid AND d.startday = o.startday AND d.fieldid = o.fieldid;
                    RETURN NULL;
                END
                $$;");

            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION calendar_day_counts_move() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    UPDATE calendar_day_counts d SET events = d.events - g.n
                    FROM (SELECT collectionid, startday, fieldid, COUNT(*) AS n
                          FROM (SELECT collectionid, substr(startiso, 1, 10) AS startday, fieldid FROM old_rows
                                EXCEPT ALL
                                SELECT collectionid, substr(startiso, 1, 10), fieldid FROM new_rows) gone
                          GROUP BY collectionid, startday, fieldid) g
                    WHERE d.collectionid = g.collectionid AND d.startday = g.startday AND d.fieldid = g.fieldid;

                    INSERT INTO calendar_day_counts (collectionid, startday, fieldid, events)
                    SELECT collectionid, startday, fieldid, COUNT(*)
                    FROM (SELECT collectionid, substr(startiso, 1, 10) AS startday, fieldid FROM new_rows
                          EXCEPT ALL
                          SELECT collectionid, substr(startiso, 1, 10), fieldid FROM old_rows) came
                    GROUP BY collectionid, startday, fieldid
                    ON CONFLICT (collectionid, startday, fieldid)
                    DO UPDATE SET events = calendar_day_counts.events + EXCLUDED.events;
                    RETURN NULL;
                END
                $$;");

            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION calendar_spans_sync() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    BEGIN
                        INSERT INTO calendar_spans (collectionid, allday, maxseconds)
                        SELECT collectionid, allday,
                               MAX(GREATEST(0, CEIL(EXTRACT(EPOCH FROM (endiso::timestamp - startiso::timestamp))))::bigint + 1)
                        FROM new_rows
                        GROUP BY collectionid, allday
                        ON CONFLICT (collectionid, allday)
                        DO UPDATE SET maxseconds = EXCLUDED.maxseconds
                        WHERE calendar_spans.maxseconds < EXCLUDED.maxseconds;
                    EXCEPTION WHEN others THEN
                        INSERT INTO calendar_spans (collectionid, allday, maxseconds)
                        SELECT collectionid, allday, MAX(calendar_span_seconds(startiso, endiso))
                        FROM new_rows
                        GROUP BY collectionid, allday
                        ON CONFLICT (collectionid, allday)
                        DO UPDATE SET maxseconds = EXCLUDED.maxseconds
                        WHERE calendar_spans.maxseconds < EXCLUDED.maxseconds;
                    END;
                    RETURN NULL;
                END
                $$;");

            migrationBuilder.Sql(@"
                CREATE TRIGGER tr_calendar_index_day_counts_insert
                AFTER INSERT ON calendar_index REFERENCING NEW TABLE AS new_rows
                FOR EACH STATEMENT EXECUTE FUNCTION calendar_day_counts_add();");

            migrationBuilder.Sql(@"
                CREATE TRIGGER tr_calendar_index_day_counts_delete
                AFTER DELETE ON calendar_index REFERENCING OLD TABLE AS old_rows
                FOR EACH STATEMENT EXECUTE FUNCTION calendar_day_counts_remove();");

            migrationBuilder.Sql(@"
                CREATE TRIGGER tr_calendar_index_day_counts_update
                AFTER UPDATE ON calendar_index REFERENCING OLD TABLE AS old_rows NEW TABLE AS new_rows
                FOR EACH STATEMENT EXECUTE FUNCTION calendar_day_counts_move();");

            migrationBuilder.Sql(@"
                CREATE TRIGGER tr_calendar_index_spans_insert
                AFTER INSERT ON calendar_index REFERENCING NEW TABLE AS new_rows
                FOR EACH STATEMENT EXECUTE FUNCTION calendar_spans_sync();");

            migrationBuilder.Sql(@"
                CREATE TRIGGER tr_calendar_index_spans_update
                AFTER UPDATE ON calendar_index REFERENCING NEW TABLE AS new_rows
                FOR EACH STATEMENT EXECUTE FUNCTION calendar_spans_sync();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_calendar_index_spans_update ON calendar_index;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_calendar_index_spans_insert ON calendar_index;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_calendar_index_day_counts_update ON calendar_index;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_calendar_index_day_counts_delete ON calendar_index;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS tr_calendar_index_day_counts_insert ON calendar_index;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS calendar_day_counts_move();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS calendar_day_counts_remove();");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS calendar_day_counts_add();");

            // Прежние построчные триггеры — как в AddCalendarDayCountsPg и AddCalendarSpansPg.
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

            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION calendar_spans_sync() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    INSERT INTO calendar_spans (collectionid, allday, maxseconds)
                    VALUES (NEW.collectionid, NEW.allday, calendar_span_seconds(NEW.startiso, NEW.endiso))
                    ON CONFLICT (collectionid, allday)
                    DO UPDATE SET maxseconds = EXCLUDED.maxseconds
                    WHERE calendar_spans.maxseconds < EXCLUDED.maxseconds;
                    RETURN NULL;
                END
                $$;");

            migrationBuilder.Sql(@"
                CREATE TRIGGER tr_calendar_index_spans
                AFTER INSERT OR UPDATE OF collectionid, allday, startiso, endiso ON calendar_index
                FOR EACH ROW EXECUTE PROCEDURE calendar_spans_sync();");
        }
    }
}
