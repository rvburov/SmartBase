using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>
/// Действие «Разобрать сообщение о регистрации» — преобразователь формата: текст сообщения,
/// пришедшего файлом в папку обмена, становится одной строкой таблицы
/// (docs/ROADMAP-API.md, раздел 9).
///
/// Сообщение выглядит так, как его набирает человек в переписке:
///
/// <code>
/// Проект: Молоток
/// Наименование: Кронштейн
/// Децимальный номер: АБВГ.301121
/// ФИО регистрирующего: Иванов Иван Иванович
/// </code>
///
/// <b>Разбор ничего не знает про регистрацию.</b> Он не ищет «наименование» и не догадывается,
/// что «Проект» — это проект: любая строка «Ключ: значение» становится колонкой с этим именем, а
/// какая колонка в какое поле действия ляжет, решает шаг сценария. Так одно и то же сообщение
/// раскладывается по-разному в разных отделах, и правится это настройкой, а не программой.
///
/// Единственное знание о предметной области здесь — <b>разбор децимального номера</b>. Оно и
/// должно быть здесь: обозначение по ГОСТ 2.201-80 — это три отдельных значения, слепленных
/// точками (<c>АБВГ.301121.005-01</c>), а действию регистрации они нужны врозь. Просить человека
/// расписать их по строкам значило бы просить его делать работу программы.
///
/// <b>Отказ — результатом, а не исключением.</b> У сообщения нет собеседника: причина обязана
/// доехать до квитанции отправителю.
/// </summary>
public sealed class ParseMessageAction : IExtensionAction
{
    /// <summary>Ключ действия внутри расширения.</summary>
    public const string ActionKey = "parse-message";

    /// <summary>Колонки, которые появляются из разобранного децимального номера.</summary>
    public const string OrgColumn       = "Код организации";

    /// <inheritdoc cref="OrgColumn"/>
    public const string EskdColumn      = "Код ЕСКД";

    /// <inheritdoc cref="OrgColumn"/>
    public const string NumberColumn    = "Номер документа";

    /// <inheritdoc cref="OrgColumn"/>
    public const string ExecutionColumn = "Исполнение";

    public string Key  => ActionKey;
    public string Name => "Разобрать сообщение о регистрации";

    public string Description =>
        "Строки вида «Ключ: значение» становятся колонками одной строки. Децимальный номер "
      + "дополнительно раскладывается на код организации, код ЕСКД, номер и исполнение.";

    public ExtensionActionKind Kind => ExtensionActionKind.Transformer;

    /// <summary>
    /// Сколько строк сообщения разбирается. Предохранитель от файла, который положили в папку по
    /// ошибке: выгрузка на сто тысяч строк не должна превращаться в таблицу из ста тысяч колонок.
    /// </summary>
    public const int MaxLines = 100;

    private static readonly JsonSerializerOptions Format = new()
    {
        // Кириллица остается кириллицей: разобранное видно и в отчете о запуске, и в тестах, а
        // «К» не читается никем.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public ActionResult Run(ActionContext context, ActionInput input)
    {
        var lines = (input.Text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length == 0)  return ActionResult.Failed("сообщение пустое");
        if (lines.Length > MaxLines)
            return ActionResult.Failed($"в сообщении строк {lines.Length}, а разбирается не "
                                     + $"больше {MaxLines}: похоже, это не сообщение");

        // Порядок колонок — порядок строк сообщения: ПЕРВАЯ становится названием записи (так
        // читает таблицу источник импорта), и переставлять ее из-за словаря нельзя. Поэтому
        // список пар, а не словарь: у словаря порядок — свойство реализации.
        var values = new List<(string Key, string Value)>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;   // «просто текст» и строка, начинающаяся с двоеточия

            var key   = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (key.Length == 0) continue;

            // Первое упоминание побеждает: во втором обычно лежит цитата переписки, а не
            // поправка. Гадать, какое из двух значений человек имел в виду, нельзя.
            if (seen.Add(key)) values.Add((key, value));
        }

        if (values.Count == 0)
            return ActionResult.Failed(
                "в сообщении не нашлось ни одной строки вида «Ключ: значение»");

        Designation(values, seen);

        var row = new JsonObject();
        foreach (var (key, value) in values) row[key] = value;

        return ActionResult.Table(new JsonArray(row).ToJsonString(Format), 1);
    }

    /// <summary>
    /// Разложить децимальный номер на составляющие. Берется ПЕРВОЕ значение, похожее на
    /// обозначение; уже занятые имена колонок не переписываются — человек мог указать код
    /// организации отдельной строкой, и его слово старше вычисленного.
    /// </summary>
    private static void Designation(List<(string Key, string Value)> values, HashSet<string> seen)
    {
        foreach (var (_, value) in values.ToList())
        {
            if (!Split(value, out var org, out var eskd, out var number, out var execution)) continue;

            Put(values, seen, OrgColumn,  org);
            Put(values, seen, EskdColumn, eskd);

            if (number.Length    > 0) Put(values, seen, NumberColumn,    number);
            if (execution.Length > 0) Put(values, seen, ExecutionColumn, execution);

            return;
        }
    }

    private static void Put(List<(string Key, string Value)> values, HashSet<string> seen,
                            string column, string value)
    {
        if (seen.Add(column)) values.Add((column, value));
    }

    /// <summary>
    /// Разобрать обозначение: <c>АБВГ.301121</c>, <c>АБВГ.301121.005</c>, <c>АБВГ.301121.005-01</c>.
    /// <c>false</c> — на обозначение не похоже.
    ///
    /// Строгость намеренная. Код ЕСКД — ровно шесть цифр, номер — ровно три, исполнение — ровно
    /// две: те же правила, по которым обозначения читает <see cref="DocRegistry.Designation"/>.
    /// Прочитать «005» как 5, а «0011» как 11 было бы догадкой, а неверная догадка выдает номер
    /// повторно и молча.
    /// </summary>
    private static bool Split(string text, out string org, out string eskd,
                              out string number, out string execution)
    {
        org = eskd = number = execution = "";

        var parts = text.Trim().Split('.');
        if (parts.Length is < 2 or > 3) return false;

        if (parts[0].Length == 0)                                     return false;
        if (parts[1].Length != EskdCatalog.CodeLength)                return false;
        if (!parts[1].All(char.IsAsciiDigit))                         return false;

        org  = parts[0];
        eskd = parts[1];

        if (parts.Length == 2) return true;

        // Хвост «005-01»: номер и, если он есть, исполнение.
        var tail  = parts[2];
        var dash  = tail.IndexOf('-');
        var digits = dash < 0 ? tail : tail[..dash];

        if (digits.Length != 3 || !digits.All(char.IsAsciiDigit)) return false;
        number = digits;

        if (dash < 0) return true;

        var rest = tail[(dash + 1)..];
        if (rest.Length == 2 && rest.All(char.IsAsciiDigit)) execution = rest;

        return true;
    }
}
