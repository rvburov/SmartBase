using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

/// <summary>Постраничное чтение записей и истории изменений для HTTP API.</summary>
public partial class ConstructorRepository
{
    /// <summary>Сколько записей отдается за одно обращение. Дальше — курсором.</summary>
    public const int ApiPage = 500;

    /// <summary>Ключ колонки названия записи в параметре fields.</summary>
    public const string ApiTitleKey = "t";

    /// <summary>Страница записей по запросу.</summary>
    /// <param name="Columns">Имена колонок — в порядке ключей запроса.</param>
    /// <param name="Rows">Записи страницы: ключ, момент правки и значения в порядке колонок.</param>
    /// <param name="More">Есть ли записи дальше курсора.</param>
    /// <param name="Trouble">Почему читать нельзя; <c>null</c> — страница готова.</param>
    public sealed record ApiRows(
        IReadOnlyList<string>                                    Columns,
        IReadOnlyList<(int Id, string Updated, string[] Values)> Rows,
        bool                                                     More,
        string?                                                  Trouble)
    {
        /// <summary>Устойчивые ключи выбранных колонок: t либо ключ свойства.</summary>
        public IReadOnlyList<string> FieldKeys { get; init; } = [];
        public static ApiRows Bad(string trouble) => new([], [], false, trouble);
    }

    /// <summary>Строка журнала изменений по запросу — имена, а не ключи: журнал ведется для
    /// человека, и ключей записей в нем нет по построению.</summary>
    public sealed record ApiChange(long Id, string At, string Action, string Target, string Name);

    /// <summary>Хвост журнала изменений по запросу.</summary>
    public sealed record ApiChangesPage(
        IReadOnlyList<ApiChange> Changes,
        bool                     More,
        string?                  Trouble)
    {
        public static ApiChangesPage Bad(string trouble) => new([], false, trouble);
    }

    /// <summary>Коллекции по явному ключу или имени, без выбора первой при совпадениях.</summary>
    public IReadOnlyList<(int Id, string Name)> ApiCollectionsIsolated(string? name = null, int? id = null)
    {
        using var ctx = new AppDbContext(db.DbPath);
        var query = ctx.DbCollections.AsNoTracking().Where(c => c.Name != null);
        if (id is int key) query = query.Where(c => c.Id == key);
        return query.Select(c => new { c.Id, c.Name }).AsEnumerable()
            .Where(c => string.IsNullOrEmpty(name) || string.Equals(c.Name!.Trim(), name.Trim(),
                StringComparison.CurrentCultureIgnoreCase))
            .Select(c => (c.Id, c.Name!)).ToList();
    }

    // ── Записи ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Страница записей по запросу — на отдельном подключении: читает ее обработчик обращения,
    /// который живет в фоне.
    /// </summary>
    /// <param name="collectionId">Коллекция.</param>
    /// <param name="fieldKeys">Состав запроса: <c>t</c> и ключи свойств.</param>
    /// <param name="since">Отдавать только записи, правленные не раньше этого момента
    /// (ISO-8601); пусто — все.</param>
    /// <param name="after">Курсор: отдавать записи с ключом больше этого. Ноль — с начала.</param>
    /// <param name="take">Сколько записей на страницу; зажимается в 1..<see cref="ApiPage"/>.</param>
    public ApiRows ApiReadIsolated(int collectionId, IReadOnlyList<string>? fieldKeys,
                                   string since, int after, int take)
    {
        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        var isolated = new ConstructorRepository(ctx);

        if (isolated.CollectionName(collectionId).Length == 0)
            return ApiRows.Bad("коллекции больше нет");

        // Имена и ключи проверяются по актуальной схеме на этом же подключении.
        var available = isolated.GetExportColumns(collectionId, includeHidden: true)
                                .Where(c => c.FieldId is int)
                                .ToDictionary(
                                    c => c.FieldId!.Value.ToString(
                                        System.Globalization.CultureInfo.InvariantCulture),
                                    StringComparer.Ordinal);

        fieldKeys ??= [ApiTitleKey, .. available.Keys];

        var names  = new List<string>(fieldKeys.Count);
        var canonicalKeys = new List<string>(fieldKeys.Count);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var fields = new List<int?>(fieldKeys.Count);
        var auditTypes = new Dictionary<int, string>();

        foreach (var key in fieldKeys)
        {
            if (key == ApiTitleKey)
            {
                if (!selected.Add(ApiTitleKey)) continue;
                canonicalKeys.Add(ApiTitleKey);
                names.Add(isolated.GetSystemFieldNames(collectionId)
                                  .GetValueOrDefault(TitleKey, "Название"));
                fields.Add(null);
                continue;
            }

            if (!available.TryGetValue(key, out var column))
            {
                var matches = available.Values.Where(c => string.Equals(c.Header, key,
                    StringComparison.CurrentCultureIgnoreCase)).ToList();
                if (matches.Count != 1)
                    return ApiRows.Bad($"свойство «{key}» не найдено однозначно: укажите ключ свойства");
                column = matches[0];
            }

            var canonical = column.FieldId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!selected.Add(canonical)) continue;
            canonicalKeys.Add(canonical);
            names.Add(column.Header);
            fields.Add(column.FieldId);
            if (column.FieldId is int auditId && FieldTypeInfo.IsAuditComputed(column.FieldType))
                auditTypes[auditId] = column.FieldType;
        }

        // Страница — ключевым курсором, как таблица: OFFSET на большой коллекции читает и
        // выбрасывает все пропущенное. «Правленные с момента» — по времени правки, а у
        // нетронутых записей его нет, и тогда спрашивается время создания.
        var query = ctx.DbRecords.AsNoTracking()
                       .Where(r => r.CollectionId == collectionId && r.Id > after);

        if (since.Length > 0)
            query = query.Where(r => r.UpdatedAt != ""
                                       ? string.Compare(r.UpdatedAt, since) >= 0
                                       : string.Compare(r.CreatedAt, since) >= 0);

        var page = query.OrderBy(r => r.Id)
                        .Take(Math.Clamp(take, 1, ApiPage) + 1)
                        .ToList();

        var more = page.Count > Math.Clamp(take, 1, ApiPage);
        if (more) page.RemoveAt(page.Count - 1);

        var values = isolated.GetFieldValuesBatch([.. page.Select(r => r.Id)],
            [.. fields.Where(f => f is not null).Select(f => f!.Value)]);
        var rows   = new List<(int, string, string[])>(page.Count);

        foreach (var record in page)
        {
            var mine = values.GetValueOrDefault(record.Id);
            var row  = new string[fields.Count];

            for (var i = 0; i < fields.Count; i++)
                row[i] = fields[i] is int field
                    ? auditTypes.TryGetValue(field, out var auditType)
                        ? auditType switch
                        {
                            "created_at" => record.CreatedAt ?? "", "updated_at" => record.UpdatedAt ?? "",
                            "created_by" => record.CreatedBy ?? "", _ => record.UpdatedBy ?? "",
                        }
                        : mine?.GetValueOrDefault(field, "") ?? ""
                    : record.Title;

            rows.Add((record.Id,
                      string.IsNullOrEmpty(record.UpdatedAt) ? record.CreatedAt ?? "" : record.UpdatedAt,
                      row));
        }

        return new ApiRows(names, rows, more, null) { FieldKeys = canonicalKeys };
    }

    // ── Журнал изменений ──────────────────────────────────────────────────────

    /// <summary>
    /// Хвост журнала изменений одной коллекции — на отдельном подключении. Отвечает на вопрос,
    /// на который не отвечает чтение записей: «что УДАЛЯЛИ». В журнале имена, а не ключи — он
    /// ведется для человека, — и об этом прямо сказано в описании API.
    /// </summary>
    /// <param name="collection">Имя коллекции — журнал хранит контекст именем.</param>
    /// <param name="after">Курсор: строки с ключом больше этого. Ноль — с начала.</param>
    /// <param name="take">Сколько строк; зажимается в 1..<see cref="ApiPage"/>.</param>
    public ApiChangesPage ApiChangesIsolated(string collection, long after, int take)
    {
        using var ctx = new AppDbContext(db.DbPath);

        var page = ctx.ChangeLog
                      .AsNoTracking()
                      .Where(c => c.Id > after
                               && (c.CollectionName == collection
                                   || (c.TargetType == "collection" && c.TargetName == collection)))
                      .OrderBy(c => c.Id)
                      .Take(Math.Clamp(take, 1, ApiPage) + 1)
                      .ToList();

        var more = page.Count > Math.Clamp(take, 1, ApiPage);
        if (more) page.RemoveAt(page.Count - 1);

        return new ApiChangesPage(
            [.. page.Select(c => new ApiChange(c.Id, c.Ts, c.Action, c.TargetType, c.TargetName))],
            more, null);
    }
}
