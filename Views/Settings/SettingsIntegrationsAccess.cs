using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using SmartBase.Controls.Dialogs;
using SmartBase.Database;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Views.Settings;

/// <summary>
/// Вкладка «Доступ извне» — половина про сеть (docs/ROADMAP-API.md, раздел 6.7). Вторая ее
/// половина, папка обмена, живет в <c>SettingsIntegrationsInbox</c>: экран один, потому что
/// вопрос у человека один — «как до нас достучаться».
///
/// Три вещи и ровно в этом порядке: каким способом к нам обращаются, кому это разрешено и что
/// переслать тому, кто будет писать отправителя.
///
/// <b>Выключено по умолчанию.</b> Порт открывают осознанно, а не обнаруживают; сетевой режим при
/// этом не просто галочка с предупреждением — его разрешает политика организации, и строка о нем
/// показана серой с объяснением, к кому идти. Запрет обязан объяснять себя, иначе его считают
/// поломкой.
/// </summary>
public partial class SettingsView
{
    /// <summary>Порт открылся или закрылся — перерисовать строку состояния.</summary>
    public void OnIngressChanged()
    {
        if (_integrationsTab == "inbox") BuildIntegrationsInbox();
    }

    // ── Как к нам обращаются ──────────────────────────────────────────────────

    private Border AccessModeRow()
    {
        var mode = AppSettings.LoadIngressMode();
        var port = AppSettings.LoadIngressPort();

        var block = new StackPanel { Spacing = 6 };

        block.Children.Add(ModeChoice("Выключен", IngressMode.Off, mode,
            "Никого не слушаем. Это состояние по умолчанию."));

        block.Children.Add(ModeChoice("Только с этого компьютера", IngressMode.Loopback, mode,
            $"Адрес {IngressGate.LoopbackHost} — программы, запущенные на этой же машине. "
            + "С соседнего компьютера сюда не достучаться физически, даже зная токен. "
            + "Прав администратора не требуется."));

        // Сетевой режим показан, но недоступен: пустое место на его месте читалось бы как
        // «такого не бывает», а серая строка с причиной — как «есть, но не вам решать».
        var network = ModeChoice("Разрешить по локальной сети", IngressMode.Network, mode,
            "Слушать адрес машины в сети разрешает политика организации, а не пользователь. "
            + "Сверх того нужны права администратора один раз — занять адрес и открыть порт "
            + "в брандмауэре.");

        network.IsEnabled = false;
        block.Children.Add(network);

        block.Children.Add(PortLine(mode, port));
        block.Children.Add(Note(StateLine()));

        return Row("Как к нам обращаются",
            "Прием по сети нужен тем отправителям, которые стоят на этом же компьютере: "
            + "учетной системе, скрипту, макросу табличного редактора. Всем остальным — папка "
            + "обмена ниже: она работает, даже когда программа закрыта.",
            block);
    }

    /// <summary>Одна строка выбора режима: переключатель и пояснение под ним.</summary>
    private Control ModeChoice(string title, IngressMode value, IngressMode current,
                               string explanation)
    {
        var radio = new RadioButton
        {
            Content   = title,
            GroupName = "ingressMode",
            IsChecked = value == current,
        };

        radio.IsCheckedChanged += (_, _) =>
        {
            if (radio.IsChecked != true || AppSettings.LoadIngressMode() == value) return;

            if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup))
            {
                radio.IsChecked = false;
                return;
            }

            AppSettings.SaveIngressMode(value);
            _ingress?.Apply();
            BuildIntegrationsInbox();
        };

        var block = new StackPanel { Spacing = 2 };
        block.Children.Add(radio);

        var note = Note("    " + explanation);
        block.Children.Add(note);

        return block;
    }

    /// <summary>Номер порта. Правится, только когда прием включен: менять номер двери, которой
    /// нет, незачем.</summary>
    private Control PortLine(IngressMode mode, int port)
    {
        var box = new TextBox
        {
            Text                     = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Width                    = 110,
            Height                   = 34,
            IsEnabled                = mode != IngressMode.Off,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius             = new Avalonia.CornerRadius(8),
        };

        var apply = SmallButton("Применить", () =>
        {
            if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup)) return;

            if (!int.TryParse(box.Text, out var wanted) || !IngressGate.ValidPort(wanted))
            {
                _ = ConfirmDialog
                    .Info($"Номер порта годится от {IngressGate.MinPort} до {IngressGate.MaxPort}.")
                    .ShowDialog((Window)VisualRoot!);
                return;
            }

            AppSettings.SaveIngressPort(wanted);
            _ingress?.Apply();
            BuildIntegrationsInbox();
        });

        apply.IsEnabled = mode != IngressMode.Off;

        var line = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin              = new Avalonia.Thickness(0, 6, 0, 0),
            Children            = { new TextBlock { Classes = { "desc" }, Text = "Порт",
                                                    VerticalAlignment = VerticalAlignment.Center },
                                    box, apply },
        };

        return line;
    }

    /// <summary>
    /// Слушаем или нет — одной фразой. «Выключено» и «не смогли открыть» выглядят на экране
    /// одинаково, а означают разное, и второе надо назвать вслух: занятый порт человек ищет
    /// часами, если ему об этом не сказали.
    /// </summary>
    private string StateLine()
    {
        if (_ingress is not { } ingress)
            return "Служба приема еще не поднялась — повторите через мгновение.";

        if (ingress.Listening)
            return $"Слушаю {ingress.Address}. Отправитель стучится сюда же.";

        if (ingress.Trouble.Length > 0)
            return "Порт не открылся: " + ingress.Trouble
                 + ". Обычно это занятый номер — попробуйте другой.";

        return "Сейчас не слушаю: прием выключен.";
    }

    // ── Токены доступа ────────────────────────────────────────────────────────

    private Border TokensRow()
    {
        var block = new StackPanel { Spacing = 6 };

        if (Integrations is not { } registry)
        {
            block.Children.Add(Note("Список станет доступен, когда откроется база данных."));
            return Row("Токены доступа", TokensHelp(), block);
        }

        var tokens   = registry.Tokens();
        var opened   = _databases?.Invoke() ?? [];

        if (tokens.Count == 0)
            block.Children.Add(Note("Не выдано ни одного токена — постучаться к нам сейчас некому."));

        foreach (var token in tokens)
        {
            var database = opened.FirstOrDefault(d => d.DbId == token.DbId)?.Name
                        ?? $"база №{token.DbId} (сейчас не открыта)";

            var line = Note($"{token.Name} — {database}"
                + (token.Enabled ? "" : " · выключен")
                + (token.Used is { } used
                    ? $" · последнее обращение {used.ToLocalTime():dd.MM.yyyy HH:mm}"
                    : " · обращений не было"));

            if (!token.Enabled) line.Foreground = Ink("BrushSecondaryText", "#8E8E93");

            block.Children.Add(line);

            var actions = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                Spacing             = 8,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin              = new Avalonia.Thickness(0, 0, 0, 6),
            };

            var item = token;

            actions.Children.Add(SmallButton(item.Enabled ? "Выключить" : "Включить", () =>
            {
                if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup)) return;

                registry.EnableToken(item.Id, !item.Enabled);
                BuildIntegrationsInbox();
            }));

            actions.Children.Add(SmallButton("Убрать", () => _ = RemoveTokenAsync(registry, item)));

            block.Children.Add(actions);
        }

        block.Children.Add(new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin              = new Avalonia.Thickness(0, 4, 0, 0),
            Children            = { SmallButton("Создать токен", () => _ = IssueTokenAsync(registry)) },
        });

        return Row("Токены доступа", TokensHelp(), block);
    }

    private static string TokensHelp() =>
        "Токен — это пользователь-робот в базе данных: он виден администратору в списке "
        + "пользователей, его именем подписаны сделанные им записи, и допуск ему отзывается там "
        + "же, где человеку. По умолчанию роботу разрешено только создавать и изменять записи — "
        + "читать базу через этот канал нельзя вовсе.";

    private async Task IssueTokenAsync(Database.IntegrationsRegistry registry)
    {
        if (VisualRoot is not Window win) return;
        if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup)) return;

        var opened = _databases?.Invoke() ?? [];

        if (opened.Count == 0)
        {
            await ConfirmDialog.Info("Сначала откройте базу данных: токен выдается под нее.")
                               .ShowDialog(win);
            return;
        }

        var made  = "";
        var owner = "";

        var dialog = new TokenDialog(
            [.. opened.Select(d => (d.DbId, d.Name))],
            (name, dbId) =>
            {
                var target = opened.FirstOrDefault(d => d.DbId == dbId);
                if (target is null) return "эта база сейчас не открыта";

                var (token, secret) = registry.Issue(name, dbId, Environment.MachineName,
                                                     DateTimeOffset.Now);

                // Робот заводится СРАЗУ: токен, которому не соответствует пользователь, работать
                // не будет, и узнавать об этом из отказа первой посылки — потерянный вечер.
                target.Permissions.EnsureRobot(token.RobotKey, token.Name, DateTimeOffset.Now);

                made  = secret;
                owner = target.Name;
                return null;
            });

        if (await dialog.ShowDialog<bool>(win) != true || made.Length == 0) return;

        BuildIntegrationsInbox();

        // Текст токена показывается ОДИН раз: хранилище, из которого секрет можно прочитать
        // обратно, рано или поздно прочитают. Отсюда и кнопка «Скопировать» рядом.
        await ConfirmDialog.Info(
            $"Токен для базы «{owner}» создан.\n\n{made}\n\n"
            + "Скопируйте его сейчас: показать второй раз программа не сможет — она хранит "
            + "только проверочный отпечаток. Потеряется — выдайте новый.",
            "Скопировать и закрыть").ShowDialog(win);

        await (TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(made) ?? Task.CompletedTask);
    }

    private async Task RemoveTokenAsync(Database.IntegrationsRegistry registry, AccessToken token)
    {
        if (VisualRoot is not Window win) return;
        if (!AllowIntegrations(Core.Models.PermAction.IntegrationsSetup)) return;

        var ok = await new ConfirmDialog(
            $"Убрать токен «{token.Name}»?\n\n"
            + "Отправитель с этим токеном перестанет проходить сразу же. Пользователь-робот в "
            + "базе останется: его именем подписаны уже сделанные записи, и удалить его значило "
            + "бы обезличить историю изменений.").ShowDialog<bool>(win);

        if (!ok) return;

        registry.RemoveToken(token.Id);
        BuildIntegrationsInbox();
    }

    // ── Описание API ──────────────────────────────────────────────────────────

    private Border ApiRow()
    {
        var block = new StackPanel { Spacing = 6 };

        var opened = _databases?.Invoke() ?? [];

        if (opened.Count == 0)
        {
            block.Children.Add(Note("Описание станет доступно, когда откроется база данных."));
            return Row("Описание для отправителя", ApiHelp(), block);
        }

        var combo = new ComboBox
        {
            ItemsSource              = opened.Select(d => d.Name).ToList(),
            SelectedIndex            = 0,
            Height                   = 34,
            MinWidth                 = 220,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        block.Children.Add(combo);

        block.Children.Add(new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            Spacing             = 8,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin              = new Avalonia.Thickness(0, 4, 0, 0),
            Children            =
            {
                SmallButton("Показать",        () => _ = ShowApiAsync(Chosen(combo, opened), copy: false)),
                SmallButton("Скопировать",     () => _ = ShowApiAsync(Chosen(combo, opened), copy: true)),
                SmallButton("Сохранить в файл", () => _ = SaveApiAsync(Chosen(combo, opened))),
            },
        });

        return Row("Описание для отправителя", ApiHelp(), block);
    }

    private static string ApiHelp() =>
        "Собирается из состава коллекций: адрес, вид сообщения и список колонок с типами. Это и "
        + "есть то, что пересылают тому, кто будет писать отправителя, — больше от нас ему "
        + "ничего не нужно. Значений полей в описании нет: отдается схема, а не данные.";

    private static DatabaseManager? Chosen(ComboBox combo, IReadOnlyList<DatabaseManager> opened) =>
        combo.SelectedIndex >= 0 && combo.SelectedIndex < opened.Count
            ? opened[combo.SelectedIndex]
            : null;

    /// <summary>
    /// Собрать описание. Схема читается на отдельном подключении и в фоне: в базе бывают сотни
    /// коллекций, а UI-поток этого не переживет.
    /// </summary>
    private async Task<string> ApiTextAsync(DatabaseManager target)
    {
        var address = _ingress?.Address ?? "";
        var name    = target.Name;
        var repo    = target.Constructor;

        var collections = await Task.Run(repo.ApiSchemaIsolated);

        return ApiDescription.Text(address, name, collections);
    }

    private async Task ShowApiAsync(DatabaseManager? target, bool copy)
    {
        if (VisualRoot is not Window win || target is null) return;

        var text = await ApiTextAsync(target);

        if (copy)
        {
            await (TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask);
            await ConfirmDialog.Info("Описание скопировано — его можно вставить в письмо.")
                               .ShowDialog(win);
            return;
        }

        await ConfirmDialog.Info(text).ShowDialog(win);
    }

    private async Task SaveApiAsync(DatabaseManager? target)
    {
        if (VisualRoot is not Window win || target is null) return;

        var text = await ApiTextAsync(target);

        var file = await win.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title               = "Куда сохранить описание",
            SuggestedFileName   = ApiDescription.FileName(target.Name),
            DefaultExtension    = "txt",
            ShowOverwritePrompt = true,
            FileTypeChoices     = [new FilePickerFileType("Текстовый файл") { Patterns = ["*.txt"] }],
        });

        if (file?.TryGetLocalPath() is not { Length: > 0 } path) return;

        try
        {
            // С меткой порядка байтов: файл открывают Блокнотом, а он без метки читает русский
            // текст как набор знаков вопроса.
            await File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(true));

            await ConfirmDialog.Info($"Описание сохранено.\n\nФайл: {path}").ShowDialog(win);
        }
        catch (Exception error)
        {
            await ConfirmDialog.Info("Сохранить не удалось.\n\n" + error.Message).ShowDialog(win);
        }
    }
}
