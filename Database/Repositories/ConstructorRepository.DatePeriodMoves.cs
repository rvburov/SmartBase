using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    // Конфиг переносимого периода собирается из исходной схемы ровно один раз: номера
    // уже сопоставленных полей могут совпасть с прежними номерами других свойств.
    // Возвращаем только совместимые фиксированные снимки; автоматические значения не пишем.
    private HashSet<int> PrepareMovedDatePeriods(IReadOnlyCollection<DbField> sourceFields, int targetCollectionId,
        IReadOnlyDictionary<int, (int FieldId, string From, string To)> plan, IReadOnlySet<int> createdFields)
    {
        var periods = sourceFields.Where(f => f.FieldType == "date_period" && f.Id is int id
            && plan.TryGetValue(id, out var mapped) && mapped.To == "date_period").ToList();
        if (periods.Count == 0) return [];
        var bySource = sourceFields.ToDictionary(f => f.Id!.Value);
        var parsed = periods.ToDictionary(f => f.Id!.Value, f => DatePeriodConfig.FromConfig(f.ConfigJson)
            ?? throw MoveDatePeriodError(f.Name, "настройка периода повреждена"));
        var lockIds = periods.Select(f => plan[f.Id!.Value].FieldId).ToList();
        foreach (var config in parsed.Values)
        {
            if (plan.TryGetValue(config.StartFieldId, out var start)) lockIds.Add(start.FieldId);
            if (config.EndFieldId is int end && plan.TryGetValue(end, out var mappedEnd)) lockIds.Add(mappedEnd.FieldId);
        }
        LockDatePeriodMetadata(lockIds);
        var targets = db.DbFields.AsNoTracking().Where(f => f.CollectionId == targetCollectionId)
            .ToList().Select(ToDbField).ToDictionary(f => f.Id!.Value);
        var compatible = new HashSet<int>();
        foreach (var source in periods)
        {
            var config = parsed[source.Id!.Value];
            int MappedDate(int oldId)
            {
                if (!bySource.TryGetValue(oldId, out var original) || original.FieldType != "date"
                    || !plan.TryGetValue(oldId, out var mapped) || mapped.To != "date"
                    || !targets.TryGetValue(mapped.FieldId, out var targetDate) || targetDate.FieldType != "date")
                    throw MoveDatePeriodError(source.Name, "сопоставьте его источники со свойствами типа «Дата» в целевой коллекции");
                return mapped.FieldId;
            }
            var expected = config with
            {
                StartFieldId = MappedDate(config.StartFieldId),
                EndFieldId = config.EndFieldId is int end ? MappedDate(end) : null,
            };
            var targetId = plan[source.Id!.Value].FieldId;
            if (!targets.TryGetValue(targetId, out var target) || target.FieldType != "date_period")
                throw MoveDatePeriodError(source.Name, "тип целевого свойства изменился, повторите перенос");
            if (createdFields.Contains(targetId))
            {
                target.ConfigJson = expected.ToConfigJson(target.ConfigJson);
                ValidateDatePeriodField(target);
                // Только конфиг: роли/позиции нового свойства уже определены путем переноса.
                db.DbFields.Find(targetId)!.ConfigJson = target.ConfigJson;
            }
            else ValidateDatePeriodField(target);
            var targetConfig = DatePeriodConfig.FromConfig(target.ConfigJson)!;
            if (!config.Automatic && !targetConfig.Automatic && expected == targetConfig)
                compatible.Add(source.Id.Value);
        }
        db.SaveChanges();
        return compatible;
    }

    private static InvalidOperationException MoveDatePeriodError(string name, string reason) =>
        new($"Не удалось перенести период «{name}»: {reason}. Исходная запись сохранена.");

    // Это перенос проверенного снимка, а не ручная запись вычисляемой ячейки. Пустой снимок
    // тоже переносим: его отсутствие позволило бы повторно зафиксировать значение при создании.
    private void CopyFixedDatePeriodSnapshot(int recordId, int fieldId, string value)
    {
        db.Database.ExecuteSqlRaw(
            "INSERT INTO \"db_field_values\" (\"recordid\", \"fieldid\", \"value\") VALUES ({0}, {1}, {2}) " +
            "ON CONFLICT (\"recordid\", \"fieldid\") DO UPDATE SET \"value\" = EXCLUDED.\"value\"", recordId, fieldId, value);
        if (db.DbFieldValues.Local.FirstOrDefault(v => v.RecordId == recordId && v.FieldId == fieldId) is { } tracked)
        {
            tracked.Value = value;
            var entry = db.Entry(tracked);
            entry.Property(v => v.Value).OriginalValue = value;
            entry.Property(v => v.Value).IsModified = false;
        }
    }
}
