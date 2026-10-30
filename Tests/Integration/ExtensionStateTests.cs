using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>Квитанция расширения переживает новое подключение и коммитится вместе с данными.</summary>
public sealed class ExtensionStateTests : IDisposable
{
    private readonly TempDatabase _db = new();

    [Fact]
    public void State_survives_reopening_a_context_and_extension_namespaces_are_separate()
    {
        _db.Constructor.WriteExtensionState("first-stand", "receipt", "first result");
        _db.Constructor.WriteExtensionState("second-stand", "receipt", "second result");

        using var context = new AppDbContext(_db.Path);
        var repo = new ConstructorRepository(context);
        Assert.Equal("first result", repo.ReadExtensionState("first-stand", "receipt"));
        Assert.Equal("second result", repo.ReadExtensionState("second-stand", "receipt"));
        Assert.Null(repo.ReadExtensionState("third-stand", "receipt"));
    }

    [Fact]
    public void Read_does_not_reuse_a_stale_value_after_another_context_changes_it()
    {
        _db.Constructor.WriteExtensionState("stand", "receipt", "before");
        Assert.Equal("before", _db.Constructor.ReadExtensionState("stand", "receipt"));
        using (var context = new AppDbContext(_db.Path))
            new ConstructorRepository(context).WriteExtensionState("stand", "receipt", "after");

        Assert.Equal("after", _db.Constructor.ReadExtensionState("stand", "receipt"));
    }

    [Fact]
    public void A_committed_receipt_and_its_record_are_both_visible_on_another_connection()
    {
        var collection = SampleData.AddCollection(_db.Constructor, "Заявки").Id!.Value;
        _db.Constructor.InCollectionWriteTransaction(collection, () =>
        {
            _db.Constructor.AddRecord(new DbRecord { CollectionId = collection, Title = "Принятая заявка" });
            _db.Constructor.WriteExtensionState("stand", "receipt", "saved result");
            return 0;
        });

        using var context = new AppDbContext(_db.Path);
        var repo = new ConstructorRepository(context);
        Assert.Equal("Принятая заявка", Assert.Single(repo.GetRecords(collection)).Title);
        Assert.Equal("saved result", repo.ReadExtensionState("stand", "receipt"));
    }

    [Fact]
    public void Failure_rolls_back_the_receipt_and_record_together()
    {
        var collection = SampleData.AddCollection(_db.Constructor, "Заявки").Id!.Value;
        Assert.Throws<InvalidOperationException>(() => _db.Constructor.InCollectionWriteTransaction<int>(collection, () =>
        {
            _db.Constructor.AddRecord(new DbRecord { CollectionId = collection, Title = "Не завершена" });
            _db.Constructor.WriteExtensionState("stand", "receipt", "must not survive");
            throw new InvalidOperationException("Имитирован отказ записи");
        }));

        using var context = new AppDbContext(_db.Path);
        var repo = new ConstructorRepository(context);
        Assert.Empty(repo.GetRecords(collection));
        Assert.Null(repo.ReadExtensionState("stand", "receipt"));
    }

    [Fact]
    public void Failed_receipt_update_preserves_the_previous_committed_result()
    {
        var collection = SampleData.AddCollection(_db.Constructor, "Заявки").Id!.Value;
        _db.Constructor.WriteExtensionState("stand", "receipt", "previous");
        Assert.Throws<InvalidOperationException>(() => _db.Constructor.InCollectionWriteTransaction<int>(collection, () =>
        {
            _db.Constructor.WriteExtensionState("stand", "receipt", "uncommitted");
            throw new InvalidOperationException("Имитирован отказ после обновления");
        }));

        using (var context = new AppDbContext(_db.Path))
            Assert.Equal("previous", new ConstructorRepository(context).ReadExtensionState("stand", "receipt"));
        _db.Constructor.WriteExtensionState("stand", "receipt", "next");
        using var fresh = new AppDbContext(_db.Path);
        Assert.Equal("next", new ConstructorRepository(fresh).ReadExtensionState("stand", "receipt"));
    }

    [Fact]
    public void An_extension_identifier_cannot_escape_its_metadata_namespace()
    {
        Assert.Throws<ArgumentException>(() => _db.Constructor.WriteExtensionState("other:stand", "key", "value"));
        Assert.Throws<ArgumentException>(() => _db.Constructor.ReadExtensionState("", "key"));
        Assert.Throws<ArgumentException>(() => _db.Constructor.ReadExtensionState("stand", ""));
        Assert.Throws<ArgumentException>(() => _db.Constructor.WriteExtensionState("stand", new string('x', 257), "value"));
    }

    public void Dispose() => _db.Dispose();
}
