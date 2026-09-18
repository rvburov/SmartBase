using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;
using SmartBase.Integrations;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Метки конфиденциальности: чтение и запись. Метка базы лежит в <c>db_meta</c> — то есть
/// ВНУТРИ файла базы, рядом с правами и пользователями, и уезжает вместе с файлом в любую
/// папку. Метка свойства — в его <c>ConfigJson</c>, рядом с настройками типа.
///
/// Смысл именно такого размещения: метка обязана путешествовать с данными. Храни ее реестр
/// баз в <c>app.db</c> — копия базы на соседнем компьютере оказалась бы без метки, и «закрытый
/// контур» снимался бы копированием файла.
///
/// Что метки значат и почему их две — <see cref="Confidentiality"/>; кто на них смотрит перед
/// отправкой — <c>Integrations/EgressGate.cs</c>.
/// </summary>
public partial class ConstructorRepository
{
    /// <summary>Метка базы. Метки нет — «Открыто».</summary>
    public ConfidentialityLabel GetDatabaseLabel() =>
        Confidentiality.Parse(db.DbMeta.AsNoTracking()
                                       .FirstOrDefault(m => m.Key == Confidentiality.Key)?.Value);

    /// <summary>
    /// Поставить метку базе. «Открыто» — строка убирается: хранить значение, равное стандарту,
    /// в этой базе не принято.
    ///
    /// Возвращает причину отказа; пусто — метка поставлена. Отказ, а не исключение: понижение
    /// метки — обычное действие человека за экраном, и неверный пароль здесь такая же опечатка,
    /// как и в окне подтверждения направления.
    /// </summary>
    /// <param name="label">Новая метка.</param>
    /// <param name="password">Мастер-пароль политики; нужен ТОЛЬКО при понижении. Повышать
    /// метку можно без него: строже — значит безопаснее, и спрашивать за это пароль значило бы
    /// отучить людей повышать метки вовсе.</param>
    public string SetDatabaseLabel(ConfidentialityLabel label, string? password = null)
    {
        var before = GetDatabaseLabel();
        if (before == label) return "";

        if (Downgrade(before, label, password, "база",
                          System.IO.Path.GetFileNameWithoutExtension(db.DbPath)) is { Length: > 0 } trouble)
            return trouble;

        var row = db.DbMeta.Find(Confidentiality.Key);

        if (label == ConfidentialityLabel.Open)
        {
            if (row is not null) db.DbMeta.Remove(row);
        }
        else if (row is null)
        {
            db.DbMeta.Add(new DbMetaEntity { Key = Confidentiality.Key, Value = Confidentiality.ToStorage(label) });
        }
        else
        {
            row.Value = Confidentiality.ToStorage(label);
        }

        Log(label < before ? "label.lower" : "label",
            "database", Confidentiality.Title(label),
            detail: $"{Confidentiality.Title(before)} → {Confidentiality.Title(label)}");

        db.SaveChanges();
        return "";
    }

    /// <summary>Метка свойства. Ключа в конфиге нет — «Открыто»; конфиг сломан — «Закрытый контур».</summary>
    public ConfidentialityLabel GetFieldLabel(int fieldId) =>
        Confidentiality.FromConfig(db.DbFields.AsNoTracking()
                                              .FirstOrDefault(f => f.Id == fieldId)?.ConfigJson);

    /// <summary>
    /// Поставить метку свойству. Остальные настройки свойства сохраняются — метка живет рядом
    /// с ними, а не вместо них.
    ///
    /// Возвращает причину отказа; пусто — метка поставлена. Понижение здесь под тем же замком,
    /// что и у базы: именно меткой поля закрывается точный случай «это поле наружу нельзя», и
    /// снять ее без пароля значило бы оставить запрет на честном слове.
    /// </summary>
    /// <param name="fieldId">Ключ свойства.</param>
    /// <param name="label">Новая метка.</param>
    /// <param name="password">Мастер-пароль политики; нужен только при понижении.</param>
    public string SetFieldLabel(int fieldId, ConfidentialityLabel label, string? password = null)
    {
        var field = db.DbFields.FirstOrDefault(f => f.Id == fieldId);
        if (field is null) return "свойство не найдено";

        var before = Confidentiality.FromConfig(field.ConfigJson);
        if (before == label) return "";

        if (Downgrade(before, label, password, "свойство", field.Name,
                      db.DbCollections.AsNoTracking()
                        .FirstOrDefault(c => c.Id == field.CollectionId)?.Name ?? "")
                is { Length: > 0 } trouble)
            return trouble;

        field.ConfigJson = Confidentiality.WriteToConfig(field.ConfigJson, label);

        Log(label < before ? "label.lower" : "label",
            "field", field.Name,
            collectionName: db.DbCollections.AsNoTracking()
                              .FirstOrDefault(c => c.Id == field.CollectionId)?.Name,
            detail: $"{Confidentiality.Title(before)} → {Confidentiality.Title(label)}");

        db.SaveChanges();
        return "";
    }

    // ── Понижение метки ───────────────────────────────────────────────────────

    /// <summary>
    /// Можно ли понизить метку, и если можно — оставить след. Возвращает причину отказа; пусто —
    /// разрешено (в том числе когда понижения и нет вовсе).
    ///
    /// Три правила, и каждое закрывает свой способ обойти запрет (docs/ROADMAP-API.md, 10.6).
    ///
    /// <list type="number">
    ///   <item><b>Понижение — по мастер-паролю политики.</b> Без него весь механизм меток
    ///   держится на честном слове: помеченное поле нельзя отправить, но метку можно снять за
    ///   две секунды, и тогда уже можно все.</item>
    ///   <item><b>Пароля в политике нет — понижать нечем.</b> То же fail-closed, что и у
    ///   исходящих направлений: отсутствие настройки обязано быть безопасным состоянием, иначе
    ///   машина без политики становится машиной без ограничений.</item>
    ///   <item><b>Не записалось — не понижаем.</b> Понижение метки — ровно то событие, ради
    ///   которого журнал безопасности заведен: с него начинается разбор «как данные ушли
    ///   наружу». Понижение, о котором не осталось следа, ничем не отличается от тайного.</item>
    /// </list>
    ///
    /// Повышение метки пароля НЕ требует и следа не оставляет: строже — значит безопаснее, а
    /// спрашивать пароль за осторожность значит отучить от нее.
    /// </summary>
    /// <param name="collection">Коллекция, к которой относится метка; пусто — метка самой базы.
    /// Идет отдельным полем строки журнала: по нему проверяющий отбирает события коллекции, а
    /// вычитывать имя из пояснения ему не должно быть нужно.</param>
    private string Downgrade(ConfidentialityLabel before, ConfidentialityLabel after,
                             string? password, string what, string name, string collection = "")
    {
        if (after >= before) return "";

        var policy = EgressGate.Policy;

        if (!policy.HasMaster)
            return "понизить метку нечем: мастер-пароль не задан ни одним источником политики "
                 + "организации. Обратитесь к ответственному за интеграции";

        if (!policy.MasterMatches(password))
            return "мастер-пароль не подошел";

        var entry = SecurityEntry.New(
            SecurityEvent.Downgrade, allowed: true,
            who:         SmartBase.Utils.CurrentUser.DisplayName,
            scenario:    "",
            address:     $"{what} «{name}»",
            fields:      what == "свойство" ? name : "",
            rows:        0,
            bytes:       0,
            bodyHash:    "",
            explanation: $"{Confidentiality.Title(before)} → {Confidentiality.Title(after)}",
            dbId:        0,
            at:          DateTimeOffset.Now,
            confirmed:   SmartBase.Utils.CurrentUser.DisplayName,
            collection:  collection);

        // Тот же порядок, что и у отправки: сначала след, потом действие. Строка, которую пишут
        // по итогу, не ляжет ровно в том случае, ради которого журнал и заведен.
        if (!SecurityJournal.Add(entry))
            return "понижение не записалось в журнал безопасности, поэтому и не выполнено: "
                 + "снятие метки без следа — ровно то, чего требование службы безопасности не "
                 + "допускает. Причина записана в «Диагностике»";

        return "";
    }

    // ── Перенос между базами ──────────────────────────────────────────────────

    /// <summary>
    /// Разрешен ли перенос данных из базы <paramref name="sourceLabel"/> в эту базу. Запрет
    /// один: из закрытого контура наружу нельзя, значит и в менее строгую базу нельзя.
    ///
    /// Без этой проверки запрет обходится в два приема и без всякой хитрости: перенес
    /// коллекцию из закрытой базы в обычную, а оттуда выгрузил как угодно. Проверка стоит
    /// в репозитории, а не в интерфейсе, ровно поэтому — дверь должна быть одна.
    /// </summary>
    public void DemandMoveFromAllowed(ConfidentialityLabel sourceLabel)
    {
        if (sourceLabel != ConfidentialityLabel.Closed) return;
        if (GetDatabaseLabel() == ConfidentialityLabel.Closed) return;

        throw new InvalidOperationException(
            "Перенос запрещен: источник помечен закрытым контуром, а база-приемник — нет. " +
            "Данные закрытого контура остаются в нем.");
    }

    /// <summary>Перенос из открытой рядом базы (обе базы уже смонтированы).</summary>
    public void DemandMoveFromAllowed(ConstructorRepository source) =>
        DemandMoveFromAllowed(source.GetDatabaseLabel());

    /// <summary>
    /// Перенос из базы, которая задана путем к файлу или строкой подключения: метка читается
    /// отдельным коротким подключением. Прочитать не вышло — считаем источник закрытым:
    /// «база есть, а метки мы не знаем» безопасно трактовать только так.
    /// </summary>
    public void DemandMoveFromAllowed(string sourceDbPath)
    {
        ConfidentialityLabel label;
        try
        {
            using var source = new AppDbContext(sourceDbPath);
            label = Confidentiality.Parse(source.DbMeta.AsNoTracking()
                                                .FirstOrDefault(m => m.Key == Confidentiality.Key)?.Value);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            label = ConfidentialityLabel.Closed;
        }

        DemandMoveFromAllowed(label);
    }

    /// <summary>
    /// Свойства коллекции, которые НЕЛЬЗЯ подставлять в исходящий шаг. Нужен экрану настройки
    /// привязки: помеченное поле не должно появляться в списке вовсе — узнавать об ограничении
    /// из ошибки в журнале человек не должен.
    /// </summary>
    /// <summary>
    /// Сколько свойств во ВСЕЙ базе несут собственную метку — строка паспорта интеграций.
    /// Считается по базе целиком, а не по коллекции: проверяющему нужно «сколько всего
    /// помечено», а не разбивка, которой он все равно не сверит.
    /// </summary>
    public int LabeledFieldCount() =>
        db.DbFields.AsNoTracking()
          .Select(f => f.ConfigJson)
          .AsEnumerable()
          .Count(json => Confidentiality.FromConfig(json) != ConfidentialityLabel.Open);

    /// <summary>
    /// Помеченные свойства ВСЕЙ базы: коллекция, свойство и метка. Нужен экрану «Безопасность»
    /// по нажатию «Показать список»: цифра отвечает на вопрос «много ли помечено», а список —
    /// на вопрос «что именно», и второй задают реже, зато тянет он имена коллекций.
    ///
    /// Порядок — коллекция, потом свойство: список читают глазами и ищут в нем знакомое имя.
    /// </summary>
    public IReadOnlyList<(string Collection, string Field, ConfidentialityLabel Label)> LabeledFields() =>
        [.. db.DbFields.AsNoTracking()
             .Join(db.DbCollections.AsNoTracking(),
                   f => f.CollectionId, c => c.Id,
                   (f, c) => new { Collection = c.Name, Field = f.Name, f.ConfigJson })
             .AsEnumerable()
             .Select(x => (x.Collection, x.Field, Label: Confidentiality.FromConfig(x.ConfigJson)))
             .Where(x => x.Label != ConfidentialityLabel.Open)
             .OrderBy(x => x.Collection, StringComparer.CurrentCulture)
             .ThenBy(x => x.Field, StringComparer.CurrentCulture)];

    public IReadOnlyList<int> LabeledFieldIds(int collectionId) =>
        [.. db.DbFields.AsNoTracking()
             .Where(f => f.CollectionId == collectionId)
             .Select(f => new { f.Id, f.ConfigJson })
             .AsEnumerable()
             .Where(f => Confidentiality.FromConfig(f.ConfigJson) != ConfidentialityLabel.Open)
             .Select(f => f.Id)];
}
