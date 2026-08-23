using Microsoft.EntityFrameworkCore;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Многопользовательская запись в один SQLite-файл (перенос сценария Tests/MultiUserHarness
/// --contend): два независимых подключения наперегонки пишут одну ячейку. Ожидание —
/// «последний побеждает», без падений (busy_timeout ждет чужого писателя, PK-конфликт
/// превращается в UPDATE), целостность файла сохранена.
/// </summary>
public sealed class ContentionTests
{
    [Fact]
    public async Task Concurrent_writes_to_one_cell_are_last_write_wins_and_keep_integrity()
    {
        using var db = new TempDatabase();
        var col   = SampleData.AddCollection(db.Constructor);
        var field = SampleData.AddField(db.Constructor, col.Id!.Value, "V");
        var rec   = SampleData.AddRecord(db.Constructor, col.Id!.Value, "R");
        int recId = rec.Id!.Value, fieldId = field.Id!.Value;
        var path  = db.Path;

        // Каждый писатель — свое подключение (общий контекст однопоточный, поэтому не
        // используем его из потоков; это и есть модель «двух пользователей»).
        void Writer(string tag)
        {
            using var ctx = new AppDbContext(path);
            ctx.ApplyPerformancePragmas();
            var repo = new ConstructorRepository(ctx); // Permissions == null → Demand no-op
            for (var i = 0; i < 50; i++)
                repo.SetFieldValue(recId, fieldId, $"{tag}{i}");
        }

        var t1 = Task.Run(() => Writer("A"));
        var t2 = Task.Run(() => Writer("B"));
        await Task.WhenAll(t1, t2);   // не должно бросить SQLITE_BUSY / конфликтом

        using var check = new AppDbContext(path);

        // Ровно одна строка значения на ячейку; значение — от одного из писателей.
        var values = check.DbFieldValues.Where(v => v.RecordId == recId && v.FieldId == fieldId).ToList();
        Assert.Single(values);
        Assert.True(values[0].Value.StartsWith('A') || values[0].Value.StartsWith('B'));

        // Файл БД не поврежден.
        var conn = check.Database.GetDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", (string)cmd.ExecuteScalar()!);
    }
}
