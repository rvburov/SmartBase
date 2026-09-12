using Microsoft.EntityFrameworkCore;
using SmartBase.Core.Models;

namespace SmartBase.Database.Repositories;

/// <summary>Свойство коллекции глазами чужой программы: имя колонки и словесный тип.</summary>
/// <param name="Name">Имя, под которым свойство ждут в сообщении.</param>
/// <param name="Type">Тип словами — «Текст», «Число», «Дата». Чужому программисту нужен не
/// внутренний код типа, а ответ на вопрос «что сюда класть».</param>
public sealed record ApiField(string Name, string Type);

/// <summary>Коллекция в описании API: имя, колонка названия и свойства.</summary>
/// <param name="Name">Имя коллекции — его отправитель пишет в поле «collection».</param>
/// <param name="Title">Как называется колонка названия записи в этой коллекции.</param>
/// <param name="Fields">Свойства, которые обмен заполняет.</param>
public sealed record ApiCollection(string Name, string Title, IReadOnlyList<ApiField> Fields);

/// <summary>
/// Описание собственного API — «что у нас есть и как это заполнить» (docs/ROADMAP-API.md,
/// раздел 7: описание генерируется из схемы коллекций, как в NocoDB и Baserow).
///
/// <b>Схема, а не данные.</b> Отсюда наружу уходят только имена коллекций и свойств с типами;
/// значений тут нет и быть не может — входящая дверь данные не отдает вовсе (10.12).
///
/// Состав свойств тот же, что у укладки: вычисляемые и связи обменом не заполняются, и называть
/// их в описании значило бы пообещать то, чего приемник не сделает.
/// </summary>
public partial class ConstructorRepository
{
    /// <summary>
    /// Схема базы для описания API — на ОТДЕЛЬНОМ подключении: описание собирают и из обработчика
    /// обращения, который живет в фоне.
    /// </summary>
    public IReadOnlyList<ApiCollection> ApiSchemaIsolated()
    {
        using var ctx = new AppDbContext(db.DbPath);
        return new ConstructorRepository(ctx).ApiSchema();
    }

    /// <summary>Схема базы для описания API: коллекции по алфавиту, в каждой — ее свойства.</summary>
    public IReadOnlyList<ApiCollection> ApiSchema()
    {
        var collections = db.DbCollections.AsNoTracking()
            .Where(c => c.Name != null)
            .Select(c => new { c.Id, c.Name })
            .AsEnumerable()
            .OrderBy(c => c.Name, StringComparer.CurrentCulture)
            .ToList();

        var result = new List<ApiCollection>(collections.Count);

        foreach (var collection in collections)
        {
            var fields = GetFields(collection.Id)
                .Where(f => f.Id is int
                         && !FieldTypeInfo.IsComputed(f.FieldType)
                         && !FieldTypeInfo.IsRelation(f.FieldType))
                .Select(f => new ApiField(f.Name, FieldTypeInfo.Label(f.FieldType)))
                .ToList();

            var title = GetSystemFieldNames(collection.Id).GetValueOrDefault(TitleKey, "Название");

            result.Add(new ApiCollection(collection.Name!, title, fields));
        }

        return result;
    }
}
