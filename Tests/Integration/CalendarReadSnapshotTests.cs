using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;

namespace SmartBase.Tests.Integration;

public sealed class CalendarReadSnapshotTests
{
    [Fact]
    public void Moving_a_date_between_day_queries_keeps_one_consistent_calendar_snapshot()
    {
        using var fixture = new MultiUserTestDatabase("sqlite");
        var collection = SampleData.AddCollection(fixture.Repo, "Снимок календаря").Id!.Value;
        var field = SampleData.AddCalendarDateField(fixture.Repo, collection).Id!.Value;
        var record = SampleData.AddRecord(fixture.Repo, collection, "Переносимая дата").Id!.Value;
        var before = new DateValue(new DateTime(2026, 11, 18, 10, 0, 0), new DateTime(2026, 11, 18, 11, 0, 0), false);
        var after = new DateValue(new DateTime(2026, 12, 18, 10, 0, 0), new DateTime(2026, 12, 18, 11, 0, 0), false);
        fixture.Repo.SetFieldValue(record, field, before.ToStorage());
        fixture.Manager.Context.ChangeTracker.Clear();

        // Представление ставит барьер ровно при чтении конца первой найденной даты.
        // WHERE однодневного timed-прохода использует StartIso, поэтому барьер уже после
        // выбора ноябрьской строки. Сосед затем коммитит декабрьскую версию на настоящем WAL.
        fixture.Manager.Context.Database.ExecuteSqlRaw("ALTER TABLE calendar_index RENAME TO calendar_snapshot_rows");
        fixture.Manager.Context.Database.ExecuteSqlRaw("""
            CREATE VIEW calendar_index AS
            SELECT RecordId, FieldId, CollectionId, StartIso,
                   calendar_observe_end(EndIso) AS EndIso, AllDay
            FROM calendar_snapshot_rows
            """);
        using var observed = new ManualResetEventSlim(false);
        using var updated = new ManualResetEventSlim(false);
        var calls = 0;
        var result = new ConcurrentQueue<CalendarRecord>();

        MultiUserTestDatabase.RunClients(fixture, 2, (repo, context, client) =>
        {
            if (client == 0)
            {
                context.Database.OpenConnection();
                var connection = (SqliteConnection)context.Database.GetDbConnection();
                connection.CreateFunction<string, string>("calendar_observe_end", value =>
                {
                    if (Interlocked.Increment(ref calls) == 1)
                    {
                        observed.Set();
                        Assert.True(updated.Wait(TimeSpan.FromSeconds(30)), "Сосед не завершил перенос даты");
                    }
                    return value;
                });
                foreach (var row in repo.GetCalendarRecordsByRangeCapped(
                             new DateTime(2026, 11, 1), new DateTime(2027, 2, 1),
                             new HashSet<int> { collection }, 20))
                    result.Enqueue(row);
            }
            else
            {
                Assert.True(observed.Wait(TimeSpan.FromSeconds(30)), "Чтение первой даты не началось");
                try
                {
                    using var transaction = context.Database.BeginTransaction();
                    var columns = after.ToRecordColumns();
                    context.Database.ExecuteSqlInterpolated($"""
                        UPDATE calendar_snapshot_rows SET StartIso={columns.StartIso}, EndIso={columns.EndIso}
                        WHERE RecordId={record} AND FieldId={field}
                        """);
                    context.Database.ExecuteSqlInterpolated($"""
                        UPDATE db_field_values SET Value={after.ToStorage()}
                        WHERE RecordId={record} AND FieldId={field}
                        """);
                    transaction.Commit();
                }
                finally { updated.Set(); }
            }
        });

        var entry = Assert.Single(result);
        Assert.Equal(record, entry.Id);
        Assert.Equal(field, entry.FieldId);
        Assert.Equal(before.Start, entry.StartDt);
        Assert.Equal(before.End, entry.EndDt);
        using var check = fixture.OpenContext();
        Assert.Equal(after.ToStorage(), check.DbFieldValues.AsNoTracking()
            .Single(value => value.RecordId == record && value.FieldId == field).Value);
    }

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("sqlite-shared")]
    [InlineData("postgres")]
    public void Calendar_read_keeps_the_callers_transaction_open(string profile)
    {
        using var fixture = new MultiUserTestDatabase(profile);
        var collection = SampleData.AddCollection(fixture.Repo, "Внешняя транзакция").Id!.Value;
        var field = SampleData.AddCalendarDateField(fixture.Repo, collection).Id!.Value;
        var record = SampleData.AddRecord(fixture.Repo, collection, "Событие").Id!.Value;
        using var transaction = fixture.Manager.Context.Database.BeginTransaction();
        fixture.Repo.SetFieldValue(record, field, "2026-11-18");
        Assert.Single(fixture.Repo.GetCalendarRecordsByRangeCapped(
            new DateTime(2026, 11, 1), new DateTime(2026, 12, 1), new HashSet<int> { collection }, 20));
        Assert.Same(transaction, fixture.Manager.Context.Database.CurrentTransaction);
        transaction.Rollback();
        fixture.Manager.Context.ChangeTracker.Clear();
        Assert.Empty(fixture.Repo.GetFieldValues(record));
    }
}
