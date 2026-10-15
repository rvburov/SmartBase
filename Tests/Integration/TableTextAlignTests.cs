using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Расположение текста в ячейках — два аспекта оформления таблицы рядом с цветом
/// (docs/developer/architecture/data-model.md): HAlign/VAlign у cell_style, record_display и
/// column_meta. Строка оформления живет, пока задан хоть один аспект, поэтому снятие цвета не
/// уносит расположение и наоборот; при дубле и переносе коллекции расположение едет теми же
/// путями, что и цвет. Каскад и правило типа — в Tests/Unit/TableStyleTests.
/// </summary>
public sealed class TableTextAlignTests
{
    private const string Green = "#34C759";

    // «По умолчанию» в меню снимает оба аспекта уровня одной записью.
    private static readonly (StyleAspect, string?)[] ResetAlign =
        [(StyleAspect.HAlign, null), (StyleAspect.VAlign, null)];

    private static (ConstructorRepository Repo, int ColId, string Key, int RecId) Seed(ConstructorRepository repo)
    {
        var col   = SampleData.AddCollection(repo);
        var field = SampleData.AddField(repo, col.Id!.Value, "A", "text");
        var rec   = SampleData.AddRecord(repo, col.Id!.Value, "R1", 1000);
        repo.SetFieldValue(rec.Id!.Value, field.Id!.Value, "v1");
        return (repo, col.Id!.Value, field.Id!.Value.ToString(), rec.Id!.Value);
    }

    // Все три уровня сразу: ячейка «по центру / снизу», строка «справа», колонка «сверху».
    private static void AlignEverything(ConstructorRepository repo, int colId, string key, int recId)
    {
        repo.SetCellStyle(colId, [(recId, key)], [(StyleAspect.HAlign, "center"), (StyleAspect.VAlign, "bottom")]);
        repo.SetRowStyle(colId, [recId], StyleAspect.HAlign, "right");
        repo.SetColumnStyle(colId, [key], StyleAspect.VAlign, "top");
    }

    private static void AssertAligned(ConstructorRepository repo, int colId)
    {
        var rows = repo.GetRowStyles(colId);
        var row  = Assert.Single(rows);
        Assert.Equal(new RowStyle(null, null, HAlign: CellHAlign.Right), row.Value);

        var column = Assert.Single(repo.GetColumnStyles(colId));
        Assert.Equal(new RowStyle(null, null, VAlign: CellVAlign.Top), column.Value);

        var cells = repo.GetCellStylesBatch(colId, [row.Key])[row.Key];
        Assert.Equal(new RowStyle(null, null, CellHAlign.Center, CellVAlign.Bottom), cells[column.Key]);
    }

    // ── Уровни ────────────────────────────────────────────────────────────────

    [Fact]
    public void Cell_alignment_lives_next_to_color()
    {
        using var db = new TempDatabase();
        var (repo, colId, key, recId) = Seed(db.Constructor);

        repo.SetCellStyle(colId, [(recId, key)], StyleAspect.Background, Green);
        repo.SetCellStyle(colId, [(recId, key)], StyleAspect.HAlign, "right");
        Assert.Equal(new RowStyle(Green, null, HAlign: CellHAlign.Right),
                     repo.GetCellStylesBatch(colId, [recId])[recId][key]);

        // «Без цвета» не уносит расположение: строка ячейки живет ради него.
        repo.SetCellStyle(colId, [(recId, key)], StyleAspect.Background, null);
        Assert.Equal(new RowStyle(null, null, HAlign: CellHAlign.Right),
                     repo.GetCellStylesBatch(colId, [recId])[recId][key]);

        // «По умолчанию» снимает оба аспекта сразу — и строки ячейки не остается.
        repo.SetCellStyle(colId, [(recId, key)], ResetAlign);
        Assert.Empty(repo.GetCellStylesBatch(colId, [recId]));

        using var ctx = new AppDbContext(db.Path);
        Assert.Empty(ctx.CellStyles.AsNoTracking().Where(c => c.CollectionId == colId));
    }

    [Fact]
    public void Row_alignment_survives_record_display_and_color_reset()
    {
        using var db = new TempDatabase();
        var (repo, colId, _, recId) = Seed(db.Constructor);

        repo.SetRowStyle(colId, [recId], [(StyleAspect.HAlign, "center"), (StyleAspect.VAlign, "top")]);
        repo.SetRowStyle(colId, [recId], StyleAspect.Background, Green);
        repo.SetRowStyle(colId, [recId], StyleAspect.Background, null);

        // Окно «Отображение записей» ничего не знает о расположении — и не должно его стирать.
        repo.SaveRecordDisplay(colId, [(recId, false, 1)]);
        repo.SaveRecordDisplay(colId, []);
        Assert.Equal(new RowStyle(null, null, CellHAlign.Center, CellVAlign.Top), repo.GetRowStyles(colId)[recId]);

        repo.SetRowStyle(colId, [recId], ResetAlign);
        Assert.Empty(repo.GetRowStyles(colId));
        using var ctx = new AppDbContext(db.Path);
        Assert.Empty(ctx.RecordDisplay.AsNoTracking().Where(d => d.CollectionId == colId));
    }

    [Fact]
    public void Column_alignment_is_a_column_meta_aspect()
    {
        using var db = new TempDatabase();
        var (repo, colId, key, _) = Seed(db.Constructor);
        var second = SampleData.AddField(repo, colId, "B", "number").Id!.Value.ToString();
        List<string> keys = ["__title__", key, second];

        repo.SetColumnStyle(colId, keys, StyleAspect.HAlign, "center");
        var styles = repo.GetColumnStyles(colId);
        Assert.Equal(3, styles.Count);
        Assert.All(keys, k => Assert.Equal(CellHAlign.Center, styles[k].HAlign));

        // Порядок и видимость пишутся по всем строкам коллекции — расположение обязано уцелеть.
        repo.SaveColumnOrder(colId, [second, key]);
        repo.SaveColumnDisplay(colId, [(key, false, 1)]);
        repo.SaveColumnDisplay(colId, []);
        Assert.All(keys, k => Assert.Equal(CellHAlign.Center, repo.GetColumnStyles(colId)[k].HAlign));

        repo.SetColumnStyle(colId, keys, ResetAlign);
        Assert.Empty(repo.GetColumnStyles(colId));
        Assert.True(repo.GetColumnOrder(colId)[second] < repo.GetColumnOrder(colId)[key]);
    }

    [Fact]
    public void Garbage_alignment_is_not_stored()
    {
        using var db = new TempDatabase();
        var (repo, colId, key, recId) = Seed(db.Constructor);

        repo.SetCellStyle(colId, [(recId, key)], StyleAspect.HAlign, "top");
        repo.SetRowStyle(colId, [recId], StyleAspect.VAlign, "left");
        repo.SetColumnStyle(colId, [key], StyleAspect.HAlign, "middle");

        Assert.Empty(repo.GetCellStylesBatch(colId, [recId]));
        Assert.Empty(repo.GetRowStyles(colId));
        Assert.Empty(repo.GetColumnStyles(colId));
    }

    /// <summary>Сырые пакетные пути (свод трудоемкости) знают столбец каждого аспекта.</summary>
    [Fact]
    public void Bulk_paths_write_alignment_columns()
    {
        using var db = new TempDatabase();
        var (repo, colId, key, recId) = Seed(db.Constructor);

        repo.BulkSetCellStyle(colId, [(recId, key)], StyleAspect.VAlign, "bottom", CancellationToken.None);
        repo.BulkSetRowStyle(colId, [recId], StyleAspect.HAlign, "center", CancellationToken.None);
        repo.BulkSetRowStyle(colId, [recId], StyleAspect.HAlign, "сбоку", CancellationToken.None);   // мусор — мимо

        Assert.Equal(CellVAlign.Bottom, repo.GetCellStylesBatch(colId, [recId])[recId][key].VAlign);
        Assert.Equal(CellHAlign.Center, repo.GetRowStyles(colId)[recId].HAlign);
    }

    // ── Дубль и перенос ───────────────────────────────────────────────────────

    [Fact]
    public void Alignment_survives_collection_duplicate()
    {
        using var db = new TempDatabase();
        var (repo, colId, key, recId) = Seed(db.Constructor);
        AlignEverything(repo, colId, key, recId);

        AssertAligned(repo, repo.DuplicateCollectionIsolated(colId));
    }

    [Fact]
    public void Alignment_survives_cross_database_move()
    {
        using var src = new TempDatabase(dbId: 1, name: "src");
        using var tgt = new TempDatabase(dbId: 2, name: "tgt");
        var (repo, colId, key, recId) = Seed(src.Constructor);
        AlignEverything(repo, colId, key, recId);

        AssertAligned(tgt.Constructor, tgt.Constructor.MoveCollectionIsolated(src.Path, colId, targetGroupId: null));
    }

    [Fact]
    public void Alignment_survives_snapshot_move()
    {
        using var src = new TempDatabase(dbId: 1, name: "src");
        using var tgt = new TempDatabase(dbId: 2, name: "tgt");
        var (repo, colId, key, recId) = Seed(src.Constructor);
        AlignEverything(repo, colId, key, recId);

        var moved = ConstructorRepository.MoveCollection(repo, tgt.Constructor, colId, null, "Перенесенная");

        AssertAligned(tgt.Constructor, moved.Id!.Value);
    }

    // ── PostgreSQL ────────────────────────────────────────────────────────────

    private static void ResetSchema(string connString)
    {
        using var conn = new NpgsqlConnection(connString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>На сервере свои пути копии: дубль через карту id (CopyCollectionDataPg) и перенос
    /// между двумя базами на двух подключениях (CopyCollectionGeneric).</summary>
    [SkippableFact]
    public void Alignment_round_trips_and_travels_on_postgres()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs1 = PgTestServer.ConnectionString(PgTestServer.Database1);
        var cs2 = PgTestServer.ConnectionString(PgTestServer.Database2);
        ResetSchema(cs1);
        ResetSchema(cs2);

        int srcColId;
        using (var m1 = DatabaseManager.Open(101, "pg1", cs1, provider: "postgres"))
        {
            var (repo, colId, key, recId) = Seed(m1.Constructor);
            AlignEverything(repo, colId, key, recId);
            AssertAligned(repo, colId);
            AssertAligned(repo, repo.DuplicateCollection(colId).Id!.Value);

            repo.SetCellStyle(colId, [(recId, key)], ResetAlign);
            Assert.Empty(repo.GetCellStylesBatch(colId, [recId]));
            repo.SetCellStyle(colId, [(recId, key)], [(StyleAspect.HAlign, "center"), (StyleAspect.VAlign, "bottom")]);
            srcColId = colId;
        }

        using var m2 = DatabaseManager.Open(103, "pg2", cs2, provider: "postgres");
        AssertAligned(m2.Constructor, m2.Constructor.MoveCollectionIsolated(cs1, srcColId, targetGroupId: null));
    }
}
