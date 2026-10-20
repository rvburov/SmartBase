using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Календарная проекция (<c>calendar_index</c>) — инварианты из docs/architecture/data-model.md:
/// SetFieldValue делает upsert/удаление одной строки проекции; удаление записи чистит проекцию
/// каскадом; видимость поля-даты — это ФИЛЬТР ЧТЕНИЯ, а не пересборка проекции.
/// </summary>
public sealed class CalendarProjectionTests
{
    private static readonly string TimedValue =
        new DateValue(new DateTime(2026, 7, 1, 10, 0, 0), new DateTime(2026, 7, 1, 11, 0, 0), false).ToStorage();

    private static int ProjectionRows(TempDatabase db, int recordId, int fieldId) =>
        db.Context.CalendarIndex.AsNoTracking().Count(x => x.RecordId == recordId && x.FieldId == fieldId);

    [Fact]
    public void SetFieldValue_on_date_field_upserts_then_removes_projection()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var date = SampleData.AddCalendarDateField(repo, col.Id!.Value);
        var rec = SampleData.AddRecord(repo, col.Id!.Value, "Событие");

        repo.SetFieldValue(rec.Id!.Value, date.Id!.Value, TimedValue);
        Assert.Equal(1, ProjectionRows(db, rec.Id!.Value, date.Id!.Value));

        repo.SetFieldValue(rec.Id!.Value, date.Id!.Value, "");
        Assert.Equal(0, ProjectionRows(db, rec.Id!.Value, date.Id!.Value));
    }

    [Fact]
    public void Deleting_record_cascades_projection()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var date = SampleData.AddCalendarDateField(repo, col.Id!.Value);
        var rec = SampleData.AddRecord(repo, col.Id!.Value, "Событие");
        repo.SetFieldValue(rec.Id!.Value, date.Id!.Value, TimedValue);
        Assert.Equal(1, ProjectionRows(db, rec.Id!.Value, date.Id!.Value));

        repo.DeleteRecord(rec.Id!.Value);
        Assert.Equal(0, ProjectionRows(db, rec.Id!.Value, date.Id!.Value));
    }

    [Fact]
    public void Range_query_returns_event_and_visibility_is_a_read_filter()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col = SampleData.AddCollection(repo);
        var date = SampleData.AddCalendarDateField(repo, col.Id!.Value);
        var rec = SampleData.AddRecord(repo, col.Id!.Value, "Событие");
        repo.SetFieldValue(rec.Id!.Value, date.Id!.Value, TimedValue);

        var start = new DateTime(2026, 7, 1);
        var end   = new DateTime(2026, 7, 2);

        var visible = repo.GetCalendarRecordsByRangeCapped(start, end, null, perDayCap: 1000);
        Assert.Contains(visible, r => r.Id == rec.Id);

        // Скрываем поле-дату: событие пропадает из чтения, НО строка проекции остается
        // (видимость — фильтр чтения, а не удаление проекции).
        repo.SetDateFieldVisible(date.Id!.Value, visible: false);

        var hidden = repo.GetCalendarRecordsByRangeCapped(start, end, null, perDayCap: 1000);
        Assert.DoesNotContain(hidden, r => r.Id == rec.Id);
        Assert.Equal(1, ProjectionRows(db, rec.Id!.Value, date.Id!.Value));
    }

    /// <summary>
    /// Тумблер «Отображать в календаре» включает только человек: выключенная дата коллекции
    /// (новое свойство, свод трудоемкости, импорт) остается выключенной после переподключения
    /// базы, и выключенная им самим — тоже. Раньше подключение включало первую дату каждой
    /// коллекции, где ни одна не была включена. Текстовый столбец «Дата» остается текстом.
    /// </summary>
    [Fact]
    public void Remount_keeps_calendar_toggle_off_and_text_date_column_as_text()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            int date, turnedOff, text;
            using (var first = SmartBase.Database.DatabaseManager.Open(1, "test", path))
            {
                var repo = first.Constructor;
                var col  = SampleData.AddCollection(repo, "трудоемкость").Id!.Value;
                date     = SampleData.AddField(repo, col, "Дата", "date").Id!.Value;
                var rec  = SampleData.AddRecord(repo, col, "Запись");
                repo.SetFieldValue(rec.Id!.Value, date, TimedValue);

                var other = SampleData.AddCollection(repo, "События").Id!.Value;
                turnedOff = SampleData.AddCalendarDateField(repo, other, "Когда").Id!.Value;
                repo.SetDateFieldVisible(turnedOff, visible: false);
                text = SampleData.AddField(repo, other, "Дата", "text", position: 2000).Id!.Value;
            }

            using var again = SmartBase.Database.DatabaseManager.Open(1, "test", path);
            var fields = again.Context.DbFields.AsNoTracking().ToDictionary(f => f.Id);
            Assert.False(fields[date].IsCalStart);
            Assert.False(fields[turnedOff].IsCalStart);
            Assert.Equal("text", fields[text].FieldType);
            Assert.False(fields[text].IsCalStart);
            Assert.False(again.Constructor.HasAnyCalendarDateField());
        }
        finally
        {
            TempDatabase.Cleanup(path);
        }
    }
}
