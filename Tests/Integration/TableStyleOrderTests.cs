using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Оформление таблицы: что выставлено последним, то и видно (RowStyle.Resolve). Смена строки или
/// колонки — цвета или расположения текста, в том числе «По умолчанию» — ложится на все их
/// ячейки: свой этот аспект у ячеек снимается в базе по всем записям, а на пересечении строки и
/// колонки побеждает выставленное позже. Иначе ячейку с особой настройкой среди миллионов строк
/// было бы не найти.
/// </summary>
public sealed class TableStyleOrderTests
{
    private const string Green  = "#34C759";
    private const string Red    = "#FF3B30";
    private const string Yellow = "#FFCC00";

    private static readonly (StyleAspect, string?)[] ResetAlign =
        [(StyleAspect.HAlign, null), (StyleAspect.VAlign, null)];

    private sealed record Table(ConstructorRepository Repo, int ColId, string A, string B, int R1, int R2);

    private static Table Seed(ConstructorRepository repo)
    {
        var col = SampleData.AddCollection(repo).Id!.Value;
        var a   = SampleData.AddField(repo, col, "A", "text").Id!.Value.ToString();
        var b   = SampleData.AddField(repo, col, "B", "text").Id!.Value.ToString();
        var r1  = SampleData.AddRecord(repo, col, "R1", 1000).Id!.Value;
        var r2  = SampleData.AddRecord(repo, col, "R2", 2000).Id!.Value;
        return new Table(repo, col, a, b, r1, r2);
    }

    // Свое оформление ячейки, как оно лежит в базе (None — строки cell_style нет).
    private static RowStyle Cell(Table t, int recId, string key) =>
        t.Repo.GetCellStylesBatch(t.ColId, [recId]).GetValueOrDefault(recId)?.GetValueOrDefault(key) ?? RowStyle.None;

    // То, что видно в ячейке: каскад из базы — ячейка, строка, колонка.
    private static RowStyle Seen(Table t, int recId, string key) =>
        RowStyle.Resolve(Cell(t, recId, key), t.Repo.GetRowStyles(t.ColId).GetValueOrDefault(recId),
                         t.Repo.GetColumnStyles(t.ColId).GetValueOrDefault(key));

    // ── Ячейки внутри строки и колонки ────────────────────────────────────────

    [Fact]
    public void Column_change_resets_that_aspect_in_every_cell_of_the_column()
    {
        using var db = new TempDatabase();
        var t = Seed(db.Constructor);
        t.Repo.SetCellStyle(t.ColId, [(t.R1, t.A)], [(StyleAspect.HAlign, "right"), (StyleAspect.Background, Green)]);
        t.Repo.SetCellStyle(t.ColId, [(t.R2, t.A)], StyleAspect.HAlign, "center");
        t.Repo.SetCellStyle(t.ColId, [(t.R1, t.B)], StyleAspect.HAlign, "right");

        t.Repo.SetColumnStyle(t.ColId, [t.A], StyleAspect.HAlign, "left");

        // Горизонталь ячеек колонки снята во всех записях; фон ячейки — другой аспект — остался,
        // ячейка без аспектов исчезла; соседняя колонка не тронута.
        Assert.Equal(new RowStyle(Green, null), Cell(t, t.R1, t.A));
        Assert.Equal(RowStyle.None, Cell(t, t.R2, t.A));
        Assert.Equal(CellHAlign.Right, Cell(t, t.R1, t.B).HAlign);
        Assert.All([t.R1, t.R2], r => Assert.Equal(CellHAlign.Left, Seen(t, r, t.A).HAlign));
    }

    [Fact]
    public void Row_change_resets_that_aspect_in_every_cell_of_the_row()
    {
        using var db = new TempDatabase();
        var t = Seed(db.Constructor);
        t.Repo.SetCellStyle(t.ColId, [(t.R1, t.A)], StyleAspect.HAlign, "right");
        t.Repo.SetCellStyle(t.ColId, [(t.R1, t.B)], [(StyleAspect.HAlign, "center"), (StyleAspect.VAlign, "top")]);
        t.Repo.SetCellStyle(t.ColId, [(t.R2, t.A)], StyleAspect.HAlign, "right");

        t.Repo.SetRowStyle(t.ColId, [t.R1], StyleAspect.HAlign, "center");

        // Сбрасывается только направление, которое меняли: вертикаль ячейки осталась.
        Assert.Equal(RowStyle.None, Cell(t, t.R1, t.A));
        Assert.Equal(new RowStyle(null, null, VAlign: CellVAlign.Top), Cell(t, t.R1, t.B));
        Assert.Equal(CellHAlign.Right, Cell(t, t.R2, t.A).HAlign);   // чужая строка не тронута
        Assert.All([t.A, t.B], k => Assert.Equal(CellHAlign.Center, Seen(t, t.R1, k).HAlign));
    }

    [Fact]
    public void Default_on_a_column_resets_its_cells_too()
    {
        using var db = new TempDatabase();
        var t = Seed(db.Constructor);
        t.Repo.SetColumnStyle(t.ColId, [t.A], StyleAspect.HAlign, "center");
        t.Repo.SetCellStyle(t.ColId, [(t.R1, t.A)], [(StyleAspect.HAlign, "right"), (StyleAspect.VAlign, "bottom")]);

        t.Repo.SetColumnStyle(t.ColId, [t.A], ResetAlign);

        Assert.Equal(RowStyle.None, Cell(t, t.R1, t.A));
        Assert.Equal(new RowStyle(null, null), Seen(t, t.R1, t.A));
        using var ctx = new AppDbContext(db.Path);
        Assert.Empty(ctx.CellStyles.AsNoTracking().Where(c => c.CollectionId == t.ColId));
    }

    // ── Строка и колонка: выставленное позже ──────────────────────────────────

    [Fact]
    public void The_later_of_row_and_column_wins_at_the_crossing()
    {
        using var db = new TempDatabase();
        var t = Seed(db.Constructor);

        t.Repo.SetRowStyle(t.ColId, [t.R1], StyleAspect.HAlign, "right");
        t.Repo.SetColumnStyle(t.ColId, [t.A], StyleAspect.HAlign, "center");
        Assert.Equal(CellHAlign.Center, Seen(t, t.R1, t.A).HAlign);   // колонка позже строки
        Assert.Equal(CellHAlign.Right,  Seen(t, t.R1, t.B).HAlign);   // у B своего нет — строка
        Assert.Equal(CellHAlign.Center, Seen(t, t.R2, t.A).HAlign);

        t.Repo.SetRowStyle(t.ColId, [t.R1], StyleAspect.HAlign, "left");
        Assert.Equal(CellHAlign.Left,   Seen(t, t.R1, t.A).HAlign);   // строка позже колонки
        Assert.Equal(CellHAlign.Center, Seen(t, t.R2, t.A).HAlign);
    }

    [Fact]
    public void Default_beats_the_earlier_level()
    {
        using var db = new TempDatabase();
        var t = Seed(db.Constructor);

        // «По умолчанию» колонке после строки: в колонке — обычный вид, в остальной строке — строка.
        t.Repo.SetRowStyle(t.ColId, [t.R1], StyleAspect.HAlign, "right");
        t.Repo.SetColumnStyle(t.ColId, [t.A], StyleAspect.HAlign, null);
        Assert.Null(Seen(t, t.R1, t.A).HAlign);
        Assert.Equal(CellHAlign.Right, Seen(t, t.R1, t.B).HAlign);

        // «По умолчанию» строке после колонки: в строке — обычный вид, в остальной колонке — колонка.
        t.Repo.SetColumnStyle(t.ColId, [t.B], StyleAspect.HAlign, "center");
        t.Repo.SetRowStyle(t.ColId, [t.R2], StyleAspect.HAlign, null);
        Assert.Null(Seen(t, t.R2, t.B).HAlign);
        Assert.Equal(CellHAlign.Center, Seen(t, t.R1, t.B).HAlign);   // колонка позже строки R1
    }

    [Fact]
    public void Cell_default_shows_the_later_of_row_and_column()
    {
        using var db = new TempDatabase();
        var t = Seed(db.Constructor);

        t.Repo.SetRowStyle(t.ColId, [t.R1], StyleAspect.HAlign, "right");
        t.Repo.SetColumnStyle(t.ColId, [t.A], StyleAspect.HAlign, "center");
        t.Repo.SetCellStyle(t.ColId, [(t.R1, t.A)], StyleAspect.HAlign, "left");
        Assert.Equal(CellHAlign.Left, Seen(t, t.R1, t.A).HAlign);
        t.Repo.SetCellStyle(t.ColId, [(t.R1, t.A)], StyleAspect.HAlign, null);
        Assert.Equal(CellHAlign.Center, Seen(t, t.R1, t.A).HAlign);   // колонка позже строки

        t.Repo.SetColumnStyle(t.ColId, [t.B], StyleAspect.HAlign, "center");
        t.Repo.SetRowStyle(t.ColId, [t.R2], StyleAspect.HAlign, "right");
        t.Repo.SetCellStyle(t.ColId, [(t.R2, t.B)], StyleAspect.HAlign, "left");
        t.Repo.SetCellStyle(t.ColId, [(t.R2, t.B)], StyleAspect.HAlign, null);
        Assert.Equal(CellHAlign.Right, Seen(t, t.R2, t.B).HAlign);    // строка позже колонки
    }

    [Fact]
    public void Colors_follow_the_same_rule()
    {
        using var db = new TempDatabase();
        var t = Seed(db.Constructor);
        t.Repo.SetCellStyle(t.ColId, [(t.R1, t.A)], [(StyleAspect.Background, Green), (StyleAspect.Text, Green)]);

        t.Repo.SetColumnStyle(t.ColId, [t.A], StyleAspect.Background, Red);
        Assert.Equal(new RowStyle(null, Green), Cell(t, t.R1, t.A));   // фон снят, цвет текста — нет
        Assert.Equal(Red, Seen(t, t.R1, t.A).Bg);

        t.Repo.SetRowStyle(t.ColId, [t.R2], StyleAspect.Background, Yellow);
        Assert.Equal(Yellow, Seen(t, t.R2, t.A).Bg);                   // строка позже колонки
        Assert.Equal(Red,    Seen(t, t.R1, t.A).Bg);

        t.Repo.SetColumnStyle(t.ColId, [t.A], StyleAspect.Background, null);   // «Без цвета» колонке
        Assert.Null(Seen(t, t.R2, t.A).Bg);                            // позже строки — без цвета
        Assert.Equal(Yellow, Seen(t, t.R2, t.B).Bg);
    }

    [Fact]
    public void Default_with_nothing_to_beat_leaves_no_trace()
    {
        using var db = new TempDatabase();
        var t = Seed(db.Constructor);

        t.Repo.SetRowStyle(t.ColId, [t.R1], StyleAspect.HAlign, "right");
        t.Repo.SetRowStyle(t.ColId, [t.R1], StyleAspect.HAlign, null);   // колонок с расположением нет
        Assert.Empty(t.Repo.GetRowStyles(t.ColId));

        t.Repo.SetColumnStyle(t.ColId, [t.A], StyleAspect.HAlign, "center");
        t.Repo.SetColumnStyle(t.ColId, [t.A], StyleAspect.HAlign, null);   // строк с расположением нет
        Assert.Empty(t.Repo.GetColumnStyles(t.ColId));

        using var ctx = new AppDbContext(db.Path);
        Assert.Empty(ctx.RecordDisplay.AsNoTracking().Where(d => d.CollectionId == t.ColId));
        Assert.Empty(ctx.ColumnMeta.AsNoTracking().Where(m => m.CollectionId == t.ColId && m.HAlignAt != null));
    }

    [Fact]
    public void Bulk_row_paint_is_stamped_like_a_manual_one()
    {
        using var db = new TempDatabase();
        var t = Seed(db.Constructor);

        t.Repo.SetColumnStyle(t.ColId, [t.A], StyleAspect.Background, Red);
        t.Repo.BulkSetRowStyle(t.ColId, [t.R1], StyleAspect.Background, Yellow, CancellationToken.None);

        Assert.Equal(Yellow, Seen(t, t.R1, t.A).Bg);   // свод красит позже колонки — виден он
        Assert.Equal(Red,    Seen(t, t.R2, t.A).Bg);
    }

    // ── PostgreSQL ────────────────────────────────────────────────────────────

    /// <summary>Снятие аспекта у ячеек — пакетные UPDATE/DELETE по списку записей или колонок:
    /// на сервере список идет массивом, на SQLite — json_each.</summary>
    [SkippableFact]
    public void The_rule_holds_on_postgres()
    {
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
        using (var conn = new NpgsqlConnection(cs))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
            cmd.ExecuteNonQuery();
        }

        using var m = DatabaseManager.Open(101, "pg1", cs, provider: "postgres");
        var t = Seed(m.Constructor);
        t.Repo.SetCellStyle(t.ColId, [(t.R1, t.A), (t.R2, t.A)], [(StyleAspect.HAlign, "right"), (StyleAspect.Background, Green)]);
        t.Repo.SetCellStyle(t.ColId, [(t.R1, t.B)], StyleAspect.HAlign, "right");

        t.Repo.SetColumnStyle(t.ColId, [t.A], StyleAspect.HAlign, "center");
        Assert.All([t.R1, t.R2], r => Assert.Equal(new RowStyle(Green, null), Cell(t, r, t.A)));

        t.Repo.SetRowStyle(t.ColId, [t.R1], StyleAspect.HAlign, "left");
        Assert.Equal(RowStyle.None, Cell(t, t.R1, t.B));
        Assert.Equal(CellHAlign.Left,   Seen(t, t.R1, t.A).HAlign);
        Assert.Equal(CellHAlign.Center, Seen(t, t.R2, t.A).HAlign);

        t.Repo.SetColumnStyle(t.ColId, [t.A], StyleAspect.HAlign, null);
        Assert.Null(Seen(t, t.R1, t.A).HAlign);
        Assert.Equal(CellHAlign.Left, Seen(t, t.R1, t.B).HAlign);
    }
}
