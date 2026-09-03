using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Core.Validators;
using SmartBase.Database.Repositories;

namespace SmartBase.Controls.Dialogs;

// Record.FieldId — выбранное поле-дата (куда кладется дата события). Контроллер при
// изменении сравнивает его с прежним полем и при смене переносит дату (очищает старое).
public record RecordDialogResult(CalendarRecord? Record, bool Deleted = false);

public partial class RecordDialog : Window
{
    // ── Общие зависимости ─────────────────────────────────────────────────────
    private readonly ConstructorRepository _repository;

    // ── Схема-режим (конструктор) ─────────────────────────────────────────────
    private readonly int  _collectionId;
    private int?          _recordId;
    private readonly Dictionary<int, (DbField Field, Control Input)> _fieldWidgets = [];

    // ── Календарь-режим ───────────────────────────────────────────────────────
    private readonly CalendarRecord? _editRecord;
    private readonly List<(string Name, int? Id, string Color)> _collections = [];
    // Смонтированные БД и выбранная цель (выбор перенесен сюда из отдельного окна).
    private readonly List<(int DbId, string Name, ConstructorRepository Repo)> _databases = [];
    private int _selectedDbId;
    // Поля-даты выбранной коллекции (для списка «Дата события» при 2+ датах).
    private List<DbField> _eventDateFields = [];
    private TimeSpan _savedStartTime = new(9, 0, 0);
    private TimeSpan _savedEndTime   = new(10, 0, 0);

    // ── Конструктор: схема-режим ──────────────────────────────────────────────
    public RecordDialog(ConstructorRepository repository, int collectionId, int? recordId = null)
    {
        InitializeComponent();

        _repository   = repository;
        _collectionId = collectionId;
        _recordId     = recordId;

        FieldsPanel.IsVisible    = true;
        CalendarSection.IsVisible = false;

        if (recordId.HasValue)
        {
            Title = "Изменить запись";
            DeleteBtn.IsVisible = true;
        }

        BuildFields();
        if (recordId.HasValue) FillValues(recordId.Value);

        CancelBtn.Click += (_, _) => Close(null);
        DeleteBtn.Click += async (_, _) =>
        {
            if (_recordId is not { } rid) return;
            // Предупреждаем, если на запись ссылаются связи (внутрибазовые — их и покажем; окно только
            // при наличии). Межбазовые входящие из других баз здесь не проверяем (окну не передан DbId).
            if (_repository.HasIncomingLinks([rid], 0))
            {
                var warn = new ConfirmDialog("Удалить запись? На нее ссылаются связи — они будут удалены.");
                if (await warn.ShowDialog<bool>(this) != true) return;
            }
            _repository.DeleteRecord(rid);
            Close(false);
        };
        SaveBtn.Click   += (_, _) => TrySaveSchema();
    }

    // ── Конструктор: календарь-режим, выбор целевой БД в самом окне ────────────
    public RecordDialog(
        IReadOnlyList<(int DbId, string Name, ConstructorRepository Repo)> databases,
        int selectedDbId,
        CalendarRecord? editRecord = null,
        DateTime? presetDate       = null,
        TimeSpan? presetTime       = null,
        bool presetAllDay          = false)
    {
        InitializeComponent();

        _databases.AddRange(databases);
        _selectedDbId = _databases.Any(d => d.DbId == selectedDbId) ? selectedDbId : _databases[0].DbId;
        _repository   = CurrentRepo;
        _editRecord   = editRecord;
        _collectionId = 0;

        FieldsPanel.IsVisible     = false;
        CalendarSection.IsVisible = true;

        // Выбор БД показываем, когда смонтировано несколько баз (и при создании, и при
        // изменении — там смена БД означает перенос записи). При смене БД список коллекций
        // перезагружается из выбранной базы.
        var showDbPicker = _databases.Count > 1;
        DbSection.IsVisible = showDbPicker;
        if (showDbPicker)
        {
            DbCombo.ItemsSource   = _databases.Select(d => d.Name).ToList();
            DbCombo.SelectedIndex = _databases.FindIndex(d => d.DbId == _selectedDbId);
            DbCombo.SelectionChanged += (_, _) =>
            {
                var idx = DbCombo.SelectedIndex;
                if (idx < 0 || idx >= _databases.Count) return;
                _selectedDbId = _databases[idx].DbId;
                LoadCollections(_databases[idx].Repo);
            };
        }

        LoadCollections(CurrentRepo);

        var now = presetDate ?? DateTime.Today;
        StartDateField.Date = now;
        EndDateField.Date   = now;

        if (presetTime.HasValue)
        {
            _savedStartTime = presetTime.Value;
            _savedEndTime   = presetTime.Value + TimeSpan.FromHours(1);
        }
        StartTimeField.Time = _savedStartTime;
        EndTimeField.Time   = _savedEndTime;

        if (editRecord is not null)
        {
            Title               = "Изменить запись";
            DeleteBtn.IsVisible = true;
            FillFromRecord(editRecord);
        }

        if (presetAllDay) AllDayCheck.IsChecked = true;

        TitleBox.TextChanged += (_, _) => HideError();

        AllDayCheck.IsCheckedChanged += (_, _) =>
        {
            HideError();
            var pos = Position;
            UpdateTimeVisibility();
            KeepWindowPosition(pos);
        };

        WireTimeField(StartTimeField, t => _savedStartTime = t);
        WireTimeField(EndTimeField,   t => _savedEndTime   = t);

        CancelBtn.Click += (_, _) => Close(null);
        DeleteBtn.Click += (_, _) => Close(new RecordDialogResult(null, Deleted: true));
        SaveBtn.Click   += (_, _) => TrySaveCalendar(editRecord?.Id);

        Deactivated += (_, _) => ClosePopups();
        Closing     += (_, _) => ClosePopups();

        // Список «Дата события» зависит от выбранной коллекции — обновляем при ее смене
        // (смена БД тоже сюда приводит: LoadCollections сбрасывает выбор коллекции).
        CollectionCombo.SelectionChanged += (_, _) => UpdateEventDateField();
        EventDateCombo.SelectionChanged  += (_, _) => UpdateEventDateHint();
        UpdateEventDateField();

        UpdateTimeVisibility();
    }

    // Список «Дата записи»: показываем всегда. Перечисляет ВСЕ поля-даты коллекции — и видимые
    // в календаре (IsCalStart), и выключенные: тумблер по умолчанию снят, поэтому раньше у
    // коллекции с готовым «Дедлайном» список был пуст, и репозиторий заводил рядом второе,
    // системное «Дата». Теперь выключенную дату можно выбрать — при сохранении ей поднимут
    // тумблер (единственный случай, когда показ включается не рукой пользователя: запись
    // заводят ИЗ календаря). По умолчанию: при изменении — поле самого события, при создании —
    // первая видимая дата, а если видимых нет — первая имеющаяся. Дат нет вовсе — список пуст,
    // и при сохранении дата ляжет в системное «Дата» (создается автоматически).
    private void UpdateEventDateField()
    {
        var idx   = CollectionCombo.SelectedIndex;
        var colId = idx >= 0 && idx < _collections.Count ? _collections[idx].Id : null;

        _eventDateFields = colId.HasValue
            ? CurrentRepo.GetFields(colId.Value).Where(f => f.FieldType == "date").ToList()
            : [];

        EventDateSection.IsVisible = true;
        // Выключенную дату помечаем прямо в списке: иначе выбор выглядел бы равнозначным, а
        // последствие у него свое — показ коллекции в календаре включится.
        EventDateCombo.ItemsSource = _eventDateFields
            .Select(f => f.IsCalStart ? f.Name : $"{f.Name} — не показывается в календаре")
            .ToList();

        var sel = _editRecord is not null
            ? _eventDateFields.FindIndex(f => f.Id == _editRecord.FieldId)
            : -1;
        if (sel < 0) sel = _eventDateFields.FindIndex(f => f.IsCalStart);
        if (sel < 0 && _eventDateFields.Count > 0) sel = 0;
        EventDateCombo.SelectedIndex = sel;
        UpdateEventDateHint();
    }

    // Подпись под списком: выбрана дата с выключенным показом — говорим, что произойдет при
    // сохранении. Молча поднимать тумблер нельзя: это меняет схему коллекции, а не только запись.
    private void UpdateEventDateHint()
    {
        var i       = EventDateCombo.SelectedIndex;
        var chosen  = i >= 0 && i < _eventDateFields.Count ? _eventDateFields[i] : null;
        var hidden  = chosen is not null && !chosen.IsCalStart;

        EventDateHint.IsVisible = hidden;
        EventDateHint.Text = hidden
            ? $"У свойства «{chosen!.Name}» показ в календаре выключен — он включится при сохранении, " +
              "иначе запись не появилась бы в календаре. Выключить обратно можно в свойствах столбца."
            : "";
    }

    // Репозиторий выбранной БД.
    private ConstructorRepository CurrentRepo
    {
        get
        {
            foreach (var d in _databases) if (d.DbId == _selectedDbId) return d.Repo;
            return _databases[0].Repo;
        }
    }

    // Перезагружает список коллекций из указанной БД. Если ни одной нет — плейсхолдер
    // «Без названия» (id=null): при сохранении репозиторий создаст эту коллекцию автоматически.
    private void LoadCollections(ConstructorRepository repo)
    {
        _collections.Clear();
        var cols = repo.GetAllCollections();
        if (cols.Count == 0)
            _collections.Add(("Без названия", null, "#007AFF"));
        foreach (var c in cols)
            _collections.Add((c.Name, c.Id, c.Color ?? "#8E8E93"));

        // Пункт списка — одна строка (TitleText): многострочное имя коллекции растянуло бы
        // строку выпадающего списка. Выбор идет по индексу, поэтому показ можно резать.
        CollectionCombo.ItemsSource   = _collections.Select(c => TitleText.FirstLine(c.Name)).ToList();
        CollectionCombo.SelectedIndex = 0;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ─── Схема-режим ──────────────────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════════════

    private void BuildFields()
    {
        var fields = _repository.GetFields(_collectionId);
        foreach (var field in fields)
        {
            var label = new TextBlock
            {
                Text       = field.Name,
                FontWeight = (Avalonia.Media.FontWeight)this.FindResource("FontWeightBold")!,
                FontSize   = (double)this.FindResource("FontSizeBase")!,
                Foreground = (Avalonia.Media.IBrush)this.FindResource("BrushSecondaryText")!,
            };
            FieldsPanel.Children.Add(label);

            var input = MakeInput(field);
            _fieldWidgets[field.Id!.Value] = (field, input);
            FieldsPanel.Children.Add(input);
        }
    }

    private Control MakeInput(DbField field)
    {
        Dictionary<string, object> config = [];
        try { config = JsonSerializer.Deserialize<Dictionary<string, object>>(field.ConfigJson ?? "{}") ?? []; }
        catch { /* ignore bad json */ }

        return field.FieldType switch
        {
            "number" => new NumericUpDown { Minimum = decimal.MinValue, Maximum = decimal.MaxValue, Height = 36 },
            "date"   => new DateEditor(),
            "select" or "status" => BuildSelectCombo(config),
            "multiselect" => BuildMultiSelect(config),
            "user"   => BuildUserCombo(),
            "checkbox" => new CheckBox { VerticalAlignment = VerticalAlignment.Center },
            "url"    => new TextBox { Watermark = "https://", Height = 36 },
            _        => new TextBox { Height = 36 },  // text, default
        };
    }

    // Тип «пользователь»: опции — допущенные пользователи базы, по умолчанию текущий.
    // В личной базе допущен только владелец — выбрать можно лишь себя.
    private ComboBox BuildUserCombo()
    {
        var opts = _repository.GetUserOptions();
        if (opts.Count == 0) opts = [Utils.CurrentUser.DisplayName];
        var combo = new ComboBox
        {
            ItemsSource         = opts,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height              = 36,
        };
        var me = opts.IndexOf(Utils.CurrentUser.DisplayName);
        combo.SelectedIndex = me >= 0 ? me : 0;
        return combo;
    }

    private static ComboBox BuildSelectCombo(Dictionary<string, object> config)
    {
        var opts = SelectOptions(config);
        return new ComboBox
        {
            ItemsSource    = opts.Count > 0 ? opts : ["—"],
            SelectedIndex  = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height         = 36,
        };
    }

    private static Components.MultiSelectBox BuildMultiSelect(Dictionary<string, object> config) =>
        new(SelectOptions(config), "", separator: MultiSeparator(config)) { HorizontalAlignment = HorizontalAlignment.Stretch };

    // Формат склейки значений «Мультисписка» из ConfigJson поля ({"separator":"..."}); по умолчанию запятая.
    private static string MultiSeparator(Dictionary<string, object> config) =>
        config.TryGetValue("separator", out var raw) && raw is JsonElement e && e.ValueKind == JsonValueKind.String
            ? MultiSelectValue.SeparatorFor(e.GetString())
            : MultiSelectValue.SeparatorFor(null);

    private static List<string> SelectOptions(Dictionary<string, object> config)
    {
        var opts = new List<string>();
        if (config.TryGetValue("options", out var raw) && raw is JsonElement elem && elem.ValueKind == JsonValueKind.Array)
            opts = elem.EnumerateArray().Select(o => o.ToString()).ToList();
        return opts;
    }

    private void FillValues(int recordId)
    {
        var values = _repository.GetFieldValues(recordId);
        foreach (var (fieldId, (field, input)) in _fieldWidgets)
        {
            var val = values.GetValueOrDefault(fieldId, "");
            switch (field.FieldType)
            {
                case "checkbox":
                    ((CheckBox)input).IsChecked = val is "1" or "true" or "True";
                    break;
                case "date":
                    ((DateEditor)input).Storage = val;
                    break;
                case "select":
                case "status":
                    var cb = (ComboBox)input;
                    var idx = (cb.ItemsSource as List<string>)?.IndexOf(val) ?? -1;
                    if (idx >= 0) cb.SelectedIndex = idx;
                    break;
                case "multiselect":
                    ((Components.MultiSelectBox)input).SetStorage(val);
                    break;
                case "user":
                    // Существующая запись показывает СВОЕ значение (в т.ч. пустое или имя
                    // пользователя, которого уже нет в базе, — добавляем его в опции сырым).
                    var ucb  = (ComboBox)input;
                    var opts = ucb.ItemsSource as List<string> ?? [];
                    if (val.Length > 0 && !opts.Contains(val))
                    {
                        opts = [.. opts, val];
                        ucb.ItemsSource = opts;
                    }
                    ucb.SelectedIndex = val.Length > 0 ? opts.IndexOf(val) : -1;
                    break;
                case "number":
                    if (decimal.TryParse(val, out var num))
                        ((NumericUpDown)input).Value = num;
                    break;
                default:
                    ((TextBox)input).Text = val;
                    break;
            }
        }
    }

    private static string GetValue(DbField field, Control input) => field.FieldType switch
    {
        "checkbox" => ((CheckBox)input).IsChecked == true ? "1" : "0",
        "date"     => ((DateEditor)input).Storage ?? "",
        "select" or "status" or "user" => ((ComboBox)input).SelectedItem?.ToString() ?? "",
        "multiselect" => ((Components.MultiSelectBox)input).Storage,
        "number"   => ((NumericUpDown)input).Value?.ToString() ?? "",
        _          => ((TextBox)input).Text ?? "",
    };

    private void TrySaveSchema()
    {
        try
        {
            if (_recordId is null)
            {
                var record = _repository.AddRecord(new DbRecord { CollectionId = _collectionId });
                _recordId = record.Id;
            }

            foreach (var (fieldId, (field, input)) in _fieldWidgets)
                _repository.SetFieldValue(_recordId!.Value, fieldId, GetValue(field, input));
        }
        catch (InvalidOperationException ex)
        {
            // Права отозвали, пока окно было открыто (PermissionDeniedException и т.п.) —
            // причину показываем баннером, ввод пользователя не теряется.
            ShowError(ex.Message);
            return;
        }
        catch (Exception ex) when (Utils.DbErrors.Handle(ex, _repository.DbPath))
        {
            // Общая база занята/недоступна, запись удалил другой пользователь: сообщение
            // показано карточкой, окно остается открытым вместе с введенным.
            return;
        }

        Close(true);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // ─── Календарь-режим ──────────────────────────────────────────────────────
    // ══════════════════════════════════════════════════════════════════════════

    // Выпадашки полей даты и времени — свои Popup'ы виджетов; закрываем их вместе,
    // когда окно теряет фокус или закрывается.
    private void ClosePopups()
    {
        StartDateField.ClosePopup();
        EndDateField.ClosePopup();
        StartTimeField.ClosePopup();
        EndTimeField.ClosePopup();
    }

    // Восстанавливает позицию окна после пересчета высоты (SizeToContent),
    // чтобы левый-верхний угол не сдвигался при переключении «Весь день».
    private void KeepWindowPosition(PixelPoint pos)
    {
        void Restore(object? _, EventArgs __)
        {
            LayoutUpdated -= Restore;
            if (Position != pos) Position = pos;
        }
        LayoutUpdated += Restore;
    }

    // Запоминаем последнее осмысленное время: при «Весь день» поля времени скрыты,
    // и при возврате переключателя их надо восстановить.
    private void WireTimeField(TimeField field, Action<TimeSpan> setSaved)
    {
        field.TimeChanged += (_, _) =>
        {
            if (AllDayCheck.IsChecked != true) setSaved(field.Time);
        };
    }

    private void FillFromRecord(CalendarRecord r)
    {
        TitleBox.Text         = r.Title;
        DescBox.Text          = r.Description;
        AllDayCheck.IsChecked = r.AllDay;
        StartDateField.Date   = r.StartDt.Date;
        EndDateField.Date     = r.EndDt.Date;

        if (!r.AllDay)
        {
            _savedStartTime = r.StartDt.TimeOfDay;
            _savedEndTime   = r.EndDt.TimeOfDay;
        }
        StartTimeField.Time = _savedStartTime;
        EndTimeField.Time   = _savedEndTime;

        if (r.CollectionId.HasValue)
        {
            var idx = _collections.FindIndex(c => c.Id == r.CollectionId);
            if (idx >= 0) CollectionCombo.SelectedIndex = idx;
        }
    }

    private void UpdateTimeVisibility()
    {
        var isAllDay = AllDayCheck.IsChecked == true;

        StartLabel.IsVisible     = !isAllDay;
        StartTimeField.IsVisible = !isAllDay;
        EndLabel.IsVisible       = !isAllDay;
        EndTimeField.IsVisible   = !isAllDay;

        var span = isAllDay ? 3 : 1;
        Grid.SetColumnSpan(StartDateField, span);
        Grid.SetColumnSpan(EndDateField, span);

        if (!isAllDay)
        {
            StartTimeField.Time = _savedStartTime;
            EndTimeField.Time   = _savedEndTime;
        }
    }

    private void TrySaveCalendar(int? existingId)
    {
        HideError();

        var title = TitleBox.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(title))
        {
            ShowError("Введите название записи");
            return;
        }

        var startDate = StartDateField.Date ?? DateTime.Today;
        var endDate   = EndDateField.Date   ?? DateTime.Today;
        var startTime = StartTimeField.Time;
        var endTime   = EndTimeField.Time;

        var isAllDay = AllDayCheck.IsChecked == true;
        var startDt  = startDate.Date + (isAllDay ? TimeSpan.Zero : startTime);
        var endDt    = endDate.Date   + (isAllDay ? new TimeSpan(23, 59, 0) : endTime);

        var colIdx = CollectionCombo.SelectedIndex;
        var colId  = colIdx >= 0 ? _collections[colIdx].Id : null;

        // Поле-дата, куда положить дату события. Если у коллекции есть даты — берем выбранную
        // (выключенной репозиторий поднимет тумблер, см. ResolveCalendarDateField); дат нет —
        // 0, и репозиторий положит дату в системное «Дата», создав его.
        var chosenFieldId = 0;
        if (_eventDateFields.Count > 0)
        {
            var di = EventDateCombo.SelectedIndex;
            if (di < 0 || di >= _eventDateFields.Count)
            {
                ShowError("Выберите дату для отображения в календаре");
                return;
            }
            chosenFieldId = _eventDateFields[di].Id ?? 0;
        }

        var candidate = new CalendarRecord
        {
            Id           = existingId,
            DbId         = _selectedDbId,
            FieldId      = chosenFieldId,
            Title        = title,
            Description  = DescBox.Text ?? "",
            StartDt      = startDt,
            EndDt        = endDt,
            AllDay       = isAllDay,
            CollectionId = colId,
            Color        = colId.HasValue ? _collections[colIdx].Color : "#007AFF",
        };

        var error = CalendarRecordValidator.Validate(candidate);
        if (error is not null)
        {
            ShowError(error);
            return;
        }

        Close(new RecordDialogResult(candidate));
    }

    private void ShowError(string message)
    {
        var pos = Position;
        NoticeBanner.Show(message);
        KeepWindowPosition(pos);
    }

    private void HideError()
    {
        if (!NoticeBanner.IsVisible) return;
        var pos = Position;
        NoticeBanner.Hide();
        KeepWindowPosition(pos);
    }
}
