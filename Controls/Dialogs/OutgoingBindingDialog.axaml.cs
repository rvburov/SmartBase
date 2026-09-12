using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Core.Models;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>Что человек решил в окне: <c>null</c> внутри — «ничего наружу не отправлять».</summary>
public sealed record OutgoingChoice(OutgoingBinding? Binding);

/// <summary>
/// Окно «Что уходит наружу» — исходящая привязка запроса (docs/ROADMAP-API.md, раздел 2 и 10.6).
/// Зеркало <see cref="IntegrationBindingDialog"/>, и зеркальность тут не ради симметрии: человек,
/// однажды разложивший ответ по полям, узнает то же окно, развернутое в другую сторону.
///
/// Три вещи, которые окно обязано показать до нажатия:
///
/// <list type="number">
///   <item><b>Уходит только отмеченное.</b> Ни «запись целиком», ни «все поля»: состав выгрузки
///   должен читаться списком и не меняться сам собой, когда в коллекции заведут новое
///   свойство.</item>
///   <item><b>Помеченное свойство выбрать нельзя</b> — и видно, ПОЧЕМУ. Убрать его из списка
///   вовсе было бы хуже: человек искал бы пропавшее поле, а не читал причину.</item>
///   <item><b>Вид тела.</b> Чужие программы просят по-разному — голый массив, массив в поле,
///   одна запись объектом, — и угадать за человека нельзя.</item>
/// </list>
///
/// Возвращает <see cref="OutgoingChoice"/>; <c>null</c> — отмена.
/// </summary>
public partial class OutgoingBindingDialog : Window
{
    /// <summary>Строка списка: свойство коллекции и имя, под которым оно уходит.</summary>
    private sealed record FieldRow(int? FieldId, CheckBox Take, TextBox Name);

    private readonly IReadOnlyList<DbOption> _dbs;
    private readonly OutgoingBinding?        _saved;

    private List<(string Name, int Id)> _collections = [];
    private readonly List<FieldRow>     _rows        = [];

    // Перестройка списков сама меняет выбор в комбобоксах: пока она идет, реакции выключены —
    // иначе подсказка считалась бы по полусобранному состоянию.
    private bool _building;

    /// <param name="dbs">Открытые базы, к которым допущен человек.</param>
    /// <param name="saved">Прошлая привязка этого запроса; <c>null</c> — собирается с нуля.</param>
    public OutgoingBindingDialog(IReadOnlyList<DbOption> dbs, OutgoingBinding? saved)
    {
        InitializeComponent();

        _dbs   = dbs;
        _saved = saved;

        ShapeCombo.ItemsSource = new List<string>
        {
            "Массив записей",
            "Массив внутри поля",
            "Одна запись объектом",
        };

        ShapeCombo.SelectedIndex = (int)(saved?.Shape ?? OutgoingShape.Array);
        EnvelopeInput.Text       = saved?.Envelope ?? "";

        DbCombo.ItemsSource   = _dbs.Select(d => d.Name).ToList();
        DbCombo.SelectedIndex = Math.Max(0, IndexOfDb(saved?.DbId ?? 0));

        RebuildCollections(saved?.CollectionId);

        DbCombo.SelectionChanged         += (_, _) => { if (!_building) RebuildCollections(null); };
        CollectionCombo.SelectionChanged += (_, _) => { if (!_building) RebuildFields(); };
        ShapeCombo.SelectionChanged      += (_, _) => { if (!_building) ShowHint(); };
        EnvelopeInput.TextChanged        += (_, _) => { if (!_building) ShowHint(); };

        ClearBtn.Click   += (_, _) => Close(new OutgoingChoice(null));
        CancelBtn.Click  += (_, _) => Close(null);
        ConfirmBtn.Click += (_, _) => Confirm();
        KeyDown          += (_, e) => { if (e.Key == Key.Escape) Close(null); };
    }

    // ── Списки ────────────────────────────────────────────────────────────────

    private DbOption? CurrentDb =>
        DbCombo.SelectedIndex >= 0 && DbCombo.SelectedIndex < _dbs.Count
            ? _dbs[DbCombo.SelectedIndex]
            : null;

    private int CurrentCollectionId =>
        CollectionCombo.SelectedIndex >= 0 && CollectionCombo.SelectedIndex < _collections.Count
            ? _collections[CollectionCombo.SelectedIndex].Id
            : 0;

    private OutgoingShape CurrentShape =>
        ShapeCombo.SelectedIndex >= 0 ? (OutgoingShape)ShapeCombo.SelectedIndex : OutgoingShape.Array;

    private int IndexOfDb(int dbId)
    {
        for (var i = 0; i < _dbs.Count; i++)
            if (_dbs[i].Id == dbId) return i;
        return 0;
    }

    private void RebuildCollections(int? select)
    {
        _building = true;

        _collections = CurrentDb is { } db
            ? db.Repo.GetAllCollections().Select(c => (c.Name, c.Id!.Value)).ToList()
            : [];

        CollectionCombo.ItemsSource = _collections.Select(c => TitleText.FirstLine(c.Name)).ToList();

        var index = select is int id ? _collections.FindIndex(c => c.Id == id) : -1;
        CollectionCombo.SelectedIndex = index >= 0 ? index : (_collections.Count > 0 ? 0 : -1);

        _building = false;
        RebuildFields();
    }

    /// <summary>
    /// Список свойств выбранной коллекции. Прошлая привязка подставляется, только если она про
    /// ЭТУ коллекцию: перенос состава в соседнюю отправил бы наружу свойства с теми же номерами,
    /// но другим смыслом.
    /// </summary>
    private void RebuildFields()
    {
        _building = true;
        _rows.Clear();
        FieldsPanel.Children.Clear();

        var collectionId = CurrentCollectionId;

        if (CurrentDb is not { } db || collectionId <= 0)
        {
            FieldsPanel.Children.Add(Note(_dbs.Count == 0
                ? "Нет открытых баз данных."
                : "В выбранной базе нет коллекций — отправлять нечего."));

            _building = false;
            ShowHint();
            return;
        }

        // Закрытый контур запрещает исходящие целиком и раньше всех прочих проверок: у такой
        // базы просто нет исходящих путей, и собирать привязку не из чего.
        var closed = EgressGate.CheckDatabase(db.Repo.GetDatabaseLabel());

        if (!closed.Allowed)
        {
            FieldsPanel.Children.Add(Note(closed.Explanation, warning: true));

            _building = false;
            ShowHint();
            return;
        }

        var saved = _saved is not null
                 && _saved.DbId == db.Id
                 && _saved.CollectionId == collectionId
            ? _saved
            : null;

        // Название записи — такое же поле выгрузки, как остальные, и идет первым: чаще всего
        // именно оно и есть «номер заказа», ради которого обмен затевали.
        AddRow(null, "Название записи", ConfidentialityLabel.Open, saved);

        // Вычисляемые свойства и связи наружу не идут: первых нет в значениях записи, вторые
        // живут в другой таблице и в JSON превратились бы в номера чужих строк.
        foreach (var field in db.Repo.GetFields(collectionId)
                                .Where(f => f.Id is int
                                         && !FieldTypeInfo.IsComputed(f.FieldType)
                                         && !FieldTypeInfo.IsRelation(f.FieldType)))
            AddRow(field.Id!.Value, TitleText.FirstLine(field.Name),
                   Confidentiality.FromConfig(field.ConfigJson), saved);

        _building = false;
        ShowHint();
    }

    /// <summary>
    /// Одна строка списка. Помеченное свойство показывается серым и с причиной: узнать, что поле
    /// наружу не уходит, надо здесь, а не из пустой колонки у той стороны через неделю.
    /// </summary>
    private void AddRow(int? fieldId, string title, ConfidentialityLabel label, OutgoingBinding? saved)
    {
        var mark    = EgressGate.CheckField(label);
        var chosen  = saved?.Fields.FirstOrDefault(f => f.FieldId == fieldId);

        var take = new CheckBox
        {
            Content           = title,
            IsChecked         = mark.Allowed && chosen is not null,
            IsEnabled         = mark.Allowed,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var name = new TextBox
        {
            Text                     = chosen?.Name ?? title,
            Height                   = 30,
            IsEnabled                = mark.Allowed,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment      = HorizontalAlignment.Stretch,
            Watermark                = "имя в JSON",
        };

        take.IsCheckedChanged += (_, _) => { if (!_building) ShowHint(); };
        name.TextChanged      += (_, _) => { if (!_building) ShowHint(); };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };

        Grid.SetColumn(take, 0);
        Grid.SetColumn(name, 2);
        grid.Children.Add(take);
        grid.Children.Add(name);

        FieldsPanel.Children.Add(grid);

        if (!mark.Allowed)
        {
            FieldsPanel.Children.Add(Note(mark.Explanation));
            return;   // помеченное поле в итог не попадает вовсе
        }

        _rows.Add(new FieldRow(fieldId, take, name));
    }

    private static TextBlock Note(string text, bool warning = false) => new()
    {
        Text         = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize     = 12,
        Foreground   = Brush(warning ? "BrushRed" : "BrushSecondaryText",
                             warning ? "#FF3B30" : "#8E8E93"),
    };

    // ── Итог ──────────────────────────────────────────────────────────────────

    private OutgoingBinding Build() =>
        new(CurrentDb?.Id ?? 0,
            CurrentCollectionId,
            [.. _rows.Where(r => r.Take.IsChecked == true)
                     .Select(r => new OutgoingField(r.FieldId, (r.Name.Text ?? "").Trim()))],
            CurrentShape,
            (EnvelopeInput.Text ?? "").Trim());

    private void Confirm()
    {
        var binding  = Build();
        var problems = binding.Problems();

        if (problems.Count > 0)
        {
            var pos = Position;
            NoticeBanner.Show(string.Join("; ", problems));
            KeepWindowPosition(pos);
            return;
        }

        Close(new OutgoingChoice(binding));
    }

    /// <summary>
    /// Вид тела из выбранного прямо сейчас — со значениями-многоточиями. Состав выгрузки читают
    /// глазами, и читать его надо здесь, до сохранения.
    /// </summary>
    private void ShowHint()
    {
        EnvelopeBox.IsVisible = CurrentShape == OutgoingShape.Wrapped;

        var binding = Build();
        var names   = binding.Fields.Select(f => $"\"{f.Name}\": …").ToList();

        var text = names.Count == 0
            ? "Ни одно поле не отмечено — отправлять нечего."
            : Shaped(binding, "{" + string.Join(", ", names) + "}");

        var pos = Position;
        HintText.Text = text;
        NoticeBanner.Hide();
        KeepWindowPosition(pos);
    }

    private static string Shaped(OutgoingBinding binding, string record) => binding.Shape switch
    {
        OutgoingShape.Single  => record,
        OutgoingShape.Wrapped => "{\"" + (binding.Envelope.Length > 0 ? binding.Envelope : "…")
                                       + "\": [" + record + "]}",
        _                     => "[" + record + "]",
    };

    private static IBrush Brush(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var res) && res is IBrush brush
            ? brush
            : new SolidColorBrush(Color.Parse(fallbackHex));

    private void KeepWindowPosition(PixelPoint pos)
    {
        void Restore(object? _, EventArgs __)
        {
            LayoutUpdated -= Restore;
            if (Position != pos) Position = pos;
        }
        LayoutUpdated += Restore;
    }
}
