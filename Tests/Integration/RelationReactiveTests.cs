using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>Обычные операции репозитория пересчитывают правила сами: без окна, ручного Refresh
/// и ожидания фонового поллера. Неподтвержденные данные не меняют материализованные связи.</summary>
public sealed class RelationReactiveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Editing_the_source_property_or_title_updates_and_clears_links_immediately(bool title)
    {
        using var f = new Fixture(sourceTitle: title);
        var record = f.AddSource("А");
        var first = f.AddTarget("А");
        var second = f.AddTarget("Б");
        f.Enable();
        Assert.Equal([first], f.Repo.GetLinkTargetIds(f.Relation, record));

        f.EditSource(record, "Б");
        Assert.Equal([second], f.Repo.GetLinkTargetIds(f.Relation, record));
        f.EditSource(record, "");
        Assert.Empty(f.Repo.GetLinkEdges(f.Relation, record));
        f.EditSource(record, "А");
        Assert.Equal([first], f.Repo.GetLinkTargetIds(f.Relation, record));
    }

    [Fact]
    public void Target_key_edits_additions_and_deletions_update_all_matching_sources()
    {
        using var f = new Fixture();
        var a = f.AddSource("А");
        var b = f.AddSource("Б");
        var first = f.AddTarget("А");
        var second = f.AddTarget("Б");
        f.Enable();

        f.TargetRepo.SetFieldValue(first, f.TargetKey, "Б");
        Assert.Empty(f.Repo.GetLinkEdges(f.Relation, a));
        Assert.Equal([first, second], f.Repo.GetLinkTargetIds(f.Relation, b));
        var third = f.AddTarget("А");
        Assert.Equal([third], f.Repo.GetLinkTargetIds(f.Relation, a));
        Assert.Equal([first, second], f.Repo.GetLinkTargetIds(f.Relation, b));
        f.TargetRepo.DeleteRecord(first);
        Assert.Equal([second], f.Repo.GetLinkTargetIds(f.Relation, b));
        Assert.Equal([third], f.Repo.GetLinkTargetIds(f.Relation, a));
    }

    [Fact]
    public void Target_title_changes_and_new_records_are_inputs_without_an_extra_property_write()
    {
        using var f = new Fixture(targetTitle: true);
        var source = f.AddSource("А");
        var first = f.AddTarget("А");
        f.Enable();

        var second = SampleData.AddRecord(f.TargetRepo, f.Target, "А").Id!.Value;
        Assert.Equal([first, second], f.Repo.GetLinkTargetIds(f.Relation, source));
        f.TargetRepo.SetRecordTitle(first, "Б");
        Assert.Equal([second], f.Repo.GetLinkTargetIds(f.Relation, source));
        f.TargetRepo.DeleteRecord(second);
        Assert.Empty(f.Repo.GetLinkEdges(f.Relation, source));
        f.TargetRepo.SetRecordTitle(first, "А");
        Assert.Equal([first], f.Repo.GetLinkTargetIds(f.Relation, source));
    }

    [Fact]
    public void Removing_one_ambiguous_single_target_rebuilds_the_remaining_link()
    {
        using var f = new Fixture(targetTitle: true);
        var source = f.AddSource("А");
        var first = f.AddTarget("А");
        f.Enable(single: true);
        var second = SampleData.AddRecord(f.TargetRepo, f.Target, "А").Id!.Value;
        Assert.Equal([first], f.Repo.GetLinkTargetIds(f.Relation, source));

        // Каскад удаления первого ребра недостаточен: правило должно выбрать оставшуюся цель.
        f.TargetRepo.DeleteRecord(first);
        Assert.Equal([second], f.Repo.GetLinkTargetIds(f.Relation, source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Outer_write_transaction_refreshes_only_after_commit_and_discards_rolled_back_work(bool commit)
    {
        using var f = new Fixture();
        var a = f.AddSource("А");
        var b = f.AddSource("Б");
        var first = f.AddTarget("А");
        var second = f.AddTarget("Б");
        f.Enable();

        int EditBoth() => f.Repo.InWriteTransaction(() =>
        {
            f.Repo.SetFieldValue(a, f.SourceKey, "Б");
            f.Repo.SetFieldValue(b, f.SourceKey, "А");
            Assert.Equal([first], f.Repo.GetLinkTargetIds(f.Relation, a));
            Assert.Equal([second], f.Repo.GetLinkTargetIds(f.Relation, b));
            if (!commit) throw new RollbackRequested();
            return 2;
        });

        if (commit) Assert.Equal(2, EditBoth());
        else Assert.Throws<RollbackRequested>(() => EditBoth());
        Assert.Equal([commit ? second : first], f.Repo.GetLinkTargetIds(f.Relation, a));
        Assert.Equal([commit ? first : second], f.Repo.GetLinkTargetIds(f.Relation, b));
        Assert.Equal(commit ? "Б" : "А", f.StoredValue(a));
        Assert.Equal(commit ? "А" : "Б", f.StoredValue(b));
        if (!commit)
        {
            // Следующий коммит не должен выпускать отложенную правку второй записи из отката.
            f.Repo.InWriteTransaction(() => { f.Repo.SetFieldValue(a, f.SourceKey, "Б"); return 1; });
            Assert.Equal([second], f.Repo.GetLinkTargetIds(f.Relation, a));
            Assert.Equal([second], f.Repo.GetLinkTargetIds(f.Relation, b));
            Assert.Equal("Б", f.StoredValue(b));
        }
    }

    [Fact]
    public void Cross_database_target_edits_refresh_the_source_and_a_closed_target_preserves_old_edges()
    {
        using var target = new TempDatabase(dbId: 202, name: "reactive-target");
        using var f = new Fixture(externalTarget: target);
        using var available = RelationDbResolver.UseScopedResolver(
            id => id == 101 ? f.Repo : id == 202 ? target.Constructor : null,
            repo => repo.DbPath == f.Database.Path ? 101 : repo.DbPath == target.Path ? 202 : 0);
        var record = f.AddSource("А");
        var first = f.AddTarget("А");
        var second = f.AddTarget("Б");
        f.Enable();
        Assert.Equal([(first, 202)], f.Repo.GetLinkEdges(f.Relation, record));
        // Регистрируем источник именно обычной правкой, без обращения к внутреннему реестру.
        f.EditSource(record, "Б");
        Assert.Equal([(second, 202)], f.Repo.GetLinkEdges(f.Relation, record));
        f.EditSource(record, "А");
        Assert.Equal([(first, 202)], f.Repo.GetLinkEdges(f.Relation, record));

        target.Constructor.SetFieldValue(first, f.TargetKey, "Снят");
        Assert.Empty(f.Repo.GetLinkEdges(f.Relation, record));
        target.Constructor.SetFieldValue(second, f.TargetKey, "А");
        Assert.Equal([(second, 202)], f.Repo.GetLinkEdges(f.Relation, record));
        using (RelationDbResolver.UseScopedResolver(_ => null,
                   repo => repo.DbPath == f.Database.Path ? 101 : repo.DbPath == target.Path ? 202 : 0))
        {
            f.EditSource(record, "Нет совпадений");
            Assert.Equal("Нет совпадений", f.StoredValue(record));
            Assert.Equal([(second, 202)], f.Repo.GetLinkEdges(f.Relation, record));
        }
        f.EditSource(record, "Б");
        Assert.Empty(f.Repo.GetLinkEdges(f.Relation, record));
    }

    private sealed class RollbackRequested : Exception { }

    private sealed class Fixture : IDisposable
    {
        public TempDatabase Database { get; } = new(dbId: 101, name: "reactive-source");
        public ConstructorRepository Repo => Database.Constructor;
        public ConstructorRepository TargetRepo { get; }
        public int Source { get; }
        public int Target { get; }
        public int SourceKey { get; }
        public int TargetKey { get; }
        public int Relation { get; }
        private readonly bool _sourceTitle;
        private readonly bool _targetTitle;
        private readonly int _targetDbId;

        public Fixture(bool sourceTitle = false, bool targetTitle = false, TempDatabase? externalTarget = null)
        {
            _sourceTitle = sourceTitle;
            _targetTitle = targetTitle;
            _targetDbId = externalTarget is null ? 0 : 202;
            TargetRepo = externalTarget?.Constructor ?? Repo;
            Source = SampleData.AddCollection(Repo, "Заказы").Id!.Value;
            Target = SampleData.AddCollection(TargetRepo, "Клиенты").Id!.Value;
            SourceKey = SampleData.AddField(Repo, Source, "Ключ заказа").Id!.Value;
            TargetKey = SampleData.AddField(TargetRepo, Target, "Ключ клиента").Id!.Value;
            Relation = Repo.AddField(new DbField
            {
                CollectionId = Source, Name = "Клиент", FieldType = "relation",
                ConfigJson = new RelationConfig(Target, false, TargetDbId: _targetDbId).ToConfigJson(),
            }).Id!.Value;
        }

        public int AddSource(string key)
        {
            var record = SampleData.AddRecord(Repo, Source, _sourceTitle ? key : "Заказ").Id!.Value;
            Repo.SetFieldValue(record, SourceKey, key);
            return record;
        }

        public int AddTarget(string key)
        {
            var record = SampleData.AddRecord(TargetRepo, Target, _targetTitle ? key : "Клиент").Id!.Value;
            if (!_targetTitle) TargetRepo.SetFieldValue(record, TargetKey, key);
            return record;
        }

        public void EditSource(int record, string key)
        {
            if (_sourceTitle) Repo.SetRecordTitle(record, key);
            else Repo.SetFieldValue(record, SourceKey, key);
        }

        public string StoredValue(int record) => Database.Context.DbFieldValues.AsNoTracking()
            .Single(v => v.RecordId == record && v.FieldId == SourceKey).Value;

        public void Enable(bool single = false)
        {
            var field = Repo.GetFields(Source).Single(f => f.Id == Relation);
            field.ConfigJson = new RelationConfig(Target, single, TargetDbId: _targetDbId,
                Auto: new RelationAutoRule(_sourceTitle ? RelationConfig.TitleField : SourceKey.ToString(),
                    _targetTitle ? RelationConfig.TitleField : TargetKey.ToString())).ToConfigJson();
            var preview = Repo.PreviewAutoRelation(field);
            Assert.True(preview.CanApply, preview.Message);
            Repo.SaveAutoRelationField(field, preview);
        }

        public void Dispose() => Database.Dispose();
    }
}
