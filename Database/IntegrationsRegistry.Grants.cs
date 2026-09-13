using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SmartBase.Database.Entities;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Выдачи чтения в app.db (docs/ROADMAP-API.md, шаг 10).
///
/// Устройство то же, что у исходящих направлений, и лежат они рядом по той же причине: порт
/// читает ОДНА машина, и разрешение выдано на ней. Пароль сюда не попадает никогда — его
/// проверяет политика, здесь остается след: кто, что и до какого числа разрешил.
///
/// Ключ выдачи — пара «токен + коллекция»: у одного робота не бывает двух выдач на одну
/// коллекцию, повторное подтверждение заменяет состав и продлевает срок.
/// </summary>
public sealed partial class IntegrationsRegistry
{
    /// <summary>Все выдачи — паспорту и экрану.</summary>
    public IReadOnlyList<ReadGrant> Grants() =>
        [.. db.IntegrationGrants.AsNoTracking().AsEnumerable().Select(ToModel)];

    /// <summary>Выдача токена на коллекцию; <c>null</c> — чтение не разрешено.</summary>
    public ReadGrant? GrantFor(int tokenId, int collectionId)
    {
        var entity = db.IntegrationGrants.AsNoTracking()
                       .FirstOrDefault(g => g.TokenId == tokenId
                                         && g.CollectionId == collectionId);

        return entity is null ? null : ToModel(entity);
    }

    /// <summary>
    /// Подтвердить выдачу мастер-паролем политики. Возвращает выдачу; <c>null</c> — не вышло,
    /// причина в <paramref name="trouble"/>.
    ///
    /// Пароль проверяется здесь, у политики, — тем же правилом, что у направления: иначе
    /// проверку пришлось бы повторять на каждом экране, и один из них однажды забыл бы ее.
    /// Существующая выдача на ту же пару «токен + коллекция» заменяется: два разрешения на одно
    /// и то же читались бы по-разному, и действовало бы более щедрое.
    /// </summary>
    /// <param name="tokenId">Кому разрешается читать.</param>
    /// <param name="collectionId">Что читать — ключ коллекции.</param>
    /// <param name="collection">Имя коллекции на момент выдачи — для экрана и паспорта.</param>
    /// <param name="fieldKeys">Состав: <c>t</c> — название записи, число — ключ свойства.</param>
    /// <param name="fields">Имена состава на момент выдачи — для экрана и паспорта.</param>
    /// <param name="password">Мастер-пароль организации.</param>
    /// <param name="who">Кто подтверждает — попадет в журнал безопасности.</param>
    /// <param name="days">На сколько дней.</param>
    /// <param name="now">Момент подтверждения.</param>
    /// <param name="trouble">Почему не вышло; пусто — вышло.</param>
    public ReadGrant? ConfirmGrant(int tokenId, int collectionId, string collection,
                                   IReadOnlyList<string> fieldKeys, string fields,
                                   string password, string who, int days, DateTimeOffset now,
                                   out string trouble)
    {
        trouble = "";

        if (Token(tokenId) is null)
        {
            trouble = "токена больше нет — выдавать чтение некому";
            return null;
        }

        if (fieldKeys.Count == 0)
        {
            trouble = "не выбран ни один столбец — отдавать нечего";
            return null;
        }

        var policy = EgressGate.Policy;

        if (!policy.HasMaster)
        {
            trouble = "в политике организации не задан мастер-пароль: подтверждать выдачи "
                    + "некому и нечем";
            return null;
        }

        if (!policy.MasterMatches(password))
        {
            trouble = "мастер-пароль не подошел";
            return null;
        }

        var entity = db.IntegrationGrants.FirstOrDefault(
            g => g.TokenId == tokenId && g.CollectionId == collectionId);

        if (entity is null)
        {
            entity = new IntegrationGrantEntity { TokenId = tokenId, CollectionId = collectionId };
            db.IntegrationGrants.Add(entity);
        }

        entity.Collection = collection;
        entity.FieldKeys  = ReadGrant.PackKeys(fieldKeys);
        entity.Fields     = fields;
        entity.Confirmed  = who;
        entity.At         = Stamp(now);
        entity.Until      = Stamp(now.AddDays(Math.Clamp(days, 1, ReadGrant.MaxDays)));

        // Отсчет простоя начинается заново: подтверждение — это «выдачей только что занимались».
        entity.UsedAt = null;

        db.SaveChanges();
        return ToModel(entity);
    }

    /// <summary>Отозвать выдачу — руками, до срока.</summary>
    public bool RevokeGrant(int id)
    {
        var entity = db.IntegrationGrants.FirstOrDefault(g => g.Id == id);
        if (entity is null) return false;

        db.IntegrationGrants.Remove(entity);
        db.SaveChanges();
        return true;
    }

    /// <summary>
    /// Убрать выдачи токена — вместе с ним самим: выдача без токена не разрешает никому и
    /// ничего, но в паспорте читалась бы как действующая.
    /// </summary>
    public void RemoveGrantsOf(int tokenId)
    {
        var mine = db.IntegrationGrants.Where(g => g.TokenId == tokenId).ToList();
        if (mine.Count == 0) return;

        db.IntegrationGrants.RemoveRange(mine);
        db.SaveChanges();
    }

    /// <summary>
    /// Отметить, что выдачей воспользовались, — от этой отметки считается простой. Ставится на
    /// УДАЧНОЕ чтение: отказ выдачей не пользовался.
    /// </summary>
    public void MarkGrantUsed(int id, DateTimeOffset at)
    {
        var entity = db.IntegrationGrants.FirstOrDefault(g => g.Id == id);
        if (entity is null) return;

        entity.UsedAt = Stamp(at);
        db.SaveChanges();
    }

    // ── Перевод ───────────────────────────────────────────────────────────────

    private static ReadGrant ToModel(IntegrationGrantEntity entity) =>
        new(entity.Id,
            entity.TokenId,
            entity.CollectionId,
            entity.Collection,
            ReadGrant.UnpackKeys(entity.FieldKeys),
            entity.Fields,
            entity.Confirmed,
            GrantMoment(entity.At)    ?? DateTimeOffset.MinValue,
            GrantMoment(entity.Until) ?? DateTimeOffset.MinValue,
            GrantMoment(entity.UsedAt));

    /// <summary>Испорченный правкой app.db срок читается как «вышел» — правка файла руками не
    /// должна продлевать разрешение.</summary>
    private static DateTimeOffset? GrantMoment(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind, out var moment)
            ? moment
            : null;
}
