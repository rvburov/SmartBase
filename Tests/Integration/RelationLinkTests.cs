using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Связи между записями (тип поля «relation»): рёбра в db_record_links, показ чипами (Id+Title),
/// кардинальность one/many, батч по видимым строкам и FK-каскады (удаление записи-конца/поля
/// чистит связи). Также — что таблица db_record_links создаётся миграцией (TempDatabase проходит
/// полный путь инициализации приложения).
/// </summary>
public sealed class RelationLinkTests
{
    // Две коллекции: «Задачи» с полем-связью «Проект» → «Проекты».
    private static (ConstructorRepository repo, int taskCol, int relField, int projCol) Setup(TempDatabase db, bool single = false)
    {
        var repo    = db.Constructor;
        var projCol = SampleData.AddCollection(repo, "Проекты").Id!.Value;
        var taskCol = SampleData.AddCollection(repo, "Задачи").Id!.Value;
        var rel     = SampleData.AddField(repo, taskCol, "Проект", "relation");
        rel.ConfigJson = new RelationConfig(projCol, single).ToConfigJson();
        repo.UpdateField(rel);
        return (repo, taskCol, rel.Id!.Value, projCol);
    }

    [Fact]
    public void Set_and_get_links_preserves_order_and_resolves_titles()
    {
        using var db = new TempDatabase();
        var (repo, taskCol, rel, projCol) = Setup(db);
        var p1 = SampleData.AddRecord(repo, projCol, "Сайт 2026").Id!.Value;
        var p2 = SampleData.AddRecord(repo, projCol, "Инфра").Id!.Value;
        var t  = SampleData.AddRecord(repo, taskCol, "Задача").Id!.Value;

        repo.SetLinks(rel, t, [p2, p1]);   // порядок выбора p2, p1

        var links = repo.GetLinks(rel, t);
        Assert.Equal([(p2, "Инфра"), (p1, "Сайт 2026")], links);   // порядок сохранён, Title резолвится

        // Переименование цели сразу отражается (Title не дублируется в связи).
        repo.SetRecordTitle(p1, "Сайт 2027");
        Assert.Equal("Сайт 2027", repo.GetLinks(rel, t).Single(x => x.Id == p1).Title);
    }

    [Fact]
    public void Single_cardinality_keeps_only_one_link()
    {
        using var db = new TempDatabase();
        var (repo, taskCol, rel, projCol) = Setup(db, single: true);
        var p1 = SampleData.AddRecord(repo, projCol, "A").Id!.Value;
        var p2 = SampleData.AddRecord(repo, projCol, "B").Id!.Value;
        var t  = SampleData.AddRecord(repo, taskCol, "T").Id!.Value;

        repo.SetLinks(rel, t, [p1, p2]);
        Assert.Equal([p1], repo.GetLinkTargetIds(rel, t));   // single → только первая

        // AddLink на single заменяет прежнюю.
        repo.AddLink(rel, t, p2);
        Assert.Equal([p2], repo.GetLinkTargetIds(rel, t));
    }

    [Fact]
    public void Set_links_filters_self_duplicates_and_foreign_collection()
    {
        using var db = new TempDatabase();
        var (repo, taskCol, rel, projCol) = Setup(db);
        var p1      = SampleData.AddRecord(repo, projCol, "A").Id!.Value;
        var t       = SampleData.AddRecord(repo, taskCol, "T").Id!.Value;
        var foreign = SampleData.AddRecord(repo, taskCol, "Чужая").Id!.Value;   // не из целевой коллекции

        repo.SetLinks(rel, t, [p1, p1, t, foreign]);   // дубль, само-ссылка, чужая коллекция
        Assert.Equal([p1], repo.GetLinkTargetIds(rel, t));
    }

    [Fact]
    public void Batch_returns_links_per_source_record()
    {
        using var db = new TempDatabase();
        var (repo, taskCol, rel, projCol) = Setup(db);
        var p1 = SampleData.AddRecord(repo, projCol, "A").Id!.Value;
        var p2 = SampleData.AddRecord(repo, projCol, "B").Id!.Value;
        var t1 = SampleData.AddRecord(repo, taskCol, "T1").Id!.Value;
        var t2 = SampleData.AddRecord(repo, taskCol, "T2").Id!.Value;
        var t3 = SampleData.AddRecord(repo, taskCol, "T3").Id!.Value;   // без связей
        repo.SetLinks(rel, t1, [p1, p2]);
        repo.SetLinks(rel, t2, [p2]);

        var batch = repo.GetLinksBatch(rel, [t1, t2, t3]);
        Assert.Equal([p1, p2], batch[t1].Select(x => x.Id));
        Assert.Equal([p2],     batch[t2].Select(x => x.Id));
        Assert.False(batch.ContainsKey(t3));   // без связей — записи нет в батче
    }

    [Fact]
    public void Deleting_target_record_cascades_link_removal()
    {
        using var db = new TempDatabase();
        var (repo, taskCol, rel, projCol) = Setup(db);
        var p1 = SampleData.AddRecord(repo, projCol, "A").Id!.Value;
        var p2 = SampleData.AddRecord(repo, projCol, "B").Id!.Value;
        var t  = SampleData.AddRecord(repo, taskCol, "T").Id!.Value;
        repo.SetLinks(rel, t, [p1, p2]);

        repo.DeleteRecord(p1);   // FK ON DELETE CASCADE (ToRecordId) чистит ребро
        Assert.Equal([p2], repo.GetLinkTargetIds(rel, t));
    }

    [Fact]
    public void Deleting_relation_field_cascades_all_its_links()
    {
        using var db = new TempDatabase();
        var (repo, taskCol, rel, projCol) = Setup(db);
        var p1 = SampleData.AddRecord(repo, projCol, "A").Id!.Value;
        var t  = SampleData.AddRecord(repo, taskCol, "T").Id!.Value;
        repo.SetLinks(rel, t, [p1]);

        repo.DeleteField(rel);   // FK ON DELETE CASCADE (FieldId) чистит все рёбра поля
        Assert.Empty(repo.GetLinkTargetIds(rel, t));
    }

    [Fact]
    public void Remove_link_drops_single_edge()
    {
        using var db = new TempDatabase();
        var (repo, taskCol, rel, projCol) = Setup(db);
        var p1 = SampleData.AddRecord(repo, projCol, "A").Id!.Value;
        var p2 = SampleData.AddRecord(repo, projCol, "B").Id!.Value;
        var t  = SampleData.AddRecord(repo, taskCol, "T").Id!.Value;
        repo.SetLinks(rel, t, [p1, p2]);

        repo.RemoveLink(rel, t, p1);
        Assert.Equal([p2], repo.GetLinkTargetIds(rel, t));
    }

    [Fact]
    public void Duplicating_record_copies_outgoing_links()
    {
        using var db = new TempDatabase();
        var (repo, taskCol, rel, projCol) = Setup(db);
        var p1 = SampleData.AddRecord(repo, projCol, "A").Id!.Value;
        var p2 = SampleData.AddRecord(repo, projCol, "B").Id!.Value;
        var t  = SampleData.AddRecord(repo, taskCol, "T").Id!.Value;
        repo.SetLinks(rel, t, [p1, p2]);

        var copy = repo.DuplicateRecord(t);

        Assert.Equal([p1, p2], repo.GetLinkTargetIds(rel, copy.Id!.Value));   // копия ссылается на те же цели
        Assert.Equal([p1, p2], repo.GetLinkTargetIds(rel, t));         // оригинал не тронут
    }

    [Fact]
    public void Bulk_duplicate_copies_links_per_record()
    {
        using var db = new TempDatabase();
        var (repo, taskCol, rel, projCol) = Setup(db);
        var p1 = SampleData.AddRecord(repo, projCol, "A").Id!.Value;
        var p2 = SampleData.AddRecord(repo, projCol, "B").Id!.Value;
        var t1 = SampleData.AddRecord(repo, taskCol, "T1").Id!.Value;
        var t2 = SampleData.AddRecord(repo, taskCol, "T2").Id!.Value;   // без связей
        repo.SetLinks(rel, t1, [p2, p1]);   // порядок p2, p1 — должен сохраниться в копии

        // Копии возвращаются в порядке ранга (Position, Id) — t1 создана раньше t2.
        var newIds = repo.DuplicateRecordsData(taskCol, [t1, t2]);

        Assert.Equal([p2, p1], repo.GetLinkTargetIds(rel, newIds[0]));   // копия t1 — те же цели и порядок
        Assert.Empty(repo.GetLinkTargetIds(rel, newIds[1]));             // копия t2 — связей не было
    }

    [Fact]
    public void Moving_record_within_same_db_transfers_links()
    {
        using var db = new TempDatabase();
        var (repo, taskCol, rel, projCol) = Setup(db);
        // Целевая коллекция с таким же полем-связью «Проект» → projCol (сопоставление по имени).
        var archive = SampleData.AddCollection(repo, "Архив").Id!.Value;
        var rel2 = SampleData.AddField(repo, archive, "Проект", "relation");
        rel2.ConfigJson = new RelationConfig(projCol, false).ToConfigJson();
        repo.UpdateField(rel2);

        var p1 = SampleData.AddRecord(repo, projCol, "A").Id!.Value;
        var p2 = SampleData.AddRecord(repo, projCol, "B").Id!.Value;
        var t  = SampleData.AddRecord(repo, taskCol, "T").Id!.Value;
        repo.SetLinks(rel, t, [p2, p1]);

        var newIds = repo.MoveRecordsToCollectionIsolated(db.Path, taskCol, [t], archive, createMissingFields: false);

        Assert.Equal([p2, p1], repo.GetLinkTargetIds(rel2.Id!.Value, newIds[0]));   // перенесены с порядком
    }

    [Fact]
    public void Relation_value_counts_group_by_title_with_no_links_bucket()
    {
        using var db = new TempDatabase();
        var (repo, taskCol, rel, projCol) = Setup(db);
        var a  = SampleData.AddRecord(repo, projCol, "Альфа").Id!.Value;
        var b  = SampleData.AddRecord(repo, projCol, "Бета").Id!.Value;
        var t1 = SampleData.AddRecord(repo, taskCol, "T1").Id!.Value;
        var t2 = SampleData.AddRecord(repo, taskCol, "T2").Id!.Value;
        SampleData.AddRecord(repo, taskCol, "T3");   // без связей
        repo.SetLinks(rel, t1, [a, b]);
        repo.SetLinks(rel, t2, [a]);

        var counts = repo.GetRelationValueCounts(taskCol, rel);

        Assert.Equal(("Альфа", 2), counts.Single(c => c.Value == "Альфа"));   // t1, t2
        Assert.Equal(("Бета",  1), counts.Single(c => c.Value == "Бета"));    // t1
        Assert.Equal(("", 1),      counts[^1]);                               // «(нет связей)» — T3, последним
    }

    [Fact]
    public void Filter_by_relation_uses_or_semantics_and_empty_bucket()
    {
        using var db = new TempDatabase();
        var (repo, taskCol, rel, projCol) = Setup(db);
        var a  = SampleData.AddRecord(repo, projCol, "Альфа").Id!.Value;
        var b  = SampleData.AddRecord(repo, projCol, "Бета").Id!.Value;
        var t1 = SampleData.AddRecord(repo, taskCol, "T1").Id!.Value;
        var t2 = SampleData.AddRecord(repo, taskCol, "T2").Id!.Value;
        var t3 = SampleData.AddRecord(repo, taskCol, "T3").Id!.Value;   // без связей
        repo.SetLinks(rel, t1, [a, b]);
        repo.SetLinks(rel, t2, [b]);

        var key = rel.ToString();
        Dictionary<string, HashSet<string>> F(params string[] vals) => new() { [key] = [.. vals] };

        Assert.Equal([t1],     repo.GetFilteredRecordIds(taskCol, F("Альфа")));           // только t1
        Assert.Equal([t1, t2], repo.GetFilteredRecordIds(taskCol, F("Альфа", "Бета")));   // ИЛИ — обе
        Assert.Equal([t3],     repo.GetFilteredRecordIds(taskCol, F("")));                // «(нет связей)»
    }

    [Fact]
    public void Column_values_for_relation_join_titles_for_sort()
    {
        using var db = new TempDatabase();
        var (repo, taskCol, rel, projCol) = Setup(db);
        var a  = SampleData.AddRecord(repo, projCol, "Альфа").Id!.Value;
        var b  = SampleData.AddRecord(repo, projCol, "Бета").Id!.Value;
        var t1 = SampleData.AddRecord(repo, taskCol, "T1").Id!.Value;
        repo.SetLinks(rel, t1, [b, a]);   // порядок b, a сохраняется в склейке

        var vals = repo.GetColumnValues(taskCol, rel.ToString());
        Assert.Equal("Бета, Альфа", vals[t1]);   // названия через разделитель поля (comma → ", ")
    }

    [Fact]
    public void Cross_db_links_store_target_db_id_and_resolve_titles_from_target_repo()
    {
        using var dbA = new TempDatabase(dbId: 1, name: "A");
        using var dbB = new TempDatabase(dbId: 2, name: "B");
        var repoA = dbA.Constructor;
        var repoB = dbB.Constructor;

        // БД-B: коллекция «Проекты» с записями (цели межбазовой связи).
        var projB = SampleData.AddCollection(repoB, "Проекты").Id!.Value;
        var b1 = SampleData.AddRecord(repoB, projB, "Инфра B").Id!.Value;
        var b2 = SampleData.AddRecord(repoB, projB, "Сайт B").Id!.Value;

        // БД-A: «Задачи» с межбазовым полем-связью на коллекцию БД-B (TargetDbId = 2).
        var taskA = SampleData.AddCollection(repoA, "Задачи").Id!.Value;
        var rel = SampleData.AddField(repoA, taskA, "Проект", "relation");
        rel.ConfigJson = new RelationConfig(projB, false, "comma", 2).ToConfigJson();
        repoA.UpdateField(rel);
        var t = SampleData.AddRecord(repoA, taskA, "Задача").Id!.Value;

        repoA.SetLinks(rel.Id!.Value, t, [b2, b1]);   // цели — Id из БД-B

        // Рёбра лежат в БД-A с порядком; TargetDbId=2. Само-ссылку НЕ отсеиваем (b1 может совпасть с t
        // по Id, но это разные записи в разных БД).
        Assert.Equal([b2, b1], repoA.GetLinkTargetIds(rel.Id!.Value, t));
        Assert.Equal([2], dbA.Context.DbRecordLinks.AsNoTracking().Select(l => l.TargetDbId).Distinct().ToList());

        // Названия целей резолвятся с репозитория БД-B.
        var titles = repoB.GetRecordTitles([b1, b2]);
        Assert.Equal("Инфра B", titles[b1]);
        Assert.Equal("Сайт B",  titles[b2]);

        // Удаление записи-источника в БД-A уносит её межбазовые рёбра (FK FromRecordId цел).
        repoA.DeleteRecord(t);
        Assert.Empty(repoA.GetLinkTargetIds(rel.Id!.Value, t));
    }
}
