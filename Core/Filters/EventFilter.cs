using MyPlanner.Core.Models;

namespace MyPlanner.Core.Filters;

public static class EventFilter
{
    public static IEnumerable<Event> BySearch(IEnumerable<Event> events, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return events;

        var q = query.ToLowerInvariant();
        return events.Where(e => e.Title.ToLowerInvariant().Contains(q));
    }

    public static IEnumerable<Event> ByCollections(IEnumerable<Event> events, ISet<int>? activeIds)
    {
        if (activeIds is null)
            return events;

        return events.Where(e => e.CollectionId.HasValue && activeIds.Contains(e.CollectionId.Value));
    }
}
