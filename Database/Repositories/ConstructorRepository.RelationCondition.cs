using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;

namespace SmartBase.Database.Repositories;

/// <summary>Новая связь отклонена условием выбора записей (или условие сломано и решать по нему
/// нельзя). Текст — для человека: какая запись и какое условие.</summary>
public sealed class RelationConditionException(string message) : InvalidOperationException(message);

/// <summary>Новые цели, отклоненные условием выбора, — для путей, которые пропускают ячейку, а не
/// отказывают всей операцией (вставка, заполнение).</summary>
internal sealed class RelationConditionRefusals
{
    public List<RelationTarget> Outside { get; } = [];
    /// <summary>Решить нельзя: условие сломано или база цели недоступна (текст причины).</summary>
    public string? Problem { get; set; }
    /// <summary>База цели недоступна, а условия нет: новые записи не проверить даже на
    /// существование. Ячейка пропускается, остальная вставка идет дальше.</summary>
    public bool TargetUnavailable { get; set; }
    public bool Any => Outside.Count > 0 || Problem is not null;
}

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

    /// <summary>Условие, разобранное для одной операции: свойства цели, текст и поломка читаются
    /// один раз, а не на каждую порцию записей.</summary>
    internal sealed record PreparedRelationCondition(int CollectionId, IReadOnlyList<RelationCondition> Conditions,
        IReadOnlyDictionary<int, string> FieldTypes, string Text, string? Broken);

    internal PreparedRelationCondition PrepareRelationCondition(int collectionId, RelationConfig cfg)
    {
        if (!cfg.HasCondition) return new(collectionId, [], new Dictionary<int, string>(), "", null);
        if (cfg.WhereUnreadable is not null)
            return new(collectionId, [], new Dictionary<int, string>(), "", "Условие выбора задано более новой версией программы " +
                "или повреждено — новые связи не выбираются. Уберите или исправьте условие в окне свойства-связи.");
        var conditions = cfg.Where!;
        var fieldIds = conditions.Where(c => c.Field != RelationConfig.TitleField).Select(c => int.Parse(c.Field)).Distinct().ToList();
        var fields = db.DbFields.AsNoTracking().Where(f => fieldIds.Contains(f.Id) && f.CollectionId == collectionId)
            .Select(f => new { f.Id, f.Name, f.FieldType }).ToList().ToDictionary(f => f.Id);
        // Подпись «Названия» — без отслеживания: условие решается и в фоне, на чужом контексте.
        var titleName = db.ColumnMeta.AsNoTracking()
            .Where(m => m.CollectionId == collectionId && m.Key == TitleKey && m.DisplayName != null)
            .Select(m => m.DisplayName).FirstOrDefault() ?? "Название";
        var parts = new List<string>();
        foreach (var condition in conditions)
        {
            var field = condition.Field == RelationConfig.TitleField ? null : fields.GetValueOrDefault(int.Parse(condition.Field));
            if (condition.Field != RelationConfig.TitleField && field is null)
                return new(collectionId, [], new Dictionary<int, string>(), "", "Условие выбора ссылается на удаленное свойство — " +
                    "новые связи не выбираются. Исправьте условие в окне свойства-связи.");
            var type = RelationCondition.TypeOf(condition.Field, field?.FieldType);
            if (!RelationCondition.OpsFor(type).Contains(condition.Op))
                return new(collectionId, [], new Dictionary<int, string>(), "", $"Условие выбора не подходит к типу свойства " +
                    $"«{field?.Name ?? titleName}» — новые связи не выбираются. Исправьте условие в окне свойства-связи.");
            parts.Add(condition.Describe(field?.Name ?? titleName, type));
        }
        return new(collectionId, conditions, fields.ToDictionary(f => f.Key, f => f.Value.FieldType), string.Join(" и ", parts), null);
    }

    /// <summary>Какие из записей <paramref name="ids"/> подходят под подготовленное условие.</summary>
    internal HashSet<int> AllowedByCondition(PreparedRelationCondition prepared, IReadOnlyCollection<int> ids, CancellationToken ct = default)
    {
        if (prepared.Conditions.Count == 0) return ids.ToHashSet();
        var fieldIds = prepared.FieldTypes.Keys.ToList();
        var allowed = new HashSet<int>();
        foreach (var chunk in ids.Distinct().Chunk(ConditionChunk))
        {
            ct.ThrowIfCancellationRequested();
            var list = chunk.ToList();
            var titles = db.DbRecords.AsNoTracking().Where(r => list.Contains(r.Id) && r.CollectionId == prepared.CollectionId)
                .Select(r => new { r.Id, r.Title }).ToDictionary(r => r.Id, r => r.Title ?? "");
            var values = fieldIds.Count == 0 ? [] : db.DbFieldValues.AsNoTracking()
                .Where(v => fieldIds.Contains(v.FieldId) && list.Contains(v.RecordId))
                .Select(v => new { v.RecordId, v.FieldId, v.Value }).ToList();
            var byRecord = values.GroupBy(v => v.RecordId)
                .ToDictionary(g => g.Key, g => g.ToDictionary(v => v.FieldId, v => v.Value ?? ""));
            foreach (var (id, title) in titles)
            {
                var own = byRecord.GetValueOrDefault(id);
                // Запись без названия хранит «Без названия» — для условия это пустое название.
                var titleText = TitleText.IsPlaceholder(title) ? "" : title;
                if (prepared.Conditions.All(c =>
                    {
                        if (c.Field == RelationConfig.TitleField) return c.Matches(titleText, "text") == true;
                        var fid = int.Parse(c.Field);
                        return c.Matches(own?.GetValueOrDefault(fid) ?? "", prepared.FieldTypes[fid]) == true;
                    }))
                    allowed.Add(id);
            }
        }
        return allowed;
    }

    /// <summary>Какие из записей <paramref name="ids"/> коллекции <paramref name="collectionId"/>
    /// подходят под условие <paramref name="cfg"/>. <paramref name="ids"/> = null — вся коллекция.
    /// Без условия подходят все переданные записи.</summary>
    public RelationConditionVerdict EvaluateRelationCondition(int collectionId, RelationConfig cfg,
        IReadOnlyCollection<int>? ids, CancellationToken ct = default)
    {
        if (!cfg.HasCondition)
            return new(ids?.ToHashSet() ?? AllRecordIds(collectionId), null, "");
        var prepared = PrepareRelationCondition(collectionId, cfg);
        if (prepared.Broken is { } broken) return new(new HashSet<int>(), broken, "");
        return new(AllowedByCondition(prepared, ids ?? (IReadOnlyCollection<int>)AllRecordIds(collectionId), ct), null, prepared.Text);
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
            // Текст в окне задается одним значением, но несколько (из файла, после смены типа) — «или»,
            // как у списка: условие с ними сохраняется без изменений.
            if (condition.Values.Count > RelationCondition.MaxValues
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
    // Окно поиска совпадает с порцией решения условия: одно окно — одна порция запросов.
    private const int ConditionWindow = ConditionChunk;

    /// <summary>Поиск записей для выбора с условием: только подходящие, в порядке записей
    /// коллекции, не больше <paramref name="take"/>. Решает та же функция, что держит ворота
    /// (<see cref="AllowedByCondition"/>), метаданные условия читаются один раз; SQL лишь сужает
    /// кандидатов. Точное «равно» по списку или «отмечено» идет от индекса (FieldId, Value) —
    /// редкое значение находится сразу; набранный запрос сужает записи тем же SQL, что поиск без
    /// условия; иначе коллекция читается окнами до набора результата.</summary>
    private List<RelationPropertyValue> SearchRelationRecordsWithCondition(int collectionId, string fieldKey,
        string term, int excludeId, int take, RelationConfig cfg, CancellationToken ct)
    {
        var prepared = PrepareRelationCondition(collectionId, cfg);
        if (prepared.Broken is not null) return [];
        var result = new List<RelationPropertyValue>();

        void Take(IReadOnlyList<int> window)
        {
            var allowed = AllowedByCondition(prepared, window, ct);
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

        var records = term.Length > 0 ? TermFilteredRecords(collectionId, fieldKey, term) : null;
        records ??= db.DbRecords.AsNoTracking().Where(r => r.CollectionId == collectionId);
        int? lastPosition = null, lastId = null;
        while (result.Count < take)
        {
            ct.ThrowIfCancellationRequested();
            var page = records;
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

    // Записи, где запрос есть в свойстве поиска, — тем же SQL, что поиск без условия (без учета
    // регистра, и у кириллицы тоже). null — свойство SQL так не сужает (несколько свойств, число,
    // дата, значения из связанных записей): окна идут по всей коллекции.
    private IQueryable<DbRecordEntity>? TermFilteredRecords(int collectionId, string fieldKey, string term)
    {
        var records = db.DbRecords.AsNoTracking().Where(r => r.CollectionId == collectionId);
        var pattern = "%" + EscapeLike(term) + "%";
        if (fieldKey == RelationConfig.TitleField)
            return db.IsPostgres ? records.Where(r => EF.Functions.ILike(r.Title, pattern, "\\"))
                : records.Where(r => SqlText.ContainsIgnoreCase(r.Title, term));
        if (!int.TryParse(fieldKey, out var fieldId)) return null;
        var type = db.DbFields.AsNoTracking().Where(f => f.Id == fieldId && f.CollectionId == collectionId)
            .Select(f => f.FieldType).FirstOrDefault();
        if (type is not ("text" or "markdown" or "url" or "select" or "status" or "user")) return null;
        var values = db.DbFieldValues.AsNoTracking().Where(v => v.FieldId == fieldId);
        values = db.IsPostgres ? values.Where(v => EF.Functions.ILike(v.Value, pattern, "\\"))
            : values.Where(v => SqlText.ContainsIgnoreCase(v.Value, term));
        var ids = values.Select(v => v.RecordId);
        return records.Where(r => ids.Contains(r.Id));
    }

    // Кандидаты от индекса (FieldId, Value) для точного условия: «равно» по списку, статусу или
    // пользователю и «отмечено» у флажка. Совпадений слишком много — пробуется следующее точное
    // условие; null — избирательного точного условия нет.
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
            if (ids.Count <= ConditionIndexCap) return ids;
        }
        return null;
    }

    /// <summary>Сообщение человеку о связях, которые дубль, перенос или импорт не создали из-за
    /// условия выбора. Ставит главное окно (уведомление); вызывается на потоке операции — подписчик
    /// маршалит в UI сам. Без подписчика операция идет так же.</summary>
    public static Action<string>? ConditionNotice;

    /// <summary>Сколько связей свойства не создано и почему: <see cref="Problem"/> = null — цели не
    /// подходят под условие, иначе — почему условие не проверить (сломано, база цели недоступна).</summary>
    internal readonly record struct SkippedLinks(int Count, string? Problem);

    private const string TargetUnavailable = "Целевая база связи недоступна: условие выбора проверить нельзя.";

    /// <summary>Решить условие на базе цели связи. Своя база — этот репозиторий; другая — свое
    /// подключение к уже открытой базе: дубль и перенос идут в фоне, а общий контекст окна в фон
    /// не уходит, и базу отсюда не монтируем. Сбой чтения — «проверить нельзя», а не отказ всей
    /// операции.</summary>
    internal RelationConditionVerdict EvaluateConditionOnTarget(RelationConfig cfg, IReadOnlyCollection<int> ids,
        CancellationToken ct = default)
    {
        try
        {
            if (cfg.TargetDbId == 0) return EvaluateRelationCondition(cfg.TargetCollectionId, cfg, ids, ct);
            if (RelationDbResolver.HasScopedResolver)
                return RelationDbResolver.RepoFor(cfg.TargetDbId)?.EvaluateRelationCondition(cfg.TargetCollectionId, cfg, ids, ct)
                    ?? new(new HashSet<int>(), TargetUnavailable, "");
            if (RelationDbResolver.MountedRepoFor(cfg.TargetDbId) is not { } mounted) return new(new HashSet<int>(), TargetUnavailable, "");
            using var context = new AppDbContext(mounted.DbPath);
            var isolated = new ConstructorRepository(context);
            return isolated.EvaluateRelationCondition(cfg.TargetCollectionId, cfg, ids, ct);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or IOException
                                       or InvalidOperationException)
        {
            return new(new HashSet<int>(), TargetUnavailable, "");
        }
    }

    /// <summary>Ребра записей <paramref name="sourceIds"/>, которые дубль не копирует: цель не
    /// подходит под условие выбора своей связи (иначе дубль стал бы обходом условия) или условие
    /// не проверить. Старые ребра в другую базу (не по настройке) и связи без условия копируются
    /// как раньше. <c>Problems</c> — почему не проверить, по свойствам.</summary>
    private (List<(int FieldId, int TargetDbId, int ToRecordId)> Refused, Dictionary<int, string> Problems)
        RefusedDuplicateTargets(int collectionId, IReadOnlyCollection<int> sourceIds)
    {
        var refused = new List<(int, int, int)>();
        var problems = new Dictionary<int, string>();
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
            // Цель не проверить (база закрыта, условие сломано) — новые связи не создаются, как у ворот.
            var verdict = EvaluateConditionOnTarget(cfg, targets);
            if (verdict.Broken is { } broken) problems[field.Id] = broken;
            refused.AddRange(targets.Where(id => !verdict.Allowed.Contains(id)).Select(id => (field.Id, cfg.TargetDbId, id)));
        }
        return (refused, problems);
    }

    // Убрать у копий ребра на цели вне условия. newIdsSql — SQL-выражение со списком id копий.
    // Возвращает, сколько связей не скопировано, по свойствам.
    private Dictionary<int, SkippedLinks> DropRefusedDuplicateLinks(
        IReadOnlyCollection<(int FieldId, int TargetDbId, int ToRecordId)> refused, IReadOnlyDictionary<int, string> problems,
        string newIdsSql)
    {
        var dropped = new Dictionary<int, SkippedLinks>();
        foreach (var group in refused.GroupBy(r => (r.FieldId, r.TargetDbId)))
            foreach (var chunk in group.Select(r => r.ToRecordId).Chunk(ConditionChunk))
            {
                // Имена колонок без кавычек: в PostgreSQL они строчные, в SQLite регистр не важен.
                #pragma warning disable EF1002   // в SQL только числа из базы
                var count = db.Database.ExecuteSqlRaw(
                    $"DELETE FROM db_record_links WHERE FieldId = {group.Key.FieldId} AND TargetDbId = {group.Key.TargetDbId} " +
                    $"AND ToRecordId IN ({string.Join(",", chunk)}) AND FromRecordId IN ({newIdsSql})");
                #pragma warning restore EF1002
                var field = group.Key.FieldId;
                dropped[field] = new(dropped.GetValueOrDefault(field).Count + count, problems.GetValueOrDefault(field));
            }
        return dropped;
    }

    // «Копия создана.» / «Копии созданы.» — по числу копий операции.
    private void ReportSkippedDuplicateLinks(IReadOnlyDictionary<int, SkippedLinks> dropped, int copies) =>
        ReportSkippedLinks(dropped, copies == 1 ? "Копия создана." : "Копии созданы.", "скопировано");

    /// <summary>Уведомление о связях, которые операция не создала. Причины — отдельными фразами:
    /// «Копия создана. Не скопировано связей — 2 («Клиент»): записи не подходят под условие выбора.»
    /// Внутри <see cref="BatchConditionNotices"/> фразы копятся и уходят одним уведомлением.</summary>
    private void ReportSkippedLinks(IReadOnlyDictionary<int, SkippedLinks> skipped, string lead, string verb)
    {
        if (skipped.Values.Sum(s => s.Count) == 0) return;
        var ids = skipped.Keys.ToList();
        var names = db.DbFields.AsNoTracking().Where(f => ids.Contains(f.Id)).Select(f => new { f.Id, f.Name }).ToList()
            .ToDictionary(f => f.Id, f => f.Name);
        var parts = skipped.Where(p => p.Value.Count > 0)
            .Select(p => (Name: names.GetValueOrDefault(p.Key, ""), p.Value.Count, Reason: p.Value.Problem ?? "записи не подходят под условие выбора."))
            .ToList();
        if (NoticeBatch.Value is { } batch) { batch.Add(lead, verb, parts); return; }
        ConditionNotice?.Invoke(SkippedLinksText(lead, verb, parts));
    }

    private static string SkippedLinksText(string lead, string verb, IEnumerable<(string Name, int Count, string Reason)> parts) =>
        lead + string.Concat(parts.GroupBy(p => p.Reason).Select(g =>
            $" Не {verb} связей — {g.Sum(p => p.Count)} ({string.Join(", ", g.Select(p => p.Name).Distinct().Select(n => $"«{n}»"))}): " +
            char.ToLowerInvariant(g.Key[0]) + g.Key[1..]));

    private static readonly AsyncLocal<SkippedLinksBatch?> NoticeBatch = new();

    /// <summary>Операция из многих шагов (дубль записей по одной в календаре и задачах): уведомления
    /// о пропущенных связях копятся и уходят одним при завершении. Фоновые шаги внутри
    /// (Task.Run) видят тот же пакет.</summary>
    public static IDisposable BatchConditionNotices(string lead)
    {
        var batch = new SkippedLinksBatch(lead);
        NoticeBatch.Value = batch;
        return batch;
    }

    private sealed class SkippedLinksBatch(string lead) : IDisposable
    {
        private readonly List<(string Verb, string Name, int Count, string Reason)> _parts = [];

        public void Add(string _, string verb, IEnumerable<(string Name, int Count, string Reason)> parts)
        {
            lock (_parts) _parts.AddRange(parts.Select(p => (verb, p.Name, p.Count, p.Reason)));
        }

        public void Dispose()
        {
            NoticeBatch.Value = null;
            List<(string Verb, string Name, int Count, string Reason)> parts;
            lock (_parts) parts = [.. _parts];
            if (parts.Count == 0) return;
            ConditionNotice?.Invoke(lead + string.Concat(parts.GroupBy(p => p.Verb)
                .Select(g => SkippedLinksText("", g.Key, g.Select(p => (p.Name, p.Count, p.Reason))))));
        }
    }

    private HashSet<int> AllRecordIds(int collectionId) =>
        db.DbRecords.AsNoTracking().Where(r => r.CollectionId == collectionId).Select(r => r.Id).ToHashSet();

    // Отказ в новой связи: какая запись и какое условие. Названия цели видит только тот, кто
    // проверен на существование целевой коллекции.
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
