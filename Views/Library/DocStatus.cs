using Microsoft.Data.Sqlite;
using SmartBase.Database;
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
/// <param name="Loading">Лента собирается прямо сейчас и делает это дольше порога.</param>
/// <param name="ReadDone">Сколько документов прочитано в текущем прогоне.</param>
/// <param name="ReadTotal">Сколько было в очереди на момент старта прогона.</param>
/// <param name="Left">Сколько еще ждать по замеру; null — мерить пока не на чем.</param>
/// <param name="Query">Запрос, по которому построена лента; пусто — обычная лента.</param>
/// <param name="InText">Сколько найдено ВНУТРИ текста (пришли со сниппетом).</param>
/// <param name="ByTitle">Сколько найдено по названию записи и имени файла.</param>
/// <param name="Capped">Выдача уперлась в потолок.</param>
/// <param name="CacheOpen">Кеш текста открылся при старте; нет — поиск внутри недоступен вовсе.</param>
/// <param name="TypesNarrowed">Фильтр типов сужает ленту — это тоже причина пустой выдачи.</param>
/// <param name="TextFailed">Поиск внутри текста по этому запросу сорвался — нашлось только по названию.</param>
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
    bool      TypesNarrowed,
    bool      Loading = false,
    bool      TextFailed = false);

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

    /// <summary>Лента собирается — про это и говорим, пока не собралась.</summary>
    public const string LoadingText = "Собираю ленту…";

    public const string ActionRead     = "Прочитать документы";
    public const string ActionReadRest = "Прочитать остальные";
    public const string ActionRefresh  = "Обновить";
    public const string ActionStop     = "Остановить";

    /// <summary>Что сказать вслед за находками по названию, когда поиск внутри текста сорвался.</summary>
    public const string TextSearchFailed =
        "Поиск внутри текста не сработал — подробности в разделе «Диагностика»";

    public static DocStatusLine Compose(DocStatusInput s)
    {
        // 1. Идет чтение — оно важнее всего остального: человек только что его запустил.
        if (s.Reading) return Reading(s);

        // 2. Лента еще собирается. Ниже чтения намеренно: у чтения есть и остаток времени, и
        //    кнопка «Остановить», а тут сказать нечего, кроме «подождите». Итоги поиска при
        //    этом показывать НЕЛЬЗЯ — они от прошлого запроса, и человек прочел бы их как
        //    ответ на новый.
        if (s.Loading) return new DocStatusLine(LoadingText, "", DocStatusAction.None);

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
                $"{NotRead(c.NotRead)} — искать можно только по названиям",
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

        // Поиск внутри текста сорвался — это важнее и потолка, и пустой выдачи: все остальное
        // подвал говорит о ПОЛНОМ ответе, а этот ответ неполон так, что уточнением запроса его
        // не поправить. Без этой ветки испорченный кеш выглядел как «ничего не найдено».
        if (s.TextFailed)
            return new DocStatusLine(
                (found == 0 ? "Ничего не найдено по названию. "
                            : $"Найдено {found}{(s.Capped ? "+" : "")} по названию. ") +
                TextSearchFailed,
                "", DocStatusAction.None);

        // Уперлись в потолок: видно, что список обрезан, а не кончился.
        if (s.Capped)
            return new DocStatusLine(
                $"Найдено {found}+ — показаны первые {found}, " +
                "уточните запрос или сузьте тип документа",
                "", DocStatusAction.None);

        if (found == 0) return NothingFound(s);

        // Нашлось только по названиям, а внутрь непрочитанных не ходили — ради этого случая подвал
        // и заведен. Без него человек решит, что поиск внутри документов не работает, и будет
        // прав ровно наполовину. Слова — те же, что у пустой выдачи: прочитанное поиск
        // просмотрел, и «внутри текста не искал» было бы неправдой, стоит прочитать хоть один.
        if (s.InText == 0 && s.Census.NotRead > 0)
            return new DocStatusLine(
                $"Найдено {found} по названию. {NotReadSkipped(s.Census.NotRead)}",
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
                $"Ничего не найдено. {NotReadSkipped(c.NotRead)}",
                ActionRead, DocStatusAction.Read);

        if (s.TypesNarrowed)
            return new DocStatusLine(
                "Ничего не найдено. Фильтр по типу документа сужает выдачу",
                "", DocStatusAction.None);

        if (c.NoText > 0)
            return new DocStatusLine(
                $"Ничего не найдено. {Plural.Count(c.NoText, "документ", "документа", "документов")} — " +
                (c.NoText == 1 ? "скан без текстового слоя, внутри него" : "сканы без текстового слоя, внутри них") +
                " поиск не ищет",
                "", DocStatusAction.None);

        if (c.Unsupported > 0)
            return new DocStatusLine(
                $"Ничего не найдено. У {c.Unsupported} " +
                $"{Plural.Of(c.Unsupported, "документа", "документов", "документов")} " +
                "формат без чтения текста — " +
                (c.Unsupported == 1 ? "он ищется" : "они ищутся") + " только по названию",
                "", DocStatusAction.None);

        return new DocStatusLine("Ничего не найдено", "", DocStatusAction.None);
    }

    // ── Согласование с числом ────────────────────────────────────────────────
    //
    // Сказуемое при числе согласуется по ФОРМЕ числа: «1 документ не прочитан», «21 документ не
    // прочитан», «2 документа не прочитаны». Местоимение, которое на это число ссылается, — по
    // СМЫСЛУ: документов 21, значит, «внутри них»; «него» — только при ровно одном. Так же и
    // «скан» со «сканами». Пока подвал склонял одно существительное, выходило «1 документ не
    // прочитаны — внутри них».

    private static string NotRead(int n) =>
        $"{Plural.Count(n, "документ", "документа", "документов")} " +
        Plural.Of(n, "не прочитан", "не прочитаны", "не прочитаны");

    // Непрочитанные документы, внутрь которых поиск не заглядывал: и при пустой выдаче, и при
    // находках только по названию.
    private static string NotReadSkipped(int n) =>
        $"{NotRead(n)} — внутри {(n == 1 ? "него" : "них")} поиск не искал";

    // ── Сорвавшееся чтение ───────────────────────────────────────────────────
    //
    // Это не подвал, а значок фоновых работ и карточка: чтение идет и тогда, когда человек давно
    // ушел из раздела. Но слова о чтении живут здесь, рядом с остальными, — как и Humanize.

    /// <summary>
    /// Строка сорвавшегося чтения в панели фоновых работ: «Сорвалось на 120 из 2000: на диске
    /// нет места». Короткая — панель узкая, а что делать дальше, говорит карточка.
    /// </summary>
    public static string ReadFailedDetail(DocIndexRunEnd end) =>
        $"Сорвалось на {end.Done} из {end.Total}: {Explain(end.Error).Reason}";

    /// <summary>
    /// Карточка о сорвавшемся чтении: причина и что с ней делать. Причина названа там, где с ней
    /// есть что сделать: место на диске освобождают, испорченный файл кеша пересоздает «Очистить».
    /// Остальное отсылает в «Диагностику» — исключение записано там целиком.
    /// </summary>
    public static string ReadFailedNotice(DocIndexRunEnd end)
    {
        var (reason, advice) = Explain(end.Error);
        return $"Чтение документов сорвалось: {reason}. {advice}";
    }

    /// <summary>
    /// Карточка о запуске чтения: человек нажал «Прочитать» и тут же может уйти в другой раздел —
    /// он должен знать, где за этим следить. Если прогон уже шел, пути добавлены к нему
    /// (<paramref name="added"/> — сколько из <paramref name="pending"/> оказались новыми).
    /// </summary>
    public static string ReadStartedNotice(bool running, int pending, int added) =>
        !running
            ? $"Читаю {Plural.Count(pending, "документ", "документа", "документов")}. " +
              "Можно работать дальше — ход виден в панели разделов слева."
            : added > 0
                ? $"Добавлено к чтению: {Plural.Count(added, "документ", "документа", "документов")}. " +
                  $"Читаю {(added == 1 ? "его" : "их")} тем же прогоном."
                : pending == 1
                    ? "Этот документ уже стоит в очереди чтения."
                    : "Эти документы уже стоят в очереди чтения.";

    /// <summary>
    /// Карточка о конце удачного чтения: человек мог уйти из раздела и не увидеть, что оно
    /// кончилось. <paramref name="neverSearchable"/> — сколько документов поиск внутри так и не
    /// увидит (сканы без текстового слоя и форматы без чтения текста).
    /// </summary>
    public static string ReadDoneNotice(int done, int neverSearchable) =>
        $"Прочитано документов: {done}. " +
        (neverSearchable > 0
            ? $"Внутри {neverSearchable} {Plural.Of(neverSearchable, "документа", "документов", "документов")} " +
              "поиск искать не будет: " +
              (neverSearchable == 1 ? "это скан без текстового слоя или формат без чтения текста."
                                    : "это сканы без текстового слоя и форматы без чтения текста.")
            : done == 1 ? "Поиск теперь ищет внутри него." : "Поиск теперь ищет внутри них.");

    private const string SeeDiagnostics = "Подробности — в разделе «Диагностика».";

    private static (string Reason, string Advice) Explain(Exception? error) => error switch
    {
        SqliteException { SqliteErrorCode: 13 } =>   // SQLITE_FULL
            ("на диске нет места",
             "Прочитанное до сбоя сохранено — освободите место и дочитайте остальное."),
        not null when DbHealthMonitor.IsCorruptionError(error) =>
            ("файл кеша поврежден",
             "Кнопка «Очистить» в настройках библиотеки документов создаст его заново."),
        SqliteException => ("кеш документов не принимает запись", SeeDiagnostics),
        _               => ("сбой в программе", SeeDiagnostics),
    };
}
