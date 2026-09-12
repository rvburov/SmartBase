using Avalonia.Controls;
using SmartBase.Database.Repositories;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>Что человек набрал в окне выдачи — экрану на подтверждение.</summary>
/// <param name="TokenId">Кому разрешается читать.</param>
/// <param name="CollectionId">Что читать — ключ коллекции.</param>
/// <param name="Collection">Имя коллекции — для следа.</param>
/// <param name="FieldKeys">Состав: <c>t</c> и ключи свойств.</param>
/// <param name="Fields">Имена состава — для следа.</param>
/// <param name="Days">На сколько дней.</param>
/// <param name="Password">Мастер-пароль — окно его не проверяет и не хранит.</param>
public sealed record GrantChoice(int TokenId, int CollectionId, string Collection,
                                 IReadOnlyList<string> FieldKeys, string Fields,
                                 int Days, string Password);

/// <summary>
/// Выдача чтения: токен, коллекция, поля, срок и мастер-пароль (docs/ROADMAP-API.md, шаг 10).
///
/// Окно само ничего не решает — как окно направления: пароль уезжает реестру, который
/// спрашивает политику. Ошибка НЕ закрывает окно: неверный пароль — обычная опечатка.
///
/// Поля по умолчанию ВЫКЛЮЧЕНЫ, отмечено только название записи: выдача — осознанный выбор
/// состава, а не «все, что есть». Галка «всего состава разом» не заведена намеренно — она
/// превращала бы осознанный выбор в привычный щелчок.
/// </summary>
public partial class ReadGrantDialog : Window
{
    private static readonly (int Days, string Text)[] Terms =
    [
        (30,  "30 дней"),
        (90,  "90 дней"),
        (180, "полгода"),
        (365, "год"),
    ];

    private readonly IReadOnlyList<(int TokenId, string Title)>                  _tokens;
    private readonly Func<int, IReadOnlyList<(int Id, string Name)>>             _collections;
    private readonly Func<int, int, IReadOnlyList<ConstructorRepository.GrantColumn>> _columns;
    private readonly Func<GrantChoice, string?>                                  _confirm;

    private IReadOnlyList<(int Id, string Name)> _shownCollections = [];
    private readonly List<(string Key, CheckBox Box)> _shownFields = [];

    public ReadGrantDialog()
        : this([], _ => [], (_, _) => [], _ => "разрешать нечего") { }

    /// <param name="tokens">Токены открытых баз — «имя — база».</param>
    /// <param name="collections">Коллекции базы токена.</param>
    /// <param name="columns">Колонки коллекции: токен, коллекция → состав с причинами отказа.</param>
    /// <param name="confirm">Проверка и запись; возвращает причину отказа либо <c>null</c>.</param>
    public ReadGrantDialog(IReadOnlyList<(int TokenId, string Title)> tokens,
                           Func<int, IReadOnlyList<(int Id, string Name)>> collections,
                           Func<int, int, IReadOnlyList<ConstructorRepository.GrantColumn>> columns,
                           Func<GrantChoice, string?> confirm)
    {
        InitializeComponent();

        _tokens      = tokens;
        _collections = collections;
        _columns     = columns;
        _confirm     = confirm;

        TokenCombo.ItemsSource   = tokens.Select(t => t.Title).ToList();
        TokenCombo.SelectedIndex = tokens.Count > 0 ? 0 : -1;

        DaysCombo.ItemsSource   = Terms.Select(t => t.Text).ToList();
        DaysCombo.SelectedIndex = 1;   // 90 дней — срок, на котором о выдаче еще помнят

        IdleHint.Text = $"Выдача отключится сама, если ею не пользоваться {ReadGrant.IdleDays} "
                      + "дней: настроенное и забытое чтение — открытая дверь, о которой никто "
                      + "не думает.";

        TokenCombo.SelectionChanged      += (_, _) => ReloadCollections();
        CollectionCombo.SelectionChanged += (_, _) => ReloadFields();

        CancelBtn.Click += (_, _) => Close(false);
        SaveBtn.Click   += (_, _) => Save();

        ReloadCollections();
    }

    private int ChosenToken =>
        TokenCombo.SelectedIndex >= 0 && TokenCombo.SelectedIndex < _tokens.Count
            ? _tokens[TokenCombo.SelectedIndex].TokenId
            : 0;

    private void ReloadCollections()
    {
        _shownCollections = ChosenToken > 0 ? _collections(ChosenToken) : [];

        CollectionCombo.ItemsSource   = _shownCollections.Select(c => c.Name).ToList();
        CollectionCombo.SelectedIndex = _shownCollections.Count > 0 ? 0 : -1;

        ReloadFields();
    }

    private void ReloadFields()
    {
        FieldsPanel.Children.Clear();
        _shownFields.Clear();

        var index = CollectionCombo.SelectedIndex;
        if (ChosenToken <= 0 || index < 0 || index >= _shownCollections.Count) return;

        foreach (var column in _columns(ChosenToken, _shownCollections[index].Id))
        {
            var box = new CheckBox
            {
                Content   = column.Trouble is null
                    ? column.Name
                    : $"{column.Name} — {column.Trouble}",
                IsChecked = column.Trouble is null && column.Key == ReadGrant.TitleKey,
                IsEnabled = column.Trouble is null,
            };

            FieldsPanel.Children.Add(box);
            _shownFields.Add((column.Key, box));
        }
    }

    private void Save()
    {
        var index = CollectionCombo.SelectedIndex;

        if (ChosenToken <= 0 || index < 0 || index >= _shownCollections.Count)
        {
            NoticeBanner.Show("Выберите токен и коллекцию.");
            return;
        }

        var chosen = _shownFields.Where(f => f.Box.IsChecked == true).ToList();

        if (chosen.Count == 0)
        {
            NoticeBanner.Show("Отметьте хотя бы одно поле — иначе отдавать нечего.");
            return;
        }

        var password = PasswordBox.Text ?? "";

        if (password.Length == 0)
        {
            NoticeBanner.Show("Введите мастер-пароль организации.");
            return;
        }

        var choice = new GrantChoice(
            ChosenToken,
            _shownCollections[index].Id,
            _shownCollections[index].Name,
            [.. chosen.Select(f => f.Key)],
            string.Join(", ", chosen.Select(f => TextOf(f.Box))),
            Terms[Math.Clamp(DaysCombo.SelectedIndex, 0, Terms.Length - 1)].Days,
            password);

        if (_confirm(choice) is { Length: > 0 } trouble)
        {
            NoticeBanner.Show(char.ToUpperInvariant(trouble[0]) + trouble[1..] + ".");
            return;
        }

        Close(true);
    }

    private static string TextOf(CheckBox box) => box.Content?.ToString() ?? "";
}
