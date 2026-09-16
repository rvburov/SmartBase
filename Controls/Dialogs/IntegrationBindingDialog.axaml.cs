using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Core.Models;
using SmartBase.Integrations;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно «В коллекцию»: куда ложится ответ сторонней программы. Открывается уже с готовым ответом —
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
///   <item><b>Колонки, которые не переносятся.</b> Сторонняя программа отдает лишнее почти всегда, и
///   пропуск колонки — обычное дело; но он должен быть виден.</item>
/// </list>
///
/// Возвращает <see cref="CollectionBinding"/> или null при отмене.
/// </summary>
public partial class IntegrationBindingDialog : Window
{
    /// <summary>
    /// Куда может лечь одна колонка ответа: никуда, в название записи, в существующее свойство —
    /// или в НОВОЕ, которое программа заведет при подтверждении.
    /// </summary>
    /// <param name="Create">Завести свойство с именем колонки. Раздел 2 обещает это с самого
    /// начала: сторонняя программа отдает колонку, которой в коллекции нет, и выбор «не переносить или
    /// заводить поле руками в другом окне» — это не выбор, а отправка человека по кругу.</param>
    private sealed record Slot(string Text, bool Transfer, int? FieldId, bool Create = false);

    private readonly IReadOnlyList<DbOption> _dbs;
    private readonly IReadOnlyList<string>   _columns;
    private readonly CollectionBinding?      _saved;

    // Настоящие значения колонок из первых строк ответа: по одной готовой строке на колонку.
    // Готовятся в конструкторе, а не при каждой перестройке списка: раскладку пересобирают на
    // каждый выбор коллекции, а образцы от него не зависят.
    private readonly IReadOnlyList<string> _samples;

    private List<(string Name, int Id)> _collections  = [];
    private readonly List<BindingMapRow> _rows        = [];
    private List<Slot>                   _slots       = [];
    private List<string>                 _matchOptions = [];

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
    /// <param name="sample">Первые строки ответа — значения в порядке <paramref name="columns"/>.
    /// Раздел 2 обещает «показ первых 20 строк», и обещает не ради полноты: раскладка подбирается
    /// ПО ИМЕНАМ, а у сторонних программ имена похожи до неразличимости, и ошибку в них без значений
    /// видно только после укладки — то есть по испорченной коллекции.</param>
    public IntegrationBindingDialog(
        IReadOnlyList<DbOption> dbs,
        IReadOnlyList<string>   columns,
        long                    rows,
        CollectionBinding?      saved,
        IReadOnlyList<IReadOnlyList<string>>? sample = null)
    {
        InitializeComponent();

        _dbs     = dbs;
        _columns = columns;
        _saved   = saved;
        _samples = BindingSample.Of(columns, sample ?? []);

        AnswerText.Text = $"В ответе записей: {rows:N0} · колонок: {columns.Count}. "
                        + "Под именем колонки — ее значения из первых строк ответа: по ним видно, "
                        + "та ли это колонка. Колонка, для которой не выбрано место, не переносится.";

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

        SearchBox.TextChanged += (_, _) => ShowFound();

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
        MapItems.ItemsSource = null;

        var collectionId = CurrentCollectionId;

        if (CurrentDb is not { } db || collectionId <= 0)
        {
            MapEmpty.Text = _dbs.Count == 0
                ? "Нет открытых баз данных."
                : "В выбранной базе нет коллекций — ответ некуда положить.";

            MapEmpty.IsVisible = true;

            _building = false;
            ShowFound();
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

        _slots = [new Slot("Не переносить", false, null), new Slot("Название записи", true, null)];
        _slots.AddRange(fields.Select(f => new Slot(TitleText.FirstLine(f.Name), true, f.Id!.Value)));

        // Последним — «завести свойство». Последним намеренно: сперва человек ищет готовое место,
        // и новое поле должно быть тем, что выбирают осознанно, а не тем, на что падает глаз.
        //
        // Тип — ТЕКСТ, как у импорта файла, и это не упрощение: импорт заводит текстовые поля
        // ровно так же. Тип у свойства меняют одним щелчком в окне коллекции, а угаданный неверно
        // тип молча портит значения при первой же укладке.
        _slots.Add(new Slot("Завести свойство с этим именем (текст)", true, null, Create: true));

        var slots   = _slots;
        var options = slots.Select(s => s.Text).ToList();

        var saved = _saved is not null
                 && _saved.DbId == db.Id
                 && _saved.CollectionId == collectionId
            ? _saved
            : null;

        for (var i = 0; i < _columns.Count; i++)
            _rows.Add(new BindingMapRow(_columns[i], options,
                                        DefaultSlot(_columns[i], slots, saved),
                                        i < _samples.Count ? _samples[i] : "")
            {
                // Перестройка списка ключей сама обновляет подсказку — звать ее отдельно незачем.
                Picked = () => { if (!_building) RebuildMatches(); },
            });

        MapEmpty.IsVisible = false;

        _building = false;
        ShowFound();
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

    private Slot Chosen(BindingMapRow row) =>
        row.Selected >= 0 && row.Selected < _slots.Count ? _slots[row.Selected] : _slots[0];

    // ── Поиск по колонкам ─────────────────────────────────────────────────────

    /// <summary>
    /// Показать строки, подходящие под поиск, и сказать, сколько их. Отбор — только показ:
    /// раскладка собирается из ВСЕХ строк, а не из видимых, иначе набранное в поиске слово
    /// молча отменяло бы настроенное для остальных колонок.
    /// </summary>
    private void ShowFound()
    {
        var needle = (SearchBox.Text ?? "").Trim();

        var shown = needle.Length == 0
            ? _rows
            : [.. _rows.Where(r => r.Column.Contains(needle, StringComparison.CurrentCultureIgnoreCase))];

        MapItems.ItemsSource = shown;

        FoundText.Text = needle.Length == 0
            ? (_rows.Count > 0 ? $"колонок: {_rows.Count}" : "")
            : $"найдено {shown.Count} из {_rows.Count}";

        // Искали и не нашли — это надо сказать: пустой список читается как «колонок нет вовсе».
        if (needle.Length > 0 && shown.Count == 0)
        {
            MapEmpty.Text      = $"Колонок со словом «{needle}» в ответе нет.";
            MapEmpty.IsVisible = true;
        }
        else if (_rows.Count > 0)
        {
            MapEmpty.IsVisible = false;
        }
    }

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
        // Новые свойства заводятся ДО сборки привязки: она хранит ключи полей, а их до создания
        // не существует. Не вышло — окно остается открытым с причиной: закрыться, не сделав
        // обещанного, значило бы уложить ответ мимо половины колонок и не сказать об этом.
        if (!MakeFields(out var trouble))
        {
            var spot = Position;
            NoticeBanner.Show(trouble);
            KeepWindowPosition(spot);
            return;
        }

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

    /// <summary>
    /// Завести свойства для колонок, которым выбрано «завести свойство». Возвращает <c>false</c> и
    /// причину, если хоть одно не завелось.
    ///
    /// <b>Заводятся они здесь, а не при укладке.</b> Привязка хранит КЛЮЧИ полей, и до создания
    /// ключа нет; отложи создание — и привязку пришлось бы учить хранить «а еще заведи вот это»,
    /// то есть класть одноразовое поручение в настройку, которая живет годами.
    ///
    /// Права спрашивает сам репозиторий (<c>AddField</c> требует «Создание свойства»), и отказ
    /// приходит сюда исключением — его человек читает строкой в окне, а не молчаливым пропуском
    /// колонки.
    /// </summary>
    private bool MakeFields(out string trouble)
    {
        trouble = "";

        if (CurrentDb is not { } db) return true;

        var collectionId = CurrentCollectionId;
        if (collectionId <= 0) return true;

        var wanted = _rows.Where(r => Chosen(r).Create).ToList();
        if (wanted.Count == 0) return true;

        var made = new List<(BindingMapRow Row, int FieldId)>();

        try
        {
            var position = db.Repo.GetFields(collectionId).Count * 1000 + 1000;

            foreach (var row in wanted)
            {
                var field = db.Repo.AddField(new Core.Models.DbField
                {
                    CollectionId = collectionId,
                    Name         = row.Column.Trim(),
                    FieldType    = "text",
                    Position     = position,
                });

                position += 1000;
                made.Add((row, field.Id!.Value));
            }
        }
        catch (Exception failure) when (failure is InvalidOperationException
                                               or PermissionDeniedException)
        {
            // Часть полей могла завестись — они остаются: удалять их обратно значило бы
            // разбирать чужую коллекцию из-за неудачи на третьей колонке. Зато выбор в списке
            // уже переставлен на них, и повторное нажатие не заведет второй копии.
            Pick(made);
            trouble = "свойство не завелось: " + failure.Message;
            return false;
        }

        Pick(made);
        return true;
    }

    /// <summary>
    /// Перевести строки с «завести свойство» на только что заведенные поля. Делается и при удаче,
    /// и при неудаче: поле, которое уже существует, обязано выбираться как существующее — иначе
    /// повторное нажатие попыталось бы завести его второй раз.
    /// </summary>
    private void Pick(IReadOnlyList<(BindingMapRow Row, int FieldId)> made)
    {
        if (made.Count == 0) return;

        _building = true;
        RebuildMap();
        _building = true;

        foreach (var row in _rows)
        {
            var known = made.FirstOrDefault(m => string.Equals(m.Row.Column, row.Column,
                                                               StringComparison.Ordinal));

            if (known.Row is null) continue;

            var slot = _slots.FindIndex(s => s.Transfer && s.FieldId == known.FieldId);
            if (slot >= 0) row.Selected = slot;
        }

        _building = false;
        RebuildMatches();
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
