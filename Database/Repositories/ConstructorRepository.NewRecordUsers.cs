using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    // Начальные значения не являются правкой или новым назначением задачи: пишем их
    // напрямую, как при создании обычной записи, до явно выбранных ролей диалога.
    private void InitializeNewRecordUserValues(int recordId, IReadOnlyCollection<int> fieldIds)
    {
        if (fieldIds.Count == 0) return;
        var user = CurrentUser.DisplayName;
        foreach (var fieldId in fieldIds)
            db.DbFieldValues.Add(new DbFieldValueEntity
            {
                RecordId = recordId, FieldId = fieldId, Value = user,
            });
        db.SaveChanges();
    }

    private CalendarRecord AddCalendarRecordWithUserDefaults(CalendarRecord record, bool initializeUserValues)
    {
        Demand(PermAction.CreateRecord, record.CollectionId);
        var original = (record.Id, record.CollectionId, record.FieldId);
        try
        {
            return ExecuteAtomicWrite(() =>
            {
                (record.Id, record.CollectionId, record.FieldId) = original;
                return AddCalendarRecordCore(record, resolveAutoFiles: true, initializeUserValues: initializeUserValues);
            });
        }
        catch
        {
            (record.Id, record.CollectionId, record.FieldId) = original;
            throw;
        }
    }

    // Перенос сохраняет значения источника, в том числе пустые. Новыми значениями
    // текущего пользователя их заменять нельзя; свойства копируются следующим шагом.
    internal CalendarRecord AddCalendarRecordForTransfer(CalendarRecord record) =>
        AddCalendarRecordWithUserDefaults(record, initializeUserValues: false);
}
