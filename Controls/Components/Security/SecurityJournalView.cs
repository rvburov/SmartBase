using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.Widgets;
using SmartBase.Database;
using static SmartBase.Controls.Widgets.SettingRows;

namespace SmartBase.Controls.Components.Security;

/// <summary>
/// Журнал безопасности (docs/ROADMAP-API.md, 10.10) — отдельный файл, который ведется для
/// проверки: только добавление, цепочка хэшей, ни тел запросов, ни значений полей.
///
/// <b>Почему он не рядом с рабочим журналом.</b> До шага 15 оба журнала жили на одной вкладке
/// «Настройки → Интеграции → Журнал» и переключались списком. Читают их, однако, разные люди:
/// рабочий — тот, у кого обмен встал, а этот — тот, кто проверяет, что уходило наружу. Вместе с
/// контуром безопасности журнал уехал в панель администратора, к своей аудитории; переключателя
/// «какой журнал» не стало — каждый журнал теперь лежит там, где его ищут.
///
/// Разница с рабочим журналом видна по тому, чего здесь НЕТ. Нет кнопки «Очистить»: журнал, о
/// котором ведется запись, чистить тому, за кем она ведется, нельзя. Нет тел запросов: их место
/// занял SHA-256, потому что журнал выгружают проверяющему целиком, и тело в нем стало бы второй
/// утечкой следом за первой. Нет счетчика расхода: потолки считаются не по этому файлу.
///
/// Зато есть то, чего нет у рабочего: <b>проверка цепочки</b>. Она первой строкой, и это не
/// украшение — журнал доказывает, что уходило именно согласованное, и первым делом надо знать,
/// можно ли ему верить.
/// </summary>
public sealed class SecurityJournalView : UserControl
{
    /// <summary>Сколько строк в странице. Больше двух сотен подряд все равно никто не читает.</summary>
    private const int Page = 200;

    private readonly StackPanel _rows = new() { Spacing = 0, Margin = new Thickness(16, 0, 16, 32) };

    private Workspace? _workspace;

    // Чем сужена выборка и сколько строк уже показано. Живет в пределах захода: это лупа, а не
    // режим работы, и запоминать ее в настройках значило бы вернуть человека к чужому взгляду.
    private SecurityEvent? _event;
    private bool           _blockedOnly;
    private int            _days = 30;
    private int            _shown;

    // Отбор по колонкам — «как в Excel»: колонка и выбранные в ее воронке значения.
    private readonly Dictionary<string, HashSet<string>> _picked = new(StringComparer.Ordinal);

    // Ключ строки с раскрытыми подробностями; 0 — ни одной.
    private long _open;

    // Итог проверки цепочки: 0 — цела, >0 — ключ первой разошедшейся строки, null — еще не
    // проверяли в этот заход. Проверка идет по всему файлу, и делать ее на каждую перерисовку
    // экрана значило бы перечитывать журнал за год ради обновления списка.
    private long? _chain;

    // Считать строки на большом журнале — секунды, а окон в это время еще нет: второй щелчок
    // по кнопке дал бы вторую выгрузку и второе окно подтверждения.
    private bool _exporting;

    public SecurityJournalView() =>
        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content                     = _rows,
        };

    /// <summary>Показать журнал — при каждом открытии экрана.</summary>
    public void Show(Workspace? workspace)
    {
        _workspace = workspace;
        Build();
    }

    /// <summary>
    /// Показать сразу заблокированные попытки за неделю — переход из контура безопасности, где
    /// их сосчитали. Переход без отбора заставил бы человека повторять руками то, что соседний
    /// экран уже знает, — и на второй странице он бы отбор забыл.
    /// </summary>
    public void ShowBlocked(Workspace? workspace)
    {
        _event       = null;
        _blockedOnly = true;
        _days        = 7;
        _shown       = 0;
        _open        = 0;
        _picked.Clear();

        Show(workspace);
    }

    // ── Сам журнал ────────────────────────────────────────────────────────────

    private void Build()
    {
        var rows = _rows.Children;
        rows.Clear();

        if (SecurityJournal.Current is not { } journal)
        {
            // Журнал не открылся при запуске. В отличие от рабочего, это не мелочь: без него
            // исходящего обмена нет вовсе — правило «не записалось — не отправляем» опирается на
            // то, что журнал есть.
            Add(rows, Row("Журнал безопасности не открылся",
                "Файл журнала не удалось открыть при запуске программы. Отправка данных наружу "
                + "при этом НЕ РАБОТАЕТ: обмен, о котором не осталось записи, не допускается. "
                + "Причина записана в разделе «Диагностика»."), last: true);
            return;
        }

        var filter = FilterNow();

        // Одна строка сверх показанного — так и узнаем, есть ли что дальше, не считая весь файл.
        var page = journal.Page(filter, take: Shown() + 1);
        var more = page.Count > Shown();

        Add(rows, ChainRow(journal));
        Add(rows, FilterRow());
        Add(rows, RowsBlock([.. page.Take(Shown())], more));
        Add(rows, FileRow(journal), last: true);
    }

    /// <summary>
    /// Сколько строк показываем: каждое «Показать еще» добавляет страницу. Читается всегда от
    /// свежего конца, а не «следующая страница после последней»: экран перестраивается целиком
    /// на каждое нажатие, и хранить накопленные страницы означало бы держать в памяти то, что и
    /// так лежит в файле рядом.
    /// </summary>
    private int Shown() => _shown > 0 ? _shown : Page;

    /// <summary>Что показываем: переключатели сверху плюс воронки в шапке таблицы.</summary>
    private SecurityFilter FilterNow()
    {
        var picked = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);

        foreach (var (column, values) in _picked)
            if (values.Count > 0) picked[column] = values;

        return new SecurityFilter(_event, _blockedOnly, Since(), picked);
    }

    /// <summary>Выбранное в воронке колонки; множество живет, пока открыт экран.</summary>
    private HashSet<string> Picked(string column)
    {
        if (!_picked.TryGetValue(column, out var values))
            _picked[column] = values = new HashSet<string>(StringComparer.Ordinal);

        return values;
    }

    /// <summary>Заголовок колонки с воронкой отбора.</summary>
    private Control Funnel(string column, string title) =>
        ColumnFilter.Head(title, () => Values(column), Picked(column), () =>
        {
            _shown = 0;
            _open  = 0;
            Build();
        });

    /// <summary>
    /// Какие значения бывают в колонке. Числа переводятся в слова здесь: в журнале событие лежит
    /// числом, а проверяющему нужно «Отправка наружу».
    /// </summary>
    private IReadOnlyList<ColumnFilterValue> Values(string column)
    {
        if (SecurityJournal.Current is not { } journal) return [];

        var values = journal.Values(column, FilterNow());
        var made   = new List<ColumnFilterValue>(values.Count);

        foreach (var (value, count) in values)
            made.Add(new ColumnFilterValue(value, ValueText(column, value), count));

        return made;
    }

    private static string ValueText(string column, string value) => column switch
    {
        "Event" => int.TryParse(value, out var kind)
            ? SecurityJournal.Name((SecurityEvent)kind)
            : value,

        "Allowed" => value == "1" ? "разрешено" : "блокировка",

        _ => value.Length > 0 ? value : "(пусто)",
    };

    private DateTimeOffset? Since() =>
        _days > 0 ? DateTimeOffset.Now.AddDays(-_days) : null;

    // ── Цепочка ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Цела ли цепочка. Проверка по кнопке, а не на каждый показ: она проходит файл целиком, а
    /// файл этот копится годами.
    ///
    /// Ответ написан словами в обе стороны. «Цела» — это не «все хорошо»: цепочка ловит правку
    /// строки и вырезанную строку, но не удаление файла целиком, и делать вид, что хэши
    /// закрывают и это, нельзя.
    /// </summary>
    private Border ChainRow(SecurityJournal journal)
    {
        var block = new StackPanel { Spacing = 6 };

        if (_chain is { } first)
        {
            var verdict = Note(first == 0
                ? $"Цепочка цела: все {journal.Count():N0} строк сходятся."
                : $"ЦЕПОЧКА РАЗОШЛАСЬ на строке №{first}. Файл правили мимо программы: строку "
                + "поправили или вырезали. Сообщите ответственному за интеграции и сверьтесь с "
                + "копией журнала в папке аудита организации.");

            if (first != 0) verdict.Foreground = Ink("BrushRed", "#FF3B30");

            block.Children.Add(DataBox(new[] { verdict }));
        }

        block.Children.Add(ButtonLine(RowButton("Проверить цепочку", () =>
        {
            _chain = journal.Verify();
            Build();
        })));

        return Row("Проверка целости",
            "Каждая строка включает хэш предыдущей, поэтому поправить или вырезать строку задним "
            + "числом незаметно не выйдет. Чего проверка не ловит: файл, удаленный или "
            + "подмененный целиком, — от этого спасает не хэш, а копия в папке аудита организации.",
            block);
    }

    // ── Что показывать ────────────────────────────────────────────────────────

    private Border FilterRow()
    {
        var kinds = Picker(
            ["Все события", "Отправки", "Итоги отправок", "Чтения по выдаче", "Подтверждения",
             "Снятия подтверждения", "Понижения меток", "Ручные выгрузки", "Показы секретов",
             "Уборки журнала"],
            _event switch
            {
                null                    => 0,
                SecurityEvent.Send      => 1,
                SecurityEvent.Result    => 2,
                SecurityEvent.Read      => 3,
                SecurityEvent.Confirm   => 4,
                SecurityEvent.Revoke    => 5,
                SecurityEvent.Downgrade => 6,
                SecurityEvent.Export    => 7,
                SecurityEvent.Reveal    => 8,
                _                       => 9,
            });

        kinds.SelectionChanged += (_, _) =>
        {
            _event = kinds.SelectedIndex switch
            {
                1 => SecurityEvent.Send,
                2 => SecurityEvent.Result,
                3 => SecurityEvent.Read,
                4 => SecurityEvent.Confirm,
                5 => SecurityEvent.Revoke,
                6 => SecurityEvent.Downgrade,
                7 => SecurityEvent.Export,
                8 => SecurityEvent.Reveal,
                9 => SecurityEvent.Trim,
                _ => null,
            };

            Reset();
        };

        var period = Picker(["7 дней", "30 дней", "Год", "Все"], _days switch
        {
            7   => 0,
            30  => 1,
            365 => 2,
            _   => 3,
        }, width: 120);

        period.SelectionChanged += (_, _) =>
        {
            _days = period.SelectedIndex switch
            {
                0 => 7,
                1 => 30,
                2 => 365,
                _ => 0,
            };

            Reset();
        };

        var blocked = new CheckBox
        {
            Content           = "Только заблокированные",
            IsChecked         = _blockedOnly,
            VerticalAlignment = VerticalAlignment.Center,
        };

        blocked.IsCheckedChanged += (_, _) =>
        {
            _blockedOnly = blocked.IsChecked == true;
            Reset();
        };

        var export = RowButton("Выгрузить CSV", () => _ = ExportAsync());

        var line = ButtonLine(kinds, period, blocked, export);

        return Row("Что показывать",
            "Заблокированные попытки — то, ради чего журнал во многом и ведется: «программа "
            + "попробовала, и ей не дали» это и есть доказательство, что рамки работают. "
            + "Выгрузка отдает ровно то, что отобрано здесь, — целиком, а не первые показанные "
            + "строки.",
            line);

        void Reset()
        {
            _open  = 0;
            _shown = 0;   // сужение начинает чтение заново: страницы были от другой выборки
            Build();
        }
    }

    // ── Выгрузка ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Выгрузить отобранное в CSV — то, чем кончается почти всякая проверка.
    ///
    /// Выгружается ВСЕ отобранное, а не показанные две сотни строк: человек, нажавший
    /// «Выгрузить», просит журнал за период, а не картинку экрана. Строки читаются лениво,
    /// страницами, поэтому размер файла ограничивает только диск.
    /// </summary>
    private async Task ExportAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (SecurityJournal.Current is not { } journal) return;
        if (_exporting) return;

        // Снимок условий ДО ожидания: их могли поменять, пока считались строки.
        var filter = FilterNow();

        _exporting = true;
        try
        {
            await CsvExportRunner.Run(owner, new CsvExportRunner.Job(
                Title:           "Выгрузка журнала безопасности",
                What:            "Журнал безопасности",
                TotalRows:       journal.Count(filter),
                Filtered:        filter.Narrow,
                DefaultFileName: CsvExportRunner.FileName("Журнал безопасности", DateTime.Now),
                Headers:         SecurityJournal.CsvHeaders,
                Rows:            ct => journal.Stream(filter)
                                              .TakeWhile(_ => !ct.IsCancellationRequested)
                                              .Select(SecurityJournal.CsvRow)));
        }
        finally { _exporting = false; }
    }

    // ── Список ────────────────────────────────────────────────────────────────

    private Border RowsBlock(IReadOnlyList<SecurityEntry> page, bool more)
    {
        var list = new StackPanel { Spacing = 0 };

        if (page.Count == 0)
            return Row("События", "", DataBox(
                FilterNow().Narrow
                    ? "Под выбранные условия не подошло ни одной строки."
                    : "Событий безопасности еще не было: наружу ничего не уходило и направлений "
                    + "не подтверждали."));

        list.Children.Add(HeaderLine());

        foreach (var entry in page)
            list.Children.Add(Line(entry));

        if (more)
        {
            var next = ButtonLine(RowButton("Показать еще", () =>
            {
                _shown = Shown() + Page;
                Build();
            }));

            next.Margin = new Thickness(0, 10, 0, -6);
            list.Children.Add(next);
        }

        return Row("События",
            "Строка на каждое событие, свежие сверху. Нажмите на строку — под ней раскроются "
            + "адрес, поля и объемы.",
            list);
    }

    /// <summary>Ширины колонок общие у шапки и строк — иначе таблица «поедет».</summary>
    private static ColumnDefinitions Columns() => new("104,8,*,8,96,8,74");

    /// <summary>
    /// Шапка таблицы. У колонок, по которым есть что отбирать, — воронка с отбором «как в
    /// Excel»; отбор уходит в SQL, а не отсеивает показанную пачку: журнал никто не чистит, он
    /// копится годами, и одно заблокированное событие полугодовой давности иначе не найти.
    /// </summary>
    private Control HeaderLine()
    {
        var grid = new Grid { ColumnDefinitions = Columns() };

        Put(grid, 0, Head("Время"));
        Put(grid, 2, Funnel("Event",   "Что произошло"));
        Put(grid, 4, Funnel("Who",     "Кто"));
        Put(grid, 6, Funnel("Allowed", "Решение"));

        return TableHead(grid);
    }

    private Control Line(SecurityEntry entry)
    {
        var open = _open == entry.Id;

        var grid = new Grid { ColumnDefinitions = Columns() };

        Put(grid, 0, Cell(Moment(entry), secondary: true));
        Put(grid, 2, Cell(EventLine(entry)));
        Put(grid, 4, Cell(entry.Who.Length > 0 ? entry.Who : "—", secondary: true));

        var verdict = Cell(entry.Allowed ? "разрешено" : "блокировка");
        verdict.Foreground = Ink(entry.Allowed ? "BrushGreen" : "BrushRed",
                                 entry.Allowed ? "#34C759" : "#FF3B30");
        Put(grid, 6, verdict);

        return TableRow(grid, open ? Details(entry) : null, open, () =>
        {
            _open = open ? 0 : entry.Id;
            Build();
        });
    }

    /// <summary>
    /// Подробности строки: то, чему не нашлось места в колонках. Лежат в ячейке с данными — как
    /// подробности строки рабочего журнала.
    /// </summary>
    private Control Details(SecurityEntry entry)
    {
        var list = new StackPanel { Spacing = 4 };

        list.Children.Add(Note(entry.Explanation.Length > 0 ? entry.Explanation : "Без объяснения."));

        // У выгрузки в «адресе» лежит путь к файлу, а у отправки — сетевой адрес. Слово должно
        // называть то, что под ним: проверяющий читает эти строки подряд и не должен гадать.
        if (entry.Address.Length > 0)
            list.Children.Add(Note((entry.Event == SecurityEvent.Export ? "Файл: " : "Адрес: ")
                                   + entry.Address));

        if (entry.Scenario.Length > 0)
            list.Children.Add(Note("Сценарий: " + entry.Scenario));
        else if (entry.Event == SecurityEvent.Send)
            list.Children.Add(Note("Сценария нет: запуск сделан руками."));

        if (entry.Confirmed.Length > 0)
            list.Children.Add(Note("Направление подтвердил: " + entry.Confirmed));

        // Поля и объемы есть не у всякого события: у подтверждения направления нет ни того, ни
        // другого, и «поля: —» рядом с ним читалось бы как потеря, а не как «неприменимо».
        if (entry.Fields.Length > 0) list.Children.Add(Note("Поля: " + entry.Fields));

        if (entry.Rows > 0 || entry.Bytes > 0)
            list.Children.Add(Note($"Записей: {entry.Rows:N0} · объем: {Size(entry.Bytes)}"));

        if (entry.BodyHash.Length > 0)
            list.Children.Add(Note((entry.Event == SecurityEvent.Export
                                        ? "Отпечаток файла: " : "Отпечаток тела: ")
                                   + entry.BodyHash));

        list.Children.Add(Note("База данных: " + DatabaseName(entry.DbId)));

        var copy = RowButton("Скопировать", () =>
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(Plain(entry)));

        return new StackPanel
        {
            Spacing  = 6,
            Children = { DataBox(list), ButtonLine(copy) },
        };
    }

    /// <summary>
    /// Строка текстом — чтобы переслать. Секрета в ней нет: адрес лежит в журнале уже
    /// замаскированным, а от тела остался один отпечаток.
    /// </summary>
    private string Plain(SecurityEntry entry) =>
        $"{Full(entry)} · {SecurityJournal.Name(entry.Event)} · "
        + $"{(entry.Allowed ? "разрешено" : "заблокировано")}\n"
        + $"кто: {entry.Who}"
        + (entry.Scenario.Length > 0  ? $" · сценарий: {entry.Scenario}"     : "")
        + (entry.Confirmed.Length > 0 ? $" · подтвердил: {entry.Confirmed}"  : "") + "\n"
        + (entry.Address.Length > 0   ? entry.Address + "\n"                 : "")
        + (entry.Fields.Length > 0    ? $"поля: {entry.Fields}\n"            : "")
        + $"записей: {entry.Rows:N0} · объем: {Size(entry.Bytes)}\n"
        + (entry.BodyHash.Length > 0
              ? (entry.Event == SecurityEvent.Export ? "отпечаток файла: " : "отпечаток тела: ")
                + entry.BodyHash + "\n"
              : "")
        + entry.Explanation;

    // ── Файл ──────────────────────────────────────────────────────────────────

    private static Border FileRow(SecurityJournal journal)
    {
        var block = DataBox(new[]
        {
            Note($"Файл: {journal.Path}"),
            Note($"Всего строк: {journal.Count():N0}."),
        });

        return Row("Где это лежит",
            "Журнал общий на все базы данных и лежит рядом с настройками программы. Кнопки "
            + "«Очистить» у него нет и не будет: журнал, который чистит тот, за кем он ведется, "
            + "не журнал. Ротация придет вместе с правилами политики.",
            block);
    }

    // ── Мелочи оформления ─────────────────────────────────────────────────────

    /// <summary>Имя базы по ключу; закрытая база названа ключом — история переживает базы.</summary>
    private string DatabaseName(int dbId)
    {
        if (dbId <= 0) return "не задета";

        var open = _workspace?.Mounted ?? [];

        return open.FirstOrDefault(d => d.DbId == dbId) is { } found
            ? found.Name
            : $"№{dbId} (сейчас не открыта)";
    }

    /// <summary>Событие колонкой — вместе с тем, чего оно касалось.</summary>
    private static string EventLine(SecurityEntry entry) => entry.Event switch
    {
        SecurityEvent.Send => entry.Address.Length > 0 ? "Отправка: " + entry.Address : "Отправка",
        _                  => SecurityJournal.Name(entry.Event),
    };

    /// <summary>Время строки; испорченный правкой файла момент показывается как есть.</summary>
    private static string Moment(SecurityEntry entry) =>
        entry.Moment is { } moment ? moment.ToLocalTime().ToString("dd.MM HH:mm:ss") : entry.At;

    /// <summary>То же время с годом — для текста, который уносят из программы.</summary>
    private static string Full(SecurityEntry entry) =>
        entry.Moment is { } moment
            ? moment.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")
            : entry.At;
}
