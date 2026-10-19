namespace SmartBase.Core.Documents;

/// <summary>Общие ограничители ленты и поиска: контроллер, кеш и проверки берут их отсюда.</summary>
public static class DocLibraryLimits
{
    public const int SearchResults = 500;
    public const int InMemoryDocuments = 10_000;
    public const long ThumbnailCacheBytes = 128L * 1024 * 1024;
}
