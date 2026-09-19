namespace SmartBase.Utils;

/// <summary>
/// Русское согласование существительного с числом: «1 документ», «2 документа», «5 документов».
///
/// Правило одно на все места, потому что у него есть ловушка, на которой спотыкаются все
/// самодельные варианты: одиннадцать–четырнадцать ведут себя как «много» вопреки последней
/// цифре. «11 документа» — типовая ошибка, и повторять ее в каждом файле незачем.
///
/// В проекте есть три более старые самодельные пары (сайдбар, окно обмена): они писались
/// до этого класса и могут переехать сюда при следующей правке рядом. Новый код берет это.
/// </summary>
public static class Plural
{
    /// <param name="one">Форма при 1: «документ».</param>
    /// <param name="few">Форма при 2–4: «документа».</param>
    /// <param name="many">Форма при 0 и 5–20: «документов».</param>
    public static string Of(int n, string one, string few, string many)
    {
        var abs = Math.Abs(n);
        if (abs % 100 is >= 11 and <= 14) return many;
        return (abs % 10) switch
        {
            1           => one,
            2 or 3 or 4 => few,
            _           => many,
        };
    }

    /// <summary>«5 документов» — число вместе с согласованным словом.</summary>
    public static string Count(int n, string one, string few, string many) =>
        $"{n} {Of(n, one, few, many)}";

    public static string Documents(int n) => Of(n, "документ", "документа", "документов");
    public static string Pages(int n)     => Of(n, "страница", "страницы", "страниц");

    /// <summary>
    /// Родительный падеж после предлогов вроде «около»: «около часа», «около двух часов»,
    /// «около двенадцати минут».
    ///
    /// Обычное согласование здесь НЕ годится и дает «около 1 час»: именительный падеж отвечает
    /// на вопрос «что», а предлог требует «чего». Правило проще основного — единственное число
    /// только при последней цифре 1 (кроме 11), во всех остальных случаях множественное.
    /// </summary>
    /// <param name="one">«часа», «минуты» — при 1, 21, 31…</param>
    /// <param name="many">«часов», «минут» — во всех остальных случаях.</param>
    public static string Genitive(int n, string one, string many)
    {
        var abs = Math.Abs(n);
        if (abs % 100 is >= 11 and <= 14) return many;
        return abs % 10 == 1 ? one : many;
    }

    public static string GenitiveMinutes(int n) => Genitive(n, "минуты", "минут");
    public static string GenitiveHours(int n)   => Genitive(n, "часа",   "часов");
}
