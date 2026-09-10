using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using SmartBase.Controls.Dialogs;
using SmartBase.Database;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Views.Settings;

/// <summary>
/// Вкладка «Прием» раздела «Интеграции» (partial SettingsView): папка, куда чужая программа
/// кладет файлы, и разбор того, что там накопилось.
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
    private void BuildIntegrationsInbox()
    {
        var rows = IntegrationsInboxTab.Children;
        rows.Clear();

        var folder    = AppSettings.LoadInboxFolder();
        var databases = _databases?.Invoke() ?? [];

        Add(rows, FolderRow(folder));
        Add(rows, DatabaseRow(databases));
        Add(rows, CheckRow(folder, databases));
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

        return Row("Разбор входящих",
            "Пока прием запускается вручную: программа обходит «Входящие», укладывает каждый "
            + "готовый файл и кладет отправителю квитанцию.",
            block);
    }

    /// <summary>
    /// Обойти папку и показать итог. Ошибки на каждом шаге показываются человеку: «кнопка не
    /// работает» — худшее, чем может кончиться обмен.
    /// </summary>
    private async Task ReceiveAsync()
    {
        if (VisualRoot is not Window win) return;

        var databases = _databases?.Invoke() ?? [];
        var target    = databases.FirstOrDefault(d => d.DbId == AppSettings.LoadInboxDb());

        if (target is null)
        {
            await ConfirmDialog.Info("База данных для приема не выбрана или закрылась.").ShowDialog(win);
            return;
        }

        // Разрешение спрашивается здесь же, у политики: сохраненный путь — всего лишь память
        // о выборе, а корни задает организация и может их поменять.
        var layout = InboxFolder.Open(AppSettings.LoadInboxFolder(), out var explanation);

        if (layout is null)
        {
            await ConfirmDialog.Info("Папка обмена недоступна: " + explanation).ShowDialog(win);
            return;
        }

        var progress = new ProgressDialog { Title = "Прием файлов" };
        using var cts = new CancellationTokenSource();
        progress.EnableCancel(cts.Cancel);

        // Сколько файлов лежит во «Входящих», заранее неизвестно — процентов тут не бывает.
        progress.SetSaving("Обход папки…");
        var closed = progress.ShowDialog(win);
        var open   = true;

        IProgress<string> ui = new Progress<string>(name => progress.SetInfo(name));

        InboxRun run;
        try
        {
            var repo = target.Constructor;
            var dbId = target.DbId;

            run = await Task.Run(() => InboxReceiver.Run(
                layout, repo, dbId, Environment.MachineName,
                DateTimeOffset.Now, cts.Token, ui));
        }
        catch (Exception failure)
        {
            if (open) { open = false; progress.Done(); await closed; }
            await ConfirmDialog.Info("Прием не состоялся: " + failure.Message).ShowDialog(win);
            return;
        }
        finally
        {
            if (open) { progress.Done(); await closed; }
        }

        BuildIntegrationsInbox();
        await ConfirmDialog.Info(Report(run)).ShowDialog(win);
    }

    /// <summary>Итог обхода: числа и построчно — что стало с каждым файлом.</summary>
    private static string Report(InboxRun run)
    {
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
