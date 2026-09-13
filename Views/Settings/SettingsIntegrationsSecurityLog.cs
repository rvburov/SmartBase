using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using SmartBase.Database;
using static SmartBase.Controls.Widgets.SettingRows;

namespace SmartBase.Views.Settings;

/// <summary>
/// Журнал безопасности на вкладке «Журнал» раздела «Интеграции» (partial SettingsView,
/// docs/ROADMAP-API.md, 10.10).
///
/// Второй журнал на той же вкладке, а не отдельным разделом: смотрят их подряд и по одному
/// поводу — «что уходило наружу». Разные файлы и разные колонки, поэтому переключатель, а не
/// общий список: свести их в одну таблицу значило бы придумать колонки, которых нет ни у того,
/// ни у другого.
///
/// Разница с журналом запусков видна по тому, чего здесь НЕТ. Нет кнопки «Очистить»: журнал, о
/// котором ведется запись, чистить тому, за кем она ведется, нельзя. Нет тел запросов: их место
/// занял SHA-256, потому что журнал выгружают проверяющему целиком, и тело в нем стало бы второй
/// утечкой следом за первой. Нет счетчика расхода: потолки считаются не по этому файлу.
///
/// Зато есть то, чего нет у рабочего: <b>проверка цепочки</b>. Она первой строкой, и это не
/// украшение — журнал доказывает, что уходило именно согласованное, и первым делом надо знать,
/// можно ли ему верить.
/// </summary>
public partial class SettingsView
{
    /// <summary>Сколько строк в странице. Больше двух сотен подряд все равно никто не читает.</summary>
    private const int SecurityPage = 200;

    // Показан журнал безопасности, а не запусков. Живет в пределах захода в программу: это
    // взгляд на одно и то же место с двух сторон, а не отдельный раздел настроек.
    private bool _securityLog;

    // Чем сужена выборка и сколько строк уже показано. Живет в пределах захода: это лупа, а не
    // режим работы, и запоминать ее в настройках значило бы вернуть человека к чужому взгляду.
    private SecurityEvent? _securityEvent;
    private bool           _securityBlockedOnly;
    private int            _securityDays = 30;
    private int            _securityShown;

    // Отбор по колонкам — «как в Excel»: колонка и выбранные в ее воронке значения. Живет в
    // пределах захода в программу, как и остальные сужения этого экрана.
    private readonly Dictionary<string, HashSet<string>> _securityPicked = new(StringComparer.Ordinal);

    // Ключ строки с раскрытыми подробностями; 0 — ни одной.
    private long _securityOpen;

    // Итог проверки цепочки: 0 — цела, >0 — ключ первой разошедшейся строки, null — еще не
    // проверяли в этот заход. Проверка идет по всему файлу, и делать ее на каждую перерисовку
    // вкладки значило бы перечитывать журнал за год ради обновления списка.
    private long? _securityChain;

    /// <summary>Переключатель журналов — первой строкой вкладки, до всего остального.</summary>
    private Border SecuritySwitchRow()
    {
        var choice = Picker(["Запуски", "Безопасность"], _securityLog ? 1 : 0);

        // Обработчик вешается ПОСЛЕ выставления значения: иначе первый же показ вкладки
        // перестроил бы сам себя.
        choice.SelectionChanged += (_, _) =>
        {
            _securityLog  = choice.SelectedIndex == 1;
            _journalOpen  = 0;
            _securityOpen = 0;

            BuildIntegrationsJournal();
        };

        return SideRow("Какой журнал",
            "«Запуски» — рабочий журнал обмена: его чистят, по нему считаются потолки. "
            + "«Безопасность» — отдельный файл для проверки: только добавление, цепочка хэшей, "
            + "ни тел, ни значений полей.",
            choice);
    }

    // ── Сам журнал ────────────────────────────────────────────────────────────

    private void BuildSecurityLog(Avalonia.Controls.Controls rows)
    {
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

        var filter = SecurityFilterNow();

        // Одна строка сверх показанного — так и узнаем, есть ли что дальше, не считая весь файл.
        var page = journal.Page(filter, take: Shown() + 1);
        var more = page.Count > Shown();

        Add(rows, ChainRow(journal));
        Add(rows, SecurityFilterRow());
        Add(rows, SecurityRowsBlock([.. page.Take(Shown())], more));
        Add(rows, SecurityFileRow(journal), last: true);
    }

    /// <summary>
    /// Сколько строк показываем: каждое «Показать еще» добавляет страницу. Читается всегда от
    /// свежего конца, а не «следующая страница после последней»: вкладка перестраивается
    /// целиком на каждое нажатие, и хранить накопленные страницы означало бы держать в памяти
    /// то, что и так лежит в файле рядом.
    /// </summary>
    private int Shown() => _securityShown > 0 ? _securityShown : SecurityPage;

    /// <summary>Что показываем: переключатели сверху плюс воронки в шапке таблицы.</summary>
    private SecurityFilter SecurityFilterNow()
    {
        var picked = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);

        foreach (var (column, values) in _securityPicked)
            if (values.Count > 0) picked[column] = values;

        return new SecurityFilter(_securityEvent, _securityBlockedOnly, Since(), picked);
    }

    /// <summary>Выбранное в воронке колонки; множество живет, пока открыт раздел.</summary>
    private HashSet<string> SecurityPicked(string column)
    {
        if (!_securityPicked.TryGetValue(column, out var values))
            _securityPicked[column] = values = new HashSet<string>(StringComparer.Ordinal);

        return values;
    }

    /// <summary>Заголовок колонки с воронкой отбора.</summary>
    private Control SecurityFunnel(string column, string title) =>
        Controls.Widgets.ColumnFilter.Head(title, () => SecurityValues(column),
                                            SecurityPicked(column),
                                            () =>
                                            {
                                                _securityShown = 0;
                                                _securityOpen  = 0;
                                                BuildIntegrationsJournal();
                                            });

    /// <summary>
    /// Какие значения бывают в колонке. Числа переводятся в слова здесь: в журнале событие лежит
    /// числом, а проверяющему нужно «Отправка наружу».
    /// </summary>
    private IReadOnlyList<Controls.Widgets.ColumnFilterValue> SecurityValues(string column)
    {
        if (SecurityJournal.Current is not { } journal) return [];

        var values = journal.Values(column, SecurityFilterNow());
        var made   = new List<Controls.Widgets.ColumnFilterValue>(values.Count);

        foreach (var (value, count) in values)
            made.Add(new Controls.Widgets.ColumnFilterValue(value, SecurityValueText(column, value),
                                                            count));

        return made;
    }

    private static string SecurityValueText(string column, string value) => column switch
    {
        "Event" => int.TryParse(value, out var kind)
            ? SecurityJournal.Name((SecurityEvent)kind)
            : value,

        "Allowed" => value == "1" ? "разрешено" : "блокировка",

        _ => value.Length > 0 ? value : "(пусто)",
    };

    private DateTimeOffset? Since() =>
        _securityDays > 0 ? DateTimeOffset.Now.AddDays(-_securityDays) : null;

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

        if (_securityChain is { } first)
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
            _securityChain = journal.Verify();
            BuildIntegrationsJournal();
        })));

        return Row("Проверка целости",
            "Каждая строка включает хэш предыдущей, поэтому поправить или вырезать строку задним "
            + "числом незаметно не выйдет. Чего проверка не ловит: файл, удаленный или "
            + "подмененный целиком, — от этого спасает не хэш, а копия в папке аудита организации.",
            block);
    }

    // ── Что показывать ────────────────────────────────────────────────────────

    private Border SecurityFilterRow()
    {
        var kinds = Picker(
            ["Все события", "Отправки", "Чтения по выдаче", "Подтверждения",
             "Снятия подтверждения", "Понижения меток", "Ручные выгрузки"],
            _securityEvent switch
            {
                null                    => 0,
                SecurityEvent.Send      => 1,
                SecurityEvent.Read      => 2,
                SecurityEvent.Confirm   => 3,
                SecurityEvent.Revoke    => 4,
                SecurityEvent.Downgrade => 5,
                _                       => 6,
            });

        kinds.SelectionChanged += (_, _) =>
        {
            _securityEvent = kinds.SelectedIndex switch
            {
                1 => SecurityEvent.Send,
                2 => SecurityEvent.Read,
                3 => SecurityEvent.Confirm,
                4 => SecurityEvent.Revoke,
                5 => SecurityEvent.Downgrade,
                6 => SecurityEvent.Export,
                _ => null,
            };

            Reset();
        };

        var period = Picker(["7 дней", "30 дней", "Год", "Все"], _securityDays switch
        {
            7   => 0,
            30  => 1,
            365 => 2,
            _   => 3,
        }, width: 120);

        period.SelectionChanged += (_, _) =>
        {
            _securityDays = period.SelectedIndex switch
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
            IsChecked         = _securityBlockedOnly,
            VerticalAlignment = VerticalAlignment.Center,
        };

        blocked.IsCheckedChanged += (_, _) =>
        {
            _securityBlockedOnly = blocked.IsChecked == true;
            Reset();
        };

        var export = RowButton("Выгрузить CSV", () => _ = ExportSecurityLogAsync());

        var line = ButtonLine(kinds, period, blocked, export);

        return Row("Что показывать",
            "Заблокированные попытки — то, ради чего журнал во многом и ведется: «программа "
            + "попробовала, и ей не дали» это и есть доказательство, что рамки работают. "
            + "Выгрузка отдает ровно то, что отобрано здесь, — целиком, а не первые показанные "
            + "строки.",
            line);

        void Reset()
        {
            _securityOpen  = 0;
            _securityShown = 0;   // сужение начинает чтение заново: страницы были от другой выборки
            BuildIntegrationsJournal();
        }
    }

    // ── Выгрузка ──────────────────────────────────────────────────────────────

    // Считать строки на большом журнале — секунды, а окон в это время еще нет: второй щелчок
    // по кнопке дал бы вторую выгрузку и второе окно подтверждения.
    private bool _securityExporting;

    /// <summary>
    /// Выгрузить отобранное в CSV — то, чем кончается почти всякая проверка.
    ///
    /// Выгружается ВСЕ отобранное, а не показанные две сотни строк: человек, нажавший
    /// «Выгрузить», просит журнал за период, а не картинку экрана. Строки читаются лениво,
    /// страницами, поэтому размер файла ограничивает только диск.
    /// </summary>
    private async Task ExportSecurityLogAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (SecurityJournal.Current is not { } journal) return;
        if (_securityExporting) return;

        // Снимок условий ДО ожидания: их могли поменять, пока считались строки.
        var filter = SecurityFilterNow();
        var narrow = _securityEvent is not null || _securityBlockedOnly || _securityDays > 0;

        _securityExporting = true;
        try
        {
            await Controls.Dialogs.CsvExportRunner.Run(owner, new Controls.Dialogs.CsvExportRunner.Job(
                Title:           "Выгрузка журнала безопасности",
                What:            "Журнал безопасности",
                TotalRows:       journal.Count(filter),
                Filtered:        narrow,
                DefaultFileName: Controls.Dialogs.CsvExportRunner.FileName(
                                     "Журнал безопасности", DateTime.Now),
                Headers:         SecurityJournal.CsvHeaders,
                Rows:            ct => journal.Stream(filter)
                                              .TakeWhile(_ => !ct.IsCancellationRequested)
                                              .Select(SecurityJournal.CsvRow)));
        }
        finally { _securityExporting = false; }
    }

    // ── Список ────────────────────────────────────────────────────────────────

    private Border SecurityRowsBlock(IReadOnlyList<SecurityEntry> page, bool more)
    {
        var list = new StackPanel { Spacing = 0 };

        if (page.Count == 0)
            return Row("События", "", DataBox(
                _securityEvent is not null || _securityBlockedOnly || _securityDays > 0
                    ? "Под выбранные условия не подошло ни одной строки."
                    : "Событий безопасности еще не было: наружу ничего не уходило и направлений "
                    + "не подтверждали."));

        list.Children.Add(SecurityHeaderLine());

        foreach (var entry in page)
            list.Children.Add(SecurityLine(entry));

        if (more)
        {
            var next = ButtonLine(RowButton("Показать еще", () =>
            {
                _securityShown = Shown() + SecurityPage;
                BuildIntegrationsJournal();
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
    private static ColumnDefinitions SecurityColumns() => new("104,8,*,8,96,8,74");

    /// <summary>
    /// Шапка таблицы. У колонок, по которым есть что отбирать, — воронка с отбором «как в
    /// Excel»; отбор уходит в SQL, а не отсеивает показанную пачку: журнал никто не чистит, он
    /// копится годами, и одно заблокированное событие полугодовой давности иначе не найти.
    /// </summary>
    private Control SecurityHeaderLine()
    {
        var grid = new Grid { ColumnDefinitions = SecurityColumns() };

        Put(grid, 0, Head("Время"));
        Put(grid, 2, SecurityFunnel("Event",   "Что произошло"));
        Put(grid, 4, SecurityFunnel("Who",     "Кто"));
        Put(grid, 6, SecurityFunnel("Allowed", "Решение"));

        return TableHead(grid);
    }

    private Control SecurityLine(SecurityEntry entry)
    {
        var open = _securityOpen == entry.Id;

        var grid = new Grid { ColumnDefinitions = SecurityColumns() };

        Put(grid, 0, Cell(SecurityMoment(entry), secondary: true));
        Put(grid, 2, Cell(EventLine(entry)));
        Put(grid, 4, Cell(entry.Who.Length > 0 ? entry.Who : "—", secondary: true));

        var verdict = Cell(entry.Allowed ? "разрешено" : "блокировка");
        verdict.Foreground = Ink(entry.Allowed ? "BrushGreen" : "BrushRed",
                                 entry.Allowed ? "#34C759" : "#FF3B30");
        Put(grid, 6, verdict);

        return TableRow(grid, open ? SecurityDetails(entry) : null, open, () =>
        {
            _securityOpen = open ? 0 : entry.Id;
            BuildIntegrationsJournal();
        });
    }

    /// <summary>
    /// Подробности строки: то, чему не нашлось места в колонках. Лежат в ячейке с данными — как
    /// подробности строки журнала обмена на соседнем экране.
    /// </summary>
    private Control SecurityDetails(SecurityEntry entry)
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
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(SecurityPlain(entry)));

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
    private string SecurityPlain(SecurityEntry entry) =>
        $"{SecurityFull(entry)} · {SecurityJournal.Name(entry.Event)} · "
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

    private Border SecurityFileRow(SecurityJournal journal)
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

    /// <summary>Событие колонкой — вместе с тем, чего оно касалось.</summary>
    private static string EventLine(SecurityEntry entry) => entry.Event switch
    {
        SecurityEvent.Send => entry.Address.Length > 0 ? "Отправка: " + entry.Address : "Отправка",
        _                  => SecurityJournal.Name(entry.Event),
    };

    /// <summary>Время строки; испорченный правкой файла момент показывается как есть.</summary>
    private static string SecurityMoment(SecurityEntry entry) =>
        entry.Moment is { } moment ? moment.ToLocalTime().ToString("dd.MM HH:mm:ss") : entry.At;

    /// <summary>То же время с годом — для текста, который уносят из программы.</summary>
    private static string SecurityFull(SecurityEntry entry) =>
        entry.Moment is { } moment
            ? moment.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")
            : entry.At;
}
