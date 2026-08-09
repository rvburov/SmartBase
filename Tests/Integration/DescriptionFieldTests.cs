using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// «Описание» — ОБЫЧНОЕ поле коллекции с ролью <c>IsDescription</c>, а не системная колонка
/// записи: коллекция живёт без него, заводят его только два пути (создание записи через
/// календарь и через задачу), и оно ведёт себя как любое свойство — переименование, смена
/// типа, удаление. Инварианты из docs/architecture/data-types/description.md.
/// </summary>
public sealed class DescriptionFieldTests
{
    private static DbField? DescField(TempDatabase db, int collectionId) =>
        db.Constructor.GetFields(collectionId).FirstOrDefault(f => f.IsDescription);

    // ── Кто НЕ заводит описание ───────────────────────────────────────────────

    [Fact]
    public void New_collection_and_plain_record_have_no_description_field()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo).Id!.Value;

        Assert.Null(repo.DescriptionFieldId(col));

        SampleData.AddRecord(repo, col, "Просто запись");
        Assert.Null(repo.DescriptionFieldId(col));

        // И в колонках экспорта (= колонках таблицы) его тоже нет — только «Название».
        var headers = repo.GetExportColumns(col, includeHidden: true).Select(c => c.Header).ToArray();
        Assert.Equal(["Название"], headers);
    }

    /// <summary>Поля нет — писать описанию некуда, правка молча игнорируется (не падает).</summary>
    [Fact]
    public void Writing_description_without_field_is_a_no_op()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo).Id!.Value;
        var rec  = SampleData.AddRecord(repo, col, "Запись").Id!.Value;

        Assert.Empty(repo.SetRecordDescription(rec, "текст"));
        Assert.Equal("", repo.GetRecordDescription(rec));
        Assert.Null(repo.DescriptionFieldId(col));
    }

    // ── Кто заводит описание ──────────────────────────────────────────────────

    [Fact]
    public void Calendar_record_creates_description_field_and_stores_text()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "События").Id!.Value;

        var ev = repo.AddCalendarRecord(new CalendarRecord
        {
            CollectionId = col, Title = "Планёрка", Description = "в переговорной",
            StartDt = new DateTime(2026, 4, 1, 10, 0, 0), EndDt = new DateTime(2026, 4, 1, 11, 0, 0),
        });

        var field = DescField(db, col);
        Assert.NotNull(field);
        Assert.Equal("Описание", field!.Name);
        Assert.Equal("text", field.FieldType);
        Assert.False(field.IsSystem);            // обычное поле: удаляется и меняет тип
        Assert.Equal("в переговорной", repo.GetRecordDescription(ev.Id!.Value));
    }

    /// <summary>Поле появляется при КАЖДОМ создании записи из календаря — даже с пустым текстом.</summary>
    [Fact]
    public void Calendar_record_creates_field_even_with_empty_text()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "События").Id!.Value;

        repo.AddCalendarRecord(new CalendarRecord
        {
            CollectionId = col, Title = "Без описания",
            StartDt = new DateTime(2026, 4, 2), EndDt = new DateTime(2026, 4, 2), AllDay = true,
        });

        Assert.NotNull(DescField(db, col));
    }

    [Fact]
    public void Task_creates_description_field_with_and_without_due()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var noDue = SampleData.AddCollection(repo, "Задачи без срока").Id!.Value;
        repo.AddTaskRecord(noDue, "Позвонить", "Иванов Иван", "Петров Пётр",
                           ConstructorRepository.TaskStatusPlanned, due: null);
        Assert.NotNull(DescField(db, noDue));

        var withDue = SampleData.AddCollection(repo, "Задачи со сроком").Id!.Value;
        repo.AddTaskRecord(withDue, "Отчёт", "Иванов Иван", "Петров Пётр",
                           ConstructorRepository.TaskStatusPlanned,
                           new DateValue(new DateTime(2026, 5, 1), new DateTime(2026, 5, 1), true));
        Assert.NotNull(DescField(db, withDue));
    }

    /// <summary>Второй вызов роль не дублирует — поле одно на коллекцию.</summary>
    [Fact]
    public void Ensure_is_idempotent()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo).Id!.Value;

        var first  = repo.EnsureDescriptionField(col);
        var second = repo.EnsureDescriptionField(col);

        Assert.Equal(first, second);
        Assert.Single(repo.GetFields(col), f => f.IsDescription);
    }

    /// <summary>Одноимённое текстовое поле, заведённое руками, получает роль, а не второго близнеца.</summary>
    [Fact]
    public void Existing_text_field_named_description_is_adopted()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo).Id!.Value;
        var mine = SampleData.AddField(repo, col, "Описание").Id!.Value;

        Assert.Equal(mine, repo.EnsureDescriptionField(col));
        Assert.Single(repo.GetFields(col), f => f.Name == "Описание");
    }

    // ── Ведёт себя как обычное свойство ───────────────────────────────────────

    [Fact]
    public void Description_field_can_be_renamed_retyped_and_deleted()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo).Id!.Value;
        var fid  = repo.EnsureDescriptionField(col);

        var field = repo.GetFields(col).First(f => f.Id == fid);
        field.Name      = "Комментарий";
        field.FieldType = "number";
        repo.UpdateField(field);

        var updated = repo.GetFields(col).First(f => f.Id == fid);
        Assert.Equal("Комментарий", updated.Name);
        Assert.Equal("number", updated.FieldType);   // тип сменился — системное поле так не умеет
        Assert.True(updated.IsDescription);          // роль (и привязка нижнего блока) сохраняется

        repo.DeleteField(fid);
        Assert.Null(repo.DescriptionFieldId(col));
        Assert.Empty(repo.GetFields(col));
    }

    /// <summary>Дубль поля роль не наследует: двух описаний в коллекции не появляется.</summary>
    [Fact]
    public void Duplicated_field_does_not_carry_the_role()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo).Id!.Value;
        var fid  = repo.EnsureDescriptionField(col);

        repo.DuplicateField(fid);

        Assert.Single(repo.GetFields(col), f => f.IsDescription);
    }

    // ── Чтение календарём ─────────────────────────────────────────────────────

    /// <summary>Карточка календаря берёт описание из поля (значение в db_field_values).</summary>
    [Fact]
    public void Calendar_reader_returns_description_from_field()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "События").Id!.Value;

        var ev = repo.AddCalendarRecord(new CalendarRecord
        {
            CollectionId = col, Title = "Планёрка", Description = "в переговорной",
            StartDt = new DateTime(2026, 4, 1, 10, 0, 0), EndDt = new DateTime(2026, 4, 1, 11, 0, 0),
        });

        var window = repo.GetCalendarRecordsByRangeCapped(
            new DateTime(2026, 4, 1), new DateTime(2026, 4, 2), null, 100);

        var card = window.Single(r => r.Id == ev.Id);
        Assert.Equal("в переговорной", card.Description);
    }

    /// <summary>Правка события пишет описание туда же, откуда его читает календарь.</summary>
    [Fact]
    public void Updating_calendar_record_rewrites_description_value()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "События").Id!.Value;

        var ev = repo.AddCalendarRecord(new CalendarRecord
        {
            CollectionId = col, Title = "Планёрка", Description = "старое",
            StartDt = new DateTime(2026, 4, 1, 10, 0, 0), EndDt = new DateTime(2026, 4, 1, 11, 0, 0),
        });

        ev.Description = "новое";
        repo.UpdateCalendarRecord(ev);

        Assert.Equal("новое", repo.GetRecordDescription(ev.Id!.Value));

        var fid = repo.DescriptionFieldId(col)!.Value;
        Assert.Equal(1, db.Context.DbFieldValues.AsNoTracking()
                          .Count(v => v.RecordId == ev.Id!.Value && v.FieldId == fid));
    }
}
