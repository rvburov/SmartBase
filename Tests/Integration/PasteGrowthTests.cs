using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Вставка за край таблицы (настройка «Досоздавать строки и столбцы при вставке»): пустые записи в
/// конце коллекции, текстовые свойства «Колонка N» и их бережная уборка при отмене — заполненное
/// после вставки остается.
/// </summary>
public sealed class PasteGrowthTests
{
    private static (ConstructorRepository Repo, int ColId, int Field, int R1, int R2) Seed(TempDatabase db)
    {
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo);
        var f    = SampleData.AddField(repo, col.Id!.Value, "A", "text");
        var r1   = SampleData.AddRecord(repo, col.Id!.Value, "R1", 1000);
        var r2   = SampleData.AddRecord(repo, col.Id!.Value, "R2", 2000);
        return (repo, col.Id!.Value, f.Id!.Value, r1.Id!.Value, r2.Id!.Value);
    }

    [Fact]
    public void Empty_records_go_to_the_end_in_order()
    {
        using var db = new TempDatabase();
        var (repo, colId, _, r1, r2) = Seed(db);

        var added = repo.AddEmptyRecords(colId, 3);

        Assert.Equal(3, added.Count);
        Assert.Equal([r1, r2, .. added], repo.GetRecords(colId).Select(r => r.Id!.Value));
        Assert.All(added, id => Assert.Equal(TitleText.Placeholder, repo.GetRecord(id)!.Title));
        Assert.All(added, id => Assert.Empty(repo.GetFieldValues(id)));
    }

    /// <summary>Значения по умолчанию у новой записи («Пользователь» — текущий) вставка не ставит:
    /// иначе отмена не узнала бы в записи нетронутую и не убрала бы ее.</summary>
    [Fact]
    public void Empty_records_get_no_default_user()
    {
        using var db = new TempDatabase();
        var (repo, colId, _, _, _) = Seed(db);
        SampleData.AddField(repo, colId, "Автор", "user");

        var added = repo.AddEmptyRecords(colId, 1);

        Assert.Empty(repo.GetFieldValues(added[0]));
    }

    [Fact]
    public void Paste_fields_are_text_and_take_free_names()
    {
        using var db = new TempDatabase();
        var (repo, colId, _, _, _) = Seed(db);
        SampleData.AddField(repo, colId, "Колонка 1", "number");

        var added = repo.AddPasteTextFields(colId, 2);

        var fields = repo.GetFields(colId);
        Assert.Equal(["Колонка 2", "Колонка 3"], added.Select(id => fields.Single(f => f.Id == id).Name));
        Assert.All(added, id => Assert.Equal("text", fields.Single(f => f.Id == id).FieldType));
        // Новые — в конце, правее всех прежних свойств.
        Assert.Equal(added, fields.OrderBy(f => f.Position).TakeLast(2).Select(f => f.Id!.Value));
    }

    [Fact]
    public void Undo_removes_only_untouched_records_and_fields()
    {
        using var db = new TempDatabase();
        var (repo, colId, field, r1, _) = Seed(db);
        var records = repo.AddEmptyRecords(colId, 3);
        var fields  = repo.AddPasteTextFields(colId, 2);

        // После вставки (ее значения отмена уже вернула) человек вписал свое в одну новую запись
        // и в один новый столбец — их отмена не трогает.
        repo.SetCellsBulk(colId, [new(records[1], field, "свое")]);
        repo.SetCellsBulk(colId, [new(r1, fields[0], "в новом столбце")]);

        var (removedRecords, removedFields) = repo.RemoveUntouchedPasteGrowth(colId, records, fields);

        Assert.Equal(2, removedRecords);
        Assert.Equal(1, removedFields);
        var left = repo.GetRecords(colId).Select(r => r.Id!.Value).ToList();
        Assert.Contains(records[1], left);
        Assert.DoesNotContain(records[0], left);
        Assert.DoesNotContain(records[2], left);
        var leftFields = repo.GetFields(colId).Select(f => f.Id!.Value).ToList();
        Assert.Contains(fields[0], leftFields);
        Assert.DoesNotContain(fields[1], leftFields);
    }

    /// <summary>Переименованная запись — тоже работа человека: «Без названия» сменилось на свое.</summary>
    [Fact]
    public void Undo_keeps_a_renamed_record()
    {
        using var db = new TempDatabase();
        var (repo, colId, _, _, _) = Seed(db);
        var records = repo.AddEmptyRecords(colId, 1);
        repo.SetRecordTitle(records[0], "Названо");

        var (removedRecords, _) = repo.RemoveUntouchedPasteGrowth(colId, records, []);

        Assert.Equal(0, removedRecords);
        Assert.NotNull(repo.GetRecord(records[0], refresh: true));
    }
}
