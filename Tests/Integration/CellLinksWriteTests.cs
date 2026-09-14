using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Вставка связей из буфера обмена: поиск записей по названиям
/// (<see cref="ConstructorRepository.ResolveRelationTitles"/>) и пакетная запись ребер
/// (<see cref="ConstructorRepository.SetLinksBulk"/>) — одной транзакцией и одной строкой журнала,
/// с отменой, которая не трогает связи, поправленные после.
/// </summary>
public sealed class CellLinksWriteTests
{
    // «Задачи» с полем-связью «Проект» → «Проекты», как в RelationLinkTests.
    private static (ConstructorRepository Repo, int TaskCol, int Rel, int ProjCol) Setup(TempDatabase db, bool single = false)
    {
        var repo    = db.Constructor;
        var projCol = SampleData.AddCollection(repo, "Проекты").Id!.Value;
        var taskCol = SampleData.AddCollection(repo, "Задачи").Id!.Value;
        var rel     = SampleData.AddField(repo, taskCol, "Проект", "relation");
        rel.ConfigJson = new RelationConfig(projCol, single).ToConfigJson();
        repo.UpdateField(rel);
        return (repo, taskCol, rel.Id!.Value, projCol);
    }

    /// <summary>Сначала весь текст ищется как одно название (в названии бывает запятая), потом —
    /// по кускам через разделитель.</summary>
    [Fact]
    public void Titles_are_matched_whole_first_then_by_parts()
    {
        using var db = new TempDatabase();
        var (repo, _, rel, projCol) = Setup(db);
        var firm  = SampleData.AddRecord(repo, projCol, "Иванов, Петров и партнеры").Id!.Value;
        var infra = SampleData.AddRecord(repo, projCol, "Инфра").Id!.Value;
        var site  = SampleData.AddRecord(repo, projCol, "Сайт").Id!.Value;

        var found = repo.ResolveRelationTitles(rel, ["Иванов, Петров и партнеры", "Инфра, Сайт", "Инфра, Нет такого", ""]);

        Assert.Equal([firm], found["Иванов, Петров и партнеры"].Ids);
        Assert.Equal([infra, site], found["Инфра, Сайт"].Ids);
        Assert.Equal(ConstructorRepository.TitleMatch.NotFound, found["Инфра, Нет такого"].Match);
        Assert.Equal(ConstructorRepository.TitleMatch.Found, found[""].Match);   // пусто — снять связи
        Assert.Empty(found[""].Ids);
    }

    /// <summary>Тезки — не угадываем, какую из записей имели в виду.</summary>
    [Fact]
    public void Namesakes_are_ambiguous()
    {
        using var db = new TempDatabase();
        var (repo, _, rel, projCol) = Setup(db);
        SampleData.AddRecord(repo, projCol, "Дубль");
        SampleData.AddRecord(repo, projCol, "Дубль");

        Assert.Equal(ConstructorRepository.TitleMatch.Ambiguous, repo.ResolveRelationTitles(rel, ["Дубль"])["Дубль"].Match);
    }

    [Fact]
    public async System.Threading.Tasks.Task Links_are_written_in_one_pass_with_one_log_row()
    {
        using var db = new TempDatabase();
        var (repo, taskCol, rel, projCol) = Setup(db);
        var p1 = SampleData.AddRecord(repo, projCol, "П1").Id!.Value;
        var p2 = SampleData.AddRecord(repo, projCol, "П2").Id!.Value;
        var t1 = SampleData.AddRecord(repo, taskCol, "З1").Id!.Value;
        var t2 = SampleData.AddRecord(repo, taskCol, "З2").Id!.Value;
        var before = (await repo.GetChangeLogPageAsync(new ConstructorRepository.ChangeLogQuery(), take: 500)).Count;

        var result = repo.SetLinksBulk(taskCol, [new(t1, rel, [p2, p1]), new(t2, rel, [p1])], "Вставка");

        Assert.Equal(2, result.Changed);
        Assert.Equal([p2, p1], repo.GetLinkTargetIds(rel, t1));
        Assert.Equal([p1], repo.GetLinkTargetIds(rel, t2));

        var page = await repo.GetChangeLogPageAsync(new ConstructorRepository.ChangeLogQuery(), take: 500);
        Assert.Equal(before + 1, page.Count);
        Assert.StartsWith("Вставка: З1 (Проект: (пусто) → «П2, П1»)", page[0].Detail);
    }

    /// <summary>Отмена возвращает прежние связи, но ячейку, которую поправили после вставки, не трогает.</summary>
    [Fact]
    public void Undo_restores_links_but_keeps_later_edits()
    {
        using var db = new TempDatabase();
        var (repo, taskCol, rel, projCol) = Setup(db);
        var p1 = SampleData.AddRecord(repo, projCol, "П1").Id!.Value;
        var p2 = SampleData.AddRecord(repo, projCol, "П2").Id!.Value;
        var t1 = SampleData.AddRecord(repo, taskCol, "З1").Id!.Value;
        var t2 = SampleData.AddRecord(repo, taskCol, "З2").Id!.Value;
        repo.SetLinks(rel, t1, [p1]);

        var paste = repo.SetLinksBulk(taskCol, [new(t1, rel, [p2]), new(t2, rel, [p2])], "Вставка");
        repo.SetLinks(rel, t2, [p1]);   // после вставки связь поправили

        var undo = repo.SetLinksBulk(taskCol,
            [.. paste.Changes.Select(c => new ConstructorRepository.LinkWrite(
                    c.RecordId, c.FieldId, c.OldIds, ConstructorRepository.LinkIdsKey(c.NewIds)))],
            "Отмена: вставка");

        Assert.Equal(1, undo.Changed);
        Assert.Equal(1, undo.Conflicts);
        Assert.Equal([p1], repo.GetLinkTargetIds(rel, t1));
        Assert.Equal([p1], repo.GetLinkTargetIds(rel, t2));
    }

    /// <summary>Правила набора — как у SetLinks: только записи целевой коллекции, без ссылки на
    /// себя, у связи «одна запись» — первая.</summary>
    [Fact]
    public void Foreign_targets_self_links_and_extra_single_links_are_dropped()
    {
        using var db = new TempDatabase();
        var (repo, taskCol, rel, projCol) = Setup(db, single: true);
        var p1 = SampleData.AddRecord(repo, projCol, "П1").Id!.Value;
        var p2 = SampleData.AddRecord(repo, projCol, "П2").Id!.Value;
        var t1 = SampleData.AddRecord(repo, taskCol, "З1").Id!.Value;
        var t2 = SampleData.AddRecord(repo, taskCol, "З2").Id!.Value;

        repo.SetLinksBulk(taskCol, [new(t1, rel, [t1, t2, p2, p1])]);

        Assert.Equal([p2], repo.GetLinkTargetIds(rel, t1));
    }

    /// <summary>Запись удалили, пока ее строка была на экране, — пакет ее пропускает и не падает.</summary>
    [Fact]
    public void Deleted_record_is_skipped()
    {
        using var db = new TempDatabase();
        var (repo, taskCol, rel, projCol) = Setup(db);
        var p1 = SampleData.AddRecord(repo, projCol, "П1").Id!.Value;
        var t1 = SampleData.AddRecord(repo, taskCol, "З1").Id!.Value;
        var t2 = SampleData.AddRecord(repo, taskCol, "З2").Id!.Value;
        repo.DeleteRecord(t2);

        var result = repo.SetLinksBulk(taskCol, [new(t1, rel, [p1]), new(t2, rel, [p1])]);

        Assert.Equal(1, result.Changed);
        Assert.Equal([p1], repo.GetLinkTargetIds(rel, t1));
    }
}
