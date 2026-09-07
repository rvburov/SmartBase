using System.Text.Json;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Стадии для группы фильтра раздела «Задачи» (<c>ConstructorRepository.GetTaskStages</c>) и
/// пункты стадий одной коллекции для окна задачи (<c>GetTaskStageOptions</c>): берутся из
/// поля «Статус», а не из трех зашитых значений — стадий у коллекции сколько угодно.
/// </summary>
public sealed class TaskStagesTests
{
    private static void EnableTasks(ConstructorRepository repo, int collectionId)
    {
        var exec = SampleData.AddField(repo, collectionId, "Исполнитель", "user", position: 1000);
        repo.SetUserFieldTaskExec(exec.Id!.Value, true);
    }

    // Стадия дописывается в пункты поля «Статус» — так делает окно свойства.
    private static void AddStage(ConstructorRepository repo, int collectionId, string stage)
    {
        var field   = repo.GetFields(collectionId).First(f => f.IsTaskStatus);
        var options = ConstructorRepository.ReadOptions(field.ConfigJson);
        options.Add(stage);
        field.ConfigJson = JsonSerializer.Serialize(new { options });
        repo.UpdateField(field);
    }

    [Fact]
    public void Stages_merge_status_options_of_task_collections_in_field_order()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var first = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, first);
        var second = SampleData.AddCollection(repo, "Ремонты").Id!.Value;
        EnableTasks(repo, second);
        AddStage(repo, second, "Проверка");

        var stages = repo.GetTaskStages();

        Assert.Equal([.. ConstructorRepository.TaskStatusOptions, "Проверка"], stages);
    }

    [Fact]
    public void Active_ids_narrow_the_stages_and_an_empty_set_gives_none()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var first = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, first);
        var second = SampleData.AddCollection(repo, "Ремонты").Id!.Value;
        EnableTasks(repo, second);
        AddStage(repo, second, "Проверка");

        Assert.Equal(ConstructorRepository.TaskStatusOptions, repo.GetTaskStages(new HashSet<int> { first }));
        Assert.Contains("Проверка", repo.GetTaskStages(new HashSet<int> { second }));
        Assert.Empty(repo.GetTaskStages(new HashSet<int>()));
    }

    /// <summary>Коллекция без исполнителя в разделе не участвует — и стадий не дает, даже
    /// если поле-статус у нее есть.</summary>
    [Fact]
    public void Collection_without_an_executor_gives_no_stages()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var col = SampleData.AddCollection(repo, "Справочник").Id!.Value;
        SampleData.AddField(repo, col, "Статус", "status");

        Assert.Empty(repo.GetTaskStages());
    }

    [Fact]
    public void Stage_options_of_a_collection_follow_its_status_field_or_fall_back_to_defaults()
    {
        using var db = new TempDatabase();
        var repo = db.Constructor;

        var withTasks = SampleData.AddCollection(repo, "Заявки").Id!.Value;
        EnableTasks(repo, withTasks);
        AddStage(repo, withTasks, "Проверка");
        Assert.Equal([.. ConstructorRepository.TaskStatusOptions, "Проверка"], repo.GetTaskStageOptions(withTasks));

        var plain = SampleData.AddCollection(repo, "Без задач").Id!.Value;
        Assert.Equal(ConstructorRepository.TaskStatusOptions, repo.GetTaskStageOptions(plain));
    }
}
