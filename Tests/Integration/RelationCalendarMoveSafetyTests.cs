using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

public sealed class RelationCalendarMoveSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Moving_an_event_with_many_targets_into_single_refuses_before_any_copy_even_in_an_outer_transaction(bool outer)
    {
        using var f = new Fixture(single: true, many: true);
        var before = f.Counts();
        var dto = (f.Draft.Id, f.Draft.CollectionId, f.Draft.FieldId);
        void Refuse()
        {
            var error = Assert.Throws<InvalidOperationException>(() => f.Repo.UpdateCalendarRecord(f.Draft));
            Assert.Contains("допускает одну связь", error.Message);
        }
        if (outer)
            f.Repo.InWriteTransaction(() =>
            {
                Refuse();
                Assert.Equal(before, f.Counts());
                return 0; // Даже успешный внешний коммит не должен зафиксировать часть переноса.
            });
        else Refuse();

        Assert.Equal(dto, (f.Draft.Id, f.Draft.CollectionId, f.Draft.FieldId));
        Assert.Equal(before, f.Counts());
        f.AssertOriginal();
    }

    [Fact]
    public void A_failed_relation_write_rolls_back_the_new_event_values_date_and_description_field_and_restores_the_dto()
    {
        using var f = new Fixture(single: true, many: false);
        var before = f.Counts();
        var dto = (f.Draft.Id, f.Draft.CollectionId, f.Draft.FieldId);
        f.Database.Context.Database.ExecuteSqlRaw($"CREATE TRIGGER reject_calendar_move BEFORE INSERT ON db_record_links WHEN NEW.FieldId = {f.TargetRelation} BEGIN SELECT RAISE(ABORT, 'calendar move test'); END;");

        var error = Assert.Throws<DbUpdateException>(() => f.Repo.UpdateCalendarRecord(f.Draft));
        var cause = Assert.IsAssignableFrom<DbException>(error.InnerException);
        Assert.Contains("calendar move test", cause.Message);

        Assert.Equal(dto, (f.Draft.Id, f.Draft.CollectionId, f.Draft.FieldId));
        Assert.Equal(before, f.Counts());
        f.AssertOriginal();
    }

    [Fact]
    public void A_successful_event_move_keeps_all_targets_and_removes_the_original_only_after_the_copy()
    {
        using var f = new Fixture(single: false, many: true);
        f.Repo.UpdateCalendarRecord(f.Draft);

        Assert.NotEqual(f.Original, f.Draft.Id);
        Assert.Null(f.Repo.GetRecord(f.Original, refresh: true));
        Assert.Equal(f.TargetCollection, f.Draft.CollectionId);
        Assert.Equal(f.TargetDate, f.Draft.FieldId);
        Assert.Equal([(f.Endpoints[1], 0), (f.Endpoints[0], 0)], f.Repo.GetLinkEdges(f.TargetRelation, f.Draft.Id!.Value));
        Assert.Single(f.Repo.GetRecords(f.TargetCollection));
        Assert.Contains(f.Database.Context.CalendarIndex.AsNoTracking(), i => i.RecordId == f.Draft.Id);
    }

    private sealed class Fixture : IDisposable
    {
        public TempDatabase Database { get; } = new();
        public ConstructorRepository Repo => Database.Constructor;
        public int SourceCollection { get; }
        public int TargetCollection { get; }
        public int SourceRelation { get; }
        public int TargetRelation { get; }
        public int TargetDate { get; }
        public int Original { get; }
        public int[] Endpoints { get; }
        public CalendarRecord Draft { get; }
        private readonly int[] _expected;

        public Fixture(bool single, bool many)
        {
            var endpoints = SampleData.AddCollection(Repo, "Проекты").Id!.Value;
            Endpoints = [SampleData.AddRecord(Repo, endpoints, "А").Id!.Value, SampleData.AddRecord(Repo, endpoints, "Б").Id!.Value];
            SourceCollection = SampleData.AddCollection(Repo, "События").Id!.Value;
            TargetCollection = SampleData.AddCollection(Repo, "Архив").Id!.Value;
            var sourceDate = SampleData.AddCalendarDateField(Repo, SourceCollection).Id!.Value;
            TargetDate = SampleData.AddCalendarDateField(Repo, TargetCollection).Id!.Value;
            SourceRelation = Relation(SourceCollection, endpoints, single: false);
            TargetRelation = Relation(TargetCollection, endpoints, single);
            Original = Repo.AddCalendarRecord(new CalendarRecord
            {
                CollectionId = SourceCollection, FieldId = sourceDate, Title = "Исходное событие", Description = "Исходное описание",
                StartDt = new DateTime(2026, 10, 1), EndDt = new DateTime(2026, 10, 1), AllDay = true,
            }).Id!.Value;
            _expected = many ? [Endpoints[1], Endpoints[0]] : [Endpoints[0]];
            Repo.SetLinks(SourceRelation, Original, _expected);
            Draft = new CalendarRecord
            {
                Id = Original, CollectionId = TargetCollection, FieldId = TargetDate, Title = "Правка события", Description = "Новое описание",
                StartDt = new DateTime(2026, 10, 2), EndDt = new DateTime(2026, 10, 3), AllDay = true,
            };
        }

        private int Relation(int source, int target, bool single) => Repo.AddField(new DbField
        {
            CollectionId = source, Name = "Проект", FieldType = "relation",
            ConfigJson = new RelationConfig(target, single).ToConfigJson(),
        }).Id!.Value;

        public (int Records, int Fields, int Values, int Edges, int Calendar, int Log) Counts() =>
            (Database.Context.DbRecords.Count(), Database.Context.DbFields.Count(), Database.Context.DbFieldValues.Count(),
                Database.Context.DbRecordLinks.Count(), Database.Context.CalendarIndex.Count(), Database.Context.ChangeLog.Count());

        public void AssertOriginal()
        {
            var original = Repo.GetRecord(Original, refresh: true)!;
            Assert.Equal(SourceCollection, original.CollectionId);
            Assert.Equal("Исходное событие", original.Title);
            Assert.Equal(_expected.Select(id => (id, 0)), Repo.GetLinkEdges(SourceRelation, Original));
            Assert.Empty(Repo.GetRecords(TargetCollection));
            Assert.False(Database.Context.DbFields.AsNoTracking().Any(f => f.CollectionId == TargetCollection && f.IsDescription));
        }

        public void Dispose() => Database.Dispose();
    }
}
