using SmartBase.Database.Repositories;
using SmartBase.Extensions.DocRegistry;

namespace SmartBase.Tests.Integration;

/// <summary>
/// Коллекция-журнал регистрации КД: столбцы в порядке эталона <see cref="RegistrySchema"/>.
///
/// Такой же стенд есть в тестах самого расширения, но проекты разные и общего кода у них нет.
/// Повторяется он не по памяти, а по ОТКРЫТОЙ части расширения — списку имен столбцов: разойдись
/// стенд с эталоном, и разбор откажет, назвав причину.
/// </summary>
internal static class RegistryStand
{
    /// <summary>Завести журнал регистрации и вернуть его id.</summary>
    public static int Create(ConstructorRepository repo, string name = "Журнал регистрации")
    {
        var collectionId = SampleData.AddCollection(repo, name).Id!.Value;

        repo.SetSystemFieldName(collectionId, RegistrySchema.TitleKey,
                                RegistrySchema.TitleColumn, "Название");

        var keys     = new List<string> { RegistrySchema.TitleKey };
        var position = 1000;

        foreach (var column in RegistrySchema.Columns.Skip(1))
        {
            var type  = column == RegistrySchema.DateColumn ? "date" : "text";
            var field = SampleData.AddField(repo, collectionId, column, type, position);

            keys.Add(field.Id!.Value.ToString());
            position += 1000;
        }

        repo.SaveColumnOrder(collectionId, keys);
        return collectionId;
    }
}
