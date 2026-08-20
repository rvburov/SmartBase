using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Цвет строк, ячеек и текста — два уровня хранения (docs/architecture/data-model.md):
/// сквозной цвет записи (db_records.Color) едет с записью при дубле и переносе, а локальное
/// оформление таблицы (record_display.Fg, cell_style, column_meta.Bg/Fg) живёт дельтами
/// рядом с ui_* и чистится вместе с записями и свойствами.
/// </summary>
public sealed class RecordColorTests
{
    private const string Green = "#34C759";
    private const string Red   = "#FF3B30";

    private static (ConstructorRepository Repo, int ColId, int FieldId, int RecId) Seed(TempDatabase db)
    {
        var repo  = db.Constructor;
        var col   = SampleData.AddCollection(repo);
        var field = SampleData.AddField(repo, col.Id!.Value, "A", "text");
        var rec   = SampleData.AddRecord(repo, col.Id!.Value, "R1", 1000);
        repo.SetFieldValue(rec.Id!.Value, field.Id!.Value, "v1");
        return (repo, col.Id!.Value, field.Id!.Value, rec.Id!.Value);
    }

    // ── Сквозной цвет записи ──────────────────────────────────────────────────

    [Fact]
    public void Record_color_round_trips_and_resets_to_null()
    {
        using var db = new TempDatabase();
        var (repo, colId, _, recId) = Seed(db);

        repo.SetRecordColor([recId], Green);
        Assert.Equal(Green, repo.GetRecordsPage(colId, 0, 10).Single().Color);

        repo.SetRecordColor([recId], null);
        Assert.Null(repo.GetRecordsPage(colId, 0, 10).Single().Color);
    }

    [Fact]
    public void Record_color_rejects_garbage()
    {
        using var db = new TempDatabase();
        var (repo, colId, _, recId) = Seed(db);

        repo.SetRecordColor([recId], "зелёный");
        Assert.Null(repo.GetRecordsPage(colId, 0, 10).Single().Color);
    }

    [Fact]
    public void Record_color_survives_duplicate()
    {
        using var db = new TempDatabase();
        var (repo, colId, _, recId) = Seed(db);
        repo.SetRecordColor([recId], Green);

        var copy = repo.DuplicateRecord(recId);

        Assert.Equal(Green, copy.Color);
        Assert.All(repo.GetRecordsPage(colId, 0, 10), r => Assert.Equal(Green, r.Color));
    }

    [Fact]
    public void Record_color_survives_batch_duplicate()
    {
        using var db = new TempDatabase();
        var (repo, colId, _, recId) = Seed(db);
        repo.SetRecordColor([recId], Red);

        var newIds = repo.DuplicateRecordsData(colId, [recId]);

        var copies = repo.GetRecordsPage(colId, 0, 10).Where(r => newIds.Contains(r.Id!.Value)).ToList();
        Assert.NotEmpty(copies);
        Assert.All(copies, r => Assert.Equal(Red, r.Color));
    }

    [Fact]
    public void Record_color_survives_collection_duplicate()
    {
        using var db = new TempDatabase();
        var (repo, colId, _, recId) = Seed(db);
        repo.SetRecordColor([recId], Green);

        var newColId = repo.DuplicateCollectionIsolated(colId);

        var copies = repo.GetRecordsPage(newColId, 0, 10);
        Assert.NotEmpty(copies);
        Assert.All(copies, r => Assert.Equal(Green, r.Color));
    }

    [Fact]
    public void Record_color_survives_cross_database_move()
    {
        using var src = new TempDatabase(dbId: 1, name: "src");
        using var tgt = new TempDatabase(dbId: 2, name: "tgt");
        var (repo, colId, _, recId) = Seed(src);
        repo.SetRecordColor([recId], Red);

        var newColId = tgt.Constructor.MoveCollectionIsolated(src.Path, colId, targetGroupId: null);

        var moved = tgt.Constructor.GetRecordsPage(newColId, 0, 10);
        Assert.NotEmpty(moved);
        Assert.All(moved, r => Assert.Equal(Red, r.Color));
    }

    // ── Оформление строки, ячейки и колонки ───────────────────────────────────

    [Fact]
    public void Row_text_color_round_trips_and_row_disappears_when_reset()
    {
        using var db = new TempDatabase();
        var (repo, colId, _, recId) = Seed(db);

        repo.SetRowTextColor(colId, [recId], Red);
        Assert.Equal(new RowStyle(null, Red), repo.GetRowStyles(colId)[recId]);

        repo.SetRowTextColor(colId, [recId], null);
        Assert.Empty(repo.GetRowStyles(colId));

        using var ctx = new AppDbContext(db.Path);
        Assert.Empty(ctx.RecordDisplay.AsNoTracking().Where(d => d.CollectionId == colId));
    }

    [Fact]
    public void Saving_record_display_keeps_row_text_color()
    {
        using var db = new TempDatabase();
        var (repo, colId, _, recId) = Seed(db);
        repo.SetRowTextColor(colId, [recId], Green);

        // Окно «Отображение записей» ничего не знает о цвете — и не должно его стирать.
        repo.SaveRecordDisplay(colId, [(recId, false, 1)]);

        Assert.Equal(Green, repo.GetRowStyles(colId)[recId].Fg);
        Assert.False(repo.GetRecordDisplay(colId)[recId].Visible);

        // Возврат видимости оставляет строку живой ради цвета.
        repo.SaveRecordDisplay(colId, []);
        Assert.Equal(Green, repo.GetRowStyles(colId)[recId].Fg);
        Assert.True(repo.GetRecordDisplay(colId).TryGetValue(recId, out var back) && back.Visible);
    }

    /// <summary>Заливка строки — это цвет записи, отдельного «фона строки» нет: каскад
    /// берёт фон у ячейки, потом у записи, потом у колонки, а уровень строки в фоне
    /// не участвует вовсе. Цвет текста — наоборот, уровень строки уважает.</summary>
    [Fact]
    public void Resolve_takes_row_fill_from_record_color_only()
    {
        var cell   = new RowStyle(null, null);
        var row    = new RowStyle(Green, Green);   // Bg на уровне строки задать нечем — игнорируется
        var column = new RowStyle(Red, Red);

        var resolved = RowStyle.Resolve(cell, row, "#FFCC00", column);

        Assert.Equal("#FFCC00", resolved.Bg);   // цвет записи, а не Bg строки
        Assert.Equal(Green,     resolved.Fg);   // цвет текста строки сильнее колонки
    }

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

        repo.SetColumnStyle(colId, key, StyleAspect.Background, Green);
        Assert.Equal(Green, repo.GetColumnStyles(colId)[key].Bg);

        // Аспект цвета не мешает аспекту видимости и наоборот.
        repo.SaveColumnDisplay(colId, [(key, false, 1)]);
        Assert.Equal(Green, repo.GetColumnStyles(colId)[key].Bg);
        Assert.False(repo.GetColumnDisplay(colId)[key].Visible);

        repo.SetColumnStyle(colId, key, StyleAspect.Background, null);
        Assert.Empty(repo.GetColumnStyles(colId));
    }

    // ── Чистка ────────────────────────────────────────────────────────────────

    [Fact]
    public void Deleting_record_clears_its_cell_styles()
    {
        using var db = new TempDatabase();
        var (repo, colId, fieldId, recId) = Seed(db);
        repo.SetCellStyle(colId, [(recId, fieldId.ToString())], StyleAspect.Background, Green);
        repo.SetRowTextColor(colId, [recId], Green);

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
