using SmartBase.Core.Exporters.TableWriters;
using SmartBase.Core.Importers.TableSources;
using Npgsql;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Условие выбора записей связи: новые связи только на подходящие записи — при выборе, вставке,
/// преобразовании и автоподборе; уже выбранные связи условие не снимает.
/// </summary>
public sealed class RelationConditionTests
{
    [Fact]
    public void Only_matching_records_can_be_chosen()
    {
        using var f = new Fixture();
        f.Repo.SetLinkEdges(f.Relation, f.Order, [new RelationTarget(0, f.Active)]);
        var error = Assert.Throws<RelationConditionException>(() =>
            f.Repo.SetLinkEdges(f.Relation, f.Order, [new RelationTarget(0, f.Active), new RelationTarget(0, f.Closed)]));
        Assert.Equal("Запись «Закрытый» не подходит под условие выбора: «Статус» равно «Активен» или «На продлении».", error.Message);
        Assert.Equal([f.Active], f.Links());
        // Старый API тоже идет через условие.
        Assert.Throws<RelationConditionException>(() => f.Repo.SetLinks(f.Relation, f.Order, [f.Closed]));
        Assert.Throws<RelationConditionException>(() => f.Repo.AddLink(f.Relation, f.Order, f.Closed));
        f.Repo.AddLink(f.Relation, f.Order, f.Renewal);
        Assert.Equal([f.Active, f.Renewal], f.Links());
    }

    [Fact]
    public void A_link_chosen_before_the_condition_stays()
    {
        using var f = new Fixture(condition: false);
        f.Repo.SetLinks(f.Relation, f.Order, [f.Closed, f.Active]);
        f.SetCondition(true);
        // Прежняя неподходящая переживает добавление, снятие соседней и перестановку.
        f.Repo.AddLink(f.Relation, f.Order, f.Renewal);
        f.Repo.SetLinkEdges(f.Relation, f.Order, [new RelationTarget(0, f.Renewal), new RelationTarget(0, f.Closed)]);
        Assert.Equal([f.Renewal, f.Closed], f.Links());
        // Снятую выбрать снова нельзя.
        f.Repo.SetLinkEdges(f.Relation, f.Order, [new RelationTarget(0, f.Renewal)]);
        Assert.Throws<RelationConditionException>(() => f.Repo.AddLink(f.Relation, f.Order, f.Closed));
    }

    [Fact]
    public void A_changed_status_is_seen_at_once()
    {
        using var f = new Fixture();
        f.Repo.SetFieldValue(f.Closed, f.Status, "Активен");
        f.Repo.AddLink(f.Relation, f.Order, f.Closed);
        Assert.Equal([f.Closed], f.Links());
    }

    [Fact]
    public void A_broken_condition_closes_new_links_but_keeps_old_ones()
    {
        using var f = new Fixture(condition: false);
        f.Repo.SetLinks(f.Relation, f.Order, [f.Active]);
        f.SetCondition(true);
        f.Repo.DeleteField(f.Status);
        var error = Assert.Throws<RelationConditionException>(() => f.Repo.AddLink(f.Relation, f.Order, f.Renewal));
        Assert.Contains("удаленное свойство", error.Message, StringComparison.Ordinal);
        Assert.Equal([f.Active], f.Links());
        // Снять связь можно и при сломанном условии.
        f.Repo.SetLinkEdges(f.Relation, f.Order, []);
        Assert.Empty(f.Links());
    }

    [Fact]
    public void Paste_skips_cells_with_records_outside_the_condition()
    {
        using var f = new Fixture();
        var second = SampleData.AddRecord(f.Repo, f.Orders, "Заказ 2").Id!.Value;
        var result = f.Repo.SetLinksBulk(f.Orders,
        [
            new ConstructorRepository.LinkWrite(f.Order, f.Relation, [f.Active, f.Closed]),
            new ConstructorRepository.LinkWrite(second, f.Relation, [f.Renewal]),
        ]);
        Assert.Equal(1, result.Changed);
        Assert.Equal(1, result.ConditionRefused);
        Assert.Equal(0, result.Conflicts);
        Assert.Empty(f.Links());
        Assert.Equal([f.Renewal], f.Repo.GetLinkEdges(f.Relation, second).Select(e => e.ToRecordId));
    }

    [Fact]
    public void Several_conditions_are_joined_with_and()
    {
        using var f = new Fixture();
        var flag = SampleData.AddField(f.Repo, f.Clients, "Проверен", "checkbox").Id!.Value;
        f.Repo.SetFieldValue(f.Renewal, flag, "1");
        f.SetCondition(true, new RelationCondition(flag.ToString(), RelationCondition.NotEmpty, []),
            new RelationCondition(RelationConfig.TitleField, RelationCondition.Contains, ["ооо"]));
        var verdict = f.Repo.EvaluateRelationCondition(f.Clients, f.Config(), [f.Active, f.Renewal, f.Closed]);
        Assert.Null(verdict.Broken);
        Assert.Equal([f.Renewal], verdict.Allowed);
        Assert.Equal("«Статус» равно «Активен» или «На продлении» и «Проверен» отмечено и «Название» содержит «ооо»", verdict.Condition);
    }

    [Fact]
    public void Search_finds_only_matching_records()
    {
        using var f = new Fixture();
        var cfg = f.Config();
        // Точное «равно» по статусу идет от индекса значений.
        Assert.Equal([f.Active, f.Renewal], f.Repo.SearchRelationRecords(f.Clients, RelationConfig.TitleField, null, condition: cfg)
            .Select(v => v.RecordId));
        Assert.Equal([f.Renewal], f.Repo.SearchRelationRecords(f.Clients, RelationConfig.TitleField, "ооо", condition: cfg)
            .Select(v => v.RecordId));
        Assert.Empty(f.Repo.SearchRelationRecords(f.Clients, RelationConfig.TitleField, "Закрыт", condition: cfg));
        // Условие без индекса (текст) — окнами по порядку записей; поиск идет по выбранному свойству.
        f.SetCondition(false);
        var text = f.Config() with { Where = [new RelationCondition(RelationConfig.TitleField, RelationCondition.NotEqual, ["Активный"])] };
        Assert.Equal([f.Renewal, f.Closed], f.Repo.SearchRelationRecords(f.Clients, RelationConfig.TitleField, null, condition: text)
            .Select(v => v.RecordId));
        Assert.Equal([f.Closed], f.Repo.SearchRelationRecords(f.Clients, f.Status.ToString(), "закр", condition: text)
            .Select(v => v.RecordId));
        Assert.Equal([f.Closed], f.Repo.SearchRelationRecords(f.Clients, RelationConfig.TitleField, null, excludeId: f.Renewal,
            condition: text).Select(v => v.RecordId));
    }

    [Fact]
    public async Task The_picker_names_the_condition_and_marks_old_links_outside_it()
    {
        using var f = new Fixture(condition: false);
        f.Repo.SetLinks(f.Relation, f.Order, [f.Closed]);
        f.SetCondition(true);
        var data = await f.Repo.GetRelationPickerDataAsync(f.Order, f.Config(), null, [new RelationTarget(0, f.Closed)]);
        Assert.Equal("«Статус» равно «Активен» или «На продлении»", data.Condition);
        Assert.Null(data.ConditionProblem);
        Assert.Equal([new RelationTarget(0, f.Closed)], data.SelectedOutside!);
        Assert.Equal([f.Active, f.Renewal], data.Candidates.Select(c => c.RecordId));
    }

    [Fact]
    public void Auto_matching_links_only_matching_records_and_keeps_old_ones()
    {
        using var f = new Fixture(condition: false);
        var code = SampleData.AddField(f.Repo, f.Clients, "Код", "text").Id!.Value;
        var orderCode = SampleData.AddField(f.Repo, f.Orders, "Код клиента", "text").Id!.Value;
        f.Repo.SetFieldValue(f.Active, code, "А1");
        f.Repo.SetFieldValue(f.Closed, code, "З1");
        var second = SampleData.AddRecord(f.Repo, f.Orders, "Заказ 2").Id!.Value;
        f.Repo.SetFieldValue(f.Order, orderCode, "А1");
        f.Repo.SetFieldValue(second, orderCode, "З1");
        var field = f.Repo.GetFields(f.Orders).Single(x => x.Id == f.Relation);
        field.ConfigJson = new RelationConfig(f.Clients, true, Auto: new RelationAutoRule(orderCode.ToString(), code.ToString()),
            Where: [new RelationCondition(f.Status.ToString(), RelationCondition.Equal, ["Активен"])]).ToConfigJson();
        var preview = f.Repo.PreviewAutoRelation(field);
        Assert.Equal(1, preview.OutsideCondition);
        f.Repo.SaveAutoRelationField(field, preview);
        Assert.Equal([f.Active], f.Links());
        Assert.Empty(f.Repo.GetLinkEdges(f.Relation, second));
        // Цель стала подходить — пересчет связывает ее.
        f.Repo.SetFieldValue(f.Closed, f.Status, "Активен");
        f.Repo.RefreshAutoRelationsForChange(f.Clients, f.Status);
        Assert.Equal([f.Closed], f.Repo.GetLinkEdges(f.Relation, second).Select(e => e.ToRecordId));
        // Перестала подходить — прежняя автосвязь остается, пока совпадает ключ.
        f.Repo.SetFieldValue(f.Closed, f.Status, "Закрыт");
        f.Repo.RefreshAutoRelationsForChange(f.Clients, f.Status);
        Assert.Equal([f.Closed], f.Repo.GetLinkEdges(f.Relation, second).Select(e => e.ToRecordId));
    }

    [Fact]
    public void A_duplicate_skips_links_outside_the_condition()
    {
        using var f = new Fixture(condition: false);
        f.Repo.SetLinks(f.Relation, f.Order, [f.Active, f.Closed]);
        f.SetCondition(true);
        var notices = new List<string>();
        ConstructorRepository.ConditionNotice = notices.Add;
        try
        {
            var copy = f.Repo.DuplicateRecord(f.Order).Id!.Value;
            Assert.Equal([f.Active], f.Repo.GetLinkEdges(f.Relation, copy).Select(e => e.ToRecordId));
            var copies = f.Repo.DuplicateRecordsData(f.Orders, [f.Order]);
            Assert.Equal([f.Active], f.Repo.GetLinkEdges(f.Relation, copies.Single()).Select(e => e.ToRecordId));
            Assert.Equal(2, notices.Count);
            Assert.Equal("Копия создана. Не скопировано связей — 1 («Клиент»): записи не подходят под условие выбора.", notices[0]);
        }
        finally { ConstructorRepository.ConditionNotice = null; }
        // Оригинал не тронут.
        Assert.Equal([f.Active, f.Closed], f.Links());
    }

    [SkippableFact]
    public void The_condition_works_on_PostgreSQL()
    {
        // Отдельный SQL дубля набора (DuplicateRecordsDataPg), поиск от индекса значений и ворота.
        Skip.IfNot(PgTestServer.IsAvailable, "Стенд PostgreSQL sb-pg (порт 5455) не поднят");
        using var f = new Fixture(condition: false, postgres: true);
        f.Repo.SetLinks(f.Relation, f.Order, [f.Active, f.Closed]);
        f.SetCondition(true);
        Assert.Equal([f.Active, f.Renewal], f.Repo.SearchRelationRecords(f.Clients, RelationConfig.TitleField, null, condition: f.Config())
            .Select(v => v.RecordId));
        var notices = new List<string>();
        ConstructorRepository.ConditionNotice = notices.Add;
        try
        {
            var copies = f.Repo.DuplicateRecordsData(f.Orders, [f.Order]);
            Assert.Equal([f.Active], f.Repo.GetLinkEdges(f.Relation, copies.Single()).Select(e => e.ToRecordId));
            Assert.Equal("Копия создана. Не скопировано связей — 1 («Клиент»): записи не подходят под условие выбора.", Assert.Single(notices));
        }
        finally { ConstructorRepository.ConditionNotice = null; }
        // Прежняя связь вне условия переживает перестановку; снятую выбрать снова нельзя.
        f.Repo.SetLinkEdges(f.Relation, f.Order, [new RelationTarget(0, f.Closed), new RelationTarget(0, f.Active), new RelationTarget(0, f.Renewal)]);
        Assert.Equal([f.Closed, f.Active, f.Renewal], f.Links());
        f.Repo.SetLinkEdges(f.Relation, f.Order, [new RelationTarget(0, f.Active)]);
        Assert.Throws<RelationConditionException>(() => f.Repo.AddLink(f.Relation, f.Order, f.Closed));
    }

    [Fact]
    public void Import_does_not_create_links_outside_the_condition()
    {
        using var f = new Fixture(condition: false);
        using var dir = new TempDir();
        var self = SampleData.AddField(f.Repo, f.Clients, "Головной", "relation");
        self.ConfigJson = new RelationConfig(f.Clients, false).ToConfigJson();
        f.Repo.UpdateField(self);
        f.Repo.SetLinks(self.Id!.Value, f.Renewal, [f.Active, f.Closed]);
        self.ConfigJson = new RelationConfig(f.Clients, false,
            Where: [new RelationCondition(f.Status.ToString(), RelationCondition.Equal, ["Активен"])]).ToConfigJson();
        f.Repo.UpdateField(self);
        var cols = f.Repo.PreparePortableExportColumnsIsolated(f.Clients, f.Repo.GetExportColumns(f.Clients, true));
        var path = dir.File("clients.json");
        using (var writer = new JsonTableWriter(path))
        {
            writer.BeginTable("Клиенты", cols, true);
            foreach (var row in f.Repo.ReadExportRowsIsolated(f.Clients, cols, false, default, portableRelations: true)) writer.WriteRow(row);
            writer.EndTable(); writer.Finish();
        }
        var input = new JsonTableSource(path);
        var notices = new List<string>();
        ConstructorRepository.ConditionNotice = notices.Add;
        try
        {
            var imported = f.Repo.ImportStreamedCollection("Копия", null, input.Analyze(null, default), input.ReadRows(default), default);
            var fields = f.Repo.GetFields(imported);
            var relation = fields.Single(x => x.Name == "Головной").Id!.Value;
            var records = f.Repo.GetRecords(imported);
            var renewal = records.Single(r => r.Title == "ООО Продление").Id!.Value;
            var active = records.Single(r => r.Title == "Активный").Id!.Value;
            Assert.Equal([active], f.Repo.GetLinkEdges(relation, renewal).Select(e => e.ToRecordId));
            // Ключ условия у копии — свойство копии.
            Assert.Equal(fields.Single(x => x.Name == "Статус").Id!.Value.ToString(),
                RelationConfig.FromConfig(fields.Single(x => x.Name == "Головной").ConfigJson)!.Where![0].Field);
            Assert.Equal("Коллекция загружена. Не создано связей — 1 («Головной»): записи не подходят под условие выбора.", Assert.Single(notices));
        }
        finally { ConstructorRepository.ConditionNotice = null; }
    }

    [Fact]
    public void A_copied_collection_points_its_condition_at_its_own_properties()
    {
        using var f = new Fixture();
        var self = SampleData.AddField(f.Repo, f.Clients, "Головной", "relation");
        self.ConfigJson = new RelationConfig(f.Clients, false,
            Where: [new RelationCondition(f.Status.ToString(), RelationCondition.Equal, ["Активен"])]).ToConfigJson();
        f.Repo.UpdateField(self);
        var copy = f.Repo.DuplicateCollection(f.Clients).Id!.Value;
        var fields = f.Repo.GetFields(copy);
        Assert.Equal(fields.Single(x => x.Name == "Статус").Id!.Value.ToString(),
            RelationConfig.FromConfig(fields.Single(x => x.Name == "Головной").ConfigJson)!.Where![0].Field);
        // Связь на чужую коллекцию свое условие не меняет.
        Assert.Equal(f.Status.ToString(), f.Config().Where![0].Field);
    }

    [Fact]
    public void Saving_checks_the_condition()
    {
        using var f = new Fixture();
        var field = f.Repo.GetFields(f.Orders).Single(x => x.Id == f.Relation);
        field.ConfigJson = new RelationConfig(f.Clients, false,
            Where: [new RelationCondition("999999", RelationCondition.Equal, ["x"])]).ToConfigJson();
        Assert.Throws<InvalidOperationException>(() => f.Repo.UpdateField(field));
        field.ConfigJson = new RelationConfig(f.Clients, false,
            Where: [new RelationCondition(f.Status.ToString(), RelationCondition.Contains, ["Акт"])]).ToConfigJson();
        Assert.Throws<InvalidOperationException>(() => f.Repo.UpdateField(field));   // у статуса нет «содержит»
        // Нечитаемое условие можно только оставить как есть.
        const string unknown = "[{\"field\":\"5\",\"op\":\"between\"}]";
        field.ConfigJson = $"{{\"targetCollectionId\":{f.Clients},\"single\":false,\"separator\":\"comma\",\"where\":{unknown}}}";
        Assert.Throws<InvalidOperationException>(() => f.Repo.UpdateField(field));
    }

    [Fact]
    public void The_integrity_check_shows_old_links_outside_the_condition_as_information()
    {
        using var f = new Fixture(condition: false);
        f.Repo.SetLinks(f.Relation, f.Order, [f.Active, f.Closed]);
        f.SetCondition(true);
        var report = f.Repo.ScanRelationIntegrityAsync(new RelationIntegrityScanOptions(FieldId: f.Relation)).GetAwaiter().GetResult();
        var issue = Assert.Single(report.Issues, i => i.Kind == RelationIntegrityIssueKind.ConditionMismatch);
        Assert.Equal(f.Closed, issue.TargetRecordId);
        Assert.True(issue.IsInformational);
        Assert.False(issue.CanRepair);
        Assert.Equal(0, report.ConfirmedIssues);
        // Смена статуса цели не меняет отпечаток: план исправления ребер не устаревает.
        f.Repo.SetFieldValue(f.Closed, f.Status, "Активен");
        var again = f.Repo.ScanRelationIntegrityAsync(new RelationIntegrityScanOptions(FieldId: f.Relation)).GetAwaiter().GetResult();
        Assert.DoesNotContain(again.Issues, i => i.Kind == RelationIntegrityIssueKind.ConditionMismatch);
        Assert.Equal(report.Fingerprint, again.Fingerprint);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempDatabase? _db;
        private readonly DatabaseManager? _pg;
        public ConstructorRepository Repo => _db?.Constructor ?? _pg!.Constructor;
        public int Clients { get; }
        public int Status { get; }
        public int Active { get; }
        public int Renewal { get; }
        public int Closed { get; }
        public int Orders { get; }
        public int Order { get; }
        public int Relation { get; }

        public Fixture(bool condition = true, bool postgres = false)
        {
            if (postgres)
            {
                var cs = PgTestServer.ConnectionString(PgTestServer.Database1);
                using (var connection = new NpgsqlConnection(cs))
                {
                    connection.Open();
                    using var reset = connection.CreateCommand();
                    reset.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;";
                    reset.ExecuteNonQuery();
                }
                _pg = DatabaseManager.Open(101, "pg-condition", cs, provider: "postgres");
            }
            else _db = new();
            Clients = SampleData.AddCollection(Repo, "Клиенты").Id!.Value;
            var status = SampleData.AddField(Repo, Clients, "Статус", "status");
            Status = status.Id!.Value;
            Active = SampleData.AddRecord(Repo, Clients, "Активный").Id!.Value;
            Renewal = SampleData.AddRecord(Repo, Clients, "ООО Продление").Id!.Value;
            Closed = SampleData.AddRecord(Repo, Clients, "Закрытый").Id!.Value;
            Repo.SetFieldValue(Active, Status, "Активен");
            Repo.SetFieldValue(Renewal, Status, "На продлении");
            Repo.SetFieldValue(Closed, Status, "Закрыт");
            Orders = SampleData.AddCollection(Repo, "Заказы").Id!.Value;
            Order = SampleData.AddRecord(Repo, Orders, "Заказ").Id!.Value;
            Relation = SampleData.AddField(Repo, Orders, "Клиент", "relation").Id!.Value;
            SetCondition(condition);
        }

        public void SetCondition(bool on, params RelationCondition[] more)
        {
            var field = Repo.GetFields(Orders).Single(x => x.Id == Relation);
            IReadOnlyList<RelationCondition>? where = on
                ? [new RelationCondition(Status.ToString(), RelationCondition.Equal, ["Активен", "На продлении"]), .. more]
                : null;
            field.ConfigJson = new RelationConfig(Clients, false, Where: where).ToConfigJson();
            Repo.UpdateField(field);
        }

        public RelationConfig Config() =>
            RelationConfig.FromConfig(Repo.GetFields(Orders).Single(x => x.Id == Relation).ConfigJson)!;

        public List<int> Links() => Repo.GetLinkEdges(Relation, Order).Select(e => e.ToRecordId).ToList();

        public void Dispose()
        {
            _db?.Dispose();
            _pg?.Dispose();
        }
    }
}
