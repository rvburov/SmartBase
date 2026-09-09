using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;
using SmartBase.Database.Entities;

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
    /// Понижение метки (со строгой на свободную) — отдельное событие в журнале изменений:
    /// иначе запрет обходится снятием метки за две секунды и без следа. Мастер-пароль
    /// ответственного здесь пока не спрашивается — он появится вместе с подтверждением
    /// исходящих направлений (docs/ROADMAP-API.md, шаг 5).
    /// </summary>
    public void SetDatabaseLabel(ConfidentialityLabel label)
    {
        var before = GetDatabaseLabel();
        if (before == label) return;

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
    }

    /// <summary>Метка свойства. Ключа в конфиге нет — «Открыто»; конфиг сломан — «Закрытый контур».</summary>
    public ConfidentialityLabel GetFieldLabel(int fieldId) =>
        Confidentiality.FromConfig(db.DbFields.AsNoTracking()
                                              .FirstOrDefault(f => f.Id == fieldId)?.ConfigJson);

    /// <summary>
    /// Поставить метку свойству. Остальные настройки свойства сохраняются — метка живет рядом
    /// с ними, а не вместо них.
    /// </summary>
    public void SetFieldLabel(int fieldId, ConfidentialityLabel label)
    {
        var field = db.DbFields.FirstOrDefault(f => f.Id == fieldId);
        if (field is null) return;

        var before = Confidentiality.FromConfig(field.ConfigJson);
        if (before == label) return;

        field.ConfigJson = Confidentiality.WriteToConfig(field.ConfigJson, label);

        Log(label < before ? "label.lower" : "label",
            "field", field.Name,
            collectionName: db.DbCollections.AsNoTracking()
                              .FirstOrDefault(c => c.Id == field.CollectionId)?.Name,
            detail: $"{Confidentiality.Title(before)} → {Confidentiality.Title(label)}");

        db.SaveChanges();
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
    public IReadOnlyList<int> LabeledFieldIds(int collectionId) =>
        [.. db.DbFields.AsNoTracking()
             .Where(f => f.CollectionId == collectionId)
             .Select(f => new { f.Id, f.ConfigJson })
             .AsEnumerable()
             .Where(f => Confidentiality.FromConfig(f.ConfigJson) != ConfidentialityLabel.Open)
             .Select(f => f.Id)];
}
