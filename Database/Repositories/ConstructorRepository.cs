using Microsoft.EntityFrameworkCore;
using MyPlanner.Core.Models;
using MyPlanner.Database.Entities;

namespace MyPlanner.Database.Repositories;

public class ConstructorRepository(AppDbContext db)
{
    // Системное поле «Дата» — единственное, что осталось schema-driven.
    // Название и Описание подняты до колонок Title/Description в db_records.
    private const string FieldDate = "Дата";

    // ── Группы ────────────────────────────────────────────────────────────────

    public List<DbGroup> GetAllGroups() =>
        db.DbGroups
          .OrderBy(g => g.Position)
          .Select(g => new DbGroup { Id = g.Id, Name = g.Name, ParentId = g.ParentId, Position = g.Position })
          .ToList();

    public DbGroup AddGroup(DbGroup group)
    {
        var entity = new DbGroupEntity { Name = group.Name, ParentId = group.ParentId, Position = group.Position };
        db.DbGroups.Add(entity);
        db.SaveChanges();
        group.Id = entity.Id;
        return group;
    }

    public void UpdateGroup(DbGroup group)
    {
        var entity = db.DbGroups.Find(group.Id);
        if (entity is null) return;
        entity.Name     = group.Name;
        entity.ParentId = group.ParentId;
        entity.Position = group.Position;
        db.SaveChanges();
    }

    public void DeleteGroup(int groupId)
    {
        // Рекурсивно удаляем все вложенные группы.
        var childIds = db.DbGroups.Where(g => g.ParentId == groupId).Select(g => g.Id).ToList();
        foreach (var childId in childIds)
            DeleteGroup(childId);

        // Удаляем коллекции этой группы; поля/записи/значения уйдут каскадно.
        db.DbCollections.RemoveRange(db.DbCollections.Where(c => c.GroupId == groupId));

        var entity = db.DbGroups.Find(groupId);
        if (entity is not null) db.DbGroups.Remove(entity);
        db.SaveChanges();
    }

    // ── Коллекции ─────────────────────────────────────────────────────────────

    public List<DbCollection> GetAllCollections() =>
        db.DbCollections
          .OrderBy(c => c.Position)
          .Select(c => new DbCollection
          {
              Id = c.Id, Name = c.Name, GroupId = c.GroupId,
              Position = c.Position, Icon = c.Icon, Color = c.Color,
          })
          .ToList();

    public List<DbCollection> GetCollectionsByGroup(int? groupId)
    {
        var query = groupId is null
            ? db.DbCollections.Where(c => c.GroupId == null)
            : db.DbCollections.Where(c => c.GroupId == groupId);

        return query.OrderBy(c => c.Position)
                    .Select(c => new DbCollection
                    {
                        Id = c.Id, Name = c.Name, GroupId = c.GroupId,
                        Position = c.Position, Icon = c.Icon, Color = c.Color,
                    })
                    .ToList();
    }

    public DbCollection AddCollection(DbCollection col)
    {
        var entity = new DbCollectionEntity
        {
            Name = col.Name, GroupId = col.GroupId,
            Position = col.Position, Icon = col.Icon, Color = col.Color,
        };
        db.DbCollections.Add(entity);
        db.SaveChanges();
        col.Id = entity.Id;

        // Title и Description теперь являются колонками таблицы db_records.
        // В схеме DbFields их больше не создаём.
        return col;
    }

    public void UpdateCollection(DbCollection col)
    {
        var entity = db.DbCollections.Find(col.Id);
        if (entity is null) return;
        entity.Name     = col.Name;
        entity.GroupId  = col.GroupId;
        entity.Position = col.Position;
        entity.Icon     = col.Icon;
        entity.Color    = col.Color;
        db.SaveChanges();
    }

    public void DeleteCollection(int collectionId)
    {
        var entity = db.DbCollections.Find(collectionId);
        if (entity is not null) db.DbCollections.Remove(entity);
        db.SaveChanges();
    }

    // ── Поля ──────────────────────────────────────────────────────────────────

    public List<DbField> GetFields(int collectionId) =>
        db.DbFields
          .Where(f => f.CollectionId == collectionId)
          .OrderBy(f => f.Position)
          .Select(f => new DbField
          {
              Id = f.Id, CollectionId = f.CollectionId, Name = f.Name,
              FieldType = f.FieldType, ConfigJson = f.ConfigJson, Position = f.Position,
              IsCalStart = f.IsCalStart, IsCalEnd = f.IsCalEnd,
              IsSystem = f.IsSystem, IsRequired = f.IsRequired,
          })
          .ToList();

    public DbField AddField(DbField field)
    {
        if (db.DbFields.Any(f => f.CollectionId == field.CollectionId && f.Name == field.Name))
            throw new InvalidOperationException($"Поле с именем «{field.Name}» уже существует в этой коллекции.");

        var entity = new DbFieldEntity
        {
            CollectionId = field.CollectionId, Name = field.Name,
            FieldType    = field.FieldType,    ConfigJson = field.ConfigJson,
            Position     = field.Position,     IsCalStart = field.IsCalStart,
            IsCalEnd     = field.IsCalEnd,     IsSystem = field.IsSystem,
            IsRequired   = field.IsRequired,
        };
        db.DbFields.Add(entity);
        db.SaveChanges();
        field.Id = entity.Id;
        return field;
    }

    public void UpdateField(DbField field)
    {
        var entity = db.DbFields.Find(field.Id);
        if (entity is null) return;
        if (entity.Name != field.Name &&
            db.DbFields.Any(f => f.CollectionId == entity.CollectionId && f.Name == field.Name && f.Id != field.Id))
            throw new InvalidOperationException($"Поле с именем «{field.Name}» уже существует в этой коллекции.");
        entity.Name       = field.Name;
        entity.FieldType  = field.FieldType;
        entity.ConfigJson = field.ConfigJson;
        entity.Position   = field.Position;
        entity.IsCalStart = field.IsCalStart;
        entity.IsCalEnd   = field.IsCalEnd;
        entity.IsSystem   = field.IsSystem;
        entity.IsRequired = field.IsRequired;
        db.SaveChanges();
    }

    public void DeleteField(int fieldId)
    {
        var entity = db.DbFields.Find(fieldId);
        if (entity is not null) db.DbFields.Remove(entity);
        db.SaveChanges();
    }

    public void ReorderFields(List<int> orderedIds)
    {
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var entity = db.DbFields.Find(orderedIds[i]);
            if (entity is not null) entity.Position = (i + 1) * 1000;
        }
        db.SaveChanges();
    }

    // ── Записи (низкоуровневые DbRecord) ──────────────────────────────────────

    public List<DbRecord> GetRecords(int collectionId) =>
        db.DbRecords
          .Where(r => r.CollectionId == collectionId)
          .OrderBy(r => r.Position)
          .Select(r => new DbRecord
          {
              Id = r.Id, CollectionId = r.CollectionId, CreatedAt = r.CreatedAt,
              Position = r.Position, StartDt = r.StartDt, EndDt = r.EndDt, AllDay = r.AllDay,
              Title = r.Title, Description = r.Description,
          })
          .ToList();

    public int CountRecords(int collectionId) =>
        db.DbRecords.Count(r => r.CollectionId == collectionId);

    public List<DbRecord> GetRecordsPage(int collectionId, int skip, int take) =>
        db.DbRecords
          .Where(r => r.CollectionId == collectionId)
          .OrderBy(r => r.Position)
          .Skip(skip)
          .Take(take)
          .Select(r => new DbRecord
          {
              Id = r.Id, CollectionId = r.CollectionId, CreatedAt = r.CreatedAt,
              Position = r.Position, StartDt = r.StartDt, EndDt = r.EndDt, AllDay = r.AllDay,
              Title = r.Title, Description = r.Description,
          })
          .ToList();

    public Dictionary<int, Dictionary<int, string>> GetFieldValuesBatch(IReadOnlyList<int> recordIds)
    {
        if (recordIds.Count == 0) return [];
        return db.DbFieldValues
            .Where(fv => recordIds.Contains(fv.RecordId))
            .GroupBy(fv => fv.RecordId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(fv => fv.FieldId, fv => fv.Value));
    }

    public int GetRecordPosition(int collectionId, int recordId)
    {
        var pos = db.DbRecords
            .Where(r => r.CollectionId == collectionId && r.Id == recordId)
            .Select(r => (int?)r.Position)
            .FirstOrDefault();
        if (pos is null) return -1;
        return db.DbRecords.Count(r => r.CollectionId == collectionId && r.Position < pos);
    }

    public DbRecord? GetRecord(int recordId)
    {
        var r = db.DbRecords.Find(recordId);
        if (r is null) return null;
        return new DbRecord
        {
            Id = r.Id, CollectionId = r.CollectionId, CreatedAt = r.CreatedAt,
            Position = r.Position, StartDt = r.StartDt, EndDt = r.EndDt, AllDay = r.AllDay,
            Title = r.Title, Description = r.Description,
        };
    }

    public void SetRecordTitle(int recordId, string title)
    {
        var rec = db.DbRecords.Find(recordId);
        if (rec is null) return;
        rec.Title = title;
        db.SaveChanges();
    }

    public void SetRecordDescription(int recordId, string? description)
    {
        var rec = db.DbRecords.Find(recordId);
        if (rec is null) return;
        rec.Description = string.IsNullOrEmpty(description) ? null : description;
        db.SaveChanges();
    }

    public DbRecord AddRecord(DbRecord record)
    {
        var entity = new DbRecordEntity
        {
            CollectionId = record.CollectionId,
            CreatedAt    = record.CreatedAt ?? DateTime.Now.ToString("o"),
            Position     = record.Position,
            StartDt      = record.StartDt,
            EndDt        = record.EndDt,
            AllDay       = record.AllDay,
            Title        = record.Title,
            Description  = record.Description,
        };
        db.DbRecords.Add(entity);
        db.SaveChanges();
        record.Id = entity.Id;
        return record;
    }

    public void UpdateRecord(DbRecord record)
    {
        if (record.Id is null) return;
        var entity = db.DbRecords.Find(record.Id.Value);
        if (entity is null) return;

        entity.CollectionId = record.CollectionId;
        entity.Position     = record.Position;
        entity.StartDt      = record.StartDt;
        entity.EndDt        = record.EndDt;
        entity.AllDay       = record.AllDay;
        entity.Title        = record.Title;
        entity.Description  = record.Description;
        db.SaveChanges();
    }

    public void ReorderRecords(List<int> orderedIds)
    {
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var entity = db.DbRecords.Find(orderedIds[i]);
            if (entity is not null) entity.Position = (i + 1) * 1000;
        }
        db.SaveChanges();
    }

    public void DeleteRecord(int recordId)
    {
        var entity = db.DbRecords.Find(recordId);
        if (entity is not null) db.DbRecords.Remove(entity);
        db.SaveChanges();
    }

    // ── Значения полей ────────────────────────────────────────────────────────

    public Dictionary<int, string> GetFieldValues(int recordId) =>
        db.DbFieldValues
          .Where(fv => fv.RecordId == recordId)
          .ToDictionary(fv => fv.FieldId, fv => fv.Value);

    /// <summary>Все значения полей коллекции одним запросом (для фильтрации/сортировки).</summary>
    public Dictionary<int, Dictionary<int, string>> GetFieldValuesByCollection(int collectionId) =>
        db.DbFieldValues
          .Where(fv => fv.Record.CollectionId == collectionId)
          .Select(fv => new { fv.RecordId, fv.FieldId, fv.Value })
          .ToList()
          .GroupBy(x => x.RecordId)
          .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.FieldId, x => x.Value));

    // ── Фильтр колонок (Excel-стиль): уникальные значения с количеством ───────

    public List<(string Value, int Count)> GetTitleCounts(int collectionId) =>
        db.DbRecords
          .Where(r => r.CollectionId == collectionId)
          .GroupBy(r => r.Title)
          .Select(g => new { g.Key, Cnt = g.Count() })
          .ToList()
          .Select(x => (x.Key ?? "", x.Cnt))
          .ToList();

    public List<(string Value, int Count)> GetDescriptionCounts(int collectionId) =>
        db.DbRecords
          .Where(r => r.CollectionId == collectionId)
          .GroupBy(r => r.Description ?? "")
          .Select(g => new { g.Key, Cnt = g.Count() })
          .ToList()
          .Select(x => (x.Key ?? "", x.Cnt))
          .ToList();

    /// <summary>
    /// Значения поля с количеством. Записи без строки значения учитываются
    /// в пустом значении "" — в UI оно показывается как «(Пусто)».
    /// </summary>
    public List<(string Value, int Count)> GetFieldValueCounts(int collectionId, int fieldId)
    {
        var groups = db.DbFieldValues
            .Where(fv => fv.FieldId == fieldId && fv.Record.CollectionId == collectionId)
            .GroupBy(fv => fv.Value)
            .Select(g => new { g.Key, Cnt = g.Count() })
            .ToList();

        var result  = groups.Select(x => (Value: x.Key ?? "", Count: x.Cnt)).ToList();
        var total   = db.DbRecords.Count(r => r.CollectionId == collectionId);
        var missing = total - groups.Sum(g => g.Cnt);
        if (missing > 0)
        {
            var emptyIdx = result.FindIndex(t => t.Value == "");
            if (emptyIdx >= 0) result[emptyIdx] = ("", result[emptyIdx].Count + missing);
            else result.Add(("", missing));
        }
        return result;
    }

    public void SetFieldValue(int recordId, int fieldId, string value)
    {
        var fv = db.DbFieldValues.FirstOrDefault(x => x.RecordId == recordId && x.FieldId == fieldId);
        if (fv is null)
            db.DbFieldValues.Add(new DbFieldValueEntity { RecordId = recordId, FieldId = fieldId, Value = value });
        else
            fv.Value = value;

        db.SaveChanges();
    }

    // ── UI настройки ──────────────────────────────────────────────────────────

    public (bool ColResize, bool RowResize, string ViewMode) GetUiSettings(int collectionId)
    {
        var s = db.UiCollectionSettings.Find(collectionId);
        return s is null ? (false, false, "table") : (s.ColResize, s.RowResize, s.ViewMode);
    }

    public void SaveUiSettings(int collectionId, bool colResize, bool rowResize, string viewMode)
    {
        var s = db.UiCollectionSettings.Find(collectionId);
        if (s is null)
        {
            db.UiCollectionSettings.Add(new UiCollectionSettingEntity
            {
                CollectionId = collectionId, ColResize = colResize,
                RowResize = rowResize, ViewMode = viewMode,
            });
        }
        else
        {
            s.ColResize = colResize;
            s.RowResize = rowResize;
            s.ViewMode  = viewMode;
        }
        db.SaveChanges();
    }

    public Dictionary<int, int> GetColWidths(int collectionId) =>
        db.UiColWidths
          .Where(w => w.CollectionId == collectionId)
          .ToDictionary(w => w.FieldId, w => w.Width);

    public void SaveColWidths(int collectionId, Dictionary<int, int> widths)
    {
        db.UiColWidths.RemoveRange(db.UiColWidths.Where(w => w.CollectionId == collectionId));
        db.UiColWidths.AddRange(widths.Select(kv => new UiColWidthEntity
        {
            CollectionId = collectionId, FieldId = kv.Key, Width = kv.Value,
        }));
        db.SaveChanges();
    }

    public Dictionary<int, int> GetRowHeights(int collectionId) =>
        db.UiRowHeights
          .Where(h => h.CollectionId == collectionId)
          .ToDictionary(h => h.RecordId, h => h.Height);

    public void SaveRowHeights(int collectionId, Dictionary<int, int> heights)
    {
        db.UiRowHeights.RemoveRange(db.UiRowHeights.Where(h => h.CollectionId == collectionId));
        db.UiRowHeights.AddRange(heights.Select(kv => new UiRowHeightEntity
        {
            CollectionId = collectionId, RecordId = kv.Key, Height = kv.Value,
        }));
        db.SaveChanges();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ─── CalendarRecord API (для календарных представлений) ───────────────────
    // ══════════════════════════════════════════════════════════════════════════

    public CalendarRecord AddCalendarRecord(CalendarRecord r)
    {
        var colId = r.CollectionId ?? GetOrCreateDefaultCollection();
        var fieldIds = EnsureSystemFields(colId);

        var maxPos = db.DbRecords
            .Where(rec => rec.CollectionId == colId)
            .Select(rec => (int?)rec.Position)
            .Max() ?? 0;

        var rec = new DbRecordEntity
        {
            CollectionId = colId,
            StartDt      = DateTime.SpecifyKind(r.StartDt, DateTimeKind.Unspecified).ToString("o"),
            EndDt        = DateTime.SpecifyKind(r.EndDt,   DateTimeKind.Unspecified).ToString("o"),
            AllDay       = r.AllDay,
            CreatedAt    = DateTime.Now.ToString("o"),
            Position     = maxPos + 1000,
            Title        = r.Title,
            Description  = string.IsNullOrEmpty(r.Description) ? null : r.Description,
        };
        db.DbRecords.Add(rec);
        db.SaveChanges();

        SetCalendarFieldValues(rec.Id, fieldIds, r);

        r.Id           = rec.Id;
        r.CollectionId = colId;
        return r;
    }

    public void UpdateCalendarRecord(CalendarRecord r)
    {
        if (r.Id is null) return;

        var rec = db.DbRecords.Find(r.Id.Value);
        if (rec is null) return;

        var oldColId = rec.CollectionId;
        var newColId = r.CollectionId ?? oldColId;

        if (oldColId != newColId)
        {
            db.DbRecords.Remove(rec);
            db.SaveChanges();
            r.CollectionId = newColId;
            r.Id = null;
            AddCalendarRecord(r);
            return;
        }

        rec.StartDt     = DateTime.SpecifyKind(r.StartDt, DateTimeKind.Unspecified).ToString("o");
        rec.EndDt       = DateTime.SpecifyKind(r.EndDt,   DateTimeKind.Unspecified).ToString("o");
        rec.AllDay      = r.AllDay;
        rec.Title       = r.Title;
        rec.Description = string.IsNullOrEmpty(r.Description) ? null : r.Description;
        db.SaveChanges();

        var fieldIds = EnsureSystemFields(newColId);
        SetCalendarFieldValues(rec.Id, fieldIds, r);
    }

    public void DeleteCalendarRecord(int id)
    {
        var rec = db.DbRecords.Find(id);
        if (rec is not null)
        {
            db.DbRecords.Remove(rec);
            db.SaveChanges();
        }
    }

    public List<CalendarRecord> GetCalendarRecordsByRange(DateTime start, DateTime end, ISet<int>? activeIds = null)
    {
        // нормализуем Kind → Unspecified, чтобы строковое сравнение ISO было корректным
        // независимо от того, в каком Kind пришли границы диапазона
        var startIso = DateTime.SpecifyKind(start, DateTimeKind.Unspecified).ToString("o");
        var endIso   = DateTime.SpecifyKind(end,   DateTimeKind.Unspecified).ToString("o");

        var query = db.DbRecords
            .Where(r => r.StartDt != null && r.StartDt.CompareTo(endIso) < 0
                                          && r.EndDt!.CompareTo(startIso) >= 0)
            .OrderBy(r => r.StartDt);

        return RowsToCalendarRecords(query, activeIds);
    }

    public List<CalendarRecord> GetAllCalendarRecords(ISet<int>? activeIds = null)
    {
        var query = db.DbRecords
            .Where(r => r.StartDt != null)
            .OrderBy(r => r.StartDt);

        return RowsToCalendarRecords(query, activeIds);
    }

    public int CountCalendarRecords() =>
        db.DbRecords.Count(r => r.StartDt != null);

    // ── Вспомогательные методы для CalendarRecord ─────────────────────────────

    private int GetOrCreateDefaultCollection()
    {
        var col = db.DbCollections.FirstOrDefault(c => c.Name == "Без названия");
        if (col is not null) return col.Id;

        var maxPos = db.DbCollections.Select(c => (int?)c.Position).Max() ?? 0;
        col = new DbCollectionEntity { Name = "Без названия", Position = maxPos + 1000, Color = "#007AFF" };
        db.DbCollections.Add(col);
        db.SaveChanges();

        EnsureSystemFields(col.Id);
        return col.Id;
    }

    private Dictionary<string, int> EnsureSystemFields(int collectionId)
    {
        var existing = db.DbFields
            .Where(f => f.CollectionId == collectionId)
            .GroupBy(f => f.Name)
            .ToDictionary(g => g.Key, g => g.Min(f => f.Id));

        if (existing.ContainsKey(FieldDate)) return existing;

        var maxPos = db.DbFields
            .Where(f => f.CollectionId == collectionId)
            .Select(f => (int?)f.Position)
            .Max() ?? 0;

        var field = new DbFieldEntity
        {
            CollectionId = collectionId,
            Name         = FieldDate,
            FieldType    = "text",
            Position     = maxPos + 1000,
            IsSystem     = true,
            IsRequired   = false,
        };
        db.DbFields.Add(field);
        db.SaveChanges();
        existing[FieldDate] = field.Id;

        return existing;
    }

    private void SetCalendarFieldValues(int recordId, Dictionary<string, int> fieldIds, CalendarRecord r)
    {
        // В DbFieldValues пишем только системное поле «Дата» — title/description
        // живут в колонках db_records (Title / Description).
        if (!fieldIds.TryGetValue(FieldDate, out var fid)) return;

        var dateStr = FormatCalendarDate(r.StartDt, r.EndDt, r.AllDay);

        var fv = db.DbFieldValues.FirstOrDefault(x => x.RecordId == recordId && x.FieldId == fid);
        if (fv is null)
        {
            db.DbFieldValues.Add(new DbFieldValueEntity { RecordId = recordId, FieldId = fid, Value = dateStr });
        }
        else
        {
            fv.Value = dateStr;
        }
        db.SaveChanges();
    }

    private List<CalendarRecord> RowsToCalendarRecords(IQueryable<DbRecordEntity> query, ISet<int>? activeIds)
    {
        var records = query.ToList();
        if (activeIds is not null)
            records = records.Where(r => activeIds.Contains(r.CollectionId)).ToList();

        if (records.Count == 0) return [];

        var colMap = db.DbCollections.ToDictionary(c => c.Id);

        var result = new List<CalendarRecord>();
        foreach (var rec in records)
        {
            if (!DateTime.TryParse(rec.StartDt, out var startDt)) continue;
            if (!DateTime.TryParse(rec.EndDt,   out var endDt))   continue;

            colMap.TryGetValue(rec.CollectionId, out var col);

            result.Add(new CalendarRecord
            {
                Id             = rec.Id,
                Title          = rec.Title ?? "",
                Description    = rec.Description ?? "",
                StartDt        = startDt,
                EndDt          = endDt,
                AllDay         = rec.AllDay,
                CollectionId   = rec.CollectionId,
                CollectionName = col?.Name ?? "",
                Color          = col?.Color ?? "#007AFF",
            });
        }

        return result;
    }

    private static string FormatCalendarDate(DateTime s, DateTime e, bool allDay)
    {
        if (allDay)
        {
            return s.Date == e.Date
                ? s.ToString("dd.MM.yyyy")
                : $"{s:dd.MM.yyyy} - {e:dd.MM.yyyy}";
        }

        var st = s.ToString("HH:mm");
        var et = e.ToString("HH:mm");
        if (s.Date == e.Date)
            return st == et ? $"{s:dd.MM.yyyy} {st}" : $"{s:dd.MM.yyyy} {st} - {et}";

        return $"{s:dd.MM.yyyy} - {e:dd.MM.yyyy} {st}";
    }
}
