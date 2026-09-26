using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Номера новых записей (<c>AllocatePositions</c>): копия и вставка встают сразу за якорем, при
/// нехватке места раздвигаются только ближайшие следующие записи, номера не выходят за int.
/// Прежде нехватка места перенумеровывала всю коллекцию шагом 1000 (на 10 млн записей — две
/// минуты на UI-потоке, номера за пределами int), а массовый дубль сдвигал весь хвост. И
/// миграция <c>RepairRecordPositions</c>, чинящая базы, испорченные теми путями.
/// </summary>
public sealed class RecordPositionTests
{
    // Записи с заданными номерами одним запросом (порядок вставки = порядок Id).
    private static List<int> Seed(TempDatabase db, int colId, IEnumerable<long> positions)
    {
        var conn = db.Context.Database.GetDbConnection();
        db.Context.Database.OpenConnection();
        try
        {
            var ids = new List<int>();
            foreach (var p in positions)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO db_records (CollectionId, CreatedAt, Position, AllDay, Title) " +
                                  $"VALUES ({colId}, '', {p}, 0, 'R') RETURNING Id";
                ids.Add(Convert.ToInt32(cmd.ExecuteScalar()));
            }
            return ids;
        }
        finally { db.Context.Database.CloseConnection(); }
    }

    private static List<(int Id, long Pos)> Order(TempDatabase db, int colId)
    {
        var conn = db.Context.Database.GetDbConnection();
        db.Context.Database.OpenConnection();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT Id, Position FROM db_records WHERE CollectionId = {colId} ORDER BY Position, Id";
            using var rd = cmd.ExecuteReader();
            var list = new List<(int, long)>();
            while (rd.Read()) list.Add((rd.GetInt32(0), rd.GetInt64(1)));
            return list;
        }
        finally { db.Context.Database.CloseConnection(); }
    }

    private static int NewCollection(TempDatabase db) => SampleData.AddCollection(db.Constructor).Id!.Value;

    [Fact]
    public void A_copy_in_a_dense_run_moves_only_the_records_up_to_the_next_gap()
    {
        using var db = new TempDatabase();
        var col = NewCollection(db);
        // R1..R10 вплотную (1..10), дальше — запись с запасом места.
        var ids = Seed(db, col, Enumerable.Range(1, 10).Select(i => (long)i).Append(100_000));

        var copy = db.Constructor.DuplicateRecord(ids[2]).Id!.Value;

        var order = Order(db, col);
        Assert.Equal(ids.Take(3).Append(copy).Concat(ids.Skip(3)), order.Select(o => o.Id));
        // Номера до якоря и запись за разрывом не тронуты; все номера разные.
        Assert.Equal([1L, 2L, 3L], order.Take(3).Select(o => o.Pos));
        Assert.Equal(100_000, order[^1].Pos);
        Assert.Equal(order.Count, order.Select(o => o.Pos).Distinct().Count());
    }

    [Fact]
    public void A_copy_in_a_run_longer_than_the_window_takes_the_source_number()
    {
        using var db = new TempDatabase();
        var col = NewCollection(db);
        // 1100 записей вплотную: раздвинуть не хватит окна — копия встает вплотную за исходной.
        var ids = Seed(db, col, Enumerable.Range(1, 1100).Select(i => (long)i));
        var before = Order(db, col);

        var first  = db.Constructor.DuplicateRecord(ids[9]).Id!.Value;
        var second = db.Constructor.DuplicateRecord(ids[9]).Id!.Value;

        var order = Order(db, col);
        var at = order.FindIndex(o => o.Id == ids[9]);
        // Обе копии между исходной и следующей записью; прочие номера не менялись.
        Assert.Equal([ids[9], first, second, ids[10]], order.Skip(at).Take(4).Select(o => o.Id));
        Assert.Equal(before, order.Where(o => o.Id != first && o.Id != second));
    }

    [Fact]
    public void Appending_near_the_top_of_int_keeps_numbers_in_range_and_order()
    {
        using var db = new TempDatabase();
        var col = NewCollection(db);
        Seed(db, col, [1000, int.MaxValue - 10]);

        var added = new List<int>();
        for (var i = 0; i < 20; i++)
            added.Add(db.Constructor.AddRecordIsolated(
                new SmartBase.Core.Models.DbRecord { CollectionId = col, Title = $"N{i}" }, null).Id!.Value);

        var order = Order(db, col);
        Assert.Equal(added, order.Skip(2).Select(o => o.Id));
        Assert.All(order, o => Assert.InRange(o.Pos, int.MinValue, int.MaxValue));
        // Запись и читается целиком (раньше номер за int ронял чтение с переполнением).
        Assert.NotNull(db.Constructor.GetRecord(added[^1]));
    }

    [Fact]
    public void A_bulk_copy_leaves_the_tail_in_place()
    {
        using var db = new TempDatabase();
        var col = NewCollection(db);
        // R1..R10 вплотную, дальше длинный хвост с шагом 1000.
        var ids = Seed(db, col, Enumerable.Range(1, 10).Select(i => (long)i)
                                   .Concat(Enumerable.Range(1, 50).Select(i => 1_000_000L + i * 1000)));
        var tailBefore = Order(db, col).Skip(10).ToList();

        var copies = db.Constructor.DuplicateRecordsData(col, [ids[1], ids[2], ids[3]]);

        var order = Order(db, col);
        Assert.Equal(ids.Take(4).Concat(copies).Concat(ids.Skip(4)), order.Select(o => o.Id));
        Assert.Equal(tailBefore, order.Skip(13).ToList());
        Assert.Equal(order.Count, order.Select(o => o.Pos).Distinct().Count());
    }

    [Fact]
    public void The_repair_migration_renumbers_only_collections_with_numbers_beyond_int()
    {
        var path = TempDatabase.NewTempPath();
        try
        {
            using (var old = new AppDbContext(path))
            {
                old.GetService<IMigrator>().Migrate("20260926160000_AddCalendarSpans");
                var conn = old.Database.GetDbConnection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO db_collections (Id, Name, Position, Icon, Color) VALUES (1, 'Сломанная', 1000, '', ''), (2, 'Целая', 2000, '', '');
                    INSERT INTO db_records (Id, CollectionId, CreatedAt, Position, AllDay, Title) VALUES
                      (1, 1, '', 5000000000, 0, 'в'), (2, 1, '', 1000, 0, 'а'), (3, 1, '', 3000000000, 0, 'б'),
                      (4, 1, '', 3000000000, 0, 'б2'), (5, 2, '', 7, 0, 'x'), (6, 2, '', 5, 0, 'y');
                    """;
                cmd.ExecuteNonQuery();
            }

            using (var ctx = new AppDbContext(path))
            {
                ctx.Database.Migrate();
                var conn = ctx.Database.GetDbConnection();
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT Id, Position FROM db_records ORDER BY Id";
                using var rd = cmd.ExecuteReader();
                var pos = new Dictionary<int, long>();
                while (rd.Read()) pos[rd.GetInt32(0)] = rd.GetInt64(1);

                // Порядок (Position, Id) прежний: а, б, б2, в — с шагом 1000.
                Assert.Equal(1000, pos[2]);
                Assert.Equal(2000, pos[3]);
                Assert.Equal(3000, pos[4]);
                Assert.Equal(4000, pos[1]);
                // Целую коллекцию миграция не трогает.
                Assert.Equal(7, pos[5]);
                Assert.Equal(5, pos[6]);
            }
        }
        finally { TempDatabase.Cleanup(path); }
    }
}
