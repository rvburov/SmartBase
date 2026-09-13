using SmartBase.Core.Models;

namespace SmartBase.Views.Profile;

/// <summary>
/// Две фразы области «все базы данных» (docs/ROADMAP-API.md, шаг 15): куда уйдут правки и что
/// стало с последней.
///
/// <b>Почему это отдельный класс, а не строки внутри панели.</b> Обе фразы — единственное, что
/// объясняет человеку границу массового действия, и они должны быть одинаковы при любом наборе
/// баз: одна база, одна закрытая, десять вперемешку. Проверить это на живом окне нельзя, а на
/// чистой функции — можно, и проверено (Tests/Unit/AcrossReportTests.cs).
///
/// Правило у обеих одно: <b>называть базы по имени</b>. «Не затронуто 2 базы» человеку нечего
/// делать с этим числом; «Архив — не открыта» он может открыть и повторить.
/// </summary>
public static class AcrossReport
{
    /// <summary>
    /// Куда уйдут правки и куда не уйдут. Стоит над таблицей до первого щелчка: область
    /// действия галки надо знать заранее, а не узнавать из отчета о сделанном.
    /// </summary>
    /// <param name="mine">Открытые базы, где вы администратор, — правка уходит в них.</param>
    /// <param name="foreign">Открытые базы, где администратор не вы.</param>
    /// <param name="closed">Заведенные, но не открытые сейчас базы.</param>
    public static string Where(IReadOnlyList<string> mine,
                               IReadOnlyList<string> foreign,
                               IReadOnlyList<string> closed)
    {
        var text = mine.Count > 0
            ? "Правки уходят сразу в открытые базы, где вы администратор: "
              + string.Join(", ", mine) + "."
            : "Открытых баз, где вы администратор, сейчас нет — править нечего.";

        var aside = new List<string>();

        foreach (var name in foreign) aside.Add($"{name} — вы не администратор");
        foreach (var name in closed)  aside.Add($"{name} — не открыта");

        if (aside.Count > 0)
            text += " Не затронуты: " + string.Join("; ", aside)
                  + ". Права лежат внутри файла каждой базы, и в закрытый файл их не положить.";

        return text;
    }

    /// <summary>
    /// Что вышло из последней массовой правки.
    ///
    /// Названы все три исхода, и порознь. «Не записалось» — поломка, о которой надо знать
    /// немедленно; «пропущено» — норма (администратору право не нужно, ожидающему допуска его
    /// некуда класть). Свалить их в одно «не везде получилось» значило бы пугать нормой и прятать
    /// поломку.
    /// </summary>
    public static string Outcome(PermAction action, bool allowed,
                                 IReadOnlyList<string> done,
                                 IReadOnlyList<string> failed,
                                 IReadOnlyList<string> skipped)
    {
        var what = $"«{PermActionLabels.Title(action)}» " + (allowed ? "разрешено" : "запрещено");

        var text = done.Count > 0
            ? $"{what} в базах: {string.Join(", ", done)}."
            : $"{what} — ни одной базы не затронуто.";

        if (failed.Count > 0)  text += " Не записалось: " + string.Join("; ", failed) + ".";
        if (skipped.Count > 0) text += " Пропущено: "     + string.Join("; ", skipped) + ".";

        return text;
    }
}
