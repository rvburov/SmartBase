using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

public sealed class FileAutoCreationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Calendar_move_resolves_after_all_source_values_have_been_copied(bool background, bool textSource)
    {
        using var f = new Fixture();
        var source = SampleData.AddField(f.Repo, f.Collection, "Код", "text", position: 0).Id!.Value;
        if (textSource) f.Configure(new FileAutoRule(source.ToString(), f.Folder, "pdf"));
        var record = f.Repo.AddCalendarRecord(new CalendarRecord
        {
            CollectionId = f.Collection, Title = "Отчет", StartDt = DateTime.Today,
            EndDt = DateTime.Today.AddHours(1),
        });
        f.Repo.SetFieldValue(record.Id!.Value, source, "Отчет");
        Assert.Equal(f.PathFor("Отчет"), f.Value(record.Id.Value));

        var target = SampleData.AddCollection(f.Repo, "Целевая").Id!.Value;
        var targetSource = SampleData.AddField(f.Repo, target, "Код", "text", position: 0).Id!.Value;
        var folder = Path.Combine(f.Folder, "Целевая");
        Directory.CreateDirectory(folder);
        var expected = Path.Combine(folder, (textSource ? "Отчет" : "Смета") + ".pdf");
        File.WriteAllText(expected, "файл цели");
        var file = SampleData.AddField(f.Repo, target, "Файл", "file");
        file.ConfigJson = new FileAutoRule(textSource ? targetSource.ToString() : FileAutoRule.TitleSource, folder, "pdf").ToConfigJson();
        f.Repo.UpdateField(file);
        using var pending = background ? new Pending(f) : null;
        var originalId = record.Id.Value;
        record.CollectionId = target;
        record.Title = "Смета";

        f.Repo.UpdateCalendarRecord(record);
        if (pending is not null)
        {
            Assert.NotEmpty(pending.Work);
            foreach (var work in pending.Work)
                Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok, ConstructorRepository.ExecuteFileAutoWork(work));
        }

        using var read = new AppDbContext(f.Database.Path);
        Assert.False(read.DbRecords.Any(r => r.Id == originalId));
        Assert.Equal(expected, read.DbFieldValues.AsNoTracking()
            .Single(v => v.RecordId == record.Id && v.FieldId == file.Id).Value);
        Assert.True(File.Exists(f.PathFor("Отчет")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Renaming_through_record_or_calendar_update_rebinds_the_file(bool calendar)
    {
        using var f = new Fixture();
        var record = SampleData.AddRecord(f.Repo, f.Collection, "Отчет");
        Assert.Equal(f.PathFor("Отчет"), f.Value(record.Id!.Value));

        if (calendar)
            f.Repo.UpdateCalendarRecord(new CalendarRecord
            {
                Id = record.Id, CollectionId = f.Collection, Title = "Смета",
                StartDt = DateTime.Today, EndDt = DateTime.Today.AddHours(1),
            });
        else
        {
            record.Title = "Смета";
            f.Repo.UpdateRecord(record);
        }

        Assert.Equal(f.PathFor("Смета"), f.Value(record.Id.Value));
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("isolated")]
    [InlineData("calendar")]
    public void A_record_created_with_a_ready_title_resolves_its_file_without_a_later_edit(string route)
    {
        using var f = new Fixture();
        var record = route switch
        {
            "isolated" => f.Repo.AddRecordIsolated(new DbRecord { CollectionId = f.Collection, Title = "Отчет" }, null).Id!.Value,
            "calendar" => f.Repo.AddCalendarRecord(new CalendarRecord
            {
                CollectionId = f.Collection, Title = "Отчет", StartDt = DateTime.Today,
                EndDt = DateTime.Today.AddHours(1),
            }).Id!.Value,
            _ => SampleData.AddRecord(f.Repo, f.Collection, "Отчет").Id!.Value,
        };

        Assert.Equal(f.PathFor("Отчет"), f.Value(record));
    }

    [Fact]
    public void Creating_a_record_queues_background_work_only_after_commit_and_rollback_discards_it()
    {
        using var f = new Fixture();
        using var pending = new Pending(f);
        int rolledBack;
        using (var transaction = f.Database.Context.Database.BeginTransaction())
        {
            rolledBack = SampleData.AddRecord(f.Repo, f.Collection, "Смета").Id!.Value;
            Assert.Empty(pending.Work);
            transaction.Rollback();
        }
        f.Database.Context.ChangeTracker.Clear();
        Assert.Empty(pending.Work);
        Assert.False(f.Database.Context.DbRecords.Any(r => r.Id == rolledBack));

        int created;
        using (var transaction = f.Database.Context.Database.BeginTransaction())
        {
            created = SampleData.AddRecord(f.Repo, f.Collection, "Отчет").Id!.Value;
            Assert.Empty(pending.Work);
            transaction.Commit();
        }
        var work = Assert.Single(pending.Work);
        Assert.Equal(created, work.RecordId);
        Assert.Equal(f.FileField, work.FieldId);
        Assert.Equal("", f.Value(created));
        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok, ConstructorRepository.ExecuteFileAutoWork(work));
        Assert.Equal(f.PathFor("Отчет"), f.Value(created));
    }

    [Fact]
    public void Creation_and_source_edits_honor_the_disabled_toggle_and_preserve_manual_paths()
    {
        using var f = new Fixture(enabled: false);
        using var pending = new Pending(f);
        var record = SampleData.AddRecord(f.Repo, f.Collection, "Отчет").Id!.Value;
        f.Repo.SetFieldValue(record, f.FileField, f.PathFor("вручную"));
        f.Repo.SetRecordTitle(record, "Смета");

        Assert.Empty(pending.Work);
        Assert.Equal(f.PathFor("вручную"), f.Value(record));
    }

    [Fact]
    public void Text_sources_and_existing_text_are_resolved_without_changing_the_source()
    {
        using var f = new Fixture(enabled: false);
        var source = SampleData.AddField(f.Repo, f.Collection, "Код", "text").Id!.Value;
        var record = SampleData.AddRecord(f.Repo, f.Collection, "Другая подпись").Id!.Value;
        f.Repo.SetFieldValue(record, source, "Отчет");
        f.Configure(new FileAutoRule(source.ToString(), f.Folder, "pdf"));

        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok, f.Repo.ResolveAutoFileField(f.FileField));
        Assert.Equal(f.PathFor("Отчет"), f.Value(record));
        f.Repo.SetFieldValue(record, source, "Смета");
        Assert.Equal(f.PathFor("Смета"), f.Value(record));
        Assert.Equal("Смета", f.Database.Context.DbFieldValues.AsNoTracking()
            .Single(v => v.RecordId == record && v.FieldId == source).Value);
    }

    [Fact]
    public void An_unavailable_folder_does_not_prevent_creation_or_invent_a_file_path()
    {
        using var f = new Fixture();
        f.Configure(new FileAutoRule(FileAutoRule.TitleSource, Path.Combine(f.Folder, "недоступно"), "pdf"));

        var record = SampleData.AddRecord(f.Repo, f.Collection, "Отчет").Id!.Value;

        Assert.Equal("", f.Value(record));
        Assert.Equal("Отчет", f.Database.Context.DbRecords.AsNoTracking().Single(r => r.Id == record).Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_invalid_source_from_another_collection_cannot_fill_or_queue_files(bool background)
    {
        using var f = new Fixture(enabled: false);
        var other = SampleData.AddCollection(f.Repo, "Другая коллекция").Id!.Value;
        var foreign = SampleData.AddField(f.Repo, other, "Чужой источник", "text").Id!.Value;
        var record = SampleData.AddRecord(f.Repo, f.Collection, "Отчет").Id!.Value;
        f.Configure(new FileAutoRule(foreign.ToString(), f.Folder, "pdf"));
        using var pending = background ? new Pending(f) : null;

        // Несогласованная пара не должна появиться в БД даже через публичную запись ячейки.
        Assert.Throws<InvalidOperationException>(() => f.Repo.SetFieldValue(record, foreign, "Отчет"));
        Assert.False(f.Database.Context.DbFieldValues.Any(value => value.RecordId == record && value.FieldId == foreign));

        // При обычной правке старое некорректное правило тоже не должно читать чужое поле.
        f.Repo.SetRecordTitle(record, "Смета");

        Assert.Equal("", f.Value(record));
        if (pending is not null) Assert.Empty(pending.Work);
    }

    private sealed class Pending : IDisposable
    {
        private readonly Fixture _fixture;
        public List<FileAutoWork> Work { get; } = [];
        public Pending(Fixture fixture)
        {
            _fixture = fixture;
            ConstructorRepository.FileAutoWorkRequested += Receive;
        }
        private void Receive(FileAutoWork work)
        {
            if (work.DbPath == _fixture.Database.Path) Work.Add(work);
        }
        public void Dispose() => ConstructorRepository.FileAutoWorkRequested -= Receive;
    }

    private sealed class Fixture : IDisposable
    {
        public TempDatabase Database { get; } = new();
        public ConstructorRepository Repo => Database.Constructor;
        public string Folder { get; } = Path.Combine(Path.GetTempPath(), "sb_file_create_" + Guid.NewGuid().ToString("N"));
        public int Collection { get; }
        public int FileField { get; }
        public Fixture(bool enabled = true)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PathFor("Отчет"), "отчет");
            File.WriteAllText(PathFor("Смета"), "смета");
            Collection = SampleData.AddCollection(Repo).Id!.Value;
            FileField = SampleData.AddField(Repo, Collection, "Файл", "file").Id!.Value;
            Configure(new FileAutoRule(FileAutoRule.TitleSource, Folder, "pdf", Enabled: enabled));
        }
        public string PathFor(string name) => Path.Combine(Folder, name + ".pdf");
        public string Value(int record)
        {
            using var read = new AppDbContext(Database.Path);
            return read.DbFieldValues.AsNoTracking().Where(v => v.RecordId == record && v.FieldId == FileField)
                .Select(v => v.Value).SingleOrDefault() ?? "";
        }
        public void Configure(FileAutoRule rule)
        {
            var field = Repo.GetFields(Collection).Single(f => f.Id == FileField);
            field.ConfigJson = rule.ToConfigJson();
            Repo.UpdateField(field);
        }

        public void Dispose()
        {
            Database.Dispose();
            try { Directory.Delete(Folder, recursive: true); } catch { }
        }
    }
}
