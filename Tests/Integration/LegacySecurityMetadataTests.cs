using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Entities;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Tests.Integration;

/// <summary>Старые ограничения остаются историческими данными и больше не закрывают рабочие операции.</summary>
public sealed class LegacySecurityMetadataTests
{
    private const string FieldConfig = "{\"confidentiality\":\"closed\",\"custom\":\"сохранить\"}";

    [SkippableTheory]
    [InlineData("sqlite")]
    [InlineData("postgres")]
    public void Reopening_a_database_ignores_old_passwords_masks_and_labels_but_preserves_people_and_values(string provider)
    {
        using var data = new MultiUserTestDatabase(provider);
        var collection = SampleData.AddCollection(data.Repo, "Старые данные").Id!.Value;
        var field = data.Repo.AddField(new DbField
            { CollectionId = collection, Name = "Описание", FieldType = "text", ConfigJson = FieldConfig }).Id!.Value;
        var person = SampleData.AddField(data.Repo, collection, "Исполнитель", "user", position: 2000).Id!.Value;
        var original = SampleData.AddRecord(data.Repo, collection, "Старая запись").Id!.Value;
        data.Repo.SetFieldValue(original, field, "Сохраненное значение");
        data.Repo.SetFieldValue(original, person, "Иванов Иван");
        StoreLegacyRestrictions(data.Manager.Context, collection);
        data.Manager.Dispose();

        using var reopened = DatabaseManager.Open(9, "После обновления", data.Path, provider: provider);
        var repo = reopened.Constructor;
        Assert.Contains("Иванов Иван", repo.GetUserOptions());
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
        var me = check.DbUsers.AsNoTracking().Single(u => u.UserKey == CurrentUser.Key);
        Assert.False(me.IsAdmin);
        Assert.False(me.IsApproved);
        Assert.True(me.AccessDecided);
        Assert.Contains(check.DbPermissions.AsNoTracking(), p => p.UserId == me.Id && p.ActionsMask == 0);
        Assert.Equal("legacy-test-only", check.DbMeta.AsNoTracking().Single(m => m.Key == "password.hash").Value);
        Assert.Equal("closed", check.DbMeta.AsNoTracking().Single(m => m.Key == "confidentiality").Value);
        Assert.Contains(check.DbUsers.AsNoTracking(), u => u.UserKey == "legacy-person" && u.DisplayName == "Иванов Иван");
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
        StoreLegacyRestrictions(source.Manager.Context, collection);
        StoreLegacyRestrictions(target.Manager.Context, null);

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

    private static void StoreLegacyRestrictions(AppDbContext context, int? collection)
    {
        var me = context.DbUsers.Single(u => u.UserKey == CurrentUser.Key);
        me.IsAdmin = false; me.IsApproved = false; me.AccessDecided = true;
        context.DbPermissions.Add(new DbPermissionEntity { UserId = me.Id, CollectionId = collection, ActionsMask = 0 });
        if (!context.DbUsers.Any(u => u.UserKey == "legacy-person"))
            context.DbUsers.Add(new DbUserEntity
            {
                UserKey = "legacy-person", DisplayName = "Иванов Иван", IsAdmin = false,
                IsApproved = false, AccessDecided = true, CreatedAt = "2026-01-01T00:00:00Z",
            });
        foreach (var (key, value) in new[]
        {
            ("password.hash", "legacy-test-only"), ("password.salt", "test-salt"),
            ("password.iterations", "210000"), ("access.password.enabled", "0"), ("confidentiality", "closed"),
        })
        {
            var row = context.DbMeta.Find(key);
            if (row is null) context.DbMeta.Add(new DbMetaEntity { Key = key, Value = value });
            else row.Value = value;
        }
        context.SaveChanges();
    }
}
