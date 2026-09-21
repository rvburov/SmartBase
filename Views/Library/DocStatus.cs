using SmartBase.Database.DocIndex;
using SmartBase.Utils;

namespace SmartBase.Views.Library;

/// <summary>Что предлагает сделать ссылка справа в подвале блока поиска.</summary>
public enum DocStatusAction
{
    /// <summary>Ссылки нет — делать нечего.</summary>
    None = 0,
    /// <summary>Прочитать непрочитанные документы отмеченных коллекций.</summary>
    Read,
    /// <summary>Остановить идущее чтение.</summary>
    Stop,
}

/// <summary>Готовая строка подвала: что написано слева и что предложено справа.</summary>
public readonly record struct DocStatusLine(string Text, string ActionText, DocStatusAction Action)
{
    public static readonly DocStatusLine Empty = new("", "", DocStatusAction.None);
}

/// <summary>Из чего складывается строка подвала.</summary>
/// <param name="Census">Перепись документов по состоянию текста (из кеша).</param>
/// <param name="Reading">Идет ли чтение прямо сейчас.</param>
/// <param name="ReadDone">Сколько документов прочитано в текущем прогоне.</param>
/// <param name="ReadTotal">Сколько было в очереди на момент старта прогона.</param>
/// <param name="Left">Сколько еще ждать по замеру; null — мерить пока не на чем.</param>
/// <param name="Query">Запрос, по которому построена лента; пусто — обычная лента.</param>
/// <param name="InText">Сколько найдено ВНУТРИ текста (пришли со сниппетом).</param>
/// <param name="ByTitle">Сколько найдено по названию записи и имени файла.</param>
/// <param name="Capped">Выдача уперлась в потолок.</param>
/// <param name="CacheOpen">Кеш текста открылся при старте; нет — поиск внутри недоступен вовсе.</param>
/// <param name="TypesNarrowed">Фильтр типов сужает ленту — это тоже причина пустой выдачи.</param>
public readonly record struct DocStatusInput(
    DocCensus Census,
    bool      Reading,
    int       ReadDone,
    int       ReadTotal,
    TimeSpan? Left,
    string    Query,
    int       InText,
    int       ByTitle,
    bool      Capped,
    bool      CacheOpen,
    bool      TypesNarrowed);

/// <summary>
/// Строка подвала блока поиска — чистая логика без Avalonia, поэтому проверяется тестами.
///
/// Главное правило подвала: он говорит либо о том, <b>что мешает искать прямо сейчас</b>, либо о
/// результате только что сделанного запроса. Сертификату вида «у вас 1 284 документа, 1 106 с
/// текстом» здесь не место: такое читают один раз в первый день, а высоту над лентой платят
/// каждый день. Когда мешать нечему и запроса нет, остается одна короткая фраза о сути раздела.
///
/// Порядок веток — по срочности: идущее чтение важнее итогов поиска, пустая выдача важнее
/// обычного «найдено», а упор в потолок важнее разбивки по источникам.
/// </summary>
public static class DocStatus
{
    /// <summary>Что написано в подвале, когда мешать нечему и запроса нет.</summary>
    public const string IdleAllRead = "поиск ищет внутри текста";

    public const string ActionRead     = "Прочитать документы";
    public const string ActionReadRest = "Прочитать остальные";
    public const string ActionRefresh  = "Обновить";
    public const string ActionStop     = "Остановить";

    public static DocStatusLine Compose(DocStatusInput s)
    {
        // 1. Идет чтение — оно важнее всего остального: человек только что его запустил.
        if (s.Reading) return Reading(s);

        // 2. Кеш не открылся — поиска внутри документов нет вовсе, и чтение не поможет.
        if (!s.CacheOpen)
            return new DocStatusLine(
                "Поиск внутри документов недоступен: кеш документов не открылся",
                "", DocStatusAction.None);

        return s.Query.Length > 0 ? Search(s) : Idle(s);
    }

    // ── Покой ────────────────────────────────────────────────────────────────

    private static DocStatusLine Idle(DocStatusInput s)
    {
        var c = s.Census;
        if (c.Total == 0) return DocStatusLine.Empty;

        // Ничего не прочитано: это и есть состояние после первого захода в раздел. Программа
        // сама ничего не начинает — она сообщает и предлагает.
        if (c.Read == 0 && c.NotRead > 0)
            return new DocStatusLine(
                $"{Plural.Count(c.NotRead, "документ", "документа", "документов")} " +
                "не прочитаны — искать можно только по названиям",
                ActionRead, DocStatusAction.Read);

        // Прочитано не все — число живое, действие рядом.
        if (c.NotRead > 0)
            return new DocStatusLine(
                $"{Plural.Count(c.Total, "документ", "документа", "документов")} · " +
                $"текст прочитан у {c.Read}",
                ActionReadRest, DocStatusAction.Read);

        // Все, что можно было прочитать, прочитано. Единственное место, где блок говорит о себе.
        //
        // Кнопка при этом ОСТАЕТСЯ, и называется она по тому, что сделает. Документ живет своей
        // жизнью: его правят в родной программе, кладут поверх новую версию, подменяют скан
        // распознанным. Кеш такого не замечает сам — он сверяет время правки и размер только
        // когда его об этом просят. Пока кнопка пропадала вместе с последним непрочитанным
        // документом, перечитать измененный файл было нечем вовсе: оставалось выключить и
        // включить коллекцию.
        return new DocStatusLine(
            $"{Plural.Count(c.Total, "документ", "документа", "документов")} · {IdleAllRead}",
            ActionRefresh, DocStatusAction.Read);
    }

    // ── Чтение ───────────────────────────────────────────────────────────────

    private static DocStatusLine Reading(DocStatusInput s)
    {
        var text = $"Читаю документы: {s.ReadDone} из {s.ReadTotal}";
        if (s.Left is { } left) text += $" · осталось {Humanize(left)}";
        return new DocStatusLine(text, ActionStop, DocStatusAction.Stop);
    }

    /// <summary>
    /// «около 12 минут», «меньше минуты», «около 2 часов». Точность тут вредна: секунды на
    /// тысячах документов все равно врут, а дергающееся число раздражает сильнее, чем помогает.
    /// </summary>
    public static string Humanize(TimeSpan left)
    {
        if (left < TimeSpan.FromMinutes(1)) return "меньше минуты";
        if (left < TimeSpan.FromHours(1))
        {
            var m = Math.Max(1, (int)Math.Round(left.TotalMinutes));
            return $"около {m} {Plural.GenitiveMinutes(m)}";
        }
        var h = Math.Max(1, (int)Math.Round(left.TotalHours));
        return $"около {h} {Plural.GenitiveHours(h)}";
    }

    // ── Поиск ────────────────────────────────────────────────────────────────

    private static DocStatusLine Search(DocStatusInput s)
    {
        var found = s.InText + s.ByTitle;

        // Уперлись в потолок: видно, что список обрезан, а не кончился.
        if (s.Capped)
            return new DocStatusLine(
                $"Найдено {found}+ — показаны первые {found}, " +
                "уточните запрос или сузьте тип документа",
                "", DocStatusAction.None);

        if (found == 0) return NothingFound(s);

        // Нашлось только по названиям, а внутрь не ходили — ради этого случая подвал и заведен.
        // Без него человек решит, что поиск внутри документов не работает, и будет прав ровно
        // наполовину.
        if (s.InText == 0 && s.Census.NotRead > 0)
            return new DocStatusLine(
                $"Найдено {found} по названию. Внутри текста не искал — документы не прочитаны",
                ActionRead, DocStatusAction.Read);

        return new DocStatusLine(
            $"Найдено {found}: {s.InText} внутри текста, {s.ByTitle} по названию и имени файла",
            "", DocStatusAction.None);
    }

    // Пустая выдача называет ОДНУ причину — самую вероятную из тех, что верны прямо сейчас.
    // Перечислять все сразу в одну строку нельзя, а выбирать есть из чего, поэтому порядок
    // такой: сперва то, что человек может исправить одним действием.
    private static DocStatusLine NothingFound(DocStatusInput s)
    {
        var c = s.Census;

        if (c.NotRead > 0)
            return new DocStatusLine(
                $"Ничего не найдено. {Plural.Count(c.NotRead, "документ", "документа", "документов")} " +
                "не прочитаны — внутри них поиск не искал",
                ActionRead, DocStatusAction.Read);

        if (s.TypesNarrowed)
            return new DocStatusLine(
                "Ничего не найдено. Фильтр по типу документа сужает выдачу",
                "", DocStatusAction.None);

        if (c.NoText > 0)
            return new DocStatusLine(
                $"Ничего не найдено. {Plural.Count(c.NoText, "документ", "документа", "документов")} — " +
                "сканы без текстового слоя, внутри них поиск не ищет",
                "", DocStatusAction.None);

        if (c.Unsupported > 0)
            return new DocStatusLine(
                $"Ничего не найдено. У {c.Unsupported} " +
                $"{Plural.Of(c.Unsupported, "документа", "документов", "документов")} " +
                "формат без чтения текста — они ищутся только по названию",
                "", DocStatusAction.None);

        return new DocStatusLine("Ничего не найдено", "", DocStatusAction.None);
    }
}
