using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Importers;
using SmartBase.Core.Models;
using SmartBase.Integrations;

namespace SmartBase.Database.Repositories;

/// <summary>
/// Что делать со строками входящего файла: привязка, выведенная из имен, и колонки, которым в
/// коллекции места не нашлось.
/// </summary>
/// <param name="Binding">Готовая привязка — та же, что рисует человек для ответа чужой программы.</param>
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
        problem = "";

        var named = db.DbCollections.AsNoTracking()
            .Where(c => c.Name != null)
            .Select(c => new { c.Id, c.Name })
            .AsEnumerable()
            .Where(c => string.Equals(c.Name!.Trim(), message.Collection.Trim(),
                                      StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (named.Count == 0)
        {
            problem = $"коллекция «{message.Collection}» в базе не найдена";
            return null;
        }

        // Две коллекции с одним именем — обычное дело в большой базе. Выбрать любую значило бы
        // однажды уложить заказы в чужую таблицу и не сказать об этом никому.
        if (named.Count > 1)
        {
            problem = $"в базе несколько коллекций с именем «{message.Collection}» — "
                    + "приемник не выбирает за человека, какая из них нужна";
            return null;
        }

        var collectionId = named[0].Id;

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
}
