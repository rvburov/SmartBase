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
}
