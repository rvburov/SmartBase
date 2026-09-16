using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Importers;
using SmartBase.Core.Models;
using SmartBase.Integrations;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Что делать со строками входящего файла: привязка, выведенная из имен, и колонки, которым в
/// коллекции места не нашлось.
/// </summary>
/// <param name="Binding">Готовая привязка — та же, что рисует человек для ответа сторонней программы.</param>
/// <param name="Ignored">Колонки файла, которые не переносятся. Отправитель узнает о них из
/// квитанции: молча выбросить присланное — худшее, что может сделать приемник.</param>
public sealed record InboxPlan(CollectionBinding Binding, IReadOnlyList<string> Ignored);

/// <summary>
/// Прием файла из папки обмена: превращение входящего сообщения в привязку и укладка.
///
/// Ключевое отличие от сетевого канала — <b>раскладку некому нарисовать</b>. Файл приходит, когда
/// человека рядом нет, поэтому колонки сводятся со свойствами коллекции ПО ИМЕНАМ. Отсюда и
/// формат: отправитель называет коллекцию и колонки так же, как они называются на экране, —
/// это единственный способ обойтись без настройки на каждого отправителя.
///
/// Дальше все то же самое, что у сетевого ответа: <c>ApplyRows</c>, обычный путь записи, журнал
/// изменений и права. Два транспорта — один код записи; иначе получились бы две расходящиеся
/// реализации одного и того же (docs/ROADMAP-API.md, раздел 7).
/// </summary>
public partial class ConstructorRepository
{
    /// <summary>
    /// Свести сообщение с коллекцией. <c>null</c> — свести не удалось, причина в
    /// <paramref name="problem"/> и уедет отправителю квитанцией.
    /// </summary>
    /// <param name="message">Разобранное сообщение.</param>
    /// <param name="schema">Схема записей файла — из нее берутся имена колонок.</param>
    /// <param name="dbId">Ключ базы для привязки: она обязана называть базу явно.</param>
    /// <param name="problem">Почему не свелось.</param>
    public InboxPlan? TryPlan(InboxMessage message, ImportSchema schema, int dbId, out string problem)
    {
        if (FindInboxCollection(message.Collection, out problem) is not { } collectionId)
            return null;

        // Вычисляемые свойства значение не хранят, свойства-связи хранят его в другой таблице:
        // ни то, ни другое обменом не заполняется.
        var fields = GetFields(collectionId)
            .Where(f => f.Id is int
                     && !FieldTypeInfo.IsComputed(f.FieldType)
                     && !FieldTypeInfo.IsRelation(f.FieldType))
            .ToList();

        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields) byName.TryAdd(field.Name.Trim(), field.Id!.Value);

        var titleLabel = GetSystemFieldNames(collectionId).GetValueOrDefault(TitleKey, "Название");

        var columns = new List<BindingColumn>();
        var ignored = new List<string>();

        foreach (var column in Columns(schema))
        {
            if (string.Equals(column.Trim(), titleLabel.Trim(), StringComparison.OrdinalIgnoreCase))
                columns.Add(new BindingColumn(column, null));
            else if (byName.TryGetValue(column.Trim(), out var fieldId))
                columns.Add(new BindingColumn(column, fieldId));
            else
                ignored.Add(column);
        }

        if (columns.Count == 0)
        {
            problem = $"ни одна колонка файла не совпала с коллекцией «{message.Collection}»; "
                    + $"в ней есть: {string.Join(", ", new[] { titleLabel }.Concat(byName.Keys))}";
            return null;
        }

        var binding = new CollectionBinding(
            dbId,
            collectionId,
            columns,
            message.Mode == InboxMode.Update ? message.Key : "",
            UpdateExisting: true,
            // Ненайденную запись заводим и в режиме «обновить»: обмен переносит состояние
            // целиком, и первая же доставка иначе не создала бы ничего.
            CreateMissing:  true);

        var problems = binding.Problems();
        if (problems.Count > 0)
        {
            problem = string.Join("; ", problems);
            return null;
        }

        return new InboxPlan(binding, ignored);
    }

    /// <summary>
    /// То же на ОТДЕЛЬНОМ подключении — для вызова с фонового потока. Свод читает состав
    /// коллекции, а общий контекст в это время занят интерфейсом: EF отвечает на второе
    /// обращение к нему «A second operation was started on this context instance».
    ///
    /// Приемник ходит сюда именно этим путем: обход папки идет в фоне целиком, от захвата до
    /// квитанции.
    /// </summary>
    public InboxPlan? TryPlanIsolated(InboxMessage message, ImportSchema schema, int dbId,
                                      out string problem)
    {
        using var ctx = new AppDbContext(db.DbPath);
        return new ConstructorRepository(ctx).TryPlan(message, schema, dbId, out problem);
    }

    /// <summary>Колонки файла в порядке схемы: название записи и свойства.</summary>
    private static IEnumerable<string> Columns(ImportSchema schema)
    {
        if (!string.IsNullOrEmpty(schema.TitleHeader)) yield return schema.TitleHeader!;
        foreach (var name in schema.FieldNames) yield return name;
    }

    /// <summary>Позиция для коллекции, создаваемой в корне, — после всех.</summary>
    private int NextCollectionPosition() =>
        (db.DbCollections.Select(c => (int?)c.Position).Max() ?? 0) + 1000;

    /// <summary>
    /// Коллекция по имени из сообщения; <c>null</c> — не нашлась либо нашлась не одна, причина
    /// в <paramref name="problem"/>. Общая развилка всех режимов приема.
    /// </summary>
    private int? FindInboxCollection(string name, out string problem) =>
        FindInboxCollection(name, out problem, out _);

    /// <summary>
    /// То же, но с отдельным ответом «нашлось несколько»: подготовке коллекции ненайденное —
    /// причина создать, а неоднозначное — отказ, и различать их по тексту нельзя.
    /// </summary>
    private int? FindInboxCollection(string name, out string problem, out bool several)
    {
        problem = "";
        several = false;

        var named = db.DbCollections.AsNoTracking()
            .Where(c => c.Name != null)
            .Select(c => new { c.Id, c.Name })
            .AsEnumerable()
            .Where(c => string.Equals(c.Name!.Trim(), name.Trim(),
                                      StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (named.Count == 0)
        {
            problem = $"коллекция «{name}» в базе не найдена";
            return null;
        }

        // Две коллекции с одним именем — обычное дело в большой базе. Выбрать любую значило бы
        // однажды уложить заказы в чужую таблицу и не сказать об этом никому.
        if (named.Count > 1)
        {
            problem = $"в базе несколько коллекций с именем «{name}» — "
                    + "приемник не выбирает за человека, какая из них нужна";
            several = true;
            return null;
        }

        return named[0].Id;
    }

    // ── Удаление по ключу (режим «delete», docs/ROADMAP-API.md, шаг 11) ────────

    /// <summary>
    /// Чем кончилось удаление по ключу.
    /// </summary>
    /// <param name="Deleted">Удалено записей.</param>
    /// <param name="Missing">Ключей, по которым удалять было нечего. Для повтора сообщения это
    /// норма: второй раз удаленное не удаляется, и квитанция честно говорит «пропущено».</param>
    /// <param name="Ambiguous">Ключей, по которым в коллекции несколько записей: какую из них
    /// хотел отправитель — неизвестно, и не тронута ни одна.</param>
    /// <param name="Empty">Строк с пустым ключом — удалять не по чему.</param>
    public sealed record InboxErase(int Deleted, int Missing, int Ambiguous, int Empty);

    /// <summary>
    /// Удалить записи по значениям ключа — на ОТДЕЛЬНОМ подключении, как и укладка.
    /// <c>null</c> — не с чего было начать, причина в <paramref name="problem"/>.
    ///
    /// Удаляется ОБЫЧНЫМ путем, по записи за раз: за это достается журнал изменений с именем
    /// того, кто удалил, и уборка связей — ровно то, что видно потом человеку и что отдает
    /// <c>/v1/changes</c>. Пакетное удаление все это обходит, а объемы обмена (тысячи, не
    /// миллионы) обычный путь держит.
    ///
    /// Право «Удаление записи» спрашивается ЗДЕСЬ, на публичной точке входа, и ДО первой
    /// записи: отказ на середине оставил бы коллекцию удаленной наполовину.
    /// </summary>
    public InboxErase? EraseRowsIsolated(InboxMessage message, ImportSchema schema,
                                         IEnumerable<ImportRow> rows, out string problem)
    {
        using var ctx = new AppDbContext(db.DbPath);
        var repo = new ConstructorRepository(ctx);

        if (repo.FindInboxCollection(message.Collection, out problem) is not { } collectionId)
            return null;

        Demand(PermAction.DeleteRecord, collectionId);

        // Ключ — название записи либо обычное свойство, как у сопоставления при обновлении.
        var titleLabel = repo.GetSystemFieldNames(collectionId)
                             .GetValueOrDefault(TitleKey, "Название");

        int? keyFieldId = null;

        if (!string.Equals(message.Key.Trim(), titleLabel.Trim(),
                           StringComparison.OrdinalIgnoreCase))
        {
            var field = repo.GetFields(collectionId).FirstOrDefault(
                f => f.Id is int
                  && string.Equals(f.Name.Trim(), message.Key.Trim(),
                                   StringComparison.OrdinalIgnoreCase));

            if (field is null)
            {
                problem = $"колонки ключа «{message.Key}» в коллекции «{message.Collection}» нет";
                return null;
            }

            keyFieldId = field.Id;
        }

        // Значение ключа берется из строк файла; колонка ключа обязана в них быть — остальные
        // колонки режим «delete» не читает вовсе.
        var keyIsTitle = string.Equals(message.Key.Trim(), (schema.TitleHeader ?? "").Trim(),
                                       StringComparison.OrdinalIgnoreCase);
        var keyAt = -1;

        if (!keyIsTitle)
        {
            for (var i = 0; i < schema.FieldNames.Count; i++)
                if (string.Equals(schema.FieldNames[i].Trim(), message.Key.Trim(),
                                  StringComparison.OrdinalIgnoreCase))
                {
                    keyAt = i;
                    break;
                }

            if (keyAt < 0)
            {
                problem = $"в записях сообщения нет колонки ключа «{message.Key}»";
                return null;
            }
        }

        var (index, duplicates) = repo.KeyIndex(collectionId, keyFieldId);

        var deleted   = 0;
        var missing   = 0;
        var ambiguous = 0;
        var empty     = 0;

        foreach (var row in rows)
        {
            var raw = keyIsTitle
                ? row.Title ?? ""
                : keyAt < row.FieldValues.Count ? row.FieldValues[keyAt] ?? "" : "";

            var key = raw.Trim();

            if (key.Length == 0)
            {
                empty++;
            }
            else if (duplicates.Contains(key))
            {
                ambiguous++;
            }
            else if (index.TryGetValue(key, out var recordId))
            {
                repo.DeleteRecord(recordId);
                index.Remove(key);   // тот же ключ в файле дважды — второй раз «нечего удалять»
                deleted++;
            }
            else
            {
                missing++;
            }

            // То же правило, что у укладки ответа (см. ApplyRows): удаленное уже удалено,
            // и держать за ним в трекере EF строки журнала изменений незачем. Без этой строки
            // время росло КВАДРАТИЧНО — EF на каждом SaveChanges обходит весь накопленный
            // список, — и удаление тысяч записей упиралось не в базу, а в этот обход.
            ctx.ChangeTracker.Clear();
        }

        return new InboxErase(deleted, missing, ambiguous, empty);
    }

    // ── Подготовка коллекции (режим «ensure-collection», шаг 11) ───────────────

    /// <summary>
    /// Чем кончилась подготовка коллекции.
    /// </summary>
    /// <param name="CreatedCollection">Коллекцию пришлось создать.</param>
    /// <param name="CreatedFields">Имена добранных полей — в квитанцию.</param>
    /// <param name="ExistingFields">Сколько полей уже было на месте.</param>
    public sealed record InboxEnsure(bool                  CreatedCollection,
                                     IReadOnlyList<string> CreatedFields,
                                     int                   ExistingFields);

    /// <summary>
    /// Создать коллекцию по схеме сообщения и добрать недостающие поля — на ОТДЕЛЬНОМ
    /// подключении. <c>null</c> — отказ, причина в <paramref name="problem"/>.
    ///
    /// <b>Существующие поля не трогаются.</b> Совпало имя и тип — поле на месте; совпало имя
    /// при другом типе — отказ ЦЕЛИКОМ, до первого созданного поля: молча перекроить чужое
    /// свойство значило бы сломать все, что на него навешано, а создать половину полей и
    /// отказаться на конфликте — оставить коллекцию в состоянии, которого не просил никто.
    ///
    /// Права спрашиваются на публичной точке входа: создание коллекции — «Создание коллекции»
    /// (право уровня базы), добор полей — «Создание свойства». Спрашиваются ОБА и всегда: иначе
    /// одно и то же сообщение проходило бы или нет в зависимости от того, что уже есть в базе,
    /// а робот без прав мог бы выведывать состав базы по разнице отказов.
    /// </summary>
    public InboxEnsure? EnsureCollectionIsolated(InboxMessage message, out string problem)
    {
        problem = "";

        Demand(PermAction.CreateCollection);
        Demand(PermAction.CreateField);

        using var ctx = new AppDbContext(db.DbPath);
        var repo = new ConstructorRepository(ctx);

        var found = repo.FindInboxCollection(message.Collection, out var lookup, out var several)
            is { } id ? (int?)id : null;

        // «Не нашлась» здесь не отказ, а причина создать; «нашлось несколько» — отказ тот же,
        // что у остальных режимов: приемник не выбирает за человека.
        if (several)
        {
            problem = lookup;
            return null;
        }

        var titleLabel = found is { } cid
            ? repo.GetSystemFieldNames(cid).GetValueOrDefault(TitleKey, "Название")
            : "Название";

        var existing = found is { } present
            ? repo.GetFields(present).Where(f => f.Id is int).ToList()
            : [];

        // Сначала — ВСЕ конфликты, потом первое создание: отказ не должен оставлять за собой
        // половину сделанного.
        var conflicts = new List<string>();
        var missing   = new List<InboxField>();
        var already   = 0;

        foreach (var field in message.Schema)
        {
            if (string.Equals(field.Name.Trim(), titleLabel.Trim(),
                              StringComparison.OrdinalIgnoreCase))
            {
                // Название записи встроено в каждую коллекцию и всегда текст.
                if (string.Equals(field.Type.Trim(), "text", StringComparison.OrdinalIgnoreCase))
                    already++;
                else
                    conflicts.Add($"«{field.Name}» — это название записи, оно текстовое, "
                                + $"а в сообщении тип «{field.Type}»");
                continue;
            }

            var known = existing.FirstOrDefault(
                f => string.Equals(f.Name.Trim(), field.Name.Trim(),
                                   StringComparison.OrdinalIgnoreCase));

            if (known is null)
            {
                missing.Add(field);
            }
            else if (string.Equals(known.FieldType, field.Type.Trim(),
                                   StringComparison.OrdinalIgnoreCase))
            {
                already++;
            }
            else
            {
                conflicts.Add($"поле «{known.Name}» в коллекции — "
                            + $"{FieldTypeInfo.Label(known.FieldType)}, "
                            + $"а в сообщении тип «{field.Type}»");
            }
        }

        if (conflicts.Count > 0)
        {
            problem = "типы полей не совпали: " + string.Join("; ", conflicts)
                    + ". Существующие поля обмен не трогает — договоритесь с хозяином коллекции";
            return null;
        }

        // Повторное сообщение находит все готовым и не делает ничего — в этом весь смысл
        // «ensure»: сторонняя программа шлет его при каждом своем запуске, не проверяя ничего.
        var createdCollection = false;

        if (found is not { } collectionId)
        {
            collectionId = repo.AddCollection(new DbCollection
            {
                Name     = message.Collection.Trim(),
                Position = repo.NextCollectionPosition(),
                Icon     = "table",
                Color    = "#007AFF",
            }).Id!.Value;

            createdCollection = true;
        }

        var next = existing.Count > 0 ? existing.Max(f => f.Position) + 1000 : 1000;
        var made = new List<string>();

        foreach (var field in missing)
        {
            repo.AddField(new DbField
            {
                CollectionId = collectionId,
                Name         = field.Name.Trim(),
                FieldType    = field.Type.Trim().ToLowerInvariant(),
                Position     = next,
            });

            next += 1000;
            made.Add(field.Name.Trim());
        }

        return new InboxEnsure(createdCollection, made, already);
    }
}
