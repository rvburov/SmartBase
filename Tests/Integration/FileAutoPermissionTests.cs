using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Importers;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>Изолированная запись не теряет ограничения последующей автоподстановки.
/// Права отзываются в самой базе, пока живой кеш окна еще разрешает работу.</summary>
public sealed class FileAutoPermissionTests
{
    [Theory]
    [InlineData("bulk", false)]
    [InlineData("bulk", true)]
    [InlineData("binding", false)]
    [InlineData("binding", true)]
    [InlineData("scenario", false)]
    [InlineData("scenario", true)]
    [InlineData("use", false)]
    [InlineData("use", true)]
    public async Task Queued_work_from_an_isolated_edit_rechecks_revoked_rights(string route, bool denyView)
    {
        using var f = new Fixture();
        await f.Edit(route);
        Assert.Equal("A", f.Source(f.Existing));
        Assert.Equal(f.Previous, f.Value(f.Existing));
        var work = f.WorkFor(f.Existing);

        f.SetDurableRights(PermAction.All & ~(denyView ? PermAction.ViewCollection : PermAction.EditRecord));
        Assert.True(f.Database.Permissions.IsAdmin); // намеренно не обновляли кеш окна
        Assert.Equal(ConstructorRepository.FileAutoResolveResult.NoAccess,
            ConstructorRepository.ExecuteFileAutoWork(work));
        Assert.Equal(f.Previous, f.Value(f.Existing));
        Assert.Equal("A", f.Source(f.Existing));
    }

    [Theory]
    [InlineData("binding", false)]
    [InlineData("binding", true)]
    [InlineData("scenario", false)]
    [InlineData("scenario", true)]
    [InlineData("use", false)]
    [InlineData("use", true)]
    public void Queued_work_from_an_isolated_creation_rechecks_revoked_rights(string route, bool denyView)
    {
        using var f = new Fixture();
        var created = f.Create(route);
        Assert.Equal("A", f.Source(created));
        Assert.Equal("", f.Value(created));
        var work = f.WorkFor(created);

        f.SetDurableRights(PermAction.All & ~(denyView ? PermAction.ViewCollection : PermAction.EditRecord));
        Assert.True(f.Database.Permissions.IsAdmin);
        Assert.Equal(ConstructorRepository.FileAutoResolveResult.NoAccess,
            ConstructorRepository.ExecuteFileAutoWork(work));
        Assert.Equal("", f.Value(created));
        Assert.Equal(f.Previous, f.Value(f.Existing));
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("scenario")]
    [InlineData("use")]
    public void Creation_with_only_create_permission_keeps_initial_fields_and_skips_auto_file(string route)
    {
        using var f = new Fixture();
        f.SetDurableRights(PermAction.CreateRecord | PermAction.ViewCollection);
        f.Database.Permissions.Reload(notify: false);
        Assert.True(f.Database.Permissions.Can(PermAction.CreateRecord, f.Collection));
        Assert.False(f.Database.Permissions.Can(PermAction.EditRecord, f.Collection));

        var created = f.Create(route);
        Assert.Equal("A", f.Source(created));
        Assert.Equal("", f.Value(created));
        Assert.Empty(f.Requested);
        Assert.Equal(f.Previous, f.Value(f.Existing));
    }

    [Fact]
    public void Work_with_kept_rights_materializes_only_the_created_record()
    {
        using var f = new Fixture();
        var created = f.Create("use");
        Assert.Equal(ConstructorRepository.FileAutoResolveResult.Ok,
            ConstructorRepository.ExecuteFileAutoWork(f.WorkFor(created)));
        Assert.Equal(f.Match, f.Value(created));
        Assert.Equal(f.Previous, f.Value(f.Existing));
    }

    private sealed class Fixture : IDisposable
    {
        public TempDatabase Database { get; } = new();
        private readonly TempDir _files = new();
        public ConcurrentQueue<FileAutoWork> Requested { get; } = new();
        private ConstructorRepository Repo => Database.Constructor;
        public int Collection { get; }
        private int SourceField { get; }
        private int FileField { get; }
        public int Existing { get; }
        public string Previous => _files.File("previous.pdf");
        public string Match => _files.File("A.pdf");

        public Fixture()
        {
            File.WriteAllText(Match, "match");
            Collection = SampleData.AddCollection(Repo, "Права автоподстановки").Id!.Value;
            SourceField = SampleData.AddField(Repo, Collection, "Источник", "text").Id!.Value;
            var file = SampleData.AddField(Repo, Collection, "Файл", "file");
            FileField = file.Id!.Value;
            Existing = SampleData.AddRecord(Repo, Collection, "Existing").Id!.Value;
            Repo.SetFieldValue(Existing, SourceField, "previous");
            Repo.SetFieldValue(Existing, FileField, Previous);
            file.ConfigJson = new FileAutoRule(SourceField.ToString(), _files.Path, "pdf").ToConfigJson();
            Repo.UpdateField(file);
            ConstructorRepository.FileAutoWorkRequested += Receive;
        }

        private void Receive(FileAutoWork work)
        {
            if (work.DbPath == Database.Path) Requested.Enqueue(work);
        }

        public FileAutoWork WorkFor(int record)
        {
            var requested = Requested.ToArray();
            Assert.NotEmpty(requested);
            Assert.All(requested, work =>
            {
                Assert.Equal(FileField, work.FieldId);
                Assert.Equal(record, work.RecordId);
            });
            return requested[^1];
        }

        public async Task Edit(string route)
        {
            if (route == "bulk")
            {
                var result = await Repo.SetCellsBulkIsolatedAsync(Collection,
                    [new(Existing, SourceField, "A")], null, "Вставка");
                Assert.Equal(1, result.Changed);
            }
            else if (route == "binding")
            {
                var result = Repo.ApplyRowsIsolated(Binding("Название"), Schema,
                    [new("Existing", ["A"])], CancellationToken.None);
                Assert.Equal(1, result.Updated);
            }
            else if (route == "scenario")
            {
                var result = Repo.WriteRecordsIsolated(Collection, [Row("Existing")], CancellationToken.None,
                    matchField: "Название", createMissing: false);
                Assert.Null(result.Trouble);
                Assert.Equal(1, result.Updated);
            }
            else
                Repo.UseIsolated(Collection, repo => repo.SetFieldValue(Existing, SourceField, "A"));
        }

        public int Create(string route)
        {
            if (route == "binding")
            {
                var result = Repo.ApplyRowsIsolated(Binding(), Schema,
                    [new("New", ["A"])], CancellationToken.None);
                Assert.Equal(1, result.Created);
            }
            else if (route == "scenario")
            {
                var result = Repo.WriteRecordsIsolated(Collection, [Row("New")], CancellationToken.None);
                Assert.Null(result.Trouble);
                Assert.Equal(1, result.Created);
            }
            else
                Repo.UseIsolated(Collection, repo =>
                {
                    var record = repo.AddRecord(new DbRecord { CollectionId = Collection, Title = "New", Position = 2000 });
                    repo.SetFieldValue(record.Id!.Value, SourceField, "A");
                    return record.Id.Value;
                });

            using var read = new AppDbContext(Database.Path);
            return read.DbRecords.Single(record => record.CollectionId == Collection && record.Title == "New").Id;
        }

        private CollectionBinding Binding(string match = "") => new(1, Collection,
            [new("Название", null), new("Источник", SourceField)], MatchColumn: match);
        private static ImportSchema Schema => new("Название", ["Источник"], 1, 0);
        private static IReadOnlyDictionary<string, string> Row(string title) => new Dictionary<string, string>
        {
            ["Название"] = title, ["Источник"] = "A",
        };

        public string Value(int record) => Read(record, FileField);
        public string Source(int record) => Read(record, SourceField);
        private string Read(int record, int field)
        {
            using var read = new AppDbContext(Database.Path);
            return read.DbFieldValues.AsNoTracking().Where(v => v.RecordId == record && v.FieldId == field)
                .Select(v => v.Value).SingleOrDefault() ?? "";
        }

        public void SetDurableRights(PermAction allowed)
        {
            using var other = new AppDbContext(Database.Path);
            var user = other.DbUsers.Single(u => u.UserKey == CurrentUser.Key);
            user.IsAdmin = false;
            user.IsApproved = true;
            var permission = other.DbPermissions.SingleOrDefault(p => p.UserId == user.Id && p.CollectionId == null);
            if (permission is null)
                other.DbPermissions.Add(new DbPermissionEntity { UserId = user.Id, ActionsMask = (int)allowed });
            else permission.ActionsMask = (int)allowed;
            other.SaveChanges();
        }

        public void Dispose()
        {
            ConstructorRepository.FileAutoWorkRequested -= Receive;
            Database.Dispose();
            _files.Dispose();
        }
    }
}
