using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Controls.Dialogs;
using SmartBase.Database;
using SmartBase.Integrations;

namespace SmartBase.Views.Settings;

/// <summary>
/// Вкладка «Журнал» раздела «Интеграции» (partial SettingsView): что уже происходило с обменом
/// и почему оно кончилось именно так (docs/ROADMAP-API.md, 10.10).
///
/// Экран отвечает на два вопроса, и оба задают в одном и том же порядке. Первый — «почему
/// отказали»: накопительные потолки считаются по этому же журналу, но до сих пор были не видны
/// никому, и отказ «слишком много запусков» выглядел поломкой. Поэтому расход идет ПЕРВОЙ
/// строкой, до списка. Второй — «почему записи не появились»: ответ лежит в неудачных строках,
/// и до них есть отдельный переключатель.
///
/// Строки только показываются. Править журнал нельзя ничем, кроме кнопки «Очистить»: рабочий
/// журнал пользователю чистить МОЖНО — в отличие от журнала безопасности, который заводится
/// отдельным файлом и не чистится никем (см. overview.md, «Два журнала»).
///
/// Таблицы с excel-фильтрами и подгрузкой страниц, как в «Диагностике», здесь нет намеренно:
/// та таблица написана под строки Serilog и не разбирается параметрами, а разложить ее на
/// общий вид — отдельная работа. Пока читается последняя пачка строк, и сколько именно —
/// сказано на экране, чтобы «показано все» не путали с «больше ничего не было».
/// </summary>
public partial class SettingsView
{
    /// <summary>Сколько строк поднимаем из файла за раз.</summary>
    private const int JournalRead = 500;

    /// <summary>Сколько показываем. Больше двух сотен строк подряд все равно никто не читает.</summary>
    private const int JournalShow = 200;

    // Что показываем: канал (null — все) и «только неудачные». Живет в пределах захода в
    // программу и намеренно НЕ запоминается в настройках: это лупа, а не режим работы.
    private RunChannel? _journalChannel;
    private bool        _journalFailedOnly;

    // Ключ строки, у которой раскрыты подробности; 0 — ни одной.
    private long _journalOpen;

    // Один курсор на все строки: каждый Cursor держит ручку системы, а строк на экране сотни.
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    /// <summary>
    /// Сценарий отработал сам. Зовет MainWindow из службы сценариев; экран перестраивается,
    /// только если журнал открыт, — иначе это работа впустую.
    /// </summary>
    public void OnScenarioRan()
    {
        if (_integrationsTab == "journal") BuildIntegrationsJournal();

        // И список сценариев: у отработавшего изменилось время последнего запуска, а у
        // соседей могла уйти или появиться строка «не сработает».
        if (_integrationsTab == "scenarios") BuildIntegrationsScenarios();
    }

    private void BuildIntegrationsJournal()
    {
        var rows = IntegrationsJournalTab.Children;
        rows.Clear();

        if (RunJournal.Current is not { } journal)
        {
            // Журнал не открылся при запуске — сам обмен при этом работает: падать из-за того,
            // что не удалось рассказать о работе, значило бы терять сделанную работу.
            Add(rows, Row("Журнал не открылся",
                "Файл журнала не удалось открыть при запуске программы. Обмен от этого не "
                + "останавливается, но история по нему не ведется, а причина записана в "
                + "разделе «Диагностика»."), last: true);
            return;
        }

        var all      = journal.Recent(JournalRead);
        var selected = all.Where(Fits).ToList();

        Add(rows, SpendRow(journal));
        Add(rows, FilterRow());
        Add(rows, RunsRow(selected));
        Add(rows, FileRow(journal, all.Count), last: true);
    }

    private bool Fits(RunEntry entry) =>
        (_journalChannel is not { } channel || entry.Channel == channel)
        && (!_journalFailedOnly || !entry.Ok);

    // ── Расход ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Сколько выбрано из накопительных потолков. Строка стоит первой не ради красоты: отказ
    /// «слишком много запусков» приходит от шлюза без объяснения, сколько именно осталось, и
    /// без этой строки его нечем проверить.
    /// </summary>
    private static Border SpendRow(RunJournal journal)
    {
        var policy = EgressSetup.Last.Set;
        var now    = DateTimeOffset.Now;
        var hour   = journal.Since(now - TimeSpan.FromHours(1));
        var day    = journal.Since(now - TimeSpan.FromDays(1));

        var list = new StackPanel { Spacing = 6 };

        // Битый журнал отдает «квота выбрана» — это его способ запереть контур, и на экране он
        // обязан выглядеть именно так, а не числом в миллиард запусков.
        if (hour.Runs == int.MaxValue || day.Bytes == long.MaxValue)
        {
            list.Children.Add(Note(
                "Счетчики не читаются, и обмен наружу поэтому запрещен: непрочитанный расход "
                + "считается выбранным до конца. Причина записана в «Диагностике»."));

            return Row("Расход", "", list);
        }

        if (policy.Any)
        {
            list.Children.Add(Note($"Запусков за час: {hour.Runs:N0} из {policy.MaxRunsPerHour:N0}."));
            list.Children.Add(Note($"Передано за сутки: {Size(day.Bytes)} из {Size(policy.MaxBytesPerDay)}."));
        }
        else
        {
            list.Children.Add(Note($"Запусков за час: {hour.Runs:N0}. Передано за сутки: {Size(day.Bytes)}."));
            list.Children.Add(Note(
                "Потолки не заданы: политики организации нет, и обмен наружу запрещен целиком — "
                + "см. вкладку «Безопасность»."));
        }

        return Row("Расход",
            "Окно скользящее: час и сутки считаются назад от текущего момента, а не с начала "
            + "часа и не с полуночи. В расход идет только то, что уходит НАРУЖУ, — файлы из "
            + "папки обмена идут внутрь и потолков не занимают.",
            list);
    }

    // ── Что показывать ────────────────────────────────────────────────────────

    private Border FilterRow()
    {
        var channel = new ComboBox
        {
            ItemsSource              = new List<string>
            {
                "Все каналы", "Запросы по сети", "Файлы из папки", "Сценарии",
            },
            SelectedIndex            = _journalChannel switch
            {
                null                 => 0,
                RunChannel.Request   => 1,
                RunChannel.Inbox     => 2,
                _                    => 3,
            },
            Height                   = 34,
            MinWidth                 = 190,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        // Обработчик вешается ПОСЛЕ выставления значения: иначе первый же показ вкладки
        // перестроил бы сам себя.
        channel.SelectionChanged += (_, _) =>
        {
            _journalChannel = channel.SelectedIndex switch
            {
                1 => RunChannel.Request,
                2 => RunChannel.Inbox,
                3 => RunChannel.Scenario,
                _ => null,
            };

            _journalOpen = 0;
            BuildIntegrationsJournal();
        };

        var failed = new CheckBox
        {
            Content           = "Только неудачные",
            IsChecked         = _journalFailedOnly,
            VerticalAlignment = VerticalAlignment.Center,
        };

        failed.IsCheckedChanged += (_, _) =>
        {
            _journalFailedOnly = failed.IsChecked == true;
            _journalOpen       = 0;
            BuildIntegrationsJournal();
        };

        var refresh = SmallButton("Обновить", () =>
        {
            _journalOpen = 0;
            BuildIntegrationsJournal();
        });

        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing     = 12,
            Children    = { channel, failed, refresh },
        };

        return Row("Что показывать",
            "Разбор «почему записи не появились» начинается с неудачных строк — их тут больше "
            + "всего и ищут.",
            line);
    }

    // ── Список запусков ───────────────────────────────────────────────────────

    private Border RunsRow(IReadOnlyList<RunEntry> runs)
    {
        var list = new StackPanel { Spacing = 0 };

        if (runs.Count == 0)
        {
            list.Children.Add(Note(_journalFailedOnly || _journalChannel is not null
                ? "Под выбранные условия не подошло ни одной строки."
                : "Обмена еще не было: ни одного запроса и ни одного принятого файла."));

            return Row("Запуски", "", list);
        }

        list.Children.Add(HeaderLine());

        foreach (var run in runs.Take(JournalShow))
            list.Children.Add(RunLine(run));

        if (runs.Count > JournalShow)
            list.Children.Add(new TextBlock
            {
                Classes      = { "desc" },
                Margin       = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Text         = $"Показаны последние {JournalShow:N0} строк из {runs.Count:N0} подошедших.",
            });

        return Row("Запуски",
            "Строка на каждый запуск, свежие сверху. Нажмите на строку — под ней раскроются "
            + "адрес, объяснение и числа.",
            list);
    }

    /// <summary>Ширины колонок общие у шапки и строк — иначе таблица «поедет».</summary>
    private static ColumnDefinitions RunColumns() => new("104,8,58,8,*,8,58,8,74");

    private static Control HeaderLine()
    {
        var grid = new Grid { ColumnDefinitions = RunColumns(), Margin = new Thickness(0, 0, 0, 4) };

        Put(grid, 0, Head("Время"));
        Put(grid, 2, Head("Канал"));
        Put(grid, 4, Head("Что запускали"));
        Put(grid, 6, Head("Строк"));
        Put(grid, 8, Head("Итог"));

        return grid;
    }

    /// <summary>
    /// Одна строка журнала. Подробности раскрываются под ней, а не в отдельной панели: панель
    /// деталей требует места, которого в узкой колонке настроек нет, а читают их по одной.
    /// </summary>
    private Control RunLine(RunEntry run)
    {
        var open = _journalOpen == run.Id;

        var grid = new Grid
        {
            ColumnDefinitions = RunColumns(),
            // Прозрачный фон нужен для попадания мышью: без него нажатие проходит мимо строки.
            Background        = Brushes.Transparent,
            Cursor            = HandCursor,
        };

        Put(grid, 0, Cell(Moment(run), secondary: true));
        Put(grid, 2, Cell(ChannelName(run.Channel), secondary: true));
        Put(grid, 4, Cell(run.Name.Length > 0 ? run.Name : "без названия"));
        Put(grid, 6, Cell(run.Rows > 0 ? $"{run.Rows:N0}" : "—", secondary: true));

        var verdict = Cell(run.Ok ? "удача" : "ошибка");
        verdict.Foreground = Ink(run.Ok ? "BrushGreen" : "BrushRed", run.Ok ? "#34C759" : "#FF3B30");
        Put(grid, 8, verdict);

        var block = new StackPanel { Spacing = 6, Children = { grid } };
        if (open) block.Children.Add(Details(run));

        // Нажатие ловит сама строка, а не рамка вокруг: иначе щелчок по раскрытым подробностям
        // закрывал бы их — ровно в тот момент, когда человек в них вчитывается.
        grid.PointerPressed += (_, _) =>
        {
            _journalOpen = open ? 0 : run.Id;
            BuildIntegrationsJournal();
        };

        return new Border
        {
            Padding         = new Thickness(0, 7, 0, 7),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush     = Ink("BrushSeparator", "#E5E5EA"),
            Child           = block,
        };
    }

    /// <summary>Подробности строки: то, чему не нашлось места в колонках.</summary>
    private Control Details(RunEntry run)
    {
        var list = new StackPanel
        {
            Spacing = 4,
            Margin  = new Thickness(0, 2, 0, 4),
        };

        list.Children.Add(Note(run.Explanation.Length > 0 ? run.Explanation : "Без объяснения."));

        // Куда ходили — только если было куда: у строки сценария адреса нет, и «не записан»
        // в ней читался бы как потеря, а не как «неприменимо».
        if (run.Address.Length > 0)
            list.Children.Add(Note(
                (run.Channel == RunChannel.Request ? "Адрес: " : "Папка: ") + run.Address));

        list.Children.Add(Note(Numbers(run)));

        list.Children.Add(Note("База данных: " + DatabaseName(run.DbId)));

        list.Children.Add(Note(run.Counts ? "В расход по потолкам засчитан." : NotCounted(run)));

        var copy = SmallButton("Скопировать", () =>
            _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(Plain(run)));

        list.Children.Add(new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin              = new Thickness(0, 4, 0, 0),
            Children            = { copy },
        });

        return list;
    }

    /// <summary>
    /// Строка текстом — чтобы переслать той стороне. Именно этим кончается почти каждый разбор
    /// неудачного обмена, и перенабирать числа руками незачем. Секрета в тексте нет: адрес в
    /// журнале и так лежит замаскированным.
    /// </summary>
    private string Plain(RunEntry run) =>
        $"{Full(run)} · {ChannelText(run.Channel)} · {run.Name} · {(run.Ok ? "удача" : "ошибка")}\n"
        + $"{run.Address}\n"
        + $"{run.Explanation}\n"
        + $"{Numbers(run)} · база: {DatabaseName(run.DbId)}";

    /// <summary>
    /// Числа запуска. Пустые не показываются: обмен файлами не меряет ни объем, ни длительность,
    /// и «0 КБ · — » рядом с принятыми записями читается как поломка, а не как «неприменимо».
    /// </summary>
    private static string Numbers(RunEntry run)
    {
        var parts = new List<string> { $"записей: {run.Rows:N0}" };

        if (run.Bytes > 0) parts.Add("объем: " + Size(run.Bytes));
        if (run.Ms    > 0) parts.Add("длительность: " + Duration(run.Ms));

        return string.Join(" · ", parts);
    }

    // ── Файл журнала ──────────────────────────────────────────────────────────

    private Border FileRow(RunJournal journal, int read)
    {
        var clear = SmallButton("Очистить журнал", () => _ = ClearJournalAsync());

        var block = new StackPanel { Spacing = 6 };
        block.Children.Add(new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children            = { clear },
        });

        block.Children.Add(Note($"Файл: {journal.Path}"));
        block.Children.Add(Note(
            $"Прочитано последних строк: {read:N0}. Журнал хранит не больше "
            + $"{RunJournal.Keep:N0} строк и подрезает себя сам при запуске программы."));

        return Row("Где это лежит",
            "Журнал общий на все базы данных и лежит рядом с настройками программы, а не внутри "
            + "файла базы: файл базы ездит по сетевой папке, и история обмена раздула бы его на "
            + "всех машинах разом.",
            block);
    }

    private async Task ClearJournalAsync()
    {
        if (VisualRoot is not Window win || RunJournal.Current is not { } journal) return;

        var confirmed = await new ConfirmDialog(
                "Очистить журнал обмена? История запусков будет удалена без возможности "
                + "восстановления.\n\nНакопленный расход по потолкам обнулится вместе с ней: "
                + "потолки считаются по этим же строкам.",
                confirmText: "Очистить")
            .ShowDialog<bool>(win);

        if (confirmed != true) return;

        journal.Clear();
        _journalOpen = 0;
        BuildIntegrationsJournal();
    }

    // ── Мелочи оформления ─────────────────────────────────────────────────────

    /// <summary>Канал колонкой — коротко, чтобы влезло.</summary>
    private static string ChannelName(RunChannel channel) => channel switch
    {
        RunChannel.Request => "сеть",
        RunChannel.Inbox   => "папка",
        _                  => "сценарий",
    };

    /// <summary>Канал в пересылаемом тексте — словами, без экрана рядом.</summary>
    private static string ChannelText(RunChannel channel) => channel switch
    {
        RunChannel.Request => "запрос по сети",
        RunChannel.Inbox   => "файл из папки",
        _                  => "сценарий",
    };

    /// <summary>Почему строка не заняла места в потолках. Причины разные, и путать их нельзя.</summary>
    private static string NotCounted(RunEntry run) => run.Channel switch
    {
        RunChannel.Inbox =>
            "В расход не идет: файл из папки обмена идет внутрь, а потолки ограничивают то, что "
            + "уходит наружу.",

        RunChannel.Scenario =>
            "В расход не идет: его уже заняли строки шагов, а посчитать одно и то же дважды "
            + "значит вдвое занизить потолок.",

        _ =>
            "В расход не идет: запуск остановлен до сети — отказом политики, выбранной квотой "
            + "или незнакомой подстановкой. Обмена не было.",
    };

    /// <summary>Время строки; испорченный правкой файла момент показывается как есть.</summary>
    private static string Moment(RunEntry run) =>
        run.Moment is { } moment ? moment.ToLocalTime().ToString("dd.MM HH:mm:ss") : run.At;

    /// <summary>
    /// То же время с годом — для текста, который уносят из программы. В колонке год лишний
    /// (смотрят вчерашнее), а в пересланной строке без него не разобраться.
    /// </summary>
    private static string Full(RunEntry run) =>
        run.Moment is { } moment ? moment.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") : run.At;

    private static string Duration(long ms) => ms switch
    {
        <= 0      => "—",
        < 1000    => $"{ms} мс",
        < 60_000  => $"{ms / 1000.0:N1} с",
        _         => $"{ms / 60_000} мин {ms % 60_000 / 1000} с",
    };

    /// <summary>Имя базы по ключу; закрытая база названа ключом — история переживает базы.</summary>
    private string DatabaseName(int dbId)
    {
        if (dbId <= 0) return "не задета";

        var open = _databases?.Invoke() ?? [];

        return open.FirstOrDefault(d => d.DbId == dbId) is { } found
            ? found.Name
            : $"№{dbId} (сейчас не открыта)";
    }

    private static TextBlock Head(string text) => new()
    {
        Classes    = { "desc" },
        Text       = text,
        FontWeight = FontWeight.SemiBold,
    };

    private static TextBlock Cell(string text, bool secondary = false)
    {
        var cell = new TextBlock
        {
            Text              = text,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (secondary) cell.Classes.Add("desc");

        return cell;
    }

    private static void Put(Grid grid, int column, Control cell)
    {
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private static IBrush Ink(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var found) && found is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallbackHex));
}
