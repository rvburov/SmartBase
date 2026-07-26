using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
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

        var newIds = repo.MoveRecordsToCollectionIsolated(db.Path, taskCol, [t], archive, createMissingFields: false,
                                                          sourceDbId: 1, targetDbId: 1);

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

    [Fact]
    public void Cross_db_single_link_replaces_and_persists()
    {
        using var dbA = new TempDatabase(dbId: 1, name: "A");
        using var dbB = new TempDatabase(dbId: 2, name: "B");
        var repoA = dbA.Constructor;
        var repoB = dbB.Constructor;
        var projB = SampleData.AddCollection(repoB, "Проекты").Id!.Value;
        var b1 = SampleData.AddRecord(repoB, projB, "B1").Id!.Value;
        var b2 = SampleData.AddRecord(repoB, projB, "B2").Id!.Value;

        var taskA = SampleData.AddCollection(repoA, "Задачи").Id!.Value;
        var rel = SampleData.AddField(repoA, taskA, "Проект", "relation");
        rel.ConfigJson = new RelationConfig(projB, true, "comma", 2).ToConfigJson();
        repoA.UpdateField(rel);
        var t = SampleData.AddRecord(repoA, taskA, "T1").Id!.Value;

        repoA.SetLinks(rel.Id!.Value, t, [b1]);
        Assert.Equal([b1], repoA.GetLinkTargetIds(rel.Id!.Value, t));   // single межбазовый — записалась
        repoA.SetLinks(rel.Id!.Value, t, [b2]);
        Assert.Equal([b2], repoA.GetLinkTargetIds(rel.Id!.Value, t));   // и заменилась
    }

    [Fact]
    public void Cross_db_move_rewrites_same_db_link_to_cross_db_origin()
    {
        using var dbA = new TempDatabase(dbId: 1, name: "A");
        using var dbB = new TempDatabase(dbId: 2, name: "B");
        var repoA = dbA.Constructor;
        var repoB = dbB.Constructor;

        // dbA: «Проекты» + «Задачи» с ВНУТРИБАЗОВОЙ связью «Проект» → Проекты(dbA) (TargetDbId=0).
        var projA = SampleData.AddCollection(repoA, "Проекты").Id!.Value;
        var p1 = SampleData.AddRecord(repoA, projA, "Проект-A").Id!.Value;
        var taskA = SampleData.AddCollection(repoA, "Задачи").Id!.Value;
        var relA = SampleData.AddField(repoA, taskA, "Проект", "relation");
        relA.ConfigJson = new RelationConfig(projA, false).ToConfigJson();
        repoA.UpdateField(relA);
        var t = SampleData.AddRecord(repoA, taskA, "Задача").Id!.Value;
        repoA.SetLinks(relA.Id!.Value, t, [p1]);

        // Переносим запись t из dbA в новую «Задачи» dbB (создаём недостающие поля).
        var taskB = SampleData.AddCollection(repoB, "Задачи").Id!.Value;
        var newIds = repoB.MoveRecordsToCollectionIsolated(dbA.Path, taskA, [t], taskB,
            createMissingFields: true, sourceDbId: 1, targetDbId: 2);

        // В dbB поле-связь «Проект» стало МЕЖБАЗОВЫМ на dbA (TargetDbId=1), ребро — на p1 (в dbA).
        using var verifyCtx = new AppDbContext(dbB.Path);
        var verify = new ConstructorRepository(verifyCtx);
        var relB = verify.GetFields(taskB).Single(f => f.Name == "Проект");
        var cfgB = RelationConfig.FromConfig(relB.ConfigJson)!;
        Assert.Equal(1,     cfgB.TargetDbId);          // внутрибазовая связь на источник → межбазовая на dbA
        Assert.Equal(projA, cfgB.TargetCollectionId);
        Assert.Equal([(p1, 1)], verify.GetLinkEdges(relB.Id!.Value, newIds[0]));   // ребро на p1 с TargetDbId=1
    }

    [Fact]
    public void Cross_db_collection_move_rewrites_external_and_self_links()
    {
        using var dbA = new TempDatabase(dbId: 1, name: "A");
        using var dbB = new TempDatabase(dbId: 2, name: "B");
        var repoA = dbA.Constructor;
        var repoB = dbB.Constructor;

        // dbA: «Проекты» (ОСТАНЕТСЯ) и «Задачи» с внешней связью на Проекты и САМО-связью на Задачи.
        var projA = SampleData.AddCollection(repoA, "Проекты").Id!.Value;
        var p1 = SampleData.AddRecord(repoA, projA, "Проект-1").Id!.Value;
        var taskA = SampleData.AddCollection(repoA, "Задачи").Id!.Value;
        var relProj = SampleData.AddField(repoA, taskA, "Проект", "relation", position: 1000);
        relProj.ConfigJson = new RelationConfig(projA, false).ToConfigJson();     // → Проекты(dbA), внутрибазовая
        repoA.UpdateField(relProj);
        var relSelf = SampleData.AddField(repoA, taskA, "Похожая", "relation", position: 2000);
        relSelf.ConfigJson = new RelationConfig(taskA, false).ToConfigJson();     // → Задачи(dbA), само-связь
        repoA.UpdateField(relSelf);
        var t1 = SampleData.AddRecord(repoA, taskA, "T1").Id!.Value;
        var t2 = SampleData.AddRecord(repoA, taskA, "T2").Id!.Value;
        repoA.SetLinks(relProj.Id!.Value, t1, [p1]);   // внешняя
        repoA.SetLinks(relSelf.Id!.Value, t1, [t2]);   // само-связь

        // Переносим коллекцию «Задачи» из dbA в dbB (ATTACH-путь).
        var newColB = repoB.MoveCollectionIsolated(dbA.Path, taskA, null, null, 10000, sourceDbId: 1, targetDbId: 2);
        Assert.NotEqual(0, newColB);

        using var verifyCtx = new AppDbContext(dbB.Path);
        var verify = new ConstructorRepository(verifyCtx);
        var fProj = verify.GetFields(newColB).Single(f => f.Name == "Проект");
        var fSelf = verify.GetFields(newColB).Single(f => f.Name == "Похожая");
        var newT1 = verifyCtx.DbRecords.AsNoTracking().First(r => r.CollectionId == newColB && r.Title == "T1").Id;
        var newT2 = verifyCtx.DbRecords.AsNoTracking().First(r => r.CollectionId == newColB && r.Title == "T2").Id;

        // Внешняя связь на «Проекты» (осталась в dbA): стала МЕЖБАЗОВОЙ на dbA (TargetDbId=1), цель = p1.
        var cProj = RelationConfig.FromConfig(fProj.ConfigJson)!;
        Assert.Equal(1, cProj.TargetDbId);
        Assert.Equal(projA, cProj.TargetCollectionId);
        Assert.Equal([(p1, 1)], verify.GetLinkEdges(fProj.Id!.Value, newT1));

        // Само-связь: осталась ВНУТРИБАЗОВОЙ (TargetDbId=0), цель-коллекция и ребро перемэплены на dbB.
        var cSelf = RelationConfig.FromConfig(fSelf.ConfigJson)!;
        Assert.Equal(0, cSelf.TargetDbId);
        Assert.Equal(newColB, cSelf.TargetCollectionId);
        Assert.Equal([(newT2, 0)], verify.GetLinkEdges(fSelf.Id!.Value, newT1));
    }

    // Регресс: пере-настройка цели поля оставляет «осиротевшие» рёбра с ЧУЖИМ TargetDbId, чей
    // ToRecordId может СЛУЧАЙНО совпасть с локальной записью. GetLinks/GetLinksBatch (внутрибазовый
    // резолв JOIN'ом к своим db_records) НЕ должны их подхватывать — иначе показ покажет чужую запись
    // («Событие 5»/«Проверка»). Каждое ребро резолвит ячейка по СВОЕМУ TargetDbId (GetLinkEdges).
    [Fact]
    public void Get_links_excludes_cross_db_edges_even_when_id_collides_locally()
    {
        using var db = new TempDatabase(dbId: 1, name: "A");
        var (repo, taskCol, rel, projCol) = Setup(db);
        var p1 = SampleData.AddRecord(repo, projCol, "Локальная цель").Id!.Value;
        var t  = SampleData.AddRecord(repo, taskCol, "Задача").Id!.Value;

        repo.SetLinks(rel, t, [p1]);   // нормальная ВНУТРИБАЗОВАЯ связь на p1 (TargetDbId=0)

        // Осиротевшее МЕЖБАЗОВОЕ ребро (TargetDbId=2), чей ToRecordId совпал с локальной p1.
        db.Context.DbRecordLinks.Add(new SmartBase.Database.Entities.DbRecordLinkEntity
        {
            FieldId = rel, FromRecordId = t, ToRecordId = p1, TargetDbId = 2, Position = 5000,
        });
        db.Context.SaveChanges();

        // Внутрибазовые резолверы видят ТОЛЬКО ребро TargetDbId=0 (чужое не приклеивается к db_records).
        Assert.Equal([(p1, "Локальная цель")], repo.GetLinks(rel, t));
        Assert.Equal([p1], repo.GetLinksBatch(rel, [t])[t].Select(x => x.Id));

        // Полный набор рёбер — со своими TargetDbId (ячейка резолвит каждое в своей БД).
        Assert.Equal([(p1, 0), (p1, 2)], repo.GetLinkEdges(rel, t));
    }

    // Наличие ВХОДЯЩИХ связей перед удалением (окно «связи будут удалены») — внутрибазовый.
    [Fact]
    public void Incoming_links_presence_same_db_for_records_and_collection()
    {
        using var db = new TempDatabase();
        var (repo, taskCol, rel, projCol) = Setup(db);
        var p1 = SampleData.AddRecord(repo, projCol, "P1").Id!.Value;
        var t1 = SampleData.AddRecord(repo, taskCol, "T1").Id!.Value;
        SampleData.AddRecord(repo, taskCol, "T2");
        repo.SetLinks(rel, t1, [p1]);   // t1 → p1

        Assert.True(repo.HasIncomingLinks([p1], 0));                  // на p1 ссылаются
        Assert.False(repo.HasIncomingLinks([t1], 0));                // на t1 — нет
        Assert.True(repo.HasIncomingLinksToCollections([projCol]));  // на «Проекты» есть входящие
        Assert.False(repo.HasIncomingLinksToCollections([taskCol])); // на «Задачи» — нет
        Assert.False(repo.HasIncomingLinks([p1], 5));                // межбазовый параметр в своей БД — нет
    }

    // Наличие ВХОДЯЩИХ межбазовых связей: из чужой базы (HasIncomingLinks с targetDbId) и карта+пересечение.
    [Fact]
    public void Incoming_links_presence_cross_db_and_intersect()
    {
        using var dbA = new TempDatabase(dbId: 1, name: "A");
        using var dbB = new TempDatabase(dbId: 2, name: "B");
        var repoA = dbA.Constructor;
        var repoB = dbB.Constructor;

        var targetsA = SampleData.AddCollection(repoA, "Цели").Id!.Value;
        var a1 = SampleData.AddRecord(repoA, targetsA, "A1").Id!.Value;
        var otherA = SampleData.AddCollection(repoA, "Прочее").Id!.Value;
        var a2 = SampleData.AddRecord(repoA, otherA, "A2").Id!.Value;

        var taskB = SampleData.AddCollection(repoB, "Задачи").Id!.Value;
        var rel = SampleData.AddField(repoB, taskB, "Цель", "relation");
        rel.ConfigJson = new RelationConfig(targetsA, false, "comma", 1).ToConfigJson();   // → dbA (TargetDbId=1)
        repoB.UpdateField(rel);
        var b1 = SampleData.AddRecord(repoB, taskB, "B1").Id!.Value;
        repoB.SetLinks(rel.Id!.Value, b1, [a1]);   // b1(dbB) → a1(dbA)

        Assert.True(repoB.HasIncomingLinks([a1], 1));    // из dbB видно входящее в dbA на a1
        Assert.False(repoB.HasIncomingLinks([a2], 1));   // на a2 — нет

        // Карта целей dbB→dbA + пересечение с коллекцией на стороне dbA.
        var map = repoB.IncomingLinkCountsByTarget(1);
        Assert.True(map.ContainsKey(a1));
        Assert.Equal([a1], repoA.RecordIdsInCollections(map.Keys.ToList(), [targetsA]));   // a1 в «Цели»
        Assert.Empty(repoA.RecordIdsInCollections(map.Keys.ToList(), [otherA]));           // но не в «Прочее»
    }

    // CollectionIdsInGroup — вся ветка группы (сама + вложенные), для подсчёта/удаления группы.
    [Fact]
    public void CollectionIdsInGroup_returns_whole_branch()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var g   = repo.AddGroup(new DbGroup { Name = "G" }).Id!.Value;
        var sub = repo.AddGroup(new DbGroup { Name = "Sub", ParentId = g }).Id!.Value;
        var c1  = SampleData.AddCollection(repo, "C1", g).Id!.Value;
        var c2  = SampleData.AddCollection(repo, "C2", sub).Id!.Value;
        SampleData.AddCollection(repo, "Снаружи");   // вне группы — не должна попасть

        Assert.Equal([c1, c2], repo.CollectionIdsInGroup(g).OrderBy(x => x).ToList());
    }

    // Межбазовый перенос ОДНОЙ коллекции: теряются только ВНЕШНИЕ входящие; само-связи внутри целы.
    [Fact]
    public void External_incoming_presence_excludes_within_collection_self_links()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;
        var colC = SampleData.AddCollection(repo, "C").Id!.Value;
        var c1 = SampleData.AddRecord(repo, colC, "c1").Id!.Value;
        var c2 = SampleData.AddRecord(repo, colC, "c2").Id!.Value;

        var relSelf = SampleData.AddField(repo, colC, "Похожая", "relation", position: 1000);
        relSelf.ConfigJson = new RelationConfig(colC, false).ToConfigJson();   // само-связь C→C
        repo.UpdateField(relSelf);
        repo.SetLinks(relSelf.Id!.Value, c1, [c2]);   // c1 → c2 (внутри C)

        // Входящие в C ЕСТЬ (само-связь), но ВНЕШНИХ нет — перенос коллекции их не теряет.
        Assert.True(repo.HasIncomingLinksToCollections([colC]));
        Assert.False(repo.HasExternalIncomingLinksToCollection(colC));

        // Добавляем ссылку ИЗВНЕ (из D) — внешние появились.
        var colD = SampleData.AddCollection(repo, "D").Id!.Value;
        var d1 = SampleData.AddRecord(repo, colD, "d1").Id!.Value;
        var relD = SampleData.AddField(repo, colD, "Цель", "relation", position: 1000);
        relD.ConfigJson = new RelationConfig(colC, false).ToConfigJson();      // D→C
        repo.UpdateField(relD);
        repo.SetLinks(relD.Id!.Value, d1, [c1]);      // d1 → c1 (извне)

        Assert.True(repo.HasExternalIncomingLinksToCollection(colC));
    }
}
