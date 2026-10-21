using System.Text.RegularExpressions;
using SmartBase.Core.Models;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>
/// Оформление ответов отправителю разметкой HTML: программа-мессенджер показывает текст
/// сообщения как HTML (решения пользователя 21.10.2026). Оформление — всегда, без
/// переключателя; слова ответов те же, что без него, меняется только вид.
///
/// Что та сторона показывает, проверено пользователем: жирный, курсив, зачеркнутый, размер
/// шрифта, цитата, список — да; цвет, заголовки, абзацы <c>&lt;p&gt;</c>, код и перевод строки
/// <c>\n</c> — нет. Поэтому строки ответа по-прежнему разделяются <c>\n</c>, а переносом той
/// стороны его заменяет сценарий (поле «Перенос строки в ответе»). Внутри блоков — цитаты и
/// списка — <c>\n</c> не ставится: замененный на перенос, он дал бы лишние пустые строки.
///
/// Значения журнала и сообщения (наименование, проект, ФИО, имя поля) экранируются
/// (<see cref="Escape"/>): наименование «Кронштейн &lt;левый&gt;» иначе пропало бы как тег.
///
/// Объяснение для журнала запусков (<see cref="ActionResult.Explanation"/>) остается
/// простым текстом — его читает тот, кто настраивал обмен, в окне программы, а не в переписке.
/// </summary>
public static class ReplyMarkup
{
    /// <summary>Просьба ответить на запрос подтверждения.</summary>
    public const string AnswerPrompt = "Подтвердите: <b>Да</b> или <b>Нет</b>";

    /// <summary>Срок подтверждения — строкой под просьбой.</summary>
    public const string Lifetime = "<i>Подтверждение действует 10 минут</i>";

    /// <summary>Знаки, которые разметка поняла бы по-своему, — сущностями.</summary>
    public static string Escape(string? text) =>
        (text ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static string Bold(string html) => $"<b>{html}</b>";

    public static string Italic(string html) => $"<i>{html}</i>";

    /// <summary>Зачеркнутое старое значение; пустому зачеркивать нечего.</summary>
    public static string Struck(string html) => html.Length == 0 ? "" : $"<s>{html}</s>";

    /// <summary>Крупнее обычного: заголовки h1–h6 та сторона не увеличивает.</summary>
    public static string Larger(string html) => $"<font size='4'>{html}</font>";

    /// <summary>Образец сообщения — цитатой, строки внутри — тегом переноса.</summary>
    public static string Quote(params string[] lines) =>
        "<blockquote>" + string.Join("<br>", lines) + "</blockquote>";

    /// <summary>Пояснения и правила — списком.</summary>
    public static string List(params string[] items) =>
        "<ul>" + string.Concat(items.Select(item => $"<li>{item}</li>")) + "</ul>";

    /// <summary>
    /// Пример и перечень допустимого в скобках — курсивом, номер документа вне их — жирным.
    /// Одним проходом: номер внутри примера остается курсивом, а не становится жирным.
    /// </summary>
    private static readonly Regex Marked = new(
        @"(?<hint>\((?:Пример|Можно): )(?<text>[^()]*)\)|(?<number>\p{Lu}{4}\.\d{6}\.\d{3}(?:-\d{2})?)",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Название поля с двоеточием в начале строки ошибки: «Номер:», «Ожидается подтверждение:»,
    /// «Примечание (первичное применение):». Точка до двоеточия — уже предложение, а не поле.
    /// </summary>
    private static readonly Regex Field = new(@"^[^:.<>]{1,60}:(?=\s|$)", RegexOptions.CultureInvariant);

    /// <summary>
    /// Строка ошибки: «<b>Номер:</b> неправильный номер (Пример: <i>…</i>)», номер документа —
    /// жирным, «Да» и «Нет» в просьбе ответить — жирным. Слова строки не меняются.
    /// </summary>
    public static string Problem(string line)
    {
        var html = Marked.Replace(Escape(line), m => m.Groups["number"].Success
            ? Bold(m.Value)
            : $"{m.Groups["hint"].Value}{Italic(m.Groups["text"].Value)})");

        html = Field.Replace(html, m => Bold(m.Value), 1);

        return html.Replace("Подтвердите: Да или Нет", AnswerPrompt)
                   .Replace("ответьте Нет", "ответьте <b>Нет</b>");
    }

    /// <summary>Ошибки по строке на каждую — как в простом тексте.</summary>
    public static string Problems(string lines) => string.Join("\n", lines.Split('\n').Select(Problem));

    /// <summary>
    /// Отказ, первое предложение которого — заголовок: «<b>Серия изменилась после запроса
    /// подтверждения.</b> Регистрация не выполнена…».
    /// </summary>
    public static string Headed(string line)
    {
        var end = line.IndexOf(". ", StringComparison.Ordinal);

        return end < 0
            ? Bold(Escape(line))
            : Bold(Escape(line[..(end + 1)])) + " " + Problem(line[(end + 2)..]);
    }

    /// <summary>Отказ с ошибками в сообщении: в журнал — простым текстом, отправителю — оформленным.</summary>
    public static ActionResult Refused(string plain) => new(false, plain, Reply: Problems(plain));

    /// <summary>Отказ с заголовком (<see cref="Headed"/>).</summary>
    public static ActionResult RefusedHeaded(string plain) => new(false, plain, Reply: Headed(plain));

    /// <summary>Что увидит человек: текст без тегов, сущности — знаками.</summary>
    public static string Text(string html) => MessageRegistration.Unmarked(html);
}
