namespace MyPlanner.Core.Models;

/// Единая логика «цвета по умолчанию»: дефолтный синий #007AFF трактуется как
/// «без цвета» и отображается серым — так же, как чекбоксы в сайдбаре.
public static class EventColors
{
    public const string Default = "#007AFF";  // дефолт коллекции = «без цвета»
    public const string Grey    = "#8E8E93";

    public static string Display(string? color) =>
        string.IsNullOrWhiteSpace(color) || color == Default ? Grey : color;
}
