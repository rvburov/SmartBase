using Avalonia.Controls;
using Avalonia.Layout;
using SmartBase.Controls.Dialogs;
using SmartBase.Database;
using SmartBase.Integrations;
using SmartBase.Setup;
using SmartBase.Utils;
using static SmartBase.Controls.Widgets.SettingRows;

namespace SmartBase.Views.Settings;

/// <summary>
/// Вкладка «Доступ извне» — половина про сеть (docs/ROADMAP-API.md, раздел 6.7). Вторая ее
/// половина, папка обмена, живет в <c>SettingsIntegrationsInbox</c>: экран один, потому что
/// вопрос у человека один — «как до нас достучаться».
///
/// Режим приема, порт и описание API для отправителя. Настройки приема относятся к этой
/// машине, а разрешения операционной системы на сетевой порт настраиваются отдельно.
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
            "Прием по сети выключен."));
        block.Children.Add(ModeChoice("Только с этого компьютера", IngressMode.Loopback, mode,
            $"Адрес {IngressGate.LoopbackHost} — программы на этой же машине. "
            + "С соседнего компьютера этот адрес недоступен."));
        block.Children.Add(ModeChoice("Разрешить по локальной сети", IngressMode.Network, mode,
            "Слушать все адреса машины. Для открытия сетевого порта могут потребоваться "
            + "права администратора операционной системы."));

        if (mode == IngressMode.Network && ListenerPermissions.Needed)
            block.Children.Add(PermissionsLine(port));

        block.Children.Add(PortLine(mode, port));
        var state = new List<Control> { Note(StateLine()) };
        if (mode == IngressMode.Network && MachineAddress.All().Count > 1)
            state.Add(Note("Адреса этой машины: " + string.Join(", ", MachineAddress.All())
                           + ". Отправителю нужен доступный ему адрес."));
        block.Children.Add(DataBox(state));

        return Row("Как к нам обращаются",
            "Сторонняя программа отправляет запросы на выбранный адрес. Для обмена файлами "
            + "используется папка ниже: файлы дождутся следующего запуска SmartBase.",
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
            AppSettings.SaveIngressMode(value);
            _ingress?.Apply();
            BuildIntegrationsInbox();
        };

        var block = new StackPanel { Spacing = 2 };
        block.Children.Add(radio);

        var note = SubNote(explanation);
        block.Children.Add(note);

        return block;
    }

    /// <summary>
    /// Разрешения системы для сетевого режима: занять адрес и открыть порт в брандмауэре.
    /// Программа не делает этого молча — команды показываются, копируются и выполняются по
    /// кнопке: менять настройки машины за спиной у того, кто за нее отвечает, нельзя.
    /// </summary>
    private Control PermissionsLine(int port)
    {
        var block = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(0, 4, 0, 0) };

        block.Children.Add(SubNote(
            "Права администратора нужны один раз на машину. Кнопка ниже выполнит обе команды "
            + "с окном контроля учетных записей; если прав нет — скопируйте их и передайте в "
            + "службу ИТ."));

        block.Children.Add(ButtonLine(
            RowButton("Настроить разрешения…", () => _ = GrantListenerAsync(port)),
            RowButton("Скопировать команды", () =>
                _ = TopLevel.GetTopLevel(this)?.Clipboard?
                    .SetTextAsync(ListenerPermissions.Script(port)))));

        return block;
    }

    private async Task GrantListenerAsync(int port)
    {
        if (VisualRoot is not Window win) return;

        var trouble = await ListenerPermissions.GrantAsync(port);

        // Успех не объявляется словами: подтвердит его только поднявшийся слушатель. Поэтому
        // сразу пробуем открыть порт заново и показываем то, чем это кончилось.
        _ingress?.Apply();
        BuildIntegrationsInbox();

        await ConfirmDialog.Info(trouble is null
            ? "Команды выполнены. Состояние приема — строкой ниже переключателей: "
            + "если порт открылся, там стоит адрес."
            : "Разрешения не выданы: " + trouble + ".\n\n"
            + "Команды можно скопировать кнопкой рядом и передать в службу ИТ.")
            .ShowDialog(win);
    }

    /// <summary>Номер порта. Правится, только когда прием включен: менять номер двери, которой
    /// нет, незачем.</summary>
    private Control PortLine(IngressMode mode, int port)
    {
        var box = Field(port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        width: 110);

        box.IsEnabled = mode != IngressMode.Off;

        var apply = RowButton("Применить", () =>
        {
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

        var label = new TextBlock
        {
            Classes           = { "desc" },
            Text              = "Порт",
            VerticalAlignment = VerticalAlignment.Center,
        };

        var line = ButtonLine(label, box, apply);
        line.Margin = new Avalonia.Thickness(0, 6, 0, -6);

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

    // ── Описание API ──────────────────────────────────────────────────────────

    private Border ApiRow()
    {
        var block = new StackPanel { Spacing = 6 };

        var opened = _databases?.Invoke() ?? [];

        if (opened.Count == 0)
        {
            block.Children.Add(DataBox("Описание станет доступно, когда откроется база данных."));
            return Row("Описание для отправителя", ApiHelp(), block);
        }

        var combo = Picker(opened.Select(d => d.Name), 0);

        // Кнопка одна: описание открывается своим окном, а копирование и сохранение живут в
        // нем же. Три кнопки в строке означали три пути к одному и тому же тексту, из которых
        // два показывали его вслепую.
        block.Children.Add(ButtonLine(
            combo,
            RowButton("Показать описание", () => _ = ShowApiAsync(Chosen(combo, opened)))));

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

        return ApiDescription.Text(address, name, collections, target.DbId);
    }

    /// <summary>
    /// Показать описание своим окном (<see cref="ApiDescriptionDialog"/>). Раньше это делало окно
    /// сообщения — оно растет по содержимому и режет длинный текст краем экрана; описание на
    /// сотню коллекций прочитать в нем было нечем (docs/ROADMAP-API.md, шаг 14).
    /// </summary>
    private async Task ShowApiAsync(DatabaseManager? target)
    {
        if (VisualRoot is not Window win || target is null) return;

        var text = await ApiTextAsync(target);

        await new ApiDescriptionDialog(text, ApiDescription.FileName(target.Name), ApiHelp())
            .ShowDialog(win);
    }
}
