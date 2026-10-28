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
///   <item><b>Вид тела.</b> Сторонние программы просят по-разному — голый массив, массив в поле,
///   одна запись объектом, — и угадать за человека нельзя.</item>
/// </list>
///
/// Возвращает <see cref="OutgoingChoice"/>; <c>null</c> — отмена.
/// </summary>
public partial class OutgoingBindingDialog : Window
{
    private readonly IReadOnlyList<DbOption> _dbs;
    private readonly OutgoingBinding?        _saved;

    private List<(string Name, int Id)>     _collections = [];
    private readonly List<OutgoingFieldRow> _rows        = [];

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

        _tests = saved?.Tests ?? [];

        RebuildCollections(saved?.CollectionId);
        ShowTests();

        DbCombo.SelectionChanged         += (_, _) => { if (!_building) RebuildCollections(null); };
        CollectionCombo.SelectionChanged += (_, _) => { if (!_building) RebuildFields(); };
        ShapeCombo.SelectionChanged      += (_, _) => { if (!_building) ShowHint(); };
        EnvelopeInput.TextChanged        += (_, _) => { if (!_building) ShowHint(); };

        SearchBox.TextChanged += (_, _) => ShowFound();

        FilterBtn.Click  += async (_, _) => await EditTestsAsync();
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
        FieldItems.ItemsSource = null;

        var collectionId = CurrentCollectionId;

        if (CurrentDb is not { } db || collectionId <= 0)
        {
            Empty(_dbs.Count == 0
                ? "Нет открытых баз данных."
                : "В выбранной базе нет коллекций — отправлять нечего.");

            _building = false;
            ShowFound();
            ShowHint();
            return;
        }

        // Закрытый контур запрещает исходящие целиком и раньше всех прочих проверок: у такой
        // базы просто нет исходящих путей, и собирать привязку не из чего.
        var closed = EgressGate.CheckDatabase(db.Repo.GetDatabaseLabel());

        if (!closed.Allowed)
        {
            Empty(closed.Explanation, warning: true);

            _building = false;
            ShowFound();
            ShowHint();
            return;
        }

        var saved = _saved is not null
                 && _saved.DbId == db.Id
                 && _saved.CollectionId == collectionId
            ? _saved
            : null;

        // Отбор привязан к свойствам ЭТОЙ коллекции и вместе с ней же и сбрасывается: условие
        // «Статус равно Отгружен», перенесенное в соседнюю коллекцию, отобрало бы там не то, а
        // при отсутствии свойства «Статус» — вовсе остановило бы обмен словами о том, чего
        // человек не делал.
        if (saved is null && _tests.Count > 0)
        {
            _tests = [];
            ShowTests();
        }

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

        FieldsEmpty.IsVisible = false;

        _building = false;
        ShowFound();
        ShowHint();
    }

    /// <summary>
    /// Одна строка списка. Помеченное свойство остается в списке, но выбрать его нельзя, и рядом
    /// написано почему: узнать, что поле наружу не уходит, надо здесь, а не из пустой колонки у
    /// той стороны через неделю.
    /// </summary>
    private void AddRow(int? fieldId, string title, ConfidentialityLabel label, OutgoingBinding? saved)
    {
        var mark   = EgressGate.CheckField(label);
        var chosen = saved?.Fields.FirstOrDefault(f => f.FieldId == fieldId);

        _rows.Add(new OutgoingFieldRow(fieldId ?? 0, title, mark.Allowed)
        {
            Takes  = mark.Allowed && chosen is not null,
            Alias  = chosen?.Name ?? title,
            Note   = mark.Allowed ? "" : mark.Explanation,
            Picked = () => { if (!_building) ShowHint(); },
        });
    }

    /// <summary>Списку нечего показать — причина словами вместо пустой рамки.</summary>
    private void Empty(string text, bool warning = false)
    {
        FieldsEmpty.Text       = text;
        FieldsEmpty.Foreground = Brush(warning ? "BrushRed" : "BrushSecondaryText",
                                       warning ? "#FF3B30" : "#8E8E93");
        FieldsEmpty.IsVisible  = true;
    }

    // ── Поиск по свойствам ────────────────────────────────────────────────────

    /// <summary>
    /// Показать строки, подходящие под поиск. Отбор — только показ: состав выгрузки собирается
    /// из ВСЕХ строк, иначе набранное слово молча снимало бы отметки с остальных.
    /// </summary>
    private void ShowFound()
    {
        var needle = (SearchBox.Text ?? "").Trim();

        var shown = needle.Length == 0
            ? _rows
            : [.. _rows.Where(r => r.Title.Contains(needle, StringComparison.CurrentCultureIgnoreCase))];

        FieldItems.ItemsSource = shown;

        ShowCount(shown.Count, needle);

        if (needle.Length > 0 && shown.Count == 0) Empty($"Свойств со словом «{needle}» здесь нет.");
        else if (_rows.Count > 0)                  FieldsEmpty.IsVisible = false;
    }

    /// <summary>
    /// Счетчик рядом с поиском. Отдельно от <see cref="ShowFound"/> намеренно: галочка меняет
    /// число отмеченных, но не состав списка, а пере-присвоение <c>ItemsSource</c> на каждый щелчок
    /// отматывало бы прокрутку к началу.
    /// </summary>
    private void ShowCount(int shown, string needle) =>
        FoundText.Text = needle.Length == 0
            ? (_rows.Count > 0 ? $"отмечено {_rows.Count(r => r.Takes)} из {_rows.Count}" : "")
            : $"найдено {shown} из {_rows.Count}";

    // ── Итог ──────────────────────────────────────────────────────────────────

    private OutgoingBinding Build() =>
        new(CurrentDb?.Id ?? 0,
            CurrentCollectionId,
            [.. _rows.Where(r => r.Allowed && r.Takes)
                     .Select(r => new OutgoingField(r.FieldId == 0 ? null : r.FieldId, r.Alias.Trim()))],
            CurrentShape,
            (EnvelopeInput.Text ?? "").Trim(),
            _tests);

    // ── Отбор ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Какие записи уходят; пусто — вся коллекция. Отбор правится своим окном и живет здесь, а
    /// не в строках списка полей: он относится к привязке целиком.
    /// </summary>
    private IReadOnlyList<ScenarioCondition> _tests = [];

    /// <summary>
    /// Открыть правку отбора. Колонки — свойства ТЕКУЩЕЙ коллекции, включая те, что наружу не
    /// идут: «Отправлено пусто» — обычный способ не слать одно и то же дважды, и требовать ради
    /// такого условия отправлять служебное поле было бы прямо вредно.
    ///
    /// Помеченных свойств в списке нет: условие по закрытому полю не выносит наружу значение, но
    /// выносит ЗНАНИЕ о нем — по тому, какие записи ушли, оно и восстанавливается.
    /// </summary>
    private async Task EditTestsAsync()
    {
        var columns = _rows.Where(r => r.Allowed).Select(r => r.Title).ToList();

        if (columns.Count == 0)
        {
            var empty = Position;
            NoticeBanner.Show("Сначала выберите коллекцию: отбирать пока не по чему.");
            KeepWindowPosition(empty);
            return;
        }

        if (await new OutgoingFilterDialog(columns, _tests)
                .ShowDialog<IReadOnlyList<ScenarioCondition>?>(this) is not { } chosen) return;

        _tests = chosen;
        ShowTests();
        ShowHint();
    }

    // Окно этого класса не растет по содержимому (заданы Width/Height), поэтому удерживать
    // позицию, как в окнах с SizeToContent, здесь не нужно и нечем: в конструкторе Position
    // еще не тот, и «удержание» уехало бы в левый верхний угол экрана.
    private void ShowTests() =>
        FilterText.Text = _tests.Count == 0
            ? "Уходит вся коллекция."
            : "Уйдут записи, у которых " + string.Join(" и ", _tests.Select(t => t.ToString())) + ".";

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

        // Счетчик отмеченного живет рядом с поиском и считается от ВСЕХ строк, а не от видимых:
        // человек ищет одно поле, а отправляет десять. Список при этом не трогаем — прокрутка
        // должна остаться там, где человек ее оставил.
        if (!_building)
            ShowCount(FieldItems.ItemsSource?.Cast<object>().Count() ?? _rows.Count,
                      (SearchBox.Text ?? "").Trim());

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

    private void KeepWindowPosition(PixelPoint pos) =>
        SmartBase.Controls.Behaviors.WindowPlacement.KeepPositionAfterLayout(this, pos);
}
