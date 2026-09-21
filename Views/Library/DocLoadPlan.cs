namespace SmartBase.Views.Library;

/// <summary>
/// Правила «читать или не читать» раздела «Библиотека документов» — чистая логика без Avalonia
/// и без базы, ради тестов (<c>DocLoadPlanTests</c>). Зеркало <c>TaskLoadPlan</c>.
/// </summary>
public static class DocLoadPlan
{
    /// <summary>
    /// Нужно ли читать документы целиком в память. Раздел меньше крышки живет в памяти (там
    /// дешевле сортировка и выделение), больше — листается страницами.
    /// </summary>
    public static bool NeedsDocRead(int sectionTotal, int cap) => sectionTotal <= cap;

    /// <summary>
    /// Совпадают ли наборы активных коллекций: тот же набор — перечитывать нечего. Сравнение
    /// по содержимому, а не по ссылке: сайдбар отдает новый словарь на каждое событие, и
    /// сравнение ссылок сделало бы любое касание галочки полной перезагрузкой раздела.
    /// null у обоих — «все коллекции», это тоже совпадение.
    /// </summary>
    public static bool SameActiveIds(Dictionary<int, HashSet<int>>? a, Dictionary<int, HashSet<int>>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;

        foreach (var (db, ids) in a)
        {
            if (!b.TryGetValue(db, out var other)) return false;
            if (ids.Count != other.Count) return false;
            if (!ids.SetEquals(other)) return false;
        }
        return true;
    }

    /// <summary>
    /// Совпадают ли наборы выбранных типов документа. null — фильтр снят; пустой набор —
    /// «ничего не подходит». Это РАЗНЫЕ состояния, и путать их нельзя: снятый фильтр показывает
    /// всю ленту, пустой — ни одной карточки.
    /// </summary>
    public static bool SameTypes(IReadOnlySet<string>? a, IReadOnlySet<string>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        return a.SetEquals(b);
    }

    // ── Отметка «Собираю ленту…» ─────────────────────────────────────────────
    //
    // Правило вынесено сюда и проверяется тестами, потому что один раз уже сломалось и стоило
    // навсегда висящей отметки на пустом разделе. Суть: показ и гашение обязаны быть закрыты
    // ОДНИМ и тем же условием — «эта загрузка все еще актуальна».
    //
    // Отметка живет СТРОКОЙ В ПОДВАЛЕ, а не оверлеем на весь блок. Оверлей закрывал ленту
    // целиком, и на большой библиотеке в 400 мс не укладывалась ни одна загрузка: раздел
    // моргал пустым экраном со словом «Загрузка…» на каждое касание галочки в сайдбаре.
    // Лента при этом остается на месте — прежние карточки честнее пустоты.
    //
    // Раздел при открытии грузится несколько раз подряд (Initialize → ShowModule →
    // SetDatabases → SetActiveIds). Обогнанная загрузка просыпается из ожидания порога уже
    // ПОСЛЕ того, как ее сменщица все показала и оверлей погасила. Если показ не закрыт
    // проверкой поколения, она зажигает оверлей поверх готовой ленты — и гасить его уже некому:
    // гашение проверкой закрыто, а новых загрузок не будет.

    /// <summary>Зажигать ли отметку: загрузка не уложилась в порог, обновление не тихое и эта
    /// загрузка все еще актуальна.</summary>
    public static bool ShouldShowLoading(bool quiet, bool timedOut, int gen, int currentGen) =>
        !quiet && timedOut && IsCurrent(gen, currentGen);

    /// <summary>Гасить отметку вправе только актуальная загрузка — иначе она погасила бы ее
    /// под уже начавшейся следующей.</summary>
    public static bool ShouldHideLoading(int gen, int currentGen) => IsCurrent(gen, currentGen);

    /// <summary>Эта загрузка все еще последняя — ее результат и показываем.</summary>
    public static bool IsCurrent(int gen, int currentGen) => gen == currentGen;

    // ── Переключатель панели чтения ──────────────────────────────────────────
    //
    // Кнопку в навбаре двигает и человек, и сама программа: открыв панель по «Прочитать» в
    // карточке, она догоняет кнопку вызовом SetReaderOpen. Присваивание IsChecked поднимает
    // то же событие, что и живое нажатие, и оно возвращается обработчику — а тот открывал
    // панель заново. Выходило два Reset, две загрузки, два открытия PDFium и два разбора
    // книги на КАЖДОЕ первое открытие с карточки; на стороне режима «как в документе» два
    // прохода писали в общий список совпадений, и счетчик со стрелками разъезжался.
    //
    // Глушить эхо флагом «сейчас двигаю сам» нельзя: флаг держится на том, что событие придет
    // синхронно, а в Avalonia так не у всякого контрола — отложенный TextChanged в этом же
    // разделе уже стоил ровно такой ошибки. Поэтому решение принимается по СОСТОЯНИЮ панели:
    // просьба открыть уже открытую — это эхо, и делать по ней нечего.

    /// <summary>Что делать по нажатию (или по эху) переключателя панели чтения.</summary>
    public static DocReaderAction ReaderToggle(bool wantOpen, bool isOpen, bool hasLastRead)
    {
        if (wantOpen == isOpen) return DocReaderAction.Ignore;
        if (!wantOpen)          return DocReaderAction.Close;

        // Открывать пустую панель нечем: кнопка возвращает последний читанный документ, а
        // если его нет — откатывается обратно.
        return hasLastRead ? DocReaderAction.Open : DocReaderAction.Rollback;
    }
}

/// <summary>Решение <see cref="DocLoadPlan.ReaderToggle"/>.</summary>
public enum DocReaderAction
{
    /// <summary>Состояние уже такое, какого просят, — это эхо своего же вызова.</summary>
    Ignore,

    /// <summary>Открыть панель на последнем читанном документе.</summary>
    Open,

    /// <summary>Закрыть панель.</summary>
    Close,

    /// <summary>Открывать нечего — вернуть кнопку в прежнее положение.</summary>
    Rollback,
}
