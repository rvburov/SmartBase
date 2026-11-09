using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Метки конфиденциальности сняты вместе с контуром доступа (30.10.2026): в старых базах они
/// остаются историческими данными (db_meta, ConfigJson свойства) и не закрывают ни чтение, ни
/// выгрузку, ни копирование и перенос. Права пользователей при этом действуют как прежде.
/// </summary>
public sealed class LegacyConfidentialityTests
{
    private const string FieldConfig = "{\"confidentiality\":\"closed\",\"custom\":\"сохранить\"}";

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void Reopening_a_database_ignores_old_labels_and_preserves_values(string provider)
    {
        using var data = new MultiUserTestDatabase(provider);
        var collection = SampleData.AddCollection(data.Repo, "Старые данные").Id!.Value;
        var field = data.Repo.AddField(new DbField
            { CollectionId = collection, Name = "Описание", FieldType = "text", ConfigJson = FieldConfig }).Id!.Value;
        var person = SampleData.AddField(data.Repo, collection, "Исполнитель", "user", position: 2000).Id!.Value;
        var original = SampleData.AddRecord(data.Repo, collection, "Старая запись").Id!.Value;
        data.Repo.SetFieldValue(original, field, "Сохраненное значение");
        data.Repo.SetFieldValue(original, person, "Иванов Иван");
        StoreLegacyLabels(data.Manager.Context);
        data.Manager.Dispose();

        using var reopened = DatabaseManager.Open(9, "После обновления", data.Path, provider: provider);
        var repo = reopened.Constructor;
        Assert.Equal("Сохраненное значение", repo.GetFieldValues(original)[field]);
        Assert.Equal("Иванов Иван", repo.GetFieldValues(original)[person]);
        Assert.Equal(FieldConfig, repo.GetFields(collection).Single(f => f.Id == field).ConfigJson);

        var created = SampleData.AddRecord(repo, collection, "Новая запись").Id!.Value;
        repo.SetFieldValue(created, field, "Измененное значение");
        repo.SetRecordTitle(created, "Измененное название");
        Assert.Equal("Измененное значение", repo.GetFieldValues(created)[field]);
        var columns = repo.GetExportColumnsIsolated(collection, includeHidden: true);
        var rows = repo.ReadExportRowsIsolated(collection, columns, false, default).ToList();
        Assert.Contains(rows, row => row.Contains("Измененное название") && row.Contains("Измененное значение"));
        Assert.Contains(rows, row => row.Contains("Старая запись") && row.Contains("Иванов Иван"));
        repo.DeleteRecord(created);
        Assert.Null(repo.GetRecord(created));
        Assert.Equal("Старая запись", repo.GetRecord(original)!.Title);

        using var check = data.OpenContext();
        Assert.Equal("closed", check.DbMeta.AsNoTracking().Single(m => m.Key == "confidentiality").Value);
        MultiUserTestDatabase.AssertHealthy(check);
    }

    [SkippableTheory]
    [InlineData("sqlite", "sqlite")]
    [InlineData("sqlite", "postgres")]
    [InlineData("postgres", "sqlite")]
    [InlineData("postgres", "postgres")]
    public void Copy_and_cross_database_move_preserve_values_and_configuration_despite_legacy_closed_labels(string sourceProvider, string targetProvider)
    {
        using var source = new MultiUserTestDatabase(sourceProvider);
        using var target = new MultiUserTestDatabase(targetProvider);
        var collection = SampleData.AddCollection(source.Repo, "Коллекция старой версии").Id!.Value;
        var field = source.Repo.AddField(new DbField
            { CollectionId = collection, Name = "Текст", FieldType = "text", ConfigJson = FieldConfig }).Id!.Value;
        var record = SampleData.AddRecord(source.Repo, collection, "Исходная запись").Id!.Value;
        source.Repo.SetFieldValue(record, field, "Полные данные");
        StoreLegacyLabels(source.Manager.Context);
        StoreLegacyLabels(target.Manager.Context);

        var copy = source.Repo.DuplicateCollection(collection).Id!.Value;
        var copiedRecord = Assert.Single(source.Repo.GetRecords(copy));
        var copiedField = Assert.Single(source.Repo.GetFields(copy));
        Assert.Equal("Полные данные", source.Repo.GetFieldValues(copiedRecord.Id!.Value)[copiedField.Id!.Value]);
        Assert.Equal(FieldConfig, copiedField.ConfigJson);
        Assert.NotNull(source.Repo.GetRecord(record));

        var moved = ConstructorRepository.MoveCollection(source.Repo, target.Repo, collection, null, "Перенесенная коллекция").Id!.Value;
        Assert.False(source.Manager.Context.DbCollections.Any(c => c.Id == collection));
        var movedRecord = Assert.Single(target.Repo.GetRecords(moved));
        var movedField = Assert.Single(target.Repo.GetFields(moved));
        Assert.Equal("Исходная запись", movedRecord.Title);
        Assert.Equal("Полные данные", target.Repo.GetFieldValues(movedRecord.Id!.Value)[movedField.Id!.Value]);
        Assert.Equal(FieldConfig, movedField.ConfigJson);
        using var sourceCheck = source.OpenContext();
        using var targetCheck = target.OpenContext();
        MultiUserTestDatabase.AssertHealthy(sourceCheck);
        MultiUserTestDatabase.AssertHealthy(targetCheck);
    }

    private static void StoreLegacyLabels(AppDbContext context)
    {
        var row = context.DbMeta.Find("confidentiality");
        if (row is null) context.DbMeta.Add(new DbMetaEntity { Key = "confidentiality", Value = "closed" });
        else row.Value = "closed";
        context.SaveChanges();
    }
}
