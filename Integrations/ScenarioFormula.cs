namespace SmartBase.Integrations;

/// <summary>
/// Одна строка шага «преобразовать строки»: как назвать новую колонку и чем ее заполнить
/// (docs/ROADMAP-API.md, шаг 12).
///
/// Форма ровно та же, что у таблицы «поле → выражение» в шаге «создать запись», и это не
/// совпадение: человек, научившийся раскладывать значения там, не должен учиться второй раз
/// здесь. Разница одна, и она в правой части — там это текст с подстановками, здесь выражение
/// (<see cref="Formula"/>): вычислять «сумма × 1,2» шаблон текста не умеет.
///
/// Имя колонки, совпавшее с уже существующей, ЗАМЕНЯЕТ ее значение — на это и расчет: обрезать
/// пробелы в «Названии» или привести «Статус» к одному написанию иначе было бы нечем. Замена
/// названа числом в отчете шага, так что молча она не проходит.
/// </summary>
/// <param name="Column">Имя новой колонки — то, которым она дальше зовется в сценарии.</param>
/// <param name="Expression">Выражение над значениями строки.</param>
public sealed record ScenarioFormula(string Column, string Expression)
{
    /// <summary>
    /// Сколько знаков умещается в имени колонки. Столько же, сколько в имени свойства: колонка
    /// шага почти всегда и едет дальше в поле коллекции.
    /// </summary>
    public const int MaxColumnLength = 100;

    /// <summary>Что не так со строкой; пустой список — годится.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();

        var name = (Column ?? "").Trim();

        if (name.Length == 0)
        {
            problems.Add("не названа колонка");
        }
        else
        {
            if (name.Length > MaxColumnLength)
                problems.Add($"имя колонки длиннее {MaxColumnLength} знаков");

            // Фигурные скобки в имени сделали бы ссылку на эту колонку неразбираемой: {{ и }}
            // выделяют имя, и имя, само содержащее их, кончалось бы не там, где написано.
            if (name.Contains('{') || name.Contains('}'))
                problems.Add("в имени колонки не должно быть фигурных скобок");
        }

        if (string.IsNullOrWhiteSpace(Expression))
            problems.Add("не заполнено выражение");
        else if (Formula.TryParse(Expression, out var trouble) is null)
            problems.Add(trouble);

        return problems;
    }

    /// <summary>Одной строкой: «Итого = {{Цена}} * {{Количество}}».</summary>
    public override string ToString() => $"{Column} = {Expression}";
}
