using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Utils;

namespace SmartBase.Database.Repositories;

// ── Вставка, которая не поместилась в таблицу ─────────────────────────────────
// С настройкой «Досоздавать строки и столбцы при вставке» блок из буфера, вышедший за последнюю
// строку или последний столбец, не отбрасывается: под него заводятся пустые записи в конце
// коллекции и текстовые свойства «Колонка N». Значения потом пишет обычная пакетная вставка.
//
// Отмена вставки (Ctrl+Z) убирает заведенное, но бережно, как и сами значения: запись или
// свойство, в которые после вставки успели что-то вписать, остаются на месте.
public partial class ConstructorRepository
{
    /// <summary>Имя досозданного свойства: «Колонка 1», «Колонка 2», … — первое свободное.</summary>
    public const string PasteColumnBaseName = "Колонка";

    /// <summary>
    /// Пустые записи в конце коллекции — одной транзакцией и одной строкой журнала. Название —
    /// «Без названия», как у любой новой записи. Значений по умолчанию (текущий пользователь в
    /// полях «Пользователь») нет: как у массовой заливки, все значения приходят от вставки.
    /// </summary>
    /// <returns>Id созданных записей сверху вниз.</returns>
    public List<int> AddEmptyRecords(int collectionId, int count)
    {
        Demand(PermAction.CreateRecord, collectionId);
        if (count <= 0) return [];

        var now    = DateTime.Now.ToString("o");
        var author = CurrentUser.DisplayName;
        using var tx = db.Database.BeginTransaction();
        var run      = AllocatePositions(collectionId, null, count);
        var entities = new List<DbRecordEntity>(count);
        for (var i = 0; i < count; i++)
            entities.Add(new DbRecordEntity
            {
                CollectionId = collectionId,
                CreatedAt    = now,
                UpdatedAt    = now,
                CreatedBy    = author,
                UpdatedBy    = author,
                Position     = run.At(i),
                Title        = TitleText.Placeholder,
            });
        db.DbRecords.AddRange(entities);
        var name = CollectionNameOf(collectionId) ?? "";
        Log("create", "record", name, name, count: count);
        db.SaveChanges();
        tx.Commit();

        var ids = entities.Select(e => e.Id).ToList();
        foreach (var id in ids) ResolveAutoFileForRecord(collectionId, id);
        NotifyRelationDataChanged(collectionId);
        return ids;
    }

    /// <summary>Текстовые свойства «Колонка N» в конце коллекции, по порядку.</summary>
    /// <returns>Id созданных свойств слева направо.</returns>
    public List<int> AddPasteTextFields(int collectionId, int count)
    {
        Demand(PermAction.CreateField, collectionId);
        var ids = new List<int>(Math.Max(0, count));
        if (count <= 0) return ids;

        var taken = db.DbFields.Where(f => f.CollectionId == collectionId)
                               .Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var number = 1;
        for (var i = 0; i < count; i++)
        {
            while (taken.Contains($"{PasteColumnBaseName} {number}")) number++;
            var name = $"{PasteColumnBaseName} {number}";
            taken.Add(name);
            var field = AddField(new DbField
            {
                CollectionId = collectionId,
                Name         = name,
                FieldType    = "text",
                Position     = NextFieldPosition(collectionId),
                ConfigJson   = "{}",
            });
            ids.Add(field.Id!.Value);
        }
        return ids;
    }

    /// <summary>
    /// Отмена досоздания: удалить из <paramref name="recordIds"/> записи, в которых не осталось
    /// данных (название «Без названия» или пустое, ни одного непустого значения, ни одной связи), и
    /// из <paramref name="fieldIds"/> — свойства без единого непустого значения и связи. Остальное
    /// человек уже успел заполнить после вставки — его не трогаем.
    /// </summary>
    public (int Records, int Fields) RemoveUntouchedPasteGrowth(int collectionId, IReadOnlyList<int> recordIds,
                                                                IReadOnlyList<int> fieldIds)
    {
        var recordList = recordIds.Distinct().ToList();
        var fieldList  = fieldIds.Distinct().ToList();

        var emptyRecords = recordList.Count == 0 ? [] : db.DbRecords.AsNoTracking()
            .Where(r => r.CollectionId == collectionId && recordList.Contains(r.Id)
                     && (r.Title == null || r.Title == "" || r.Title == TitleText.Placeholder)
                     && !db.DbFieldValues.Any(v => v.RecordId == r.Id && v.Value != "")
                     && !db.DbRecordLinks.Any(l => l.FromRecordId == r.Id))
            .Select(r => r.Id).ToList();
        var emptyFields = fieldList.Count == 0 ? [] : db.DbFields.AsNoTracking()
            .Where(f => f.CollectionId == collectionId && fieldList.Contains(f.Id)
                     && !db.DbFieldValues.Any(v => v.FieldId == f.Id && v.Value != "")
                     && !db.DbRecordLinks.Any(l => l.FieldId == f.Id))
            .Select(f => f.Id).ToList();

        if (emptyRecords.Count > 0)
        {
            Demand(PermAction.DeleteRecord, collectionId);
            DeleteRecordsData(emptyRecords);
        }
        if (emptyFields.Count > 0) Demand(PermAction.DeleteField, collectionId);
        foreach (var fieldId in emptyFields) DeleteField(fieldId);
        return (emptyRecords.Count, emptyFields.Count);
    }
}
