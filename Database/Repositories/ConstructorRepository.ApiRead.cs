using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Integrations;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Чтение записей под выдачу — то, что уходит в ответ на <c>GET /v1/records</c> и
/// <c>GET /v1/changes</c> (docs/ROADMAP-API.md, шаг 10).
///
/// Ответ на чтение — данные, ПОКИДАЮЩИЕ программу, поэтому рубежи здесь те же, что у
/// исходящей отправки (<c>ReadOutgoingIsolated</c>), и стоят они вторым эшелоном после окна
/// выдачи: метку могли поставить ПОСЛЕ того, как выдачу подтвердили, и тогда чтение обязано
/// встать, а не отдавать поле, которое кто-то только что закрыл.
/// </summary>
public partial class ConstructorRepository
{
    /// <summary>Сколько записей отдается за одно обращение. Дальше — курсором.</summary>
    public const int ApiPage = 500;

    /// <summary>Колонка выдачи глазами окна: ключ хранения, имя и почему ее нельзя выбрать.</summary>
    /// <param name="Key"><c>t</c> — название записи, число — ключ свойства.</param>
    /// <param name="Name">Имя колонки — то, под которым значение уйdет в ответ.</param>
    /// <param name="Trouble">Почему колонку нельзя включить в выдачу; <c>null</c> — можно.</param>
    public sealed record GrantColumn(string Key, string Name, string? Trouble);

    /// <summary>Страница записей под выдачу.</summary>
    /// <param name="Columns">Имена колонок — в порядке ключей выдачи.</param>
    /// <param name="Rows">Записи страницы: ключ, момент правки и значения в порядке колонок.</param>
    /// <param name="More">Есть ли записи дальше курсора.</param>
    /// <param name="Trouble">Почему читать нельзя; <c>null</c> — страница готова.</param>
    public sealed record ApiRows(
        IReadOnlyList<string>                                    Columns,
        IReadOnlyList<(int Id, string Updated, string[] Values)> Rows,
        bool                                                     More,
        string?                                                  Trouble)
    {
        public static ApiRows Bad(string trouble) => new([], [], false, trouble);
    }

    /// <summary>Строка журнала изменений под выдачу — имена, а не ключи: журнал ведется для
    /// человека, и ключей записей в нем нет по построению.</summary>
    public sealed record ApiChange(long Id, string At, string Action, string Target, string Name);

    /// <summary>Хвост журнала изменений под выдачу.</summary>
    public sealed record ApiChangesPage(
        IReadOnlyList<ApiChange> Changes,
        bool                     More,
        string?                  Trouble)
    {
        public static ApiChangesPage Bad(string trouble) => new([], false, trouble);
    }

    // ── Ключ коллекции по имени ───────────────────────────────────────────────

    /// <summary>
    /// Ключ коллекции по имени — на отдельном подключении: отправитель называет коллекцию
    /// ИМЕНЕМ, как и в сообщении, потому что ключей чужой базы он знать не может.
    /// </summary>
    public int? CollectionIdByNameIsolated(string name)
    {
        using var ctx = new AppDbContext(db.DbPath);

        return new ConstructorRepository(ctx).GetAllCollections()
            .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.CurrentCultureIgnoreCase))
            ?.Id;
    }

    // ── Колонки для окна выдачи ───────────────────────────────────────────────

    /// <summary>
    /// Колонки коллекции глазами окна выдачи — на отдельном подключении. Помеченные и
    /// невыдаваемые (вычисляемые, связи) показываются С ПРИЧИНОЙ, а не прячутся: пустое место
    /// на месте свойства читается как поломка, а серая строка «помечено» — как правило.
    /// </summary>
    public IReadOnlyList<GrantColumn> GrantColumnsIsolated(int collectionId)
    {
        using var ctx = new AppDbContext(db.DbPath);
        var isolated  = new ConstructorRepository(ctx);

        var columns = new List<GrantColumn>
        {
            new(ReadGrant.TitleKey, isolated.GetSystemFieldNames(collectionId)
                                            .GetValueOrDefault(TitleKey, "Название"), null),
        };

        foreach (var column in isolated.GetExportColumns(collectionId, includeHidden: true))
        {
            if (column.FieldId is not int id) continue;

            var mark = EgressGate.CheckField(Confidentiality.FromConfig(column.ConfigJson));

            columns.Add(new GrantColumn(
                id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                column.Header,
                FieldTypeInfo.IsComputed(column.FieldType) ? "вычисляемое — считается на месте"
                : FieldTypeInfo.IsRelation(column.FieldType) ? "связь — ключи чужой базе не нужны"
                : !mark.Allowed ? "помечено: " + mark.Explanation
                : null));
        }

        return columns;
    }

    // ── Записи ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Страница записей под выдачу — на отдельном подключении: читает ее обработчик обращения,
    /// который живет в фоне.
    ///
    /// Права спрашиваются на входе — права того, кто ОТКРЫЛ базу на машине-приемнике: слушает
    /// его процесс, и обмен не может отдавать его руками то, что ему самому не показывают.
    /// </summary>
    /// <param name="collectionId">Коллекция.</param>
    /// <param name="fieldKeys">Состав выдачи: <c>t</c> и ключи свойств.</param>
    /// <param name="since">Отдавать только записи, правленные не раньше этого момента
    /// (ISO-8601); пусто — все.</param>
    /// <param name="after">Курсор: отдавать записи с ключом больше этого. Ноль — с начала.</param>
    /// <param name="take">Сколько записей на страницу; зажимается в 1..<see cref="ApiPage"/>.</param>
    public ApiRows ApiReadIsolated(int collectionId, IReadOnlyList<string> fieldKeys,
                                   string since, int after, int take)
    {
        Demand(PermAction.ViewCollection, collectionId);

        using var ctx = new AppDbContext(db.DbPath);
        ctx.ApplyPerformancePragmas();
        var isolated = new ConstructorRepository(ctx);

        // Рубеж контура — тот же, что у отправки: закрытая база и просроченная политика
        // останавливают чтение независимо от выдачи.
        var closed = EgressGate.CheckDatabase(isolated.GetDatabaseLabel());
        if (!closed.Allowed) return ApiRows.Bad(closed.Explanation);

        if (isolated.CollectionName(collectionId).Length == 0)
            return ApiRows.Bad("коллекции, на которую выдано чтение, больше нет");

        // Колонки состава — живыми именами и с живыми метками. Пропавшее свойство
        // останавливает чтение целиком: отдать урезанный состав молча значило бы, что та
        // сторона строит на нем отчет и не знает, что колонка пропала.
        var available = isolated.GetExportColumns(collectionId, includeHidden: true)
                                .Where(c => c.FieldId is int)
                                .ToDictionary(
                                    c => c.FieldId!.Value.ToString(
                                        System.Globalization.CultureInfo.InvariantCulture),
                                    StringComparer.Ordinal);

        var names  = new List<string>(fieldKeys.Count);
        var fields = new List<int?>(fieldKeys.Count);

        foreach (var key in fieldKeys)
        {
            if (key == ReadGrant.TitleKey)
            {
                names.Add(isolated.GetSystemFieldNames(collectionId)
                                  .GetValueOrDefault(TitleKey, "Название"));
                fields.Add(null);
                continue;
            }

            if (!available.TryGetValue(key, out var column))
                return ApiRows.Bad("свойства из выдачи в коллекции больше нет — "
                                 + "выдачу надо подтвердить заново");

            var mark = EgressGate.CheckField(Confidentiality.FromConfig(column.ConfigJson));

            if (!mark.Allowed)
                return ApiRows.Bad($"свойство «{column.Header}»: {mark.Explanation}");

            names.Add(column.Header);
            fields.Add(column.FieldId);
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

        var values = isolated.GetFieldValuesBatch([.. page.Select(r => r.Id)]);
        var rows   = new List<(int, string, string[])>(page.Count);

        foreach (var record in page)
        {
            var mine = values.GetValueOrDefault(record.Id);
            var row  = new string[fields.Count];

            for (var i = 0; i < fields.Count; i++)
                row[i] = fields[i] is int field
                    ? mine?.GetValueOrDefault(field, "") ?? ""
                    : record.Title;

            rows.Add((record.Id,
                      record.UpdatedAt.Length > 0 ? record.UpdatedAt : record.CreatedAt,
                      row));
        }

        return new ApiRows(names, rows, more, null);
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
        Demand(PermAction.ViewCollection);

        using var ctx = new AppDbContext(db.DbPath);
        var isolated  = new ConstructorRepository(ctx);

        // Рубеж контура тот же, что у чтения записей: имена записей — тоже данные, и
        // просроченная политика останавливает и их.
        var closed = EgressGate.CheckDatabase(isolated.GetDatabaseLabel());
        if (!closed.Allowed) return ApiChangesPage.Bad(closed.Explanation);

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
