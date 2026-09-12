using Microsoft.EntityFrameworkCore;
using SmartBase.Database.Entities;
using SmartBase.Integrations;

namespace SmartBase.Database;

/// <summary>
/// Токены входящего канала в app.db (docs/ROADMAP-API.md, 10.12).
///
/// Лежат рядом с соединениями и по той же причине: порт слушает ОДНА машина, и токен к ней
/// привязан. Секрета здесь нет — только проверочный хэш, поэтому и «показать токен еще раз»
/// невозможно: единственный момент, когда он существует в виде текста, — <see cref="Issue"/>.
///
/// Пользователь-робот в <c>db_users</c> заводится не здесь: реестр живет на системной базе и о
/// составе чужих баз ничего не знает. Здесь — только строка токена и ее ключ робота; свести одно
/// с другим обязан тот, кто держит в руках обе базы (<c>IngressHandler</c> при обращении и экран
/// «Доступ извне» при выдаче).
/// </summary>
public sealed partial class IntegrationsRegistry
{
    /// <summary>Все токены, свежие сверху: последний выданный ищут чаще прочих.</summary>
    public IReadOnlyList<AccessToken> Tokens() =>
        [.. db.IntegrationTokens.AsNoTracking()
             .OrderByDescending(t => t.Id)
             .AsEnumerable()
             .Select(ToModel)];

    /// <summary>Токен по ключу; <c>null</c> — такого нет.</summary>
    public AccessToken? Token(int id)
    {
        var entity = db.IntegrationTokens.AsNoTracking().FirstOrDefault(t => t.Id == id);
        return entity is null ? null : ToModel(entity);
    }

    /// <summary>
    /// Выдать новый токен. Возвращает пару «строка реестра и ТЕКСТ токена» — единственный раз,
    /// когда текст существует: дальше остается только хэш, и показать секрет еще раз нельзя
    /// никому, включая администратора.
    /// </summary>
    /// <param name="name">Имя для человека; оно же имя робота в истории изменений.</param>
    /// <param name="dbId">База, в которую этому токену разрешено писать.</param>
    /// <param name="machine">Имя машины — часть ключа робота.</param>
    /// <param name="now">Момент выдачи.</param>
    public (AccessToken Token, string Secret) Issue(string name, int dbId, string machine,
                                                    DateTimeOffset now)
    {
        var secret = AccessToken.NewSecret();

        var entity = new IntegrationTokenEntity
        {
            Name      = (name ?? "").Trim(),
            DbId      = dbId,
            Hash      = AccessToken.HashOf(secret),
            RobotKey  = "",   // ключ робота включает номер строки, а он появится после вставки
            CreatedAt = Stamp(now),
            Enabled   = true,
        };

        db.IntegrationTokens.Add(entity);
        db.SaveChanges();

        entity.RobotKey = AccessToken.KeyOf(entity.Id, machine);
        db.SaveChanges();

        return (ToModel(entity), AccessToken.Compose(entity.Id, secret));
    }

    /// <summary>Включить или выключить токен. Выключенный не пускают, но строка остается.</summary>
    public void EnableToken(int id, bool enabled)
    {
        var entity = db.IntegrationTokens.FirstOrDefault(t => t.Id == id);
        if (entity is null || entity.Enabled == enabled) return;

        entity.Enabled = enabled;
        db.SaveChanges();
    }

    /// <summary>
    /// Убрать токен. Пользователь-робот в целевой базе при этом ОСТАЕТСЯ: его строка — часть
    /// истории изменений, и удалить ее значило бы обезличить уже сделанные записи. Отзывает
    /// допуск роботу администратор базы, обычной панелью.
    /// </summary>
    public bool RemoveToken(int id)
    {
        var entity = db.IntegrationTokens.FirstOrDefault(t => t.Id == id);
        if (entity is null) return false;

        db.IntegrationTokens.Remove(entity);
        db.SaveChanges();
        return true;
    }

    /// <summary>
    /// Найти токен по его тексту. <c>null</c> — текст не разобрался, строки нет, токен выключен
    /// или секрет не подошел. Причина наружу не выдается ни в одном из случаев: подсказка
    /// «токен есть, но выключен» помогает подбирать и ничего не дает честному отправителю.
    ///
    /// Сравнение секрета идет PBKDF2, то есть медленно, — и это нормально: обращений тут единицы
    /// в минуту, а перебор упирается в ту же медленность.
    /// </summary>
    public AccessToken? Verify(string? text)
    {
        if (!AccessToken.TryParse(text, out var id, out var secret)) return null;

        var entity = db.IntegrationTokens.AsNoTracking().FirstOrDefault(t => t.Id == id);
        if (entity is null || !entity.Enabled) return null;

        var token = ToModel(entity);
        return token.Matches(secret) ? token : null;
    }

    /// <summary>
    /// Отметить, что токеном пользовались. Ставится на КАЖДОЕ принятое обращение, включая
    /// отклоненное правами: экран отвечает на вопрос «стучится ли вообще эта программа», а
    /// «стучится и получает отказ» — самый нужный из ответов.
    /// </summary>
    public void MarkTokenUsed(int id, DateTimeOffset at)
    {
        var entity = db.IntegrationTokens.FirstOrDefault(t => t.Id == id);
        if (entity is null) return;

        entity.UsedAt = Stamp(at);
        db.SaveChanges();
    }

    /// <summary>Токены, выданные под эту базу, — экрану и описанию API.</summary>
    public IReadOnlyList<AccessToken> TokensOf(int dbId) =>
        [.. Tokens().Where(t => t.DbId == dbId)];

    // ── Перевод ───────────────────────────────────────────────────────────────

    private static AccessToken ToModel(IntegrationTokenEntity entity) =>
        new(entity.Id,
            entity.Name,
            entity.DbId,
            entity.Hash,
            entity.RobotKey,
            entity.CreatedAt,
            entity.UsedAt ?? "",
            entity.Enabled);
}
