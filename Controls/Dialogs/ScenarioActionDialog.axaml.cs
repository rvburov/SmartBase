using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using SmartBase.Extensions;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>Действие, которое можно поставить шагом: чье оно и что умеет.</summary>
/// <param name="ExtensionId">Ключ расширения — он и хранится в шаге.</param>
/// <param name="ExtensionName">Имя расширения для человека.</param>
/// <param name="Action">Само действие.</param>
public sealed record ActionChoice(string ExtensionId, string ExtensionName, IExtensionAction Action);

/// <summary>Коллекция, в которую действие может писать.</summary>
/// <param name="DbId">База данных.</param>
/// <param name="CollectionId">Коллекция.</param>
/// <param name="Database">Имя базы — баз бывает открыто несколько, и тезки среди коллекций
/// обычное дело.</param>
/// <param name="Name">Имя коллекции.</param>
public sealed record CollectionChoice(int DbId, int CollectionId, string Database, string Name);

/// <summary>
/// Окно настройки шага «действие расширения»: что делать, куда писать и откуда брать значения.
///
/// Три решения окна стоит назвать.
///
/// <list type="number">
///   <item><b>Коллекцию выбирают здесь, а не в расширении.</b> Точка входа принадлежит
///   сценарию: одно и то же расширение вешается на разные журналы и разные каналы, и знать о
///   них ему незачем.</item>
///   <item><b>Поля показывает само действие.</b> Список приходит из
///   <see cref="IExtensionAction.Fields"/> — окно не знает ни одного имени поля наперед, иначе
///   новое расширение требовало бы правки этого окна.</item>
///   <item><b>Значение — обычный текст с подстановками.</b> Одно поле вместо переключателя
///   «постоянное / из колонки»: постоянная часть и колонка в жизни встречаются вместе («узел
///   {{Название}} проекта {{Проект}}»), а два разных элемента управления на один смысл пришлось
///   бы объяснять.</item>
/// </list>
///
/// Возвращает <see cref="ScenarioStep"/> или null при отмене.
/// </summary>
public partial class ScenarioActionDialog : Window
{
    private readonly IReadOnlyList<ActionChoice>     _actions;
    private readonly IReadOnlyList<CollectionChoice> _places;

    // Набранные значения по ключам полей. Живут отдельно от полей ввода: при смене действия
    // список полей перерисовывается целиком, а набранное для совпадающих ключей теряться не
    // должно — половина расширений называет «Наименование» одинаково.
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    private readonly ScenarioStep _step;

    /// <param name="step">Что правим; <see cref="ScenarioStep.OfAction"/> с пустыми ключами —
    /// новый шаг.</param>
    /// <param name="actions">Действия всех расширений в порядке показа.</param>
    /// <param name="places">Коллекции открытых баз.</param>
    public ScenarioActionDialog(ScenarioStep                    step,
                                IReadOnlyList<ActionChoice>     actions,
                                IReadOnlyList<CollectionChoice> places)
    {
        InitializeComponent();

        _step    = step;
        _actions = actions;
        _places  = places;

        foreach (var (key, value) in step.Fields) _values[key] = value;

        ActionCombo.ItemsSource = _actions.Select(a => a.Action.Name + " — " + a.ExtensionName).ToList();
        PlaceCombo.ItemsSource  = _places.Select(p => p.Name + " — " + p.Database).ToList();

        ActionCombo.SelectedIndex = Math.Max(0, IndexOfAction(step));
        PlaceCombo.SelectedIndex  = Math.Max(0, IndexOfPlace(step));
        FolderBox.Text            = step.Folder;

        ActionCombo.SelectionChanged += (_, _) => ShowFields();

        ShowFields();

        // Ставить нечего — сказать об этом надо до того, как человек нажмет «Готово».
        if (_actions.Count == 0)
            NoticeBanner.Show("Ни одно расширение не объявило действий, которые можно поставить "
                            + "шагом сценария.");
        else if (_places.Count == 0)
            NoticeBanner.Show("Нет открытых баз данных: операции будет некуда писать.");

        SaveBtn.IsEnabled = _actions.Count > 0;

        CancelBtn.Click += (_, _) => Close(null);
        SaveBtn.Click   += (_, _) => Save();
        KeyDown         += (_, e) => { if (e.Key == Key.Escape) Close(null); };
    }

    private int IndexOfAction(ScenarioStep step) =>
        _actions.ToList().FindIndex(a => a.ExtensionId == step.Extension
                                      && a.Action.Key  == step.Action);

    private int IndexOfPlace(ScenarioStep step) =>
        _places.ToList().FindIndex(p => p.DbId == step.DbId && p.CollectionId == step.CollectionId);

    private ActionChoice? Chosen =>
        ActionCombo.SelectedIndex >= 0 && ActionCombo.SelectedIndex < _actions.Count
            ? _actions[ActionCombo.SelectedIndex]
            : null;

    // ── Поля выбранного действия ──────────────────────────────────────────────

    /// <summary>
    /// Показать поля выбранного действия. Список строится заново, а не правится по одному:
    /// у другого действия и поля другие, а согласовывать два списка правками — верный способ
    /// показать чужое поле под своим именем.
    /// </summary>
    private void ShowFields()
    {
        FieldsPanel.Children.Clear();

        if (Chosen is not { } choice)
        {
            ActionHint.Text = "";
            return;
        }

        ActionHint.Text = choice.Action.Description;

        // Преобразователь формата и автоматизация в базу не пишут: первый разбирает текст,
        // вторая считает строки. Показывать им «куда писать» значило бы спрашивать о том, чего
        // они не делают, — и человек искал бы у себя ошибку, не найдя коллекции по смыслу.
        var writes = choice.Action.Kind == ExtensionActionKind.Operation;

        // Поля у автоматизации есть, но они означают другое: не значения записи, а настройки
        // шага — какая колонка ключевая, как назвать лист. Подстановок в них нет: поле
        // заполняется один раз, а строк много.
        var counts = choice.Action.Kind == ExtensionActionKind.Rows;
        var fields = writes || (counts && choice.Action.Fields.Count > 0);

        PlaceLabel.IsVisible = writes;
        PlaceCombo.IsVisible = writes;

        FolderLabel.IsVisible = choice.Action.MakesFile;
        FolderHint.IsVisible  = choice.Action.MakesFile;
        FolderBox.IsVisible   = choice.Action.MakesFile;

        FieldsLabel.Text      = counts ? "Настройки" : "Значения полей";
        FieldsHint.IsVisible  = writes;
        FieldsLabel.IsVisible = fields;
        FieldsBox.IsVisible   = fields;

        if (!fields || choice.Action.Fields.Count == 0)
        {
            FieldsPanel.Children.Add(new TextBlock
            {
                Classes      = { "desc" },
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Text         = "Этому действию значения не нужны.",
            });

            return;
        }

        foreach (var field in choice.Action.Fields)
        {
            FieldsPanel.Children.Add(new TextBlock
            {
                Text       = field.Required ? field.Label : field.Label + " — необязательно",
                Classes    = { "desc" },
                Margin     = new Thickness(0, 0, 0, 2),
            });

            var key = field.Key;

            var box = new TextBox
            {
                Text                     = _values.TryGetValue(key, out var value) ? value : "",
                Height                   = 30,
                Watermark                = field.Watermark,
                VerticalContentAlignment = VerticalAlignment.Center,
                CornerRadius             = new CornerRadius(6),
            };

            box.TextChanged += (_, _) => _values[key] = box.Text ?? "";

            FieldsPanel.Children.Add(box);
        }
    }

    // ── Сохранение ────────────────────────────────────────────────────────────

    private void Save()
    {
        if (Chosen is not { } choice)
        {
            NoticeBanner.Show("Не выбрано действие.");
            return;
        }

        // Преобразователь ни в базу не пишет, ни полей не ждет: настройка у него кончается
        // выбором самого действия.
        if (choice.Action.Kind == ExtensionActionKind.Transformer)
        {
            Close(_step with
            {
                Kind         = ScenarioStepKind.Action,
                Extension    = choice.ExtensionId,
                Action       = choice.Action.Key,
                DbId         = 0,
                CollectionId = 0,
                FieldValues  = new Dictionary<string, string>(StringComparer.Ordinal),
            });

            return;
        }

        // Автоматизация: коллекции у нее нет, зато бывают настройки и папка для файла.
        if (choice.Action.Kind == ExtensionActionKind.Rows)
        {
            var folder = (FolderBox.Text ?? "").Trim();

            if (choice.Action.MakesFile && folder.Length == 0)
            {
                NoticeBanner.Show("Не выбрана папка для файла: без нее шаг не сработает.");
                return;
            }

            if (Settings(choice) is not { } settings) return;

            Close(_step with
            {
                Kind         = ScenarioStepKind.Action,
                Extension    = choice.ExtensionId,
                Action       = choice.Action.Key,
                DbId         = 0,
                CollectionId = 0,
                Folder       = folder,
                FieldValues  = settings,
            });

            return;
        }

        if (PlaceCombo.SelectedIndex < 0 || PlaceCombo.SelectedIndex >= _places.Count)
        {
            NoticeBanner.Show("Не выбрана коллекция.");
            return;
        }

        var place = _places[PlaceCombo.SelectedIndex];

        if (Settings(choice) is not { } fields) return;

        Close(_step with
        {
            Kind         = ScenarioStepKind.Action,
            Extension    = choice.ExtensionId,
            Action       = choice.Action.Key,
            DbId         = place.DbId,
            CollectionId = place.CollectionId,
            FieldValues  = fields,
        });
    }

    /// <summary>
    /// Поля выбранного действия из набранного; <c>null</c> — не заполнено обязательное, и об
    /// этом уже сказано в окне.
    ///
    /// В шаг уезжают только поля ВЫБРАННОГО действия: набранное для другого осталось бы мусором
    /// в хранении и всплыло бы, если действие когда-нибудь заведет такой же ключ.
    /// </summary>
    private Dictionary<string, string>? Settings(ActionChoice choice)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in choice.Action.Fields)
        {
            var value = (_values.TryGetValue(field.Key, out var text) ? text : "").Trim();

            if (field.Required && value.Length == 0)
            {
                NoticeBanner.Show($"Поле «{field.Label}» обязательно: без него действие не "
                                + "выполнится, и сценарий остановится на этом шаге.");
                return null;
            }

            if (value.Length > 0) fields[field.Key] = value;
        }

        return fields;
    }
}
