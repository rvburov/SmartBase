using SmartBase.Core.Models;
using SmartBase.ViewModels.ConstructorDb;

namespace SmartBase.Tests.Integration;

/// <summary>
/// «Без названия» — одно правило для всех мест правки названия (<see cref="TitleText"/>): в поле
/// ввода это подсказка, стертое название хранится как «Без названия». Здесь — таблица: ячейка
/// (<see cref="TableRowVm"/>) и вставка из буфера, которая ложится пакетом.
/// </summary>
public sealed class TitlePlaceholderTests
{
    [Fact]
    public void Erased_title_in_a_table_cell_is_stored_as_placeholder()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Договоры").Id!.Value;
        var rid  = SampleData.AddRecord(repo, col, "Договор").Id!.Value;
        var row  = new TableRowVm(repo, rid, 1, [], "Договор");

        row[TableRowVm.TitleKey] = "   ";
        Assert.Equal(TitleText.Placeholder, repo.GetRecord(rid)!.Title);
        Assert.Equal(TitleText.Placeholder, row[TableRowVm.TitleKey]);

        // Набранное ячейка пишет как есть — пробелы в тексте тоже данные.
        row[TableRowVm.TitleKey] = "  Новый  ";
        Assert.Equal("  Новый  ", repo.GetRecord(rid)!.Title);
    }

    /// <summary>Вошли в правку записи без названия и вышли, ничего не набрав, — это не правка:
    /// пустое название старой записи в базе не переписывается.</summary>
    [Fact]
    public void Untitled_cell_left_without_typing_writes_nothing()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Договоры").Id!.Value;
        var rid  = SampleData.AddRecord(repo, col, "").Id!.Value;
        var row  = new TableRowVm(repo, rid, 1, [], "");

        row[TableRowVm.TitleKey] = "";
        row[TableRowVm.TitleKey] = TitleText.Placeholder;

        Assert.Equal("", repo.GetRecord(rid)!.Title);
    }

    [Fact]
    public void Empty_title_from_the_clipboard_is_stored_as_placeholder()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var col  = SampleData.AddCollection(repo, "Договоры").Id!.Value;
        var rid  = SampleData.AddRecord(repo, col, "Договор").Id!.Value;

        var title = new PasteColumn(TableRowVm.TitleKey, "text", IsTitle: true);
        var plan  = TablePastePlanner.Plan([[""]], 0, 0, [title], rowCount: 1);
        var cell  = Assert.Single(plan.Cells);

        var result = repo.SetCellsBulk(col, [], [(rid, cell.StorageValue)], "Вставка");

        Assert.Equal(1, result.Changed);
        Assert.Equal(TitleText.Placeholder, repo.GetRecord(rid)!.Title);
    }
}
