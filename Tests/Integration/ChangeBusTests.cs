using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Шина своих изменений (<see cref="ChangeBus"/>): любой коммит в базу поднимает событие с путем
/// базы и типами затронутых объектов; внутри транзакции событие ждет коммита и на откате не
/// поднимается; изолированное фоновое подключение публикует под тем же путем; UI-таблицы
/// (ширины колонок) шину не трогают.
/// </summary>
public sealed class ChangeBusTests
{
    // Событие статическое, тесты идут параллельно — слушаем только свою базу (по пути).
    private sealed class Listener : IDisposable
    {
        private readonly string _path;
        private readonly List<IReadOnlyList<ChangeBus.Change>> _events = [];

        public Listener(string path)
        {
            _path = path;
            ChangeBus.Committed += On;
        }

        private void On(string path, IReadOnlyList<ChangeBus.Change> changes)
        {
            if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) return;
            lock (_events) _events.Add(changes);
        }

        public int Count
        {
            get { lock (_events) return _events.Count; }
        }

        public HashSet<string> Types
        {
            get { lock (_events) return _events.SelectMany(e => e).Select(c => c.TargetType).ToHashSet(); }
        }

        public void Clear()
        {
            lock (_events) _events.Clear();
        }

        public void Dispose() => ChangeBus.Committed -= On;
    }

    private static (int ColId, int FieldId, int RecordId) Seed(ConstructorRepository repo)
    {
        var col = SampleData.AddCollection(repo, "Шина");
        var f   = SampleData.AddField(repo, col.Id!.Value, "Текст");
        var r   = SampleData.AddRecord(repo, col.Id!.Value, "Запись");
        return (col.Id!.Value, f.Id!.Value, r.Id!.Value);
    }

    [Fact]
    public void Cell_edit_publishes_record_change_for_its_database()
    {
        using var db = new TempDatabase();
        var (_, fieldId, recordId) = Seed(db.Constructor);

        using var bus = new Listener(db.Path);
        db.Constructor.SetFieldValue(recordId, fieldId, "значение");

        Assert.True(bus.Count >= 1);
        Assert.Contains("record", bus.Types);
        Assert.DoesNotContain(bus.Types, ChangeBus.IsStructural);
    }

    [Fact]
    public void Collection_and_field_changes_are_structural()
    {
        using var db = new TempDatabase();
        using var bus = new Listener(db.Path);

        var col = SampleData.AddCollection(db.Constructor, "Структура");
        Assert.Contains("collection", bus.Types);

        bus.Clear();
        SampleData.AddField(db.Constructor, col.Id!.Value, "Поле");
        Assert.Contains("field", bus.Types);
        Assert.Contains(bus.Types, ChangeBus.IsStructural);
    }

    [Fact]
    public void Transaction_publishes_once_and_only_on_commit()
    {
        using var db = new TempDatabase();
        var (colId, fieldId, _) = Seed(db.Constructor);

        using var bus = new Listener(db.Path);
        using (var tx = db.Context.Database.BeginTransaction())
        {
            var r = SampleData.AddRecord(db.Constructor, colId, "В транзакции");
            db.Constructor.SetFieldValue(r.Id!.Value, fieldId, "значение");
            Assert.Equal(0, bus.Count);   // до коммита соседи изменений не видят — и экран тоже

            tx.Commit();
        }

        Assert.Equal(1, bus.Count);
        Assert.Contains("record", bus.Types);
    }

    [Fact]
    public void Rolled_back_transaction_publishes_nothing_and_next_write_still_does()
    {
        using var db = new TempDatabase();
        var (colId, _, _) = Seed(db.Constructor);

        using var bus = new Listener(db.Path);
        using (var tx = db.Context.Database.BeginTransaction())
        {
            SampleData.AddRecord(db.Constructor, colId, "Откат");
            tx.Rollback();
        }
        db.Context.ChangeTracker.Clear();   // как WriteScope после отката
        Assert.Equal(0, bus.Count);

        SampleData.AddRecord(db.Constructor, colId, "После отката");
        Assert.True(bus.Count >= 1);
    }

    [Fact]
    public void Isolated_background_context_publishes_under_the_same_path()
    {
        using var db = new TempDatabase();
        var (colId, _, recordId) = Seed(db.Constructor);
        var second = SampleData.AddRecord(db.Constructor, colId, "Вторая");

        using var bus = new Listener(db.Path);
        // Массовый путь: свое подключение, транзакция на все, строка журнала одним LogNow.
        db.Constructor.DeleteRecordsIsolated([recordId, second.Id!.Value]);

        Assert.True(bus.Count >= 1);
        Assert.Contains(bus.Types, t => t is "record" or "records");
    }

    [Fact]
    public void Ui_only_writes_stay_silent()
    {
        using var db = new TempDatabase();
        var (colId, fieldId, _) = Seed(db.Constructor);

        using var bus = new Listener(db.Path);
        db.Constructor.SaveColWidths(colId, new Dictionary<int, int> { [fieldId] = 240 });

        Assert.Equal(0, bus.Count);
    }
}
