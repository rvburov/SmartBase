using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Пакетная правка прямоугольника ячеек (вставка из буфера и очистка выделения): инвариант
/// «нет строки значения = пусто», отсутствие холостых записей при повторной вставке того же
/// блока, аудит «последней правки» и ОДНА строка журнала со счетом вместо строки на ячейку.
/// </summary>
public sealed class CellRangeWriteTests
{
    private static (ConstructorRepository Repo, int ColId, int F1, int F2, int R1, int R2) Seed(TempDatabase db)
    {
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo);
        var f1   = SampleData.AddField(repo, col.Id!.Value, "A", "text");
        var f2   = SampleData.AddField(repo, col.Id!.Value, "B", "text");
        var r1   = SampleData.AddRecord(repo, col.Id!.Value, "R1", 1000);
        var r2   = SampleData.AddRecord(repo, col.Id!.Value, "R2", 2000);
        return (repo, col.Id!.Value, f1.Id!.Value, f2.Id!.Value, r1.Id!.Value, r2.Id!.Value);
    }

    [Fact]
    public void Values_are_written_in_one_pass()
    {
        using var db = new TempDatabase();
        var (repo, colId, f1, f2, r1, r2) = Seed(db);

        var written = repo.SetCellsBulk(colId, [
            new(r1, f1, "a1"), new(r1, f2, "b1"),
            new(r2, f1, "a2"), new(r2, f2, "b2"),
        ]);

        Assert.Equal(4, written.Changed);
        Assert.Equal("a1", repo.GetFieldValues(r1)[f1]);
        Assert.Equal("b2", repo.GetFieldValues(r2)[f2]);
    }

    /// <summary>Повторная вставка того же блока ничего не меняет: иначе она поднимала бы
    /// «изменено» у всех записей и мусорила журналом.</summary>
    [Fact]
    public void Writing_the_same_values_again_changes_nothing()
    {
        using var db = new TempDatabase();
        var (repo, colId, f1, _, r1, _) = Seed(db);
        repo.SetCellsBulk(colId, [new(r1, f1, "a1")]);

        var written = repo.SetCellsBulk(colId, [new(r1, f1, "a1")]);

        Assert.Equal(0, written.Changed);
    }

    /// <summary>Очистка удаляет строку значения, а не оставляет ее пустой: «нет строки = пусто» —
    /// инвариант, на котором держатся счетчики фильтра и экспорт.</summary>
    [Fact]
    public void Clearing_removes_the_value_row()
    {
        using var db = new TempDatabase();
        var (repo, colId, f1, _, r1, _) = Seed(db);
        repo.SetCellsBulk(colId, [new(r1, f1, "a1")]);

        var written = repo.SetCellsBulk(colId, [new(r1, f1, "")]);

        Assert.Equal(1, written.Changed);
        Assert.False(repo.GetFieldValues(r1).ContainsKey(f1));
    }

    /// <summary>Очистка пустой ячейки — не изменение: строки значения и так нет.</summary>
    [Fact]
    public void Clearing_an_already_empty_cell_is_not_a_change()
    {
        using var db = new TempDatabase();
        var (repo, colId, f1, _, r1, _) = Seed(db);

        Assert.Equal(0, repo.SetCellsBulk(colId, [new(r1, f1, "")]).Changed);
    }

    /// <summary>Отмена: старые значения из итога пишутся обратно с проверкой ExpectedOld — и
    /// ячейку, которую после вставки поправили, не трогают.</summary>
    [Fact]
    public void Undo_restores_old_values_but_keeps_later_edits()
    {
        using var db = new TempDatabase();
        var (repo, colId, f1, f2, r1, r2) = Seed(db);
        repo.SetCellsBulk(colId, [new(r1, f1, "было"), new(r2, f1, "было")]);

        var paste = repo.SetCellsBulk(colId,
            [new(r1, f1, "стало"), new(r2, f1, "стало"), new(r1, f2, "новое")],
            [(r1, "Переименована")], "Вставка");
        Assert.Equal(4, paste.Changed);
        Assert.Equal(4, paste.Changes.Count);

        repo.SetFieldValue(r2, f1, "сосед");   // после вставки ячейку поправили

        var undo = repo.SetCellsBulk(colId,
            [.. paste.Changes.Where(c => c.FieldId is not null)
                  .Select(c => new ConstructorRepository.CellWrite(c.RecordId, c.FieldId!.Value, c.OldValue, c.NewValue))],
            [.. paste.Changes.Where(c => c.FieldId is null)
                  .Select(c => new ConstructorRepository.TitleWrite(c.RecordId, c.OldValue, c.NewValue))],
            "Отмена: вставка");

        Assert.Equal(3, undo.Changed);
        Assert.Equal(1, undo.Conflicts);
        Assert.Equal("было", repo.GetFieldValues(r1)[f1]);
        Assert.False(repo.GetFieldValues(r1).ContainsKey(f2));
        Assert.Equal("сосед", repo.GetFieldValues(r2)[f1]);
        Assert.Equal("R1", repo.GetRecord(r1)!.Title);
    }

    /// <summary>Строка журнала пакета говорит не только «сколько», но и «что было и что стало» —
    /// хотя бы для первых изменений.</summary>
    [Fact]
    public async System.Threading.Tasks.Task Log_row_tells_what_changed()
    {
        using var db = new TempDatabase();
        var (repo, colId, f1, _, r1, r2) = Seed(db);

        repo.SetCellsBulk(colId, [new(r1, f1, "a1"), new(r2, f1, "a2")], null, "Вставка");

        var page = await repo.GetChangeLogPageAsync(new ConstructorRepository.ChangeLogQuery(), take: 500);
        Assert.StartsWith("Вставка: R1 (A: (пусто) → «a1»)", page[0].Detail);
        Assert.Contains("R2", page[0].Detail);
    }

    /// <summary>Сосед удалил запись, пока ее строка была на экране. Раньше ее значение упиралось во
    /// внешний ключ и откатывало ВЕСЬ пакет вместе с живыми записями; теперь удаленная запись
    /// просто пропускается, а остальные значения ложатся.</summary>
    [Fact]
    public void Deleted_record_does_not_fail_the_batch()
    {
        using var db = new TempDatabase();
        var (repo, colId, f1, f2, r1, r2) = Seed(db);
        repo.DeleteRecord(r2);

        var written = repo.SetCellsBulk(colId, [
            new(r1, f1, "a1"), new(r1, f2, "b1"),
            new(r2, f1, "a2"), new(r2, f2, "b2"),
        ], [(r2, "Призрак")]);

        Assert.Equal(2, written.Changed);
        Assert.Equal("a1", repo.GetFieldValues(r1)[f1]);
        Assert.Equal("b1", repo.GetFieldValues(r1)[f2]);
    }

    /// <summary>Запись чужой коллекции пакет не трогает, даже если ее id попал в список.</summary>
    [Fact]
    public void Record_of_another_collection_is_not_written()
    {
        using var db = new TempDatabase();
        var (repo, colId, f1, _, r1, _) = Seed(db);
        var other    = SampleData.AddCollection(repo, "Чужая");
        var stranger = SampleData.AddRecord(repo, other.Id!.Value, "Чужак", 1000);

        var written = repo.SetCellsBulk(colId, [new(r1, f1, "a1"), new(stranger.Id!.Value, f1, "x")],
                                        [(stranger.Id!.Value, "Переименован")]);

        Assert.Equal(1, written.Changed);
        Assert.False(repo.GetFieldValues(stranger.Id!.Value).ContainsKey(f1));
        Assert.Equal("Чужак", repo.GetRecord(stranger.Id!.Value)!.Title);
    }

    [Fact]
    public void Titles_are_written_too()
    {
        using var db = new TempDatabase();
        var (repo, colId, _, _, r1, r2) = Seed(db);

        var written = repo.SetCellsBulk(colId, [], [(r1, "Первая"), (r2, "R2")]);

        Assert.Equal(1, written.Changed);   // у второй записи название не менялось
        Assert.Equal("Первая", repo.GetRecord(r1)!.Title);
    }

    /// <summary>Пакетный путь пишет ОДНУ строку журнала со счетом — правило проекта для массовых
    /// операций; строка на ячейку сделала бы журнал нечитаемым.</summary>
    [Fact]
    public async System.Threading.Tasks.Task One_log_row_with_the_count()
    {
        using var db = new TempDatabase();
        var (repo, colId, f1, f2, r1, r2) = Seed(db);
        var before = (await repo.GetChangeLogPageAsync(new ConstructorRepository.ChangeLogQuery(), take: 500)).Count;

        repo.SetCellsBulk(colId, [
            new(r1, f1, "a1"), new(r1, f2, "b1"),
            new(r2, f1, "a2"), new(r2, f2, "b2"),
        ]);

        var page = await repo.GetChangeLogPageAsync(new ConstructorRepository.ChangeLogQuery(), take: 500);
        Assert.Equal(before + 1, page.Count);   // одна строка на все четыре ячейки
        Assert.Equal(4, page[0].Count);
    }

    /// <summary>После правки у записи обновляется «последняя правка» — на ней держатся столбцы
    /// «Дата изменения» и «Изменение пользователем».</summary>
    [Fact]
    public void Audit_of_the_last_edit_is_updated()
    {
        using var db = new TempDatabase();
        var (repo, colId, f1, _, r1, _) = Seed(db);
        var before = repo.GetRecord(r1)!.UpdatedAt;

        repo.SetCellsBulk(colId, [new(r1, f1, "a1")]);

        var after = repo.GetRecord(r1)!.UpdatedAt;
        Assert.NotEqual(before, after);
        Assert.False(string.IsNullOrEmpty(after));
    }

    /// <summary>Поле-дата держит проекцию календаря: без нее запись не появилась бы в календаре
    /// до перечитывания коллекции.</summary>
    [Fact]
    public void Date_values_reach_the_calendar_projection()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo);
        var date = SampleData.AddCalendarDateField(repo, col.Id!.Value, "Когда");
        var rec  = SampleData.AddRecord(repo, col.Id!.Value, "R1", 1000);
        DateValue.TryParse("01.02.2026", out var dv);

        repo.SetCellsBulk(col.Id!.Value, [new(rec.Id!.Value, date.Id!.Value, dv.ToStorage())]);

        var events = repo.GetCalendarRecordsByRange(new DateTime(2026, 2, 1), new DateTime(2026, 2, 3));
        Assert.Contains(events, e => e.Id == rec.Id!.Value);
    }

    [Fact]
    public void Reading_a_rectangle_returns_only_filled_cells()
    {
        using var db = new TempDatabase();
        var (repo, colId, f1, f2, r1, r2) = Seed(db);
        repo.SetCellsBulk(colId, [new(r1, f1, "a1"), new(r2, f2, "b2")]);

        var values = repo.GetCellValues([r1, r2], [f1, f2]);

        Assert.Equal(2, values.Count);
        Assert.Equal("a1", values[(r1, f1)]);
        Assert.Equal("b2", values[(r2, f2)]);
        Assert.False(values.ContainsKey((r1, f2)));
    }
}
