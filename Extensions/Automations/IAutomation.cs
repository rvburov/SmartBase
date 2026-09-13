using System.Globalization;

namespace SmartBase.Extensions.Automations;

/// <summary>
/// Одна строка, с которой работает автоматизация: колонка → значение.
///
/// Обертка, а не голый словарь, — ради того, чтобы автоматизация умещалась в один класс с одним
/// методом (docs/ROADMAP-API.md, шаг 13). Разбор числа, поиск колонки без учета регистра и
/// «копия с еще одной колонкой» иначе переписывались бы в каждой автоматизации заново, а
/// переписанное в десятый раз расходится.
///
/// Строка неизменяемая: <see cref="With"/> возвращает копию. Так автоматизация не может
/// случайно испортить то, что ей дали, — а дали ей строки, которые живут дальше по сценарию.
/// </summary>
public sealed class AutomationRow
{
    private readonly Dictionary<string, string> _values;

    public AutomationRow(IReadOnlyDictionary<string, string> values)
    {
        _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, value) in values) _values[column] = value ?? "";
    }

    /// <summary>Строка из пар — для тестов и для автоматизаций, собирающих ответ с нуля.</summary>
    public static AutomationRow Of(params (string Column, string Value)[] cells)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, value) in cells) values[column] = value ?? "";

        return new AutomationRow(values);
    }

    /// <summary>
    /// Значение колонки; колонки нет — пустая строка. Регистр не важен: имена приходят из чужой
    /// выгрузки, где «Проект» и «проект» — одно и то же.
    /// </summary>
    public string this[string column] =>
        _values.TryGetValue(column ?? "", out var value) ? value : "";

    /// <summary>Есть ли такая колонка вообще — в отличие от «есть, но пустая».</summary>
    public bool Has(string column) => _values.ContainsKey(column ?? "");

    /// <summary>Имена колонок этой строки.</summary>
    public IReadOnlyCollection<string> Columns => _values.Keys;

    /// <summary>Все значения — то, что уходит дальше по сценарию.</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>
    /// Число из колонки; <c>null</c> — там не число. Запятая принимается как десятичный
    /// разделитель: в базе числа лежат сырым вводом, и «12,5» там обычное дело.
    /// </summary>
    public double? Number(string column)
    {
        var raw = this[column].Trim().Replace(',', '.');

        return raw.Length > 0
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value)
                ? value
                : null;
    }

    /// <summary>Копия строки с добавленной или замененной колонкой.</summary>
    public AutomationRow With(string column, string value)
    {
        var copy = new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase)
        {
            [column] = value ?? "",
        };

        return new AutomationRow(copy);
    }

    /// <summary>Число в колонку — тем же каноном, каким его пишет экспорт: точкой и без хвостов.</summary>
    public AutomationRow With(string column, double value) =>
        With(column, value.ToString("0.##########", CultureInfo.InvariantCulture));
}

/// <summary>Что пришло автоматизации: строки предыдущего шага и настройки, заданные в шаге.</summary>
/// <param name="Rows">Строки; их не больше 500 — столько сценарий передает между шагами.</param>
/// <param name="Values">Настройки по ключам из <see cref="IAutomation.Settings"/>.</param>
public sealed record AutomationInput(
    IReadOnlyList<AutomationRow>        Rows,
    IReadOnlyDictionary<string, string> Values)
{
    /// <summary>Настройка; не заданной — пустая строка. Крайние пробелы снимаются.</summary>
    public string Setting(string key) =>
        Values.TryGetValue(key ?? "", out var value) ? (value ?? "").Trim() : "";
}

/// <summary>
/// Отказ автоматизации, сказанный человеческими словами: «колонка «Проект» в строках не
/// нашлась». Доезжает до отчета о запуске и до квитанции отправителю как есть.
///
/// Бросать его — законный способ отказаться: у автоматизации один метод, и заводить ради отказа
/// второй результат-обертку значило бы усложнить ровно то, что шаг 13 упрощает. Любое ДРУГОЕ
/// исключение оболочка тоже переживет, но покажет как «сорвалось» — с текстом для разработчика.
/// </summary>
public sealed class AutomationRefusal(string explanation) : Exception(explanation);

/// <summary>
/// Автоматизация — «строки пришли, строки ушли» (docs/ROADMAP-API.md, шаг 13).
///
/// <b>Это и есть ответ на вопрос «а можно писать скрипты?».</b> Нельзя и не будет: песочницы в
/// .NET нет, и скрипту в окне нельзя запретить открыть сокет мимо шлюза — один такой шаг обнулил
/// бы весь контур безопасности (docs/ROADMAP-API.md, раздел 13). Зато церемония срезана до
/// одного файла: новый класс, реализующий этот интерфейс, САМ появляется в списке шагов
/// сценария — реестр находит его по сборке (<see cref="AutomationRegistry"/>), править не надо
/// ни реестр расширений, ни окно настройки шага.
///
/// Что автоматизации доступно: строки, которые принес предыдущий шаг, и настройки этого шага.
/// Больше ничего — ни базы, ни сети, ни файлов. Права она поэтому не спрашивает, а рубеж
/// контура ее не касается вовсе.
/// </summary>
public interface IAutomation
{
    /// <summary>Стабильный ключ — он хранится в шаге сценария; слаг вида «sum-by-key».</summary>
    string Key { get; }

    /// <summary>Имя для человека — его видно в списке шагов.</summary>
    string Name { get; }

    /// <summary>Что делает — одной строкой, под именем.</summary>
    string What { get; }

    /// <summary>
    /// Настройки шага: имя колонки-ключа, разделитель, имя листа. Заполняются ОДИН раз на шаг,
    /// а не на строку, поэтому подстановок <c>{{колонка}}</c> в них нет — колонка из какой
    /// именно строки, ответа не имеет.
    /// </summary>
    IReadOnlyList<ExtensionActionField> Settings => [];

    /// <summary>
    /// Строки пришли — строки ушли. Пустой список означает «дальше идти незачем», и сценарий на
    /// этом кончается без жалобы — так же, как на отборе, не нашедшем ни одной подходящей строки.
    ///
    /// Отказ — <see cref="AutomationRefusal"/> с человеческим текстом.
    /// </summary>
    IReadOnlyList<AutomationRow> Run(AutomationInput input);
}
