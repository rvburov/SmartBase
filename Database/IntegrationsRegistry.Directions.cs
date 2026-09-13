using System.Globalization;
using SmartBase.Database.Entities;
using SmartBase.Integrations;
using Microsoft.EntityFrameworkCore;

namespace SmartBase.Database;

/// <summary>
/// Подтвержденные исходящие направления в app.db (docs/ROADMAP-API.md, 10.8).
///
/// Лежат рядом с запросами и по той же причине: соединения и запросы локальные, а подтверждение
/// выдается на ЭТОЙ машине и этому человеку. Положи его в файл базы — и один утвердительный
/// ответ открыл бы направление на всех машинах, где эта база открыта, включая те, о которых
/// подтверждавший не знал.
///
/// <b>Пароль сюда не попадает никогда.</b> Проверяет его политика (подписанный файл вне
/// программы), а здесь остается только след: кто подтвердил, когда и до какого числа. Подобрать
/// пароль, правя app.db, нельзя — его там нет.
/// </summary>
public sealed partial class IntegrationsRegistry
{
    /// <summary>Направление запроса; <c>null</c> — не подтверждено.</summary>
    public OutgoingDirection? Direction(int requestId)
    {
        var entity = db.IntegrationDirections.AsNoTracking()
                       .FirstOrDefault(d => d.RequestId == requestId);

        return entity is null ? null : ToModel(entity);
    }

    /// <summary>
    /// Подтвердить направление мастер-паролем политики. Возвращает подтвержденное направление;
    /// <c>null</c> — пароль не подошел или подтверждать нечего.
    ///
    /// Пароль проверяется ЗДЕСЬ, у политики, а не на экране: иначе проверку пришлось бы
    /// повторить в каждом месте, откуда подтверждают, и одно из них однажды ее забыло бы.
    /// </summary>
    /// <param name="request">Что подтверждаем.</param>
    /// <param name="password">Мастер-пароль организации.</param>
    /// <param name="who">Кто подтверждает — попадет в журнал безопасности.</param>
    /// <param name="days">На сколько дней.</param>
    /// <param name="now">Момент подтверждения.</param>
    /// <param name="trouble">Почему не вышло; пусто — вышло.</param>
    public OutgoingDirection? ConfirmDirection(IntegrationRequest request, string password,
                                               string who, int days, DateTimeOffset now,
                                               out string trouble)
    {
        trouble = "";

        if (request.Id <= 0)
        {
            trouble = "запрос еще не сохранен";
            return null;
        }

        if (!request.Outward)
        {
            trouble = "этот запрос данные наружу не уносит — подтверждать нечего";
            return null;
        }

        var policy = EgressGate.Policy;

        if (!policy.HasMaster)
        {
            trouble = "в политике организации не задан мастер-пароль: подтверждать исходящие "
                    + "направления некому и нечем";
            return null;
        }

        if (!policy.MasterMatches(password))
        {
            trouble = "мастер-пароль не подошел";
            return null;
        }

        // Состав исходящих полей входит в отпечаток: подтверждают «этот запрос с ЭТИМИ полями».
        // Спрашивается он здесь, у реестра, потому что привязка лежит в той же таблице, а не в
        // карточке, — экрану подтверждения об этом знать незачем.
        var direction = OutgoingDirection.Confirm(request, who, now, days, OutgoingMark(request.Id));

        var entity = db.IntegrationDirections.FirstOrDefault(d => d.RequestId == request.Id);

        if (entity is null)
        {
            entity = new IntegrationDirectionEntity { RequestId = request.Id };
            db.IntegrationDirections.Add(entity);
        }

        entity.Fingerprint = direction.Fingerprint;
        entity.Confirmed   = direction.Confirmed;
        entity.At          = Stamp(direction.At);
        entity.Until       = Stamp(direction.Until);

        // Отсчет простоя начинается заново: подтверждение — это и есть «им только что занимались».
        entity.UsedAt = null;

        db.SaveChanges();
        return direction;
    }

    /// <summary>
    /// Снять подтверждение. Зовется и руками, и тогда, когда запрос правили: держать разрешение,
    /// выданное другому тексту запроса, нельзя ни минуты.
    /// </summary>
    public bool RevokeDirection(int requestId)
    {
        var entity = db.IntegrationDirections.FirstOrDefault(d => d.RequestId == requestId);
        if (entity is null) return false;

        db.IntegrationDirections.Remove(entity);
        db.SaveChanges();
        return true;
    }

    /// <summary>
    /// Отметить, что направлением воспользовались, — от этой отметки считается простой.
    /// Ставится на УДАЧНУЮ отправку: отказ шлюза направлением не пользовался.
    /// </summary>
    public void MarkDirectionUsed(int requestId, DateTimeOffset at)
    {
        var entity = db.IntegrationDirections.FirstOrDefault(d => d.RequestId == requestId);
        if (entity is null) return;

        entity.UsedAt = Stamp(at);
        db.SaveChanges();
    }

    /// <summary>
    /// Почему запросу НЕЛЬЗЯ отправлять прямо сейчас; <c>null</c> — можно.
    ///
    /// Единственное место, где этот ответ собирается целиком, — и звать его обязаны и экран
    /// («что не так с направлением»), и отправка. Два ответа на один вопрос рано или поздно
    /// разойдутся, и разойдутся в сторону разрешения.
    ///
    /// Ответ — причина без подлежащего: ее подставляют и в карточку («Направление: срок вышел»),
    /// и в отказ отправки («исходящее направление: срок вышел»). Договори здесь «исходящее
    /// направление» — и в карточке вышло бы «Направление: исходящее направление не
    /// подтверждено».
    /// </summary>
    public string? OutwardTrouble(IntegrationRequest request, DateTimeOffset now)
    {
        if (!request.Outward) return null;   // чтение подтверждения не требует

        if (Direction(request.Id) is not { } direction)
            return "не подтверждено мастер-паролем";

        return direction.Trouble(request, now, OutgoingMark(request.Id));
    }

    /// <summary>
    /// Отпечаток состава исходящей привязки; пусто — привязки нет. Обертка ради одного: и
    /// подтверждение, и проверка обязаны спрашивать состав ОДИНАКОВО, иначе подтвержденное
    /// направление переставало бы действовать в тот же миг.
    /// </summary>
    private string OutgoingMark(int requestId) => Outgoing(requestId)?.Mark() ?? "";

    // ── Перевод ───────────────────────────────────────────────────────────────

    private static OutgoingDirection ToModel(IntegrationDirectionEntity entity) =>
        new(entity.RequestId,
            entity.Fingerprint,
            entity.Confirmed,
            Moment(entity.At)    ?? DateTimeOffset.MinValue,
            Moment(entity.Until) ?? DateTimeOffset.MinValue,
            Moment(entity.UsedAt));

    private static string Stamp(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>
    /// Момент из строки хранения; <c>null</c> — строки нет или она испорчена правкой файла
    /// руками. Испорченный срок читается как <c>MinValue</c>, то есть «вышел» — правка app.db не
    /// должна продлевать разрешение.
    /// </summary>
    private static DateTimeOffset? Moment(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                                DateTimeStyles.RoundtripKind, out var moment)
            ? moment
            : null;
}
