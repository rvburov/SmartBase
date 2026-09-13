using System.Globalization;
using SmartBase.Core.Models;

namespace SmartBase.Integrations;

/// <summary>Какого рода значение получилось у выражения.</summary>
public enum FormulaKind
{
    /// <summary>Текст — и то, что пришло из строки, тоже: в таблице шага все значения текстовые.</summary>
    Text = 0,

    /// <summary>Число.</summary>
    Number,

    /// <summary>Да или нет — то, что дает сравнение.</summary>
    Logic,

    /// <summary>Дата, при необходимости со временем.</summary>
    Date,
}

/// <summary>
/// Отказ вычислителя, сказанный человеческими словами: «в колонке «Сумма» не число: «около
/// трех»». Наружу не выходит — его ловит <see cref="Formula.Run"/> и отдает строкой, — но внутри
/// дерева выражения бросать удобнее, чем протаскивать признак неудачи через каждый узел.
/// </summary>
public sealed class FormulaTrouble(string explanation) : Exception(explanation);

/// <summary>
/// Значение внутри выражения шага «преобразовать строки» (docs/ROADMAP-API.md, шаг 12).
///
/// <b>Типов ровно четыре, и они не выдумываются на ходу.</b> В таблице шага все значения —
/// ТЕКСТ, пришедший из чужой выгрузки. Чтобы его сложить, надо решить, что это число; чтобы
/// прибавить к нему дни — что это дата. Решение принимается в одном месте — здесь, — и
/// принимается по правилам, которые можно назвать словами:
///
/// <list type="bullet">
///   <item><b>Текст в число приводится сам собой.</b> Либо весь текст — число, либо это отказ с
///   объяснением; угадывать тут нечего.</item>
///   <item><b>Текст в дату сам собой НЕ приводится.</b> «10.05» — это десятое мая или число
///   десять и пять сотых? Двусмысленность решается не догадкой программы, а функцией
///   <c>ДАТА(…)</c>, которую человек пишет сам.</item>
/// </list>
///
/// Канон вывода тот же, что в остальной программе: число — с точкой и без экспоненты (как его
/// пишет экспорт, <c>WriterHelpers.TryNumber</c>), дата — «дд.ММ.гггг» (как ее приносит
/// календарный триггер и понимает <see cref="DateValue"/>), «да» и «нет» — словами.
/// </summary>
public readonly record struct FormulaValue
{
    private readonly string   _text;
    private readonly double   _number;
    private readonly bool     _logic;
    private readonly DateTime _moment;
    private readonly bool     _timed;

    private FormulaValue(FormulaKind kind, string text, double number, bool logic,
                         DateTime moment, bool timed)
    {
        Kind    = kind;
        _text   = text;
        _number = number;
        _logic  = logic;
        _moment = moment;
        _timed  = timed;
    }

    /// <summary>Какого рода это значение.</summary>
    public FormulaKind Kind { get; }

    public static FormulaValue Of(string? text) =>
        new(FormulaKind.Text, text ?? "", 0, false, default, false);

    public static FormulaValue Of(double number) =>
        new(FormulaKind.Number, "", number, false, default, false);

    public static FormulaValue Of(bool logic) =>
        new(FormulaKind.Logic, "", 0, logic, default, false);

    /// <param name="timed">Время в дате значащее: «дд.ММ.гггг ЧЧ:мм» против «дд.ММ.гггг».</param>
    public static FormulaValue OfDate(DateTime moment, bool timed) =>
        new(FormulaKind.Date, "", 0, false, timed ? moment : moment.Date, timed);

    // ── Что уходит в колонку ──────────────────────────────────────────────────

    /// <summary>
    /// Готовое значение колонки. Дальше оно живет обычной строкой таблицы шага — его отбирают
    /// условиями, кладут в поле записи, выгружают в файл, — поэтому вид должен совпадать с тем,
    /// который в этих местах и так принят.
    /// </summary>
    public string ToText() => Kind switch
    {
        FormulaKind.Number => Text(_number),
        FormulaKind.Logic  => _logic ? "да" : "нет",
        FormulaKind.Date   => _moment.ToString(_timed ? "dd.MM.yyyy HH:mm" : "dd.MM.yyyy",
                                               CultureInfo.InvariantCulture),
        _                  => _text,
    };

    /// <summary>
    /// Число словами: точка разделителем и без экспоненты — ровно так его пишет экспорт и читает
    /// поле типа «число». Хвост из нулей не показывается: 2 — это «2», а не «2,0000000000».
    /// </summary>
    private static string Text(double value) =>
        value.ToString("0.##########", CultureInfo.InvariantCulture);

    // ── Приведения ────────────────────────────────────────────────────────────

    /// <summary>
    /// Число из значения. <paramref name="where"/> — что об этом сказать человеку: имя колонки
    /// или название функции. Отказ вместо нуля намеренно: пустая колонка, молча ставшая нулем,
    /// дает неверный итог, который никак не отличить от верного.
    /// </summary>
    public double AsNumber(string where) => Kind switch
    {
        FormulaKind.Number => _number,

        FormulaKind.Text => TryNumber(_text, out var value)
            ? value
            : throw new FormulaTrouble(_text.Trim().Length == 0
                ? $"{where}: пусто, а нужно число"
                // Текст, похожий на дату, — самая частая причина попасть сюда: человек написал
                // «{{Срок}} + 30», имея в виду дни. Сказать ему про ДАТА(…) дешевле, чем дать
                // отказ «это не число» и оставить догадываться.
                : $"{where}: «{_text.Trim()}» — это не число"
                  + (DateValue.TryParse(_text, out _)
                      ? "; если это дата, оберните ее в ДАТА(…)"
                      : "")),

        FormulaKind.Logic => throw new FormulaTrouble($"{where}: «да» и «нет» — не числа"),

        _ => throw new FormulaTrouble($"{where}: дата — не число; "
                                    + "разницу в днях дает вычитание одной даты из другой"),
    };

    /// <summary>
    /// Да или нет. Текст принимается ровно тот, которым это записывают в программе: «да»/«нет»
    /// (так приходит «Весь день» календарного события) и «1»/«0» (так лежит галочка в базе).
    /// Пусто — это «нет»: у незаполненной галочки другого разумного прочтения нет.
    /// </summary>
    public bool AsLogic(string where) => Kind switch
    {
        FormulaKind.Logic => _logic,

        FormulaKind.Text => _text.Trim().ToLowerInvariant() switch
        {
            "да" or "1" or "истина" or "true"   => true,
            "нет" or "0" or "ложь" or "false" or "" => false,
            _ => throw new FormulaTrouble(
                $"{where}: «{_text.Trim()}» — это не «да» и не «нет»; сравните значение, "
                + "например {{Статус}} = \"Оплачен\""),
        },

        _ => throw new FormulaTrouble($"{where}: тут нужно «да» или «нет», а это "
                                    + (Kind == FormulaKind.Number ? "число" : "дата")),
    };

    /// <summary>
    /// Дата и признак значащего времени. Текст сюда НЕ приводится — только то, что уже стало
    /// датой через <c>ДАТА(…)</c> или <c>СЕГОДНЯ()</c>; почему так, сказано в описании типа.
    /// </summary>
    public (DateTime Moment, bool Timed) AsDate(string where) =>
        Kind == FormulaKind.Date
            ? (_moment, _timed)
            : throw new FormulaTrouble($"{where}: тут нужна дата; текст превращает в дату "
                                     + "функция ДАТА(…)");

    /// <summary>Пустое ли значение — как это понимает отбор: текста нет или он из одних пробелов.</summary>
    public bool IsBlank => Kind == FormulaKind.Text && _text.Trim().Length == 0;

    // ── Разбор текста ─────────────────────────────────────────────────────────

    /// <summary>
    /// Строгий разбор числа: весь текст целиком и ничего лишнего. Запятая принимается как
    /// десятичный разделитель — тем же правилом, каким числа читает экспорт: в базе они лежат
    /// сырым вводом, и «12,5» там обычное дело.
    /// </summary>
    public static bool TryNumber(string? raw, out double value)
    {
        value = 0;

        var text = (raw ?? "").Trim().Replace(',', '.');
        if (text.Length == 0) return false;

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    /// <summary>
    /// Терпимый разбор числа — то, что делает функция <c>ЧИСЛО(…)</c>. От строгого отличается
    /// ровно двумя послаблениями, и оба про вид, в котором числа приходят из чужих выгрузок:
    /// пробелы-разделители разрядов («1 234,50») и хвост из букв и знаков («1 250 руб.»).
    ///
    /// Дальше этого терпимость не идет: «около трех» числом не станет, и это отказ, а не ноль.
    /// </summary>
    public static bool TryLooseNumber(string? raw, out double value)
    {
        value = 0;

        var text = (raw ?? "").Trim();
        if (text.Length == 0) return false;

        // Берется ПЕРВОЕ число слева: «1 250 руб. за штуку» — это 1250, а не попытка сложить
        // все числа строки. Все, что после него, отбрасывается молча — иначе единица измерения
        // рядом с суммой требовала бы от человека отдельной обрезки.
        var digits = new System.Text.StringBuilder(text.Length);
        var seen   = false;
        var dot    = false;

        foreach (var symbol in text)
        {
            if (char.IsDigit(symbol))
            {
                digits.Append(symbol);
                seen = true;
                continue;
            }

            if (symbol is '-' or '+' && digits.Length == 0) { digits.Append(symbol); continue; }

            // Пробел внутри числа — разделитель разрядов; он же после числа означает его конец,
            // но это выяснится на следующем знаке, поэтому просто пропускаем.
            if (symbol is ' ' or '\u00A0' or '\u202F') continue;

            if (symbol is '.' or ',' && seen && !dot) { digits.Append('.'); dot = true; continue; }

            if (seen) break;

            return false;
        }

        if (!seen) return false;

        // Точка в хвосте — конец предложения, а не десятичный разделитель: «1 250 руб.».
        var collected = digits.ToString().TrimEnd('.');

        return double.TryParse(collected, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }
}
