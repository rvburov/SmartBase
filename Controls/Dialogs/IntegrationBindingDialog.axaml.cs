using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Core.Models;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно «В коллекцию»: куда ложится ответ чужой программы. Открывается уже с готовым ответом —
/// колонки в списке настоящие, а не придуманные заранее, поэтому раскладку не приходится
/// угадывать по документации.
///
/// Три вещи, которые окно обязано показать до нажатия, а не после:
///
/// <list type="number">
///   <item><b>База данных названа явно.</b> Соединения и запросы общие для всех баз, а коллекции
///   у каждой базы свои — молча положить ответ в ту базу, что открыта сейчас, значит однажды
///   уложить его не туда.</item>
///   <item><b>Что случится с уже заведенными записями.</b> Обмен повторяется, и «заведет дубли»
///   человек должен прочитать до переноса.</item>
///   <item><b>Колонки, которые не переносятся.</b> Чужая программа отдает лишнее почти всегда, и
///   пропуск колонки — обычное дело; но он должен быть виден.</item>
/// </list>
///
/// Возвращает <see cref="CollectionBinding"/> или null при отмене.
/// </summary>
public partial class IntegrationBindingDialog : Window
{
    /// <summary>Куда может лечь одна колонка ответа: никуда, в название записи или в свойство.</summary>
    private sealed record Slot(string Text, bool Transfer, int? FieldId);

    private sealed record MapRow(string Column, ComboBox Combo, List<Slot> Slots);

    private readonly IReadOnlyList<DbOption> _dbs;
    private readonly IReadOnlyList<string>   _columns;
    private readonly CollectionBinding?      _saved;

    private List<(string Name, int Id)> _collections = [];
    private readonly List<MapRow>       _rows        = [];
    private List<string>                _matchOptions = [];

    // Перестройка списков сама меняет выбор в комбобоксах: пока она идет, реакции на выбор
    // выключены — иначе подсказка и список ключей считались бы по полусобранному состоянию.
    private bool _building;

    /// <param name="dbs">Открытые базы, к которым допущен человек. Пустой список сюда не
    /// доводится: окно без базы бесполезно, и вызывающий код говорит об этом раньше.</param>
    /// <param name="columns">Колонки ответа в порядке схемы источника; первая — та, что стала
    /// названием записи.</param>
    /// <param name="rows">Сколько записей в ответе — для строки над раскладкой.</param>
    /// <param name="saved">Прошлая привязка этого запроса; null — раскладка подбирается по
    /// совпадению имен.</param>
    public IntegrationBindingDialog(
        IReadOnlyList<DbOption> dbs,
        IReadOnlyList<string>   columns,
        long                    rows,
        CollectionBinding?      saved)
    {
        InitializeComponent();

        _dbs     = dbs;
        _columns = columns;
        _saved   = saved;

        AnswerText.Text = $"В ответе записей: {rows:N0} · колонок: {columns.Count}. "
                        + "Колонка, для которой не выбрано место, не переносится.";

        UpdateCheck.IsChecked = saved?.UpdateExisting ?? true;
        CreateCheck.IsChecked = saved?.CreateMissing  ?? true;

        DbCombo.ItemsSource   = _dbs.Select(d => d.Name).ToList();
        DbCombo.SelectedIndex = Math.Max(0, IndexOfDb(saved?.DbId ?? 0));

        RebuildCollections(saved?.CollectionId);

        DbCombo.SelectionChanged         += (_, _) => { if (!_building) RebuildCollections(null); };
        CollectionCombo.SelectionChanged += (_, _) => { if (!_building) RebuildMap(); };
        MatchCombo.SelectionChanged      += (_, _) => { if (!_building) ShowHint(); };
        UpdateCheck.IsCheckedChanged     += (_, _) => ShowHint();
        CreateCheck.IsCheckedChanged     += (_, _) => ShowHint();

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
        RebuildMap();
    }

    /// <summary>
    /// Раскладка колонок под выбранную коллекцию. Прошлая привязка подставляется только если
    /// она про ЭТУ коллекцию: перенос раскладки в соседнюю коллекцию сложил бы значения в
    /// свойства с теми же номерами, но другим смыслом.
    /// </summary>
    private void RebuildMap()
    {
        _building = true;
        _rows.Clear();
        MapPanel.Children.Clear();

        var collectionId = CurrentCollectionId;

        if (CurrentDb is not { } db || collectionId <= 0)
        {
            MapPanel.Children.Add(new TextBlock
            {
                Text         = _dbs.Count == 0
                    ? "Нет открытых баз данных."
                    : "В выбранной базе нет коллекций — ответ некуда положить.",
                TextWrapping = TextWrapping.Wrap,
                Foreground   = Brush("BrushSecondaryText", "#8E8E93"),
            });

            _building = false;
            RebuildMatches();
            return;
        }

        // Вычисляемые свойства (дата создания, автор правки) значения не хранят — они берутся
        // из метаданных записи; свойства-связи хранят его в другой таблице. Ни то, ни другое
        // обменом не заполняется, и предлагать их значило бы обещать несбыточное.
        var fields = db.Repo.GetFields(collectionId)
            .Where(f => f.Id is int
                     && !FieldTypeInfo.IsComputed(f.FieldType)
                     && !FieldTypeInfo.IsRelation(f.FieldType))
            .ToList();

        var slots = new List<Slot> { new("Не переносить", false, null), new("Название записи", true, null) };
        slots.AddRange(fields.Select(f => new Slot(TitleText.FirstLine(f.Name), true, f.Id!.Value)));

        var saved = _saved is not null
                 && _saved.DbId == db.Id
                 && _saved.CollectionId == collectionId
            ? _saved
            : null;

        foreach (var column in _columns)
        {
            var combo = new ComboBox
            {
                ItemsSource         = slots.Select(s => s.Text).ToList(),
                Height              = 30,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
            };

            combo.SelectedIndex = DefaultSlot(column, slots, saved);
            // Перестройка списка ключей сама обновляет подсказку — звать ее отдельно незачем.
            combo.SelectionChanged += (_, _) => { if (!_building) RebuildMatches(); };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,*") };

            var name = new TextBlock
            {
                Text              = column,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming      = TextTrimming.CharacterEllipsis,
            };

            Grid.SetColumn(name, 0);
            Grid.SetColumn(combo, 2);
            grid.Children.Add(name);
            grid.Children.Add(combo);

            MapPanel.Children.Add(grid);
            _rows.Add(new MapRow(column, combo, slots));
        }

        _building = false;
        RebuildMatches();
    }

    /// <summary>
    /// Куда колонка ложится по умолчанию: прошлая привязка, иначе свойство с тем же именем,
    /// иначе название записи для первой колонки ответа, иначе никуда. Угадывание тут уместно:
    /// раскладка видна целиком и правится одним щелчком.
    /// </summary>
    private int DefaultSlot(string column, List<Slot> slots, CollectionBinding? saved)
    {
        if (saved is not null)
        {
            var bound = saved.Columns.FirstOrDefault(
                c => string.Equals(c.Column, column, StringComparison.OrdinalIgnoreCase));

            if (bound is null) return 0;

            var index = slots.FindIndex(s => s.Transfer && s.FieldId == bound.FieldId);
            return index >= 0 ? index : 0;
        }

        var byName = slots.FindIndex(
            s => s.Transfer && s.FieldId is not null
              && string.Equals(s.Text, column, StringComparison.OrdinalIgnoreCase));

        if (byName >= 0) return byName;

        // Первая колонка схемы — та, которую источник выбрал названием записи.
        return _columns.Count > 0
            && string.Equals(_columns[0], column, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    /// <summary>
    /// Список «Искать записи по» — только из переносимых колонок: искать по тому, чего в
    /// коллекции не будет, невозможно. Выбор сохраняется по имени, а не по номеру.
    /// </summary>
    private void RebuildMatches()
    {
        _building = true;

        var previous = MatchCombo.SelectedIndex >= 0 && MatchCombo.SelectedIndex < _matchOptions.Count
            ? _matchOptions[MatchCombo.SelectedIndex]
            : _saved?.MatchColumn ?? "";

        _matchOptions = [""];
        _matchOptions.AddRange(_rows.Where(r => Chosen(r).Transfer).Select(r => r.Column));

        MatchCombo.ItemsSource = _matchOptions
            .Select(c => c.Length == 0 ? "Не искать — заводить все" : c)
            .ToList();

        var index = _matchOptions.FindIndex(
            c => string.Equals(c, previous, StringComparison.OrdinalIgnoreCase));

        MatchCombo.SelectedIndex = index >= 0 ? index : 0;

        _building = false;
        ShowHint();
    }

    private static Slot Chosen(MapRow row) =>
        row.Combo.SelectedIndex >= 0 && row.Combo.SelectedIndex < row.Slots.Count
            ? row.Slots[row.Combo.SelectedIndex]
            : row.Slots[0];

    // ── Итог ──────────────────────────────────────────────────────────────────

    private CollectionBinding Build() =>
        new(CurrentDb?.Id ?? 0,
            CurrentCollectionId,
            [.. _rows.Where(r => Chosen(r).Transfer)
                     .Select(r => new BindingColumn(r.Column, Chosen(r).FieldId))],
            MatchCombo.SelectedIndex >= 0 && MatchCombo.SelectedIndex < _matchOptions.Count
                ? _matchOptions[MatchCombo.SelectedIndex]
                : "",
            UpdateCheck.IsChecked == true,
            CreateCheck.IsChecked == true);

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

        Close(binding);
    }

    /// <summary>Что именно случится при переносе — словами и до нажатия.</summary>
    private void ShowHint()
    {
        var binding = Build();

        var text = !binding.Matches
            ? "Каждая строка ответа станет НОВОЙ записью. Повторный запуск заведет их снова — "
            + "для обмена, который повторяется, выберите, по чему искать уже заведенные."
            : (binding.UpdateExisting, binding.CreateMissing) switch
            {
                (true,  true)  => $"Записи ищутся по «{binding.MatchColumn}»: найденные обновляются, "
                                + "ненайденные заводятся.",
                (true,  false) => $"Записи ищутся по «{binding.MatchColumn}»: найденные обновляются, "
                                + "ненайденные пропускаются.",
                (false, true)  => $"Записи ищутся по «{binding.MatchColumn}»: найденные не трогаются, "
                                + "заводятся только ненайденные.",
                _              => "Ни обновление найденных, ни создание новых не выбрано — "
                                + "переносить нечего.",
            };

        if (binding.Matches)
            text += " Строки с пустым ключом и записи, у которых такое значение ключа в коллекции "
                  + "не одно, не трогаются — о них будет сказано в итоге.";

        var pos = Position;
        HintText.Text = text;
        NoticeBanner.Hide();
        KeepWindowPosition(pos);
    }

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
