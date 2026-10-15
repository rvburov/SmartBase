using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Цвет строк, ячеек, колонок и текста — оформление ТАБЛИЦЫ (docs/architecture/data-model.md):
/// record_display.Bg/Fg на строку, cell_style на ячейку, column_meta.Bg/Fg на колонку. Все
/// живет дельтами рядом с ui_*, едет с коллекцией при дубле и переносе и чистится вместе с
/// записями и свойствами. Сквозного цвета записи (db_records.Color) больше нет — заливка
/// строки не «протекает» в календарь и задачи, см. RowStyle.Resolve.
/// </summary>
public sealed class TableColorTests
{
    private const string Green = "#34C759";
    private const string Red   = "#FF3B30";

    // Вид без отметок выставления: тест сверяет значения, а не время (RowStyle.At).
    private static RowStyle Look(RowStyle style) => style with { At = default };

    private static (ConstructorRepository Repo, int ColId, int FieldId, int RecId) Seed(TempDatabase db)
    {
        var repo  = db.Constructor;
        var col   = SampleData.AddCollection(repo);
        var field = SampleData.AddField(repo, col.Id!.Value, "A", "text");
        var rec   = SampleData.AddRecord(repo, col.Id!.Value, "R1", 1000);
        repo.SetFieldValue(rec.Id!.Value, field.Id!.Value, "v1");
        return (repo, col.Id!.Value, field.Id!.Value, rec.Id!.Value);
    }

    // ── Оформление строки ─────────────────────────────────────────────────────

    [Fact]
    public void Row_style_round_trips_and_row_disappears_when_reset()
    {
        using var db = new TempDatabase();
        var (repo, colId, _, recId) = Seed(db);

        repo.SetRowStyle(colId, [recId], StyleAspect.Background, Green);
        repo.SetRowStyle(colId, [recId], StyleAspect.Text, Red);
        Assert.Equal(new RowStyle(Green, Red), Look(repo.GetRowStyles(colId)[recId]));

        // Аспекты независимы: сброс одного не трогает другой, строка живет ради оставшегося.
        repo.SetRowStyle(colId, [recId], StyleAspect.Background, null);
        Assert.Equal(new RowStyle(null, Red), Look(repo.GetRowStyles(colId)[recId]));

        repo.SetRowStyle(colId, [recId], StyleAspect.Text, null);
        Assert.Empty(repo.GetRowStyles(colId));

        using var ctx = new AppDbContext(db.Path);
        Assert.Empty(ctx.RecordDisplay.AsNoTracking().Where(d => d.CollectionId == colId));
    }

    [Fact]
    public void Row_style_rejects_garbage()
    {
        using var db = new TempDatabase();
        var (repo, colId, _, recId) = Seed(db);

        repo.SetRowStyle(colId, [recId], StyleAspect.Background, "зеленый");
        Assert.Empty(repo.GetRowStyles(colId));
    }

    [Fact]
    public void Saving_record_display_keeps_row_colors()
    {
        using var db = new TempDatabase();
        var (repo, colId, _, recId) = Seed(db);
        repo.SetRowStyle(colId, [recId], StyleAspect.Background, Green);

        // Окно «Отображение записей» ничего не знает о цвете — и не должно его стирать.
        repo.SaveRecordDisplay(colId, [(recId, false, 1)]);

        Assert.Equal(Green, repo.GetRowStyles(colId)[recId].Bg);
        Assert.False(repo.GetRecordDisplay(colId)[recId].Visible);

        // Возврат видимости оставляет строку живой ради цвета.
        repo.SaveRecordDisplay(colId, []);
        Assert.Equal(Green, repo.GetRowStyles(colId)[recId].Bg);
        Assert.True(repo.GetRecordDisplay(colId).TryGetValue(recId, out var back) && back.Visible);
    }

    /// <summary>Пачка строк: цвет ставится и снимается СРАЗУ у всего выделения, и аспекты
    /// не мешают друг другу. Раньше так умели только строки; у колонок цвет получала лишь
    /// та, по которой кликнули (см. Column_style_applies_to_every_selected_column).</summary>
    [Fact]
    public void Row_style_applies_to_every_selected_record()
    {
        using var db = new TempDatabase();
        var (repo, colId, _, first) = Seed(db);
        var second = SampleData.AddRecord(repo, colId, "R2", 2000).Id!.Value;
        var third  = SampleData.AddRecord(repo, colId, "R3", 3000).Id!.Value;
        List<int> all = [first, second, third];

        repo.SetRowStyle(colId, all, StyleAspect.Background, Green);
        repo.SetRowStyle(colId, all, StyleAspect.Text, Red);

        var styles = repo.GetRowStyles(colId);
        Assert.Equal(3, styles.Count);
        Assert.All(all, id => Assert.Equal(new RowStyle(Green, Red), Look(styles[id])));

        // Сброс фона у всей пачки: цвет текста остается, строки живы.
        repo.SetRowStyle(colId, all, StyleAspect.Background, null);
        styles = repo.GetRowStyles(colId);
        Assert.All(all, id => Assert.Equal(new RowStyle(null, Red), Look(styles[id])));

        // Сброс второго аспекта — строк не остается вовсе.
        repo.SetRowStyle(colId, all, StyleAspect.Text, null);
        Assert.Empty(repo.GetRowStyles(colId));
    }

    /// <summary>Цвет пачке строк ставится и после того, как их строки-дельты были удалены
    /// сбросом: путь «строки нет → добавить» должен работать для каждой записи набора.</summary>
    [Fact]
    public void Row_style_can_be_set_again_after_full_reset()
    {
        using var db = new TempDatabase();
        var (repo, colId, _, first) = Seed(db);
        var second = SampleData.AddRecord(repo, colId, "R2", 2000).Id!.Value;
        List<int> both = [first, second];

        repo.SetRowStyle(colId, both, StyleAspect.Background, Green);
        repo.SetRowStyle(colId, both, StyleAspect.Background, null);
        repo.SetRowStyle(colId, both, StyleAspect.Background, Red);

        var styles = repo.GetRowStyles(colId);
        Assert.All(both, id => Assert.Equal(Red, styles[id].Bg));
    }

    [Fact]
    public void Row_style_survives_collection_duplicate()
    {
        using var db = new TempDatabase();
        var (repo, colId, _, recId) = Seed(db);
        repo.SetRowStyle(colId, [recId], StyleAspect.Background, Green);

        var newColId = repo.DuplicateCollectionIsolated(colId);

        var styles = repo.GetRowStyles(newColId);
        Assert.Single(styles);
        Assert.Equal(Green, styles.Values.Single().Bg);
    }

    [Fact]
    public void Row_style_survives_cross_database_move()
    {
        using var src = new TempDatabase(dbId: 1, name: "src");
        using var tgt = new TempDatabase(dbId: 2, name: "tgt");
        var (repo, colId, _, recId) = Seed(src);
        repo.SetRowStyle(colId, [recId], StyleAspect.Background, Red);

        var newColId = tgt.Constructor.MoveCollectionIsolated(src.Path, colId, targetGroupId: null);

        var styles = tgt.Constructor.GetRowStyles(newColId);
        Assert.Single(styles);
        Assert.Equal(Red, styles.Values.Single().Bg);
    }

    /// <summary>Каскад: точечное перебивает общее — ячейка, потом строка, потом колонка;
    /// оба аспекта считаются одинаково и независимо друг от друга.</summary>
    [Fact]
    public void Resolve_prefers_cell_then_row_then_column()
    {
        var cell   = new RowStyle(Green, null);
        var row    = new RowStyle(Red, Red);
        var column = new RowStyle("#FFCC00", "#FFCC00");

        var resolved = RowStyle.Resolve(cell, row, column);

        Assert.Equal(Green, resolved.Bg);   // фон ячейки сильнее строки и колонки
        Assert.Equal(Red,   resolved.Fg);   // текста у ячейки нет — берем у строки

        // Пустые уровни пропускаются насквозь до колонки.
        var fromColumn = RowStyle.Resolve(RowStyle.None, RowStyle.None, column);
        Assert.Equal("#FFCC00", fromColumn.Bg);
        Assert.Equal("#FFCC00", fromColumn.Fg);
    }

    // ── Оформление ячейки и колонки ───────────────────────────────────────────

    [Fact]
    public void Cell_style_is_keyed_by_column_key_including_title()
    {
        using var db = new TempDatabase();
        var (repo, colId, fieldId, recId) = Seed(db);
        var fieldKey = fieldId.ToString();

        repo.SetCellStyle(colId, [(recId, fieldKey)], StyleAspect.Background, Green);
        repo.SetCellStyle(colId, [(recId, "__title__")], StyleAspect.Text, Red);

        var cells = repo.GetCellStylesBatch(colId, [recId])[recId];
        Assert.Equal(Green, cells[fieldKey].Bg);
        Assert.Equal(Red,   cells["__title__"].Fg);

        repo.SetCellStyle(colId, [(recId, fieldKey)], StyleAspect.Background, null);
        Assert.DoesNotContain(fieldKey, repo.GetCellStylesBatch(colId, [recId])[recId].Keys);
    }

    [Fact]
    public void Column_style_lives_as_column_meta_aspect()
    {
        using var db = new TempDatabase();
        var (repo, colId, fieldId, _) = Seed(db);
        var key = fieldId.ToString();

        repo.SetColumnStyle(colId, [key], StyleAspect.Background, Green);
        Assert.Equal(Green, repo.GetColumnStyles(colId)[key].Bg);

        // Аспект цвета не мешает аспекту видимости и наоборот.
        repo.SaveColumnDisplay(colId, [(key, false, 1)]);
        Assert.Equal(Green, repo.GetColumnStyles(colId)[key].Bg);
        Assert.False(repo.GetColumnDisplay(colId)[key].Visible);

        repo.SetColumnStyle(colId, [key], StyleAspect.Background, null);
        Assert.Empty(repo.GetColumnStyles(colId));
    }

    /// <summary>Колонки выделяются пачкой (Shift+клик по заголовкам) — цвет обязан лечь на
    /// все выделение и сняться со всего, включая системную «Название». Раньше красилась
    /// только та колонка, по которой был правый клик.</summary>
    [Fact]
    public void Column_style_applies_to_every_selected_column()
    {
        using var db = new TempDatabase();
        var (repo, colId, fieldId, _) = Seed(db);
        var second = SampleData.AddField(repo, colId, "B", "text").Id!.Value;
        List<string> keys = ["__title__", fieldId.ToString(), second.ToString()];

        repo.SetColumnStyle(colId, keys, StyleAspect.Background, Green);
        repo.SetColumnStyle(colId, keys, StyleAspect.Text, Red);

        var styles = repo.GetColumnStyles(colId);
        Assert.Equal(3, styles.Count);
        Assert.All(keys, k => Assert.Equal(new RowStyle(Green, Red), Look(styles[k])));

        repo.SetColumnStyle(colId, keys, StyleAspect.Background, null);
        styles = repo.GetColumnStyles(colId);
        Assert.All(keys, k => Assert.Equal(new RowStyle(null, Red), Look(styles[k])));

        repo.SetColumnStyle(colId, keys, StyleAspect.Text, null);
        Assert.Empty(repo.GetColumnStyles(colId));
    }

    /// <summary>Порядок колонок задан аспектом Position той же строки column_meta — сброс
    /// цвета не должен уносить строку с порядком (и наоборот).</summary>
    [Fact]
    public void Resetting_column_color_keeps_column_order()
    {
        using var db = new TempDatabase();
        var (repo, colId, fieldId, _) = Seed(db);
        var second = SampleData.AddField(repo, colId, "B", "text").Id!.Value;
        List<string> keys = [fieldId.ToString(), second.ToString()];

        repo.SaveColumnOrder(colId, [second.ToString(), fieldId.ToString()]);
        repo.SetColumnStyle(colId, keys, StyleAspect.Background, Green);
        repo.SetColumnStyle(colId, keys, StyleAspect.Background, null);

        Assert.Empty(repo.GetColumnStyles(colId));
        var order = repo.GetColumnOrder(colId);
        Assert.True(order[second.ToString()] < order[fieldId.ToString()]);
    }

    // ── Чистка ────────────────────────────────────────────────────────────────

    [Fact]
    public void Deleting_record_clears_its_colors()
    {
        using var db = new TempDatabase();
        var (repo, colId, fieldId, recId) = Seed(db);
        repo.SetCellStyle(colId, [(recId, fieldId.ToString())], StyleAspect.Background, Green);
        repo.SetRowStyle(colId, [recId], StyleAspect.Background, Green);

        repo.DeleteRecordsData([recId]);

        using var ctx = new AppDbContext(db.Path);
        Assert.Empty(ctx.CellStyles.AsNoTracking().Where(c => c.CollectionId == colId));
        Assert.Empty(ctx.RecordDisplay.AsNoTracking().Where(d => d.CollectionId == colId));
    }

    [Fact]
    public void Deleting_field_clears_cell_styles_of_its_column()
    {
        using var db = new TempDatabase();
        var (repo, colId, fieldId, recId) = Seed(db);
        repo.SetCellStyle(colId, [(recId, fieldId.ToString()), (recId, "__title__")],
                          StyleAspect.Background, Green);

        repo.DeleteField(fieldId);

        var cells = repo.GetCellStylesBatch(colId, [recId])[recId];
        Assert.Equal(["__title__"], cells.Keys.ToArray());
    }

    [Fact]
    public void Deleting_collection_clears_cell_styles()
    {
        using var db = new TempDatabase();
        var (repo, colId, fieldId, recId) = Seed(db);
        repo.SetCellStyle(colId, [(recId, fieldId.ToString())], StyleAspect.Background, Green);

        repo.DeleteCollection(colId);

        using var ctx = new AppDbContext(db.Path);
        Assert.Empty(ctx.CellStyles.AsNoTracking());
    }
}
