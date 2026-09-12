using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using SmartBase.Controls.Dialogs;
using SmartBase.Database;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Views.Settings;

/// <summary>
/// Папочная половина вкладки «Доступ извне» (partial SettingsView): папка, куда чужая программа
/// кладет файлы, и разбор того, что там накопилось. Сетевая половина — в
/// <c>SettingsIntegrationsAccess</c>; экран один, потому что вопрос у человека один — «как до
/// нас достучаться».
///
/// Канал единственный, у которого нет двери, и потому единственный, который работает
/// асинхронно: файл дождется, пока приемник включат. При выключаемом на ночь сервере это
/// главное его свойство, а не мелочь.
///
/// Настройка машинная — папка та же, а смотрят ее несколько компьютеров разом (захват идет
/// переименованием). Экран об этом говорит прямо: иначе человек, включивший прием на своей
/// машине, будет думать, что включил его для всех.
///
/// Разрешена ли сама папка, решает политика организации. Экран показывает ее вердикт СРАЗУ, под
/// строкой пути, — не дожидаясь нажатия: ответ «туда нельзя» нужен до того, как человек начнет
/// договариваться с отправителем о формате.
/// </summary>
public partial class SettingsView
{
    // Последний обход и когда он был — чтобы человек видел, что автоматический разбор живой.
    // Без этой строки включенный тумблер ничем не отличается от выключенного, пока файлы не
    // придут; а «не работает ли оно вообще» — первый вопрос, который тут задают.
    private InboxRun?       _lastInboxRun;
    private DateTimeOffset? _lastInboxAt;

    /// <summary>
    /// Автоматический обход что-то тронул. Зовет MainWindow из службы приема; экран
    /// перестраивается, только если вкладка открыта, — иначе это работа впустую.
    /// </summary>
    public void OnInboxSwept(InboxRun run)
    {
        _lastInboxRun = run;
        _lastInboxAt  = DateTimeOffset.Now;

        if (_integrationsTab == "inbox") BuildIntegrationsInbox();

        // Открытый журнал тоже обновляется: обход только что дописал в него строки, и смотреть
        // на устаревший список, пока прием работает у тебя на глазах, незачем.
        if (_integrationsTab == "journal") BuildIntegrationsJournal();
    }

    private void BuildIntegrationsInbox()
    {
        var rows = IntegrationsInboxTab.Children;
        rows.Clear();

        var folder    = AppSettings.LoadInboxFolder();
        var databases = _databases?.Invoke() ?? [];

        // Порядок тот же, что в плане (раздел 6.7): сначала сеть, потом папка, потом токены и
        // описание. Сеть первой не потому, что важнее, — с нее начинают читать.
        Add(rows, AccessModeRow());

        Add(rows, FolderRow(folder));
        Add(rows, DatabaseRow(databases));
        Add(rows, AutoRow(folder, databases));
        Add(rows, CheckRow(folder, databases));

        Add(rows, TokensRow());
        Add(rows, ApiRow());

        Add(rows, Row("Что класть в папку",
            "Это же описание можно переслать той стороне: больше от нее ничего не требуется.",
            InboxHelp()), last: true);
    }

    // ── Папка ─────────────────────────────────────────────────────────────────

    private Border FolderRow(string folder)
    {
        var box = new TextBox
        {
            Text                     = folder,
            Watermark                = @"P:\Обмен",
            Height                   = 34,
            IsReadOnly               = true,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius             = new Avalonia.CornerRadius(8),
        };

        var choose = SmallButton("Выбрать", () => _ = ChooseInboxFolderAsync());
        var clear  = SmallButton("Убрать",  () =>
        {
            if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup)) return;

            AppSettings.SaveInboxFolder("");
            BuildIntegrationsInbox();
        });

        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto,8,Auto") };
        Grid.SetColumn(box, 0);
        Grid.SetColumn(choose, 2);
        Grid.SetColumn(clear, 4);
        line.Children.Add(box);
        line.Children.Add(choose);
        line.Children.Add(clear);

        var block = new StackPanel { Spacing = 6 };
        block.Children.Add(line);
        block.Children.Add(Note(FolderVerdict(folder)));

        return Row("Папка обмена",
            "Под ней программа заводит «Входящие», «Обработанные», «Ошибки» и «Квитанции». "
            + "Одну и ту же папку могут смотреть несколько компьютеров: файл достанется тому, "
            + "кто успел взять его первым.",
            block);
    }

    /// <summary>
    /// Что скажет политика об этой папке. Ответ дается сразу, без нажатия: узнать «туда нельзя»
    /// после того, как с отправителем договорились о формате, — потерянный день.
    /// </summary>
    private static string FolderVerdict(string folder)
    {
        if (folder.Length == 0)
            return "Папка не выбрана — прием выключен.";

        var decision = EgressGate.CheckFolder(folder);

        if (!decision.Allowed)
            return "Политика: " + decision.Explanation
                 + ". Корни папок обмена задает организация — см. вкладку «Безопасность».";

        return Directory.Exists(folder)
            ? "Политика: папка разрешена."
            : "Политика: папка разрешена, но сейчас недоступна — возможно, отключен сетевой диск.";
    }

    private async Task ChooseInboxFolderAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup)) return;

        IStorageFolder? start = null;
        var current = AppSettings.LoadInboxFolder();
        if (current.Length > 0 && Directory.Exists(current))
            start = await top.StorageProvider.TryGetFolderFromPathAsync(current);

        var chosen = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                  = "Папка обмена",
            AllowMultiple          = false,
            SuggestedStartLocation = start,
        });

        if (chosen.FirstOrDefault() is not { } folder) return;

        AppSettings.SaveInboxFolder(folder.Path.LocalPath);
        BuildIntegrationsInbox();
    }

    // ── База ──────────────────────────────────────────────────────────────────

    private Border DatabaseRow(IReadOnlyList<DatabaseManager> databases)
    {
        if (databases.Count == 0)
            return Row("База данных",
                "Список станет доступен, когда откроется база данных: входящие файлы ложатся "
                + "в ее коллекции.");

        var combo = new ComboBox
        {
            ItemsSource              = databases.Select(d => d.Name).ToList(),
            Height                   = 34,
            HorizontalAlignment      = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        var saved = AppSettings.LoadInboxDb();
        var index = databases.ToList().FindIndex(d => d.DbId == saved);
        combo.SelectedIndex = index >= 0 ? index : -1;

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex >= 0 && combo.SelectedIndex < databases.Count)
                AppSettings.SaveInboxDb(databases[combo.SelectedIndex].DbId);
        };

        return Row("База данных",
            "Куда ложатся входящие файлы. Коллекцию отправитель называет ИМЕНЕМ — номера "
            + "коллекций у каждой базы свои, и знать их он не может.",
            combo);
    }

    // ── Автоматический разбор ─────────────────────────────────────────────────

    /// <summary>
    /// Тумблер «разбирать самому». Выключен по умолчанию, и это правильный умолчательный
    /// выбор: запись в базу без участия человека включают осознанно, а не обнаруживают.
    /// </summary>
    private Border AutoRow(string folder, IReadOnlyList<DatabaseManager> databases)
    {
        var ready  = folder.Length > 0 && databases.Any(d => d.DbId == AppSettings.LoadInboxDb());
        var toggle = new ToggleSwitch
        {
            IsChecked = AppSettings.LoadInboxAuto(),
            IsEnabled = ready,
        };

        toggle.IsCheckedChanged += (_, _) => AppSettings.SaveInboxAuto(toggle.IsChecked == true);

        var block = new StackPanel { Spacing = 6 };
        block.Children.Add(toggle);

        if (!ready)
            block.Children.Add(Note("Станет доступно, когда выбраны папка и база данных."));
        else
            block.Children.Add(Note(
                $"Программа заглядывает в папку раз в {InboxService.TickSeconds} секунд, пока она "
                + "открыта. Если папка пропадет (выключили сетевой диск), следующая попытка будет "
                + $"через {InboxService.RetryDelay.TotalMinutes:N0} минут, а не каждые полминуты — "
                + "иначе за ночь журнал заполнится одинаковыми ошибками."));

        return Row("Разбирать самому",
            "Файлы будут приниматься без нажатия кнопки. База, которая сейчас недоступна, "
            + "прием останавливает: складывать записи некуда, и файлы зря уехали бы в «Ошибки».",
            block);
    }

    // ── Разбор ────────────────────────────────────────────────────────────────

    private Border CheckRow(string folder, IReadOnlyList<DatabaseManager> databases)
    {
        var target = databases.FirstOrDefault(d => d.DbId == AppSettings.LoadInboxDb());

        var button = SmallButton("Проверить папку", () => _ = ReceiveAsync());
        button.IsEnabled = folder.Length > 0 && target is not null;

        var block = new StackPanel { Spacing = 6 };
        block.Children.Add(new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            Children            = { button },
        });

        if (folder.Length == 0)      block.Children.Add(Note("Сначала выберите папку обмена."));
        else if (target is null)     block.Children.Add(Note("Сначала выберите базу данных."));

        if (_lastInboxRun is { } last && _lastInboxAt is { } at)
            block.Children.Add(Note($"Последний обход в {at.ToLocalTime():HH:mm} — {last.Summary()}."));

        return Row("Разбор входящих",
            "Пока прием запускается вручную: программа обходит «Входящие», укладывает каждый "
            + "готовый файл и кладет отправителю квитанцию.",
            block);
    }

    /// <summary>
    /// Обойти папку и показать итог. Сам обход ведет служба приема — та же, что ходит по
    /// расписанию: две реализации одного обхода разошлись бы на первой же правке, и ручная
    /// проверка перестала бы отвечать на вопрос «а что будет само».
    /// </summary>
    private async Task ReceiveAsync()
    {
        if (VisualRoot is not Window win) return;

        if (_inboxSweep is not { } sweep)
        {
            await ConfirmDialog.Info("Прием сейчас недоступен: база данных еще не открыта.")
                .ShowDialog(win);
            return;
        }

        var progress = new ProgressDialog { Title = "Прием файлов" };
        using var cts = new CancellationTokenSource();

        // Отмена останавливает обход МЕЖДУ файлами, а не посреди зависшего сетевого чтения:
        // обещать большее нечестно — файловый ввод-вывод на мертвой шаре не отменяется.
        progress.EnableCancel(cts.Cancel);

        // Сколько файлов лежит во «Входящих», заранее неизвестно — процентов тут не бывает.
        progress.SetSaving("Обход папки…");
        var closed = progress.ShowDialog(win);
        var open   = true;

        InboxRun run;
        try
        {
            run = await sweep(cts.Token);
        }
        catch (Exception failure)
        {
            run = InboxRun.Failed(failure.Message);
        }
        finally
        {
            if (open) { open = false; progress.Done(); await closed; }
        }

        _lastInboxRun = run;
        _lastInboxAt  = DateTimeOffset.Now;

        BuildIntegrationsInbox();
        await ConfirmDialog.Info(Report(run)).ShowDialog(win);
    }

    /// <summary>Итог обхода: числа и построчно — что стало с каждым файлом.</summary>
    private static string Report(InboxRun run)
    {
        if (run.Problem.Length > 0) return "Прием не состоялся: " + run.Problem;

        var text = "Итог: " + run.Summary() + ".";

        if (run.Receipts.Count == 0) return text;

        text += "\n\n" + string.Join("\n", run.Receipts.Take(20).Select(r => r.Summary()));

        if (run.Receipts.Count > 20)
            text += $"\n… и еще файлов: {run.Receipts.Count - 20}. "
                  + "Полные ответы лежат в папке «Квитанции».";

        return text;
    }

    // ── Подсказка отправителю ─────────────────────────────────────────────────

    /// <summary>
    /// Короткое описание формата прямо на экране. Без него человеку нечего переслать той
    /// стороне, а искать документацию разработчика он не станет.
    /// </summary>
    private static Control InboxHelp()
    {
        var list = new StackPanel { Spacing = 6 };

        list.Children.Add(Note(
            "Отправитель кладет файл в «Входящие». Чтобы приемник не взял недописанный файл, "
            + "пусть пишет во временное имя и переименовывает: «заказы.json.tmp» → «заказы.json»."));

        list.Children.Add(Note("Внутри файла:"));
        list.Children.Add(Note("""
            {
              "collection": "Заказы",
              "mode": "update",
              "key": "Номер",
              "id": "2026-09-10-0042",
              "rows": [ { "Номер": "А-1", "Название": "Молоток" } ]
            }
            """));

        list.Children.Add(Note(
            "«collection» — имя коллекции; «mode» — «add» (добавить) или «update» (найти по "
            + "ключу и обновить); «key» — колонка, по которой ищется уже заведенная запись; "
            + "«id» — номер сообщения, защита от повторной доставки."));

        list.Children.Add(Note(
            "Колонки сводятся со свойствами коллекции ПО ИМЕНАМ; то, чему места не нашлось, "
            + "перечислено в квитанции. Ответ на каждый файл кладется в «Квитанции»."));

        return list;
    }
}
