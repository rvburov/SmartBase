using Microsoft.EntityFrameworkCore;
using MyPlanner.Core.Models;
using MyPlanner.Database.Entities;

namespace MyPlanner.Database.Repositories;

public class EventRepository(AppDbContext db)
{
    private const string FieldName = "Название";
    private const string FieldDate = "Дата";
    private const string FieldDesc = "Описание";

    // ── Публичный API ──────────────────────────────────────────────────────────

    public Event Add(Event e)
    {
        var colId = e.CollectionId ?? GetOrCreateDefaultCollection();
        var fieldIds = EnsureSystemFields(colId);

        var maxPos = db.DbRecords
            .Where(r => r.CollectionId == colId)
            .Select(r => (int?)r.Position)
            .Max() ?? 0;

        var rec = new DbRecordEntity
        {
            CollectionId = colId,
            StartDt      = DateTime.SpecifyKind(e.StartDt, DateTimeKind.Unspecified).ToString("o"),
            EndDt        = DateTime.SpecifyKind(e.EndDt,   DateTimeKind.Unspecified).ToString("o"),
            AllDay       = e.AllDay,
            CreatedAt    = DateTime.Now.ToString("o"),
            Position     = maxPos + 1000,
        };
        db.DbRecords.Add(rec);
        db.SaveChanges();

        SetFieldValues(rec.Id, fieldIds, e);

        e.Id           = rec.Id;
        e.CollectionId = colId;
        return e;
    }

    public void Update(Event e)
    {
        if (e.Id is null) return;

        var rec = db.DbRecords.Find(e.Id.Value);
        if (rec is null) return;

        var oldColId = rec.CollectionId;
        var newColId = e.CollectionId ?? oldColId;

        if (oldColId != newColId)
        {
            db.DbRecords.Remove(rec);
            db.SaveChanges();
            e.CollectionId = newColId;
            e.Id = null;
            Add(e);
            return;
        }

        rec.StartDt = DateTime.SpecifyKind(e.StartDt, DateTimeKind.Unspecified).ToString("o");
        rec.EndDt   = DateTime.SpecifyKind(e.EndDt,   DateTimeKind.Unspecified).ToString("o");
        rec.AllDay  = e.AllDay;
        db.SaveChanges();

        var fieldIds = EnsureSystemFields(newColId);
        SetFieldValues(rec.Id, fieldIds, e);
    }

    public void Delete(int id)
    {
        var rec = db.DbRecords.Find(id);
        if (rec is not null)
        {
            db.DbRecords.Remove(rec);
            db.SaveChanges();
        }
    }

    public List<Event> GetByDateRange(DateTime start, DateTime end, ISet<int>? activeIds = null)
    {
        // нормализуем Kind → Unspecified, чтобы строковое сравнение ISO было корректным
        // независимо от того, в каком Kind пришли границы диапазона
        var startIso = DateTime.SpecifyKind(start, DateTimeKind.Unspecified).ToString("o");
        var endIso   = DateTime.SpecifyKind(end,   DateTimeKind.Unspecified).ToString("o");

        var query = db.DbRecords
            .Where(r => r.StartDt != null && r.StartDt.CompareTo(endIso) < 0
                                          && r.EndDt!.CompareTo(startIso) >= 0)
            .OrderBy(r => r.StartDt);

        return RowsToEvents(query, activeIds);
    }

    public List<Event> GetAll(ISet<int>? activeIds = null)
    {
        var query = db.DbRecords
            .Where(r => r.StartDt != null)
            .OrderBy(r => r.StartDt);

        return RowsToEvents(query, activeIds);
    }

    public int Count() =>
        db.DbRecords.Count(r => r.StartDt != null);

    // ── Вспомогательные методы ─────────────────────────────────────────────────

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
            .ToDictionary(f => f.Name, f => f.Id);

        var maxPos = db.DbFields
            .Where(f => f.CollectionId == collectionId)
            .Select(f => (int?)f.Position)
            .Max() ?? 0;

        foreach (var name in new[] { FieldName, FieldDate, FieldDesc })
        {
            if (existing.ContainsKey(name)) continue;

            maxPos += 1000;
            var field = new DbFieldEntity
            {
                CollectionId = collectionId,
                Name         = name,
                // «Описание» — markdown; остальное — text
                FieldType    = name == FieldDesc ? "markdown" : "text",
                Position     = maxPos,
                IsSystem     = true,
                IsRequired   = name == FieldName,
            };
            db.DbFields.Add(field);
            db.SaveChanges();
            existing[name] = field.Id;
        }

        return existing;
    }

    private void SetFieldValues(int recordId, Dictionary<string, int> fieldIds, Event e)
    {
        var dateStr = FormatEventDate(e.StartDt, e.EndDt, e.AllDay);

        var pairs = new[]
        {
            (FieldName, e.Title),
            (FieldDate, dateStr),
            (FieldDesc, e.Description),
        };

        foreach (var (name, value) in pairs)
        {
            if (!fieldIds.TryGetValue(name, out var fid)) continue;

            var fv = db.DbFieldValues.FirstOrDefault(x => x.RecordId == recordId && x.FieldId == fid);
            if (fv is null)
            {
                db.DbFieldValues.Add(new DbFieldValueEntity { RecordId = recordId, FieldId = fid, Value = value });
            }
            else
            {
                fv.Value = value;
            }
        }
        db.SaveChanges();
    }

    private List<Event> RowsToEvents(IQueryable<DbRecordEntity> query, ISet<int>? activeIds)
    {
        var records = query.ToList();
        if (activeIds is not null)
            records = records.Where(r => activeIds.Contains(r.CollectionId)).ToList();

        if (records.Count == 0) return [];

        var colMap = db.DbCollections.ToDictionary(c => c.Id);
        var colIds = records.Select(r => r.CollectionId).Distinct().ToList();

        // Имена полей в коллекции могут дублироваться — берём первое поле с таким именем,
        // иначе ToDictionary упадёт на дубликатном ключе.
        var fieldsByCol = colIds.ToDictionary(
            cid => cid,
            cid => db.DbFields
                .Where(f => f.CollectionId == cid)
                .AsEnumerable()
                .GroupBy(f => f.Name)
                .ToDictionary(g => g.Key, g => g.First().Id)
        );

        var recIds = records.Select(r => r.Id).ToList();
        var fvMap = db.DbFieldValues
            .Where(fv => recIds.Contains(fv.RecordId))
            .GroupBy(fv => fv.RecordId)
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(fv => fv.FieldId, fv => fv.Value)
            );

        var events = new List<Event>();
        foreach (var rec in records)
        {
            if (!DateTime.TryParse(rec.StartDt, out var startDt)) continue;
            if (!DateTime.TryParse(rec.EndDt,   out var endDt))   continue;

            colMap.TryGetValue(rec.CollectionId, out var col);
            fieldsByCol.TryGetValue(rec.CollectionId, out var fields);
            fvMap.TryGetValue(rec.Id, out var values);

            var titleFid = fields?.GetValueOrDefault(FieldName);
            var descFid  = fields?.GetValueOrDefault(FieldDesc);

            events.Add(new Event
            {
                Id             = rec.Id,
                Title          = (titleFid.HasValue ? values?.GetValueOrDefault(titleFid.Value) : null) ?? "",
                Description    = (descFid.HasValue  ? values?.GetValueOrDefault(descFid.Value)  : null) ?? "",
                StartDt        = startDt,
                EndDt          = endDt,
                AllDay         = rec.AllDay,
                CollectionId   = rec.CollectionId,
                CollectionName = col?.Name ?? "",
                Color          = col?.Color ?? "#007AFF",
            });
        }

        return events;
    }

    private static string FormatEventDate(DateTime s, DateTime e, bool allDay)
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
