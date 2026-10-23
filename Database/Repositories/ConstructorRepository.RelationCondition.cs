using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

/// <summary>Новая связь отклонена условием выбора записей (или условие сломано и решать по нему
/// нельзя). Текст — для человека: какая запись и какое условие.</summary>
public sealed class RelationConditionException(string message) : InvalidOperationException(message);

/// <summary>Решение по условию выбора для набора записей-целей: какие подходят и сломано ли
/// условие. <see cref="Broken"/> — текст, почему решать нельзя (новые связи не выбираются).</summary>
public sealed record RelationConditionVerdict(IReadOnlySet<int> Allowed, string? Broken, string Condition);

/// <summary>
/// Условие выбора записей связи (<see cref="RelationCondition"/>): одна функция решает, подходит
/// ли запись-цель, и ее зовут все пути, которые создают новые связи, — выбор, вставка,
/// преобразование «Текст → Связь», автоподбор, импорт, дубль. Методы вызываются на репозитории
/// целевой базы. Уже выбранные связи условие не снимает; их несоответствия показывает проверка.
/// </summary>
public partial class ConstructorRepository
{
    private const int ConditionChunk = 500;

    /// <summary>Какие из записей <paramref name="ids"/> коллекции <paramref name="collectionId"/>
    /// подходят под условие <paramref name="cfg"/>. <paramref name="ids"/> = null — вся коллекция
    /// (автоподбор). Без условия подходят все переданные записи.</summary>
    public RelationConditionVerdict EvaluateRelationCondition(int collectionId, RelationConfig cfg,
        IReadOnlyCollection<int>? ids)
    {
        if (!cfg.HasCondition)
            return new(ids?.ToHashSet() ?? AllRecordIds(collectionId), null, "");
        if (cfg.WhereUnreadable is not null)
            return new(new HashSet<int>(), "Условие выбора задано более новой версией программы или повреждено — " +
                "новые связи не выбираются. Уберите или исправьте условие в окне свойства-связи.", "");
        var conditions = cfg.Where!;
        var fieldIds = conditions.Where(c => c.Field != RelationConfig.TitleField).Select(c => int.Parse(c.Field)).Distinct().ToList();
        var fields = db.DbFields.AsNoTracking().Where(f => fieldIds.Contains(f.Id) && f.CollectionId == collectionId)
            .ToDictionary(f => f.Id);
        var titleName = GetSystemFieldNames(collectionId).GetValueOrDefault(TitleKey, "Название");
        var parts = new List<string>();
        foreach (var condition in conditions)
        {
            var field = condition.Field == RelationConfig.TitleField ? null : fields.GetValueOrDefault(int.Parse(condition.Field));
            if (condition.Field != RelationConfig.TitleField && field is null)
                return new(new HashSet<int>(), "Условие выбора ссылается на удаленное свойство — новые связи не " +
                    "выбираются. Исправьте условие в окне свойства-связи.", "");
            var type = RelationCondition.TypeOf(condition.Field, field?.FieldType);
            if (!RelationCondition.OpsFor(type).Contains(condition.Op))
                return new(new HashSet<int>(), $"Условие выбора не подходит к типу свойства «{field?.Name ?? titleName}» — " +
                    "новые связи не выбираются. Исправьте условие в окне свойства-связи.", "");
            parts.Add(condition.Describe(field?.Name ?? titleName, type));
        }
        var text = string.Join(" и ", parts);
        var allowed = new HashSet<int>();
        foreach (var chunk in (ids ?? (IReadOnlyCollection<int>)AllRecordIds(collectionId)).Distinct().Chunk(ConditionChunk))
        {
            var list = chunk.ToList();
            var titles = db.DbRecords.AsNoTracking().Where(r => list.Contains(r.Id) && r.CollectionId == collectionId)
                .Select(r => new { r.Id, r.Title }).ToDictionary(r => r.Id, r => r.Title ?? "");
            var values = fieldIds.Count == 0 ? [] : db.DbFieldValues.AsNoTracking()
                .Where(v => fieldIds.Contains(v.FieldId) && list.Contains(v.RecordId))
                .Select(v => new { v.RecordId, v.FieldId, v.Value }).ToList();
            var byRecord = values.GroupBy(v => v.RecordId)
                .ToDictionary(g => g.Key, g => g.ToDictionary(v => v.FieldId, v => v.Value ?? ""));
            foreach (var (id, title) in titles)
            {
                var own = byRecord.GetValueOrDefault(id);
                if (conditions.All(c =>
                    {
                        if (c.Field == RelationConfig.TitleField) return c.Matches(title, "text") == true;
                        var fid = int.Parse(c.Field);
                        return c.Matches(own?.GetValueOrDefault(fid) ?? "", fields[fid].FieldType) == true;
                    }))
                    allowed.Add(id);
            }
        }
        return new(allowed, null, text);
    }

    /// <summary>Проверка условия выбора перед сохранением свойства-связи: каждое свойство есть в
    /// целевой коллекции, сравнение подходит к его типу, значения заданы. Нечитаемое условие
    /// (поврежденное или более новой версии) сохраняется только без изменений.</summary>
    public void ValidateRelationCondition(DbField field)
    {
        if (field.FieldType != "relation" || RelationConfig.FromConfig(field.ConfigJson) is not { HasCondition: true } cfg)
            return;
        if (cfg.WhereUnreadable is not null)
        {
            var stored = field.Id is int id
                ? RelationConfig.FromConfig(db.DbFields.AsNoTracking().Where(f => f.Id == id).Select(f => f.ConfigJson).FirstOrDefault())
                : null;
            if (stored?.WhereUnreadable != cfg.WhereUnreadable)
                throw new InvalidOperationException("Условие выбора записей повреждено или задано более новой версией программы — уберите его.");
            return;
        }
        var where = cfg.Where!;
        if (where.Count > RelationCondition.MaxConditions)
            throw new InvalidOperationException($"Условий выбора записей — не больше {RelationCondition.MaxConditions}.");
        var target = cfg.TargetDbId == 0 ? this : RelationDbResolver.RepoFor(cfg.TargetDbId)
            ?? throw new InvalidOperationException("Целевая база связи недоступна: условие выбора проверить нельзя.");
        target.Demand(PermAction.ViewCollection, cfg.TargetCollectionId);
        var options = target.GetRelationPropertyOptions(cfg.TargetCollectionId).ToDictionary(o => o.Key);
        foreach (var condition in where)
        {
            if (!options.TryGetValue(condition.Field, out var option))
                throw new InvalidOperationException("Свойства из условия выбора нет в целевой коллекции — выберите другое или уберите условие.");
            var type = RelationCondition.TypeOf(condition.Field, option.FieldType);
            if (!RelationCondition.OpsFor(type).Contains(condition.Op))
                throw new InvalidOperationException($"Свойство «{option.Name}» нельзя поставить в условие выбора с таким сравнением.");
            if (condition.NeedsValue && condition.Values.All(v => v.Trim().Length == 0))
                throw new InvalidOperationException($"Укажите значение в условии «{option.Name}».");
            if (condition.Values.Count > (RelationCondition.IsChoice(type) ? RelationCondition.MaxValues : 1)
                || condition.Values.Any(v => v.Length > RelationCondition.MaxValueLength))
                throw new InvalidOperationException(
                    $"Значение в условии «{option.Name}» длиннее {RelationCondition.MaxValueLength} знаков или их слишком много.");
        }
    }

    /// <summary>Текст условия связи для окон («Статус» равно «Активен» и «ИНН» заполнено); пусто —
    /// условия нет или оно сломано.</summary>
    public string DescribeRelationCondition(int collectionId, RelationConfig cfg) =>
        cfg.HasCondition ? EvaluateRelationCondition(collectionId, cfg, []).Condition : "";

    // Сколько кандидатов из индекса точного значения разбирать в памяти; больше — окнами по порядку.
    private const int ConditionIndexCap = 4096;
    private const int ConditionWindow = 512;

    /// <summary>Поиск записей для выбора с условием: только подходящие, в порядке записей
    /// коллекции, не больше <paramref name="take"/>. Решает та же функция, что держит ворота
    /// (<see cref="EvaluateRelationCondition"/>); SQL лишь сужает кандидатов. Точное «равно»
    /// по списку или «отмечено» идет от индекса (FieldId, Value) — редкое значение находится
    /// сразу; иначе коллекция читается окнами до набора результата.</summary>
    private List<RelationPropertyValue> SearchRelationRecordsWithCondition(int collectionId, string fieldKey,
        string term, int excludeId, int take, RelationConfig cfg, CancellationToken ct)
    {
        var probe = EvaluateRelationCondition(collectionId, cfg, []);
        if (probe.Broken is not null) return [];
        var result = new List<RelationPropertyValue>();

        void Take(IReadOnlyList<int> window)
        {
            var allowed = EvaluateRelationCondition(collectionId, cfg, window).Allowed;
            var ids = window.Where(id => id != excludeId && allowed.Contains(id)).ToList();
            if (ids.Count == 0) return;
            var values = ReadRelationPropertyValues(collectionId, fieldKey, ids);
            foreach (var value in values)
            {
                if (term.Length > 0 && (value.Display == RelationProperty.MissingField
                    || !value.Display.Contains(term, StringComparison.CurrentCultureIgnoreCase))) continue;
                result.Add(value);
                if (result.Count >= take) return;
            }
        }

        if (ExactConditionCandidates(collectionId, cfg) is { } exact)
        {
            // Кандидатов немного: их порядок — порядок записей коллекции.
            var ordered = new List<(int Id, int Position)>();
            foreach (var chunk in exact.Chunk(ConditionChunk).Select(c => c.ToList()))
                ordered.AddRange(db.DbRecords.AsNoTracking().Where(r => chunk.Contains(r.Id) && r.CollectionId == collectionId)
                    .Select(r => new { r.Id, r.Position }).ToList().Select(r => (r.Id, r.Position)));
            foreach (var window in ordered.OrderBy(r => r.Position).ThenBy(r => r.Id).Select(r => r.Id).Chunk(ConditionWindow))
            {
                ct.ThrowIfCancellationRequested();
                Take(window);
                if (result.Count >= take) break;
            }
            return result;
        }

        int? lastPosition = null, lastId = null;
        while (result.Count < take)
        {
            ct.ThrowIfCancellationRequested();
            var page = db.DbRecords.AsNoTracking().Where(r => r.CollectionId == collectionId);
            if (lastPosition is int position && lastId is int id)
                page = page.Where(r => r.Position > position || r.Position == position && r.Id > id);
            var window = page.OrderBy(r => r.Position).ThenBy(r => r.Id).Take(ConditionWindow)
                .Select(r => new { r.Id, r.Position }).ToList();
            if (window.Count == 0) break;
            Take(window.Select(r => r.Id).ToList());
            (lastPosition, lastId) = (window[^1].Position, window[^1].Id);
        }
        return result;
    }

    // Кандидаты от индекса (FieldId, Value) для точного условия: «равно» по списку, статусу или
    // пользователю и «отмечено» у флажка. null — такого условия нет или совпадений слишком много.
    private List<int>? ExactConditionCandidates(int collectionId, RelationConfig cfg)
    {
        foreach (var condition in cfg.Where ?? [])
        {
            if (!int.TryParse(condition.Field, out var fieldId)) continue;
            var type = db.DbFields.AsNoTracking().Where(f => f.Id == fieldId && f.CollectionId == collectionId)
                .Select(f => f.FieldType).FirstOrDefault();
            IReadOnlyList<string>? values = RelationCondition.IsChoice(type) && condition.Op == RelationCondition.Equal
                ? condition.Values
                : type == "checkbox" && condition.Op == RelationCondition.NotEmpty ? CheckboxValue.CheckedSpellings : null;
            if (values is null) continue;
            var list = values.ToList();
            var ids = db.DbFieldValues.AsNoTracking().Where(v => v.FieldId == fieldId && list.Contains(v.Value))
                .Select(v => v.RecordId).Take(ConditionIndexCap + 1).ToList();
            return ids.Count > ConditionIndexCap ? null : ids;
        }
        return null;
    }

    /// <summary>Сообщение человеку о связях, которые дубль не скопировал из-за условия выбора.
    /// Ставит главное окно (уведомление); вызывается на потоке операции — подписчик маршалит в UI
    /// сам. Без подписчика копия создается так же.</summary>
    public static Action<string>? ConditionNotice;

    /// <summary>Ребра записей <paramref name="sourceIds"/>, которые дубль не копирует: цель не
    /// подходит под условие выбора своей связи (иначе дубль стал бы обходом условия). Старые
    /// ребра в другую базу (не по настройке) и связи без условия копируются как раньше.</summary>
    private List<(int FieldId, int TargetDbId, int ToRecordId)> RefusedDuplicateTargets(int collectionId,
        IReadOnlyCollection<int> sourceIds)
    {
        var refused = new List<(int, int, int)>();
        var fields = db.DbFields.AsNoTracking().Where(f => f.CollectionId == collectionId && f.FieldType == "relation").ToList();
        foreach (var field in fields)
        {
            if (RelationConfig.FromConfig(field.ConfigJson) is not { HasCondition: true } cfg) continue;
            var targets = new HashSet<int>();
            foreach (var chunk in sourceIds.Chunk(ConditionChunk).Select(c => c.ToList()))
                targets.UnionWith(db.DbRecordLinks.AsNoTracking()
                    .Where(l => l.FieldId == field.Id && l.TargetDbId == cfg.TargetDbId && chunk.Contains(l.FromRecordId))
                    .Select(l => l.ToRecordId).Distinct().ToList());
            if (targets.Count == 0) continue;
            var target = cfg.TargetDbId == 0 ? this : RelationDbResolver.RepoFor(cfg.TargetDbId);
            // Цель не проверить (база закрыта, условие сломано) — новые связи не создаются, как у ворот.
            var allowed = target is null ? new HashSet<int>()
                : target.EvaluateRelationCondition(cfg.TargetCollectionId, cfg, targets) is { Broken: null } verdict
                    ? verdict.Allowed : new HashSet<int>();
            refused.AddRange(targets.Where(id => !allowed.Contains(id)).Select(id => (field.Id, cfg.TargetDbId, id)));
        }
        return refused;
    }

    // Убрать у копий ребра на цели вне условия. newIdsSql — SQL-выражение со списком id копий.
    // Возвращает, сколько связей не скопировано, по свойствам.
    private Dictionary<int, int> DropRefusedDuplicateLinks(
        IReadOnlyCollection<(int FieldId, int TargetDbId, int ToRecordId)> refused, string newIdsSql)
    {
        var dropped = new Dictionary<int, int>();
        foreach (var group in refused.GroupBy(r => (r.FieldId, r.TargetDbId)))
            foreach (var chunk in group.Select(r => r.ToRecordId).Chunk(ConditionChunk))
            {
                // Имена колонок без кавычек: в PostgreSQL они строчные, в SQLite регистр не важен.
                #pragma warning disable EF1002   // в SQL только числа из базы
                var count = db.Database.ExecuteSqlRaw(
                    $"DELETE FROM db_record_links WHERE FieldId = {group.Key.FieldId} AND TargetDbId = {group.Key.TargetDbId} " +
                    $"AND ToRecordId IN ({string.Join(",", chunk)}) AND FromRecordId IN ({newIdsSql})");
                #pragma warning restore EF1002
                dropped[group.Key.FieldId] = dropped.GetValueOrDefault(group.Key.FieldId) + count;
            }
        return dropped;
    }

    private void ReportSkippedDuplicateLinks(IReadOnlyDictionary<int, int> dropped) =>
        ReportSkippedLinks(dropped, "Копия создана. Не скопировано связей");

    // «Копия создана. Не скопировано связей — 2 («Клиент»): записи не подходят под условие выбора.»
    private void ReportSkippedLinks(IReadOnlyDictionary<int, int> skipped, string lead)
    {
        var total = skipped.Values.Sum();
        if (total == 0) return;
        var ids = skipped.Keys.ToList();
        var names = db.DbFields.AsNoTracking().Where(f => ids.Contains(f.Id)).Select(f => f.Name).ToList();
        ConditionNotice?.Invoke($"{lead} — {total} ({string.Join(", ", names.Select(n => $"«{n}»"))}): " +
            "записи не подходят под условие выбора.");
    }

    private HashSet<int> AllRecordIds(int collectionId) =>
        db.DbRecords.AsNoTracking().Where(r => r.CollectionId == collectionId).Select(r => r.Id).ToHashSet();

    // Отказ в новой связи: какая запись и какое условие. Названия цели видит только тот, кто
    // прошел проверку права просмотра целевой коллекции.
    internal string ConditionRefusal(int collectionId, IReadOnlyCollection<int> refused, string condition)
    {
        var names = db.DbRecords.AsNoTracking().Where(r => refused.Contains(r.Id) && r.CollectionId == collectionId)
            .OrderBy(r => r.Position).ThenBy(r => r.Id).Take(3).Select(r => r.Title).ToList()
            .Select(t => $"«{TitleText.FirstLine(t ?? "")}»").ToList();
        var who = refused.Count == 1 ? $"Запись {names.FirstOrDefault() ?? ""} не подходит"
            : $"Записи {string.Join(", ", names)}{(refused.Count > names.Count ? $" и еще {refused.Count - names.Count}" : "")} не подходят";
        return $"{who} под условие выбора: {condition}.";
    }
}
