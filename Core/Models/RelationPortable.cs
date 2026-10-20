namespace SmartBase.Core.Models;

/// <summary>Свойство переносимой JSON-коллекции: исходный ключ нужен для восстановления графа.</summary>
public sealed record RelationPortableField(int Id, string Name, string FieldType, string? ConfigJson = null);

/// <summary>Версия и происхождение JSON-коллекции. Пути и строки подключений в файл не попадают.</summary>
public sealed record RelationPortableSchema(int Version, int SourceCollectionId, int SourceDbId,
    string SourceIdentity, Dictionary<int, string> DatabaseIdentities, List<RelationPortableField> Fields,
    string TitleHeader = "Название");

/// <summary>Значения обычных свойств и упорядоченные полные цели связей хранятся раздельно.
/// Вычисляемые свойства передаются настройками схемы, без сохранения показанного результата.</summary>
public sealed record RelationPortableRow(int Id, string Title, Dictionary<int, string> Values,
    Dictionary<int, List<RelationTarget>> Links);
