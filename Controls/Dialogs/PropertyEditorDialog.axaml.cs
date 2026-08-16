using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;

namespace SmartBase.Controls.Dialogs;

/// <summary>Тип поля для ComboBox: обводочная иконка (lucide) + подпись.</summary>
public sealed class FieldTypeItem
{
    public required string   Type  { get; init; }
    public required string   Label { get; init; }
    public required Geometry Icon  { get; init; }
    /// <summary>Первый элемент «системной» группы (вычисляемые типы) — над ним рисуется
    /// разделительная черта в выпадающем списке.</summary>
    public bool StartsGroup { get; init; }
}

/// <summary>
/// Notion-style мини-диалог для создания/изменения одного свойства (поля коллекции).
/// Только два параметра: название и тип. Markdown как тип скрыт (это системное body-поле).
/// Для типа «Дата» есть переключатель «Отображать в календаре» (флаг IsCalStart поля) —
/// несколько полей-дат коллекции могут быть включены одновременно.
/// </summary>
public partial class PropertyEditorDialog : Window
{
    // Иконки берём из общего источника FieldTypeIcons (lucide, 24×24).
    private static readonly FieldTypeItem[] FieldTypes =
    [
        new() { Type = "text",     Label = "Текст",   Icon = Components.FieldTypeIcons.ForType("text")!     },
        new() { Type = "number",   Label = "Число",   Icon = Components.FieldTypeIcons.ForType("number")!   },
        new() { Type = "date",     Label = "Дата",    Icon = Components.FieldTypeIcons.ForType("date")!     },
        new() { Type = "select",   Label = "Список",  Icon = Components.FieldTypeIcons.ForType("select")!   },
        new() { Type = "status",   Label = "Статус",  Icon = Components.FieldTypeIcons.ForType("status")!   },
        new() { Type = "multiselect", Label = "Мультисписок", Icon = Components.FieldTypeIcons.ForType("multiselect")! },
        new() { Type = "checkbox", Label = "Чекбокс", Icon = Components.FieldTypeIcons.ForType("checkbox")! },
        new() { Type = "url",      Label = "Ссылка",  Icon = Components.FieldTypeIcons.ForType("url")!      },
        new() { Type = "file",     Label = "Файл",    Icon = Components.FieldTypeIcons.ForType("file")!     },
        new() { Type = "relation", Label = "Связь",   Icon = Components.FieldTypeIcons.ForType("relation")! },
        new() { Type = "user",     Label = "Пользователь", Icon = Components.FieldTypeIcons.ForType("user")! },
        // Вычисляемые типы (значение авто из метаданных записи, ячейка read-only). Идут внизу
        // отдельной группой — StartsGroup у первого рисует разделительную черту над ней.
        new() { Type = "created_at", Label = "Дата создания",          Icon = Components.FieldTypeIcons.ForType("created_at")!, StartsGroup = true },
        new() { Type = "updated_at", Label = "Дата изменения",         Icon = Components.FieldTypeIcons.ForType("updated_at")! },
        new() { Type = "created_by", Label = "Создание пользователем",  Icon = Components.FieldTypeIcons.ForType("created_by")! },
        new() { Type = "updated_by", Label = "Изменение пользователем", Icon = Components.FieldTypeIcons.ForType("updated_by")! },
    ];

    private readonly ConstructorRepository _repository;
    private readonly int  _collectionId;
    private readonly int? _editFieldId;
    // Реестровый DbId базы этого поля — чтобы отличить «своя БД» (пишем TargetDbId=0) от межбазовой.
    private readonly int  _currentDbId;

    // Режим системной колонки («Название»/«Описание»): тип зафиксирован «Текст»,
    // запись идёт не в DbFields, а через результат диалога (см. ResultName).
    private readonly bool _systemMode;

    // Редактируемое поле целиком — из его флагов собирается плашка «привязано к разделу»
    // (см. UpdateRoleNote). Для нового свойства null: ролей у него ещё нет.
    private readonly DbField? _editField;

    private string _selectedType = "text";

    // Текстовые поля пунктов списка (типы «Список»/«Статус»). Собираются в ConfigJson при
    // сохранении; строки строятся в BuildOptionRow и живут в OptionsList.
    private readonly List<TextBox> _optionBoxes = [];

    // Формат склейки значений «Мультисписка»/«Связи» — порядок совпадает с пунктами
    // SeparatorCombo и RelationSeparatorCombo в XAML (общий список; ключ хранится в ConfigJson,
    // не индекс, поэтому порядок можно менять без миграции сохранённых полей).
    private static readonly string[] SeparatorKeys =
        ["comma", "dot", "space", "semicolon", "newline", "comma_tight", "dot_tight"];

    // Формат числа: порядок пунктов NumberFormatCombo в XAML.
    private static readonly NumberFormatKind[] NumberFormatKinds =
        [NumberFormatKind.Plain, NumberFormatKind.Grouped, NumberFormatKind.Currency, NumberFormatKind.Percent];

    // Строка пунктов, которая сейчас в режиме ввода (поле + действие фиксации). Клик мимо неё
    // (в т.ч. по неинтерактивному фону) фиксирует ввод — см. DismissOptionEditor.
    private (TextBox Box, Action Commit)? _editingOption;

    /// <summary>В системном режиме — введённая подпись (после Сохранить); иначе null.</summary>
    public string? ResultName { get; private set; }

    public PropertyEditorDialog(ConstructorRepository repository, int collectionId, int? editFieldId = null,
                                int currentDbId = 0)
    {
        InitializeComponent();

        _repository   = repository;
        _collectionId = collectionId;
        _editFieldId  = editFieldId;
        _currentDbId  = currentDbId;

        TypeCombo.ItemsSource = FieldTypes;

        // По умолчанию новое поле-дата отображается в календаре (включено).
        CalDateToggle.IsChecked = true;
        // Задачи — наоборот, только по явному включению (иначе любое user-поле плодило бы задачи).
        TaskToggle.IsChecked = false;
        // Формат «Мультисписка» по умолчанию — запятая (первый пункт).
        SeparatorCombo.SelectedIndex = 0;
        // Формат «Числа» по умолчанию — «Обычное число» (показ = сырому вводу).
        ApplyNumberFormat(NumberFormat.Default);
        // Смена формата числа переключает видимость под-опций (валюта — только для «Денег»,
        // блок опций скрыт для «Обычного числа»).
        NumberFormatCombo.SelectionChanged += (_, _) => UpdateNumberOptionsVisibility();

        // Авто-привязка файлов: выбор столбца-источника открывает блок «папка/формат»;
        // «Свой формат…» — поле ручного расширения; кнопка — системный выбор папки.
        FileSourceCombo.SelectionChanged += (_, _) => UpdateFileAutoVisibility();
        FileFormatCombo.SelectionChanged += (_, _) => UpdateFileFormatCustomVisibility();
        FileFolderBtn.Click              += async (_, _) => await PickFolderAsync();
        PopulateFileSources();
        PopulateRelationDatabases();

        if (editFieldId.HasValue)
        {
            Title = "Изменение свойства";
            var field = repository.GetFields(collectionId).FirstOrDefault(f => f.Id == editFieldId);
            _editField = field;
            if (field is not null)
            {
                NameBox.Text  = field.Name;
                _selectedType = field.FieldType;
                CalDateToggle.IsChecked = field.IsCalStart;
                TaskToggle.IsChecked    = field.IsTaskExec;
                // Существующие опции «Списка»/«Статуса» — в редактор пунктов.
                if (FieldTypeInfo.IsOptionList(field.FieldType))
                    foreach (var o in ParseOptions(field.ConfigJson)) AddOptionRow(o);
                // «Мультисписок» — восстановить выбранный формат склейки значений.
                if (field.FieldType == "multiselect")
                {
                    var sepIdx = Array.IndexOf(SeparatorKeys, MultiSelectValue.SeparatorKeyFromConfig(field.ConfigJson));
                    SeparatorCombo.SelectedIndex = sepIdx >= 0 ? sepIdx : 0;
                }
                // «Число» — восстановить выбранный формат показа (валюта/проценты/разряды).
                if (field.FieldType == "number")
                    ApplyNumberFormat(NumberFormatValue.FromConfig(field.ConfigJson));
                // «Файл» — восстановить правило авто-привязки (столбец/папка/формат), если есть.
                if (field.FieldType == "file")
                    RestoreFileAutoRule(field.ConfigJson);
                // «Связь» — восстановить целевую коллекцию + кардинальность.
                if (field.FieldType == "relation")
                    RestoreRelationConfig(field.ConfigJson);
            }
        }

        // Добавление пункта: сбрасываем поиск (иначе новая пустая строка не пройдёт фильтр),
        // добавляем строку и ставим в неё фокус.
        AddOptionBtn.Click   += (_, _) => { OptionsSearch.Text = ""; AddOptionRow("").Focus(); };
        OptionsSearch.TextChanged += (_, _) => FilterOptionRows();

        // Клик в любое место диалога мимо активного поля пункта фиксирует его (снимает фокус —
        // каретка гаснет). Tunnel: срабатывает раньше обработчиков строк, поэтому клик по другой
        // строке сперва зафиксирует текущую, затем откроет ту.
        AddHandler(PointerPressedEvent, DismissOptionEditor, RoutingStrategies.Tunnel);

        var idx = Array.FindIndex(FieldTypes, t => t.Type == _selectedType);
        TypeCombo.SelectedIndex = idx >= 0 ? idx : 0;
        TypeCombo.SelectionChanged += (_, _) =>
        {
            var i = TypeCombo.SelectedIndex;
            if (i >= 0 && i < FieldTypes.Length) _selectedType = FieldTypes[i].Type;
            UpdateCalDateVisibility();
        };
        UpdateCalDateVisibility();

        // Плашка «привязано к разделу» показывает ТЕКУЩЕЕ состояние окна: снял тумблер —
        // привязки уже нет, и надпись пропадает, не дожидаясь «Сохранить».
        CalDateToggle.IsCheckedChanged += (_, _) => UpdateRoleNote();
        TaskToggle.IsCheckedChanged    += (_, _) => UpdateRoleNote();

        CancelBtn.Click += (_, _) => Close(false);
        SaveBtn.Click   += (_, _) => TrySave();
        NameBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) TrySave(); };
        NameBox.TextChanged += (_, _) => HideError();
    }

    // Переключатель «Отображать в календаре» виден только для типа «Дата»,
    // «Добавить в раздел задач» — только для типа «Пользователь», редактор пунктов —
    // для «Список»/«Статус». Новый «Статус» с пустым набором засеивается тремя статусами
    // по умолчанию (при переключении/открытии) — как у авто-поля задач.
    private void UpdateCalDateVisibility()
    {
        CalDateSection.IsVisible = _selectedType == "date";
        TaskSection.IsVisible    = _selectedType == "user";
        OptionsSection.IsVisible = FieldTypeInfo.IsOptionList(_selectedType);
        // Формат склейки нужен только «Мультисписку» (у select/status значение одно).
        SeparatorSection.IsVisible = _selectedType == "multiselect";
        // Формат числа — только для типа «Число».
        NumberSection.IsVisible = _selectedType == "number";
        // Авто-привязка файлов — только для типа «Файл».
        FileSection.IsVisible = _selectedType == "file";
        // Настройка связи — только для типа «Связь».
        RelationSection.IsVisible = _selectedType == "relation";

        if (_selectedType == "status" && _optionBoxes.Count == 0)
            foreach (var o in ConstructorRepository.TaskStatusOptions) AddOptionRow(o);

        UpdateTaskToggleAvailability();
        // Смена типа рвёт привязку даты (календарь строится только по полям типа «Дата») —
        // плашка обязана это отразить сразу.
        UpdateRoleNote();
    }

    /// <summary>
    /// Исполнитель у коллекции ОДИН, поэтому тумблер «Добавить в раздел задач» доступен только
    /// свободному свойству либо тому, что уже несёт роль. Занята роль другим столбцом — тумблер
    /// гаснет, а подпись называет носителя: молча выключенный переключатель читался бы как
    /// поломка, и человек искал бы причину в правах или типе поля.
    /// </summary>
    private void UpdateTaskToggleAvailability()
    {
        if (_selectedType != "user" || _systemMode)
        {
            TaskToggleHint.IsVisible = false;
            TaskToggle.IsEnabled     = true;
            return;
        }

        var busy = _repository.TaskExecFieldId(_collectionId);
        var free = busy is null || busy == _editFieldId;

        TaskToggle.IsEnabled = free;
        if (!free) TaskToggle.IsChecked = false;

        TaskToggleHint.IsVisible = !free;
        TaskToggleHint.Text = free
            ? ""
            : $"Исполнитель уже назначен — свойство «{_repository.FieldName(busy!.Value)}». " +
              "Исполнитель может быть только один: снимите переключатель с него, чтобы назначить это свойство.";
    }

    // ── Привязка к разделу ──────────────────────────────────────────────────────

    /// <summary>
    /// Красная плашка «привязано к разделу»: столбец несёт роль, и данные из него подтягиваются
    /// в календарь или задачи. Ни в таблице, ни в панели записи этого не видно — значения просто
    /// «сами» оказываются в разделе, поэтому называем привязку там же, где её теряют (смена типа,
    /// снятый тумблер, удаление свойства). Собирается по флагам поля: роли нет — блока нет.
    /// </summary>
    private void UpdateRoleNote()
    {
        if (_systemMode || _editField is null || _repository is null)
        {
            RoleNote.Hide();
            return;
        }

        // Дата и исполнитель управляются тумблерами ЭТОГО окна, поэтому смотрим не на сохранённый
        // флаг, а на положение тумблера. Тип берём выбранный, а не сохранённый: роль на
        // несовместимом типе не работает, и плашка гаснет ещё до «Сохранить» — то же самое, что
        // потом сделает с флагом сама запись (ClearRolesIncompatibleWithType).
        var effective = new DbField
        {
            FieldType      = _selectedType,
            IsCalStart     = _editField.IsCalStart && CalDateToggle.IsChecked == true,
            IsTaskExec     = _editField.IsTaskExec && TaskToggle.IsChecked == true,
            IsTaskAssigner = _editField.IsTaskAssigner,
            IsTaskStatus   = _editField.IsTaskStatus,
            IsDescription  = _editField.IsDescription,
        };

        // Роли задач читает только коллекция с исполнителем, «Описание» в календаре — только
        // коллекция с видимой датой: без них плашка молчит, как молчит и сам раздел.
        var binding = FieldRoleText.Binding(effective,
                                            _repository.HasTaskExecField(_collectionId),
                                            _repository.HasCalendarDateField(_collectionId));

        if (binding is null) { RoleNote.Hide(); return; }

        RoleNote.Show(
            $"Это свойство привязано {binding}. Раздел берёт данные отсюда: " +
            "если удалить свойство или снять привязку, они перестанут в нём отображаться.");
    }

    /// <summary>
    /// Спрашивает перед сменой типа, которая снимет привязку к разделу. Роль работает только на
    /// своём типе (список статусов нужен «Статусу», дата — сроку), поэтому смена типа для раздела
    /// равна удалению столбца — и предупреждение здесь теми же словами, что в окне удаления
    /// (<c>FieldRoleText</c>). true — терять нечего либо пользователь согласился.
    /// </summary>
    private async Task<bool> ConfirmRoleLossOnTypeChange()
    {
        if (_editFieldId is not int fieldId || _editField is null) return true;
        if (_editField.FieldType == _selectedType) return true;

        // Роли, которые раздел уже не читает (нет исполнителя / нет видимой даты), терять нечего —
        // о них молчит и плашка, так что окно про них спрашивать не должно.
        var hasTasks    = _repository.HasTaskExecField(_collectionId);
        var hasCalendar = _repository.HasCalendarDateField(_collectionId);
        var lost = _repository.RolesLostByTypeChange(fieldId, _selectedType)
                              .Where(r => r switch
                              {
                                  FieldRole.TaskAssigner or FieldRole.TaskStatus or FieldRole.TaskExec => hasTasks,
                                  _ => hasCalendar || hasTasks,
                              })
                              .ToList();
        if (lost.Count == 0) return true;

        var typeLabel = FieldTypes.FirstOrDefault(t => t.Type == _selectedType)?.Label ?? _selectedType;
        var roles     = FieldRoleText.JoinRu(lost.Select(FieldRoleText.Title));
        var needs     = FieldRoleText.JoinRu(
            lost.Select(ConstructorRepository.RoleTypeNames).Distinct());

        var text = $"Свойство «{_editField.Name}» привязано к разделу — {roles}. " +
                   $"Роль работает только на типе {needs}, поэтому смена типа на «{typeLabel}» " +
                   $"снимет привязку: {FieldRoleText.TypeChangeImpact(lost)}.\n\n" +
                   "Значения столбца останутся на месте. Продолжить?";

        return await new ConfirmDialog(text, "Продолжить").ShowDialog<bool>(this);
    }

    // ── Роли раздела: подтягивание одноимённых столбцов ─────────────────────────

    /// <summary>
    /// Спрашивает, использовать ли существующие столбцы коллекции для ролей раздела. Совпадение
    /// ищется ТОЛЬКО по имени («Назначил», «Статус», «Дата», «Описание»), поэтому решение за
    /// пользователем: подтянуть их (данные поедут в раздел) или оставить нетронутыми и завести
    /// отдельные поля. Совпадений нет — спрашивать не о чем, разметка идёт молча (true).
    /// </summary>
    private async Task<bool> ConfirmRoleAdoption(IReadOnlyList<FieldRole> roles, int excludeFieldId,
                                                 string sectionName)
    {
        if (_repository is null) return true;

        var plan = _repository.PlanRoles(_collectionId, roles, excludeFieldId);
        if (!plan.NeedsConfirmation) return true;

        var found = string.Join("\n", plan.Candidates.Select(c => $"    • «{c.FieldName}»"));
        var text  = $"В коллекции уже есть столбцы с подходящими названиями:\n{found}\n\n" +
                    $"«Подтвердить» — они станут полями раздела ({sectionName}), " +
                    "и данные из них будут подтягиваться туда.";

        if (plan.FieldsToCreateIfDeclined.Count > 0)
        {
            var created = string.Join(", ", plan.FieldsToCreateIfDeclined.Select(n => $"«{n}»"));
            text += $"\n«Отменить» — столбцы останутся нетронутыми, а для раздела будут созданы " +
                    $"новые поля: {created}.";
        }
        else
        {
            text += "\n«Отменить» — столбцы останутся нетронутыми.";
        }

        return await new ConfirmDialog(text, "Подтвердить", "Отменить").ShowDialog<bool>(this);
    }

    // ── Авто-привязка файлов ────────────────────────────────────────────────────

    /// <summary>Пункт выпадашки столбца-источника. Key — строковый ключ колонки (пустой —
    /// ручной выбор/без авто; «__title__» — системная колонка; число — Id текст-поля).
    /// Foreground: приглушённый для пункта-заглушки «Выбрать…».</summary>
    public sealed class SourceFieldItem
    {
        public string  Key        { get; init; } = "";
        public string  Name       { get; init; } = "";
        public IBrush? Foreground { get; init; }
        public override string ToString() => Name;
    }

    // Заполнить список столбцов-источников: серая заглушка «Выбрать…» (= без авто, ручной выбор),
    // системное «Название» (на него тоже можно ссылаться — значение в db_records) и поля типа
    // «Текст» этой коллекции (в том числе «Описание» — оно обычное текстовое поле).
    private void PopulateFileSources()
    {
        if (_systemMode || _repository is null) return;
        var grey    = ResourceBrush("BrushSecondaryText", Brushes.Gray);
        var primary = ResourceBrush("BrushPrimaryText", Brushes.Black);
        var names   = _repository.GetSystemFieldNames(_collectionId);

        var items = new List<SourceFieldItem>
        {
            new() { Key = "", Name = "Выбрать…", Foreground = grey },
            new() { Key = FileAutoRule.TitleSource, Name = names.GetValueOrDefault(FileAutoRule.TitleSource, "Название"), Foreground = primary },
        };
        foreach (var f in _repository.GetFields(_collectionId))
            if (f.FieldType == "text" && f.Id is int fid)
                items.Add(new SourceFieldItem { Key = fid.ToString(), Name = f.Name, Foreground = primary });

        FileSourceCombo.ItemsSource   = items;
        FileSourceCombo.SelectedIndex = 0;
    }

    // Разобрать сохранённое правило и выставить контролы (столбец/папку/формат/вложенность).
    private void RestoreFileAutoRule(string? configJson)
    {
        var rule = FileAutoRule.FromConfig(configJson);
        // Правила нет — это новое поле или ручной режим: галочка остаётся в состоянии
        // по умолчанию (включена), как задано в разметке.
        if (rule is null) { FileSourceCombo.SelectedIndex = 0; return; }
        FileRecursiveCheck.IsChecked = rule.Recursive;

        if (FileSourceCombo.ItemsSource is IEnumerable<SourceFieldItem> items)
        {
            var idx = items.ToList().FindIndex(i => i.Key == rule.SourceKey);
            FileSourceCombo.SelectedIndex = idx >= 0 ? idx : 0;
        }
        FileFolderText.Text = rule.Folder;

        // Формат: выбрать из списка либо «Свой формат…» + поле.
        var found = false;
        foreach (var it in FileFormatCombo.Items)
            if (it is ComboBoxItem { Content: string c } && string.Equals(c, rule.Format, StringComparison.Ordinal))
            {
                FileFormatCombo.SelectedItem = it; found = true; break;
            }
        if (!found)
        {
            SelectFormatCustom();
            FileFormatCustom.Text = rule.Format;
        }
        UpdateFileAutoVisibility();
    }

    // Папка/формат нужны только когда выбран реальный столбец-источник (авто-режим).
    private void UpdateFileAutoVisibility() =>
        FileAutoOptions.IsVisible = SelectedSource() is { Key.Length: > 0 };

    // «Свой формат…» открывает поле ручного ввода расширения.
    private void UpdateFileFormatCustomVisibility() =>
        FileFormatCustom.IsVisible = SelectedFormatIsCustom();

    private SourceFieldItem? SelectedSource() => FileSourceCombo.SelectedItem as SourceFieldItem;

    private bool SelectedFormatIsCustom() =>
        (FileFormatCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() == "Свой формат…";

    private void SelectFormatCustom()
    {
        foreach (var it in FileFormatCombo.Items)
            if (it is ComboBoxItem { Content: "Свой формат…" }) { FileFormatCombo.SelectedItem = it; break; }
    }

    // Итоговый формат (расширение без точки): из списка или из поля «Свой формат…».
    private string SelectedFormat()
    {
        if (SelectedFormatIsCustom()) return (FileFormatCustom.Text ?? "").Trim().TrimStart('.');
        return ((FileFormatCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "").Trim().TrimStart('.');
    }

    private async System.Threading.Tasks.Task PickFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Выберите папку с файлами", AllowMultiple = false,
        });
        if (folders.Count == 0) return;
        var path = folders[0].Path.LocalPath;
        FileFolderText.Text = path;
        if (!Directory.Exists(path)) ShowError("Папка недоступна");
        else HideError();
    }

    // Собрать ConfigJson поля «Файл»: правило авто-привязки, если выбран столбец-источник и
    // корректно заданы папка+формат. Иначе — пустой конфиг (ручной режим). null → ошибка показана.
    private string? BuildFileConfigJson()
    {
        var src = SelectedSource();
        if (src is not { Key.Length: > 0 }) return "{}";   // ручной режим

        var folder = FileFolderText.Text?.Trim() ?? "";
        var format = SelectedFormat();
        if (folder.Length == 0) { ShowError("Выберите папку с файлами"); return null; }
        if (!Directory.Exists(folder)) { ShowError("Папка недоступна"); return null; }
        if (format.Length == 0) { ShowError("Выберите формат файла"); return null; }
        return new FileAutoRule(src.Key, folder, format, FileRecursiveCheck.IsChecked == true).ToConfigJson();
    }

    // ── Связь (relation) ─────────────────────────────────────────────────────────

    /// <summary>Пункт выпадашки целевой коллекции связи (Id + имя).</summary>
    private sealed class TargetCollectionItem
    {
        public int    Id   { get; init; }
        public string Name { get; init; } = "";
        public override string ToString() => Name;
    }

    /// <summary>Пункт выпадашки целевой БД связи (реестровый DbId + имя).</summary>
    private sealed class RelationDbItem
    {
        public int    Id   { get; init; }
        public string Name { get; init; } = "";
        public override string ToString() => Name;
    }

    // Репозиторий выбранной целевой БД: своя (_repository) или чужая (через резолвер реестра).
    private ConstructorRepository? RepoForDb(int dbId) =>
        dbId == _currentDbId || dbId <= 0 ? _repository : RelationDbResolver.RepoFor(dbId);

    // Список целевых БД (реестр). Резолвер не настроен (тесты/безоконный) → одна «своя» база.
    // Смена БД перезаполняет список коллекций (PopulateRelationTargets).
    private void PopulateRelationDatabases()
    {
        if (_systemMode || _repository is null) return;
        var dbs = RelationDbResolver.Databases()
            .Select(d => new RelationDbItem { Id = d.DbId, Name = d.Name }).ToList();
        if (dbs.Count == 0) dbs = [new RelationDbItem { Id = _currentDbId, Name = "Текущая база" }];

        RelationCardinalityCombo.SelectedIndex = 1;  // дефолт «Одну» (0 — Несколько); не сбрасывается при смене БД
        RelationSeparatorCombo.SelectedIndex   = 0;

        RelationDbCombo.ItemsSource        = dbs;
        RelationDbCombo.SelectionChanged  -= OnRelationDbChanged;
        RelationDbCombo.SelectionChanged  += OnRelationDbChanged;
        RelationDbCombo.SelectedIndex      = Math.Max(0, dbs.FindIndex(d => d.Id == _currentDbId));  // → targets
        PopulateRelationTargets();
    }

    private void OnRelationDbChanged(object? sender, Avalonia.Controls.SelectionChangedEventArgs e) => PopulateRelationTargets();

    // Список целевых коллекций ВЫБРАННОЙ базы (для своей БД — self-relation допустима).
    private void PopulateRelationTargets()
    {
        if (_systemMode || _repository is null) return;
        var dbId = (RelationDbCombo.SelectedItem as RelationDbItem)?.Id ?? _currentDbId;
        var repo = RepoForDb(dbId);
        var items = new List<TargetCollectionItem>();
        if (repo is not null)
            foreach (var c in repo.GetAllCollections())
                // Пункт списка — одна строка (TitleText); выбор идёт по Id.
                if (c.Id.HasValue)
                    items.Add(new TargetCollectionItem { Id = c.Id.Value, Name = TitleText.FirstLine(c.Name) });
        RelationTargetCombo.ItemsSource   = items;
        RelationTargetCombo.SelectedIndex = -1;      // цель нужно выбрать явно (список сменился)
    }

    // Восстановить сохранённое правило связи (целевая БД + коллекция + one/many + разделитель).
    private void RestoreRelationConfig(string? configJson)
    {
        var cfg = RelationConfig.FromConfig(configJson);
        if (cfg is null) return;
        // Целевая БД (0 → своя). Смена комбо перезаполнит список коллекций до выбора цели ниже.
        var dbId = cfg.TargetDbId > 0 ? cfg.TargetDbId : _currentDbId;
        if (RelationDbCombo.ItemsSource is IEnumerable<RelationDbItem> dbs)
        {
            var idx = dbs.ToList().FindIndex(d => d.Id == dbId);
            if (idx >= 0) RelationDbCombo.SelectedIndex = idx;
        }
        if (RelationTargetCombo.ItemsSource is IEnumerable<TargetCollectionItem> items)
            RelationTargetCombo.SelectedIndex = items.ToList().FindIndex(i => i.Id == cfg.TargetCollectionId);
        RelationCardinalityCombo.SelectedIndex = cfg.Single ? 1 : 0;
        var sepIdx = Array.IndexOf(SeparatorKeys, cfg.Separator);
        RelationSeparatorCombo.SelectedIndex = sepIdx >= 0 ? sepIdx : 0;
    }

    // Собрать ConfigJson поля-связи. null — целевая коллекция не выбрана (ошибка показана).
    private string? BuildRelationConfigJson()
    {
        if (RelationTargetCombo.SelectedItem is not TargetCollectionItem t)
        {
            ShowError("Выберите целевую коллекцию");
            return null;
        }
        var single     = RelationCardinalityCombo.SelectedIndex == 1;   // 0 — Несколько, 1 — Одну
        var separator  = SeparatorKeys[Math.Clamp(RelationSeparatorCombo.SelectedIndex, 0, SeparatorKeys.Length - 1)];
        var dbId       = (RelationDbCombo.SelectedItem as RelationDbItem)?.Id ?? _currentDbId;
        var targetDbId = dbId == _currentDbId ? 0 : dbId;   // своя БД → 0 (внутрибазовая связь)
        return new RelationConfig(t.Id, single, separator, targetDbId).ToConfigJson();
    }

    // Под-опции формата числа: блок опций (точность/разряды/валюта) скрыт для «Обычного числа»
    // (показ = сырому вводу), поле валюты — только для формата «Деньги».
    private void UpdateNumberOptionsVisibility()
    {
        var idx  = Math.Clamp(NumberFormatCombo.SelectedIndex, 0, NumberFormatKinds.Length - 1);
        var kind = NumberFormatKinds[idx];
        NumberOptions.IsVisible   = kind != NumberFormatKind.Plain;
        CurrencySection.IsVisible = kind == NumberFormatKind.Currency;
    }

    // Разложить формат числа по контролам диалога (обратное к BuildNumberConfigJson).
    private void ApplyNumberFormat(NumberFormat fmt)
    {
        NumberFormatCombo.SelectedIndex     = Math.Max(0, Array.IndexOf(NumberFormatKinds, fmt.Kind));
        DecimalsCombo.SelectedIndex         = fmt.Decimals < 0 ? 0 : Math.Clamp(fmt.Decimals + 1, 1, 5);
        ThousandsToggle.IsChecked           = fmt.Thousands;
        CurrencyBox.Text                    = fmt.CurrencySymbol;
        SymbolPosCombo.SelectedIndex        = fmt.SymbolSuffix ? 0 : 1;
        DecimalSeparatorCombo.SelectedIndex = fmt.DecimalSeparator == DecimalMark.Dot ? 1 : 0;
        UpdateNumberOptionsVisibility();
    }

    // Собрать формат числа из контролов диалога → ConfigJson поля.
    private string BuildNumberConfigJson()
    {
        var idx      = Math.Clamp(NumberFormatCombo.SelectedIndex, 0, NumberFormatKinds.Length - 1);
        var kind     = NumberFormatKinds[idx];
        var decimals = DecimalsCombo.SelectedIndex <= 0 ? -1 : DecimalsCombo.SelectedIndex - 1;   // «Авто» = -1
        var symbol   = CurrencyBox.Text?.Trim() is { Length: > 0 } s ? s : "₽";
        var mark     = DecimalSeparatorCombo.SelectedIndex == 1 ? DecimalMark.Dot : DecimalMark.Comma;
        var fmt = new NumberFormat(kind, decimals, ThousandsToggle.IsChecked == true, symbol,
            SymbolPosCombo.SelectedIndex != 1, mark);
        return JsonSerializer.Serialize(NumberFormatValue.ToConfigObject(fmt));
    }

    // Одна строка редактора пунктов: показ значения (с многоточием) + поле ввода + удаление.
    // Показ и ввод — ДВА контрола, видим всегда ровно один (переключаются), поэтому нет ни
    // «прыжка» размера, ни просвечивания текста. Возвращает TextBox (чтобы сфокусировать
    // вновь добавленный). Значения собираются в BuildOptionsConfigJson из этого же TextBox.
    private TextBox AddOptionRow(string value)
    {
        var primary   = ResourceBrush("BrushPrimaryText", Brushes.Black);
        var secondary = ResourceBrush("BrushSecondaryText", Brushes.Gray);

        // Поле ввода (авторитетное значение) — плоское, видно только при редактировании.
        var box = new TextBox
        {
            Text                     = value,
            Watermark                = "Название пункта",
            Height                   = 30,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsVisible                = false,
        };
        box.Classes.Add("optionRow");

        // Показ значения — TextBlock с многоточием в конце (TextBox сам «…» не умеет). Клик по нему
        // переключает строку в режим ввода. Шрифт привязан к полю → при переключении размер тот же.
        var display = new TextBlock
        {
            // Левый отступ = Padding поля optionRow («2,0») — чтобы при клике текст не сдвигался.
            Margin            = new Thickness(2, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            Cursor            = new Cursor(StandardCursorType.Ibeam),
        };
        display.Bind(TextBlock.FontSizeProperty,   box.GetObservable(TextBox.FontSizeProperty));
        display.Bind(TextBlock.FontFamilyProperty, box.GetObservable(TextBox.FontFamilyProperty));

        // Показ: пустое значение — приглушённый плейсхолдер (как watermark поля).
        void ShowDisplay()
        {
            var v = box.Text ?? "";
            display.Text       = v.Length > 0 ? v : "Название пункта";
            display.Foreground = v.Length > 0 ? primary : secondary;
            display.IsVisible  = true;
            box.IsVisible      = false;
            if (_editingOption?.Box == box) _editingOption = null;
        }
        // Ввод: показать поле, курсор в конец. Фиксация — по уходу фокуса или клику мимо строки.
        void ShowEditor()
        {
            display.IsVisible = false;
            box.IsVisible     = true;
            box.Focus();
            box.CaretIndex    = (box.Text ?? "").Length;
            _editingOption    = (box, ShowDisplay);
        }
        display.PointerPressed += (_, _) => ShowEditor();
        box.LostFocus          += (_, _) => ShowDisplay();
        box.KeyDown            += (_, e) => { if (e.Key is Key.Enter or Key.Escape) ShowDisplay(); };

        // Панель фиксированной высоты строки (30) — чтобы список не «дёргался» при переключении
        // показ/ввод (скрытое поле в измерение не входит).
        var editArea = new Panel { Height = 30 };
        editArea.Children.Add(display);
        editArea.Children.Add(box);

        var del = new Button
        {
            Background        = Brushes.Transparent,
            BorderThickness   = new Thickness(0),
            Padding           = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Center,
            Content           = BuildDeleteGlyph(),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        del.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(editArea, 0);
        Grid.SetColumn(del, 1);
        grid.Children.Add(editArea);
        grid.Children.Add(del);

        del.Click += (_, _) =>
        {
            if (_editingOption?.Box == box) _editingOption = null;
            _optionBoxes.Remove(box);
            OptionsList.Children.Remove(grid);
        };

        box.Tag = grid;   // строка-контейнер для фильтра (box лежит внутри editArea, не прямо в grid)

        // Заполненная строка стартует в показе, новая (пустая) — сразу в вводе (её фокусирует вызвавший).
        if (value.Length > 0) ShowDisplay();
        else ShowEditor();

        _optionBoxes.Add(box);
        OptionsList.Children.Add(grid);
        return box;
    }

    // Фильтр строк-пунктов по полю поиска: строка видна, если её текст содержит запрос
    // (пустой запрос — видны все). Совпадение по текущему тексту строки.
    private void FilterOptionRows()
    {
        var q = OptionsSearch.Text?.Trim() ?? "";
        foreach (var box in _optionBoxes)
            if (box.Tag is Control row)
                row.IsVisible = q.Length == 0 || (box.Text ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    // Клик мимо активного поля пункта — фиксирует ввод (прячет поле → фокус снимается, каретка
    // гаснет). Клик по самому полю (правка курсора) не трогаем.
    private void DismissOptionEditor(object? sender, PointerPressedEventArgs e)
    {
        if (_editingOption is not { } ed) return;
        if (IsWithin(e.Source, ed.Box)) return;
        ed.Commit();
    }

    // Является ли source самим target или его потомком в визуальном дереве.
    private static bool IsWithin(object? source, Visual target)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
            if (ReferenceEquals(v, target)) return true;
        return false;
    }

    private Control BuildDeleteGlyph()
    {
        // Крестик удаления строки — как остальные иконки приложения: Viewbox стандартного
        // размера (IconSizeSmall=16) + Canvas 24×24 + Path, чтобы сохранить «сетку 24» и
        // естественные поля глифа (иначе Stretch растягивал крестик на весь бокс — выглядел
        // крупным). Масштаб зума наследуется от LayoutTransform окна.
        var geo    = this.TryFindResource("IconClose", out var g) && g is Geometry geom ? geom : null;
        var size   = this.TryFindResource("IconSizeSmall", out var s) && s is double d ? d : 16.0;
        var thick  = this.TryFindResource("IconStrokeSmall", out var t) && t is double td ? td : 1.8;
        var stroke = this.TryFindResource("BrushSecondaryText", out var b) && b is IBrush br ? br : Brushes.Gray;

        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Data            = geo,
            Stroke          = stroke,
            StrokeThickness = thick,   // стандарт: IconStrokeSmall (как иконки-оглавления колонок)
            StrokeLineCap   = PenLineCap.Round,
            StrokeJoin      = PenLineJoin.Round,
        });
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }

    // Кисть из ресурсов окна с запасным значением (для контролов, создаваемых в коде).
    private IBrush ResourceBrush(string key, IBrush fallback) =>
        this.TryFindResource(key, out var r) && r is IBrush b ? b : fallback;

    // Разбор опций из ConfigJson поля (как MergeSelectOptions в репозитории): {"options":[...]}.
    private static IEnumerable<string> ParseOptions(string? configJson)
    {
        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(configJson) ? "{}" : configJson);
            if (doc.RootElement.TryGetProperty("options", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
                        result.Add(s);
        }
        catch (JsonException) { /* нечитаемый конфиг — пустой набор */ }
        return result;
    }

    // Сборка ConfigJson из редактора пунктов: непустые, обрезанные, без дублей (регистр игнор.).
    // Для «Мультисписка» дополнительно сохраняется формат склейки значений (separator).
    private string BuildOptionsConfigJson()
    {
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new List<string>();
        foreach (var b in _optionBoxes)
        {
            var v = b.Text?.Trim() ?? "";
            if (v.Length > 0 && seen.Add(v)) options.Add(v);
        }
        if (_selectedType == "multiselect")
        {
            var separator = SeparatorKeys[Math.Clamp(SeparatorCombo.SelectedIndex, 0, SeparatorKeys.Length - 1)];
            return JsonSerializer.Serialize(new { options, separator });
        }
        return JsonSerializer.Serialize(new { options });
    }

    /// <summary>
    /// Конструктор для системной колонки «Название»/«Описание». Визуально как
    /// «Изменение свойства», но тип зафиксирован «Текст» (ComboBox заблокирован),
    /// а сохранение возвращает подпись через ResultName, не трогая DbFields.
    /// </summary>
    public PropertyEditorDialog(string currentName)
    {
        InitializeComponent();

        _repository   = null!;   // в системном режиме не используется
        _collectionId = 0;
        _editFieldId  = null;
        _systemMode   = true;

        Title = "Изменение свойства";

        TypeCombo.ItemsSource   = FieldTypes;
        TypeCombo.SelectedIndex = Array.FindIndex(FieldTypes, t => t.Type == "text");
        TypeCombo.IsEnabled     = false;   // тип «Текст» нельзя изменить

        // Пояснение: «Название»/«Описание» — базовые свойства целостности, их нельзя
        // удалить и сменить тип. Показываем только в этом (системном) режиме.
        SystemNote.Show("Это базовое свойство записи — на нём держится целостность коллекции. "
                      + "Его нельзя удалить, а тип изменить: доступно только переименование.");

        NameBox.Text = currentName;

        CancelBtn.Click += (_, _) => Close(false);
        SaveBtn.Click   += (_, _) => TrySave();
        NameBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) TrySave(); };
        NameBox.TextChanged += (_, _) => HideError();

        Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void ShowError(string message)
    {
        var pos = Position;            // баннер увеличит высоту — держим позицию
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

    // Держим позицию окна при пересчёте высоты (SizeToContent), чтобы оно не «прыгало».
    private void KeepWindowPosition(PixelPoint pos)
    {
        void Restore(object? _, EventArgs __)
        {
            LayoutUpdated -= Restore;
            if (Position != pos) Position = pos;
        }
        LayoutUpdated += Restore;
    }

    private async void TrySave()
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(name))
        {
            ShowError("Укажите название свойства");
            NameBox.Focus();
            return;
        }

        // Системная колонка: возвращаем подпись наружу, запись делает контроллер
        // (метаданные), DbFields не трогаем.
        if (_systemMode)
        {
            ResultName = name;
            Close(true);
            return;
        }

        // «Файл»: собрать конфиг авто-привязки (или пустой «{}» для ручного режима).
        // null — валидация не прошла (ошибка уже показана баннером).
        var fileConfig = "{}";
        if (_selectedType == "file")
        {
            var cfg = BuildFileConfigJson();
            if (cfg is null) return;
            fileConfig = cfg;
        }
        var fileAuto = _selectedType == "file" && FileAutoRule.FromConfig(fileConfig) is not null;

        // «Связь»: собрать конфиг (целевая коллекция + one/many). null — цель не выбрана.
        var relationConfig = "{}";
        if (_selectedType == "relation")
        {
            var cfg = BuildRelationConfigJson();
            if (cfg is null) return;
            relationConfig = cfg;
        }

        // Смена типа уносит привязку к разделу — предупреждаем ровно так же, как при удалении
        // столбца: для раздела это одно и то же (данные ему станут недоступны), а раньше тип
        // менялся молча и плашка оставалась висеть над уже неработающей ролью.
        if (!await ConfirmRoleLossOnTypeChange()) return;

        // Включение авто-привязки на поле с уже сохранёнными значениями — подтверждаем затирание.
        if (fileAuto && _editFieldId is int existId && _repository.FieldHasValues(existId))
        {
            var ok = await new ConfirmDialog(
                "Значения столбца будут заменены авто-подстановкой. Продолжить?", "Продолжить")
                .ShowDialog<bool>(this);
            if (!ok) return;
        }

        int savedFieldId;
        try
        {
            if (_editFieldId.HasValue)
            {
                var field = _repository.GetFields(_collectionId).FirstOrDefault(f => f.Id == _editFieldId);
                if (field is null) { Close(true); return; }

                field.Name = name;
                // Системные поля имеют фиксированный тип (markdown/title), их тип не меняем.
                if (!field.IsSystem) field.FieldType = _selectedType;
                // Пункты «Списка»/«Статуса», формат «Числа», правило «Файла» — в ConfigJson.
                if (FieldTypeInfo.IsOptionList(_selectedType)) field.ConfigJson = BuildOptionsConfigJson();
                else if (_selectedType == "number")           field.ConfigJson = BuildNumberConfigJson();
                else if (_selectedType == "file")             field.ConfigJson = fileConfig;
                else if (_selectedType == "relation")         field.ConfigJson = relationConfig;
                _repository.UpdateField(field);
                savedFieldId = _editFieldId.Value;
            }
            else
            {
                var fields = _repository.GetFields(_collectionId);
                var maxPos = fields.Count > 0 ? fields.Max(f => f.Position) : 0;

                var added = _repository.AddField(new DbField
                {
                    CollectionId = _collectionId,
                    Name         = name,
                    FieldType    = _selectedType,
                    Position     = maxPos + 1000,
                    ConfigJson   = FieldTypeInfo.IsOptionList(_selectedType) ? BuildOptionsConfigJson()
                                 : _selectedType == "number"                 ? BuildNumberConfigJson()
                                 : _selectedType == "file"                   ? fileConfig
                                 : _selectedType == "relation"               ? relationConfig
                                 : "{}",
                });
                savedFieldId = added.Id!.Value;
            }
        }
        catch (InvalidOperationException ex)
        {
            // Дубль имени поля и прочие нарушения — показываем в баннере, не роняя приложение.
            ShowError(ex.Message);
            NameBox.Focus();
            return;
        }

        // Для типа «Дата» применяем переключатель «Отображать в календаре»: включённая дата
        // тянет за собой роль «Описание» (окно события правит и его), поэтому сначала
        // спрашиваем про одноимённые столбцы коллекции.
        if (_selectedType == "date")
        {
            var inCalendar = CalDateToggle.IsChecked == true;
            var adopt      = !inCalendar || await ConfirmRoleAdoption(ConstructorRepository.CalendarRoles,
                                                                      savedFieldId, "календарь");
            try
            {
                _repository.SetDateFieldVisible(savedFieldId, inCalendar, adopt);
                // Тумблер мог быть включён и раньше — тогда SetDateFieldVisible уходит в ранний
                // возврат, а проверить столбцы всё равно нужно (пользователь мог переименовать
                // поля и хочет переключить роль на другое).
                if (inCalendar)
                    _repository.EnsureRoles(_collectionId, ConstructorRepository.CalendarRoles,
                                            adopt, savedFieldId);
            }
            catch (InvalidOperationException ex)
            {
                ShowError(ex.Message);
                return;
            }
        }

        // Для типа «Пользователь» — «Добавить в раздел задач»: коллекция доводится до полного
        // набора ролей («Назначил», «Статус», «Дата», «Описание»). Нет права на создание полей —
        // покажем причину баннером.
        if (_selectedType == "user")
        {
            var inTasks = TaskToggle.IsChecked == true;
            var adopt   = !inTasks || await ConfirmRoleAdoption(ConstructorRepository.TaskRoles,
                                                                savedFieldId, "раздел «Задачи»");
            try
            {
                _repository.SetUserFieldTaskExec(savedFieldId, inTasks, adopt);
                if (inTasks)
                    _repository.EnsureRoles(_collectionId, ConstructorRepository.TaskRoles,
                                            adopt, savedFieldId);
            }
            catch (InvalidOperationException ex)
            {
                ShowError(ex.Message);
                return;
            }
        }

        // Авто-привязка файлов: материализуем пути. Окно прогресса — только если операция
        // не успела быстро (маленькая коллекция закрывает диалог без мелькания окна).
        if (fileAuto)
        {
            using var cts = new CancellationTokenSource();
            ProgressDialog? dlg = null;
            var progress = new Progress<double>(f => dlg?.SetProgress(f, "Поиск файлов…"));
            var bg = _repository.ResolveAutoFileFieldAsync(savedFieldId, progress, cts.Token);
            if (await Task.WhenAny(bg, Task.Delay(400)) != bg)
            {
                dlg = new ProgressDialog { Title = "Привязка файлов" };
                dlg.SetProgress(0, "Поиск файлов…");
                dlg.EnableCancel(() => cts.Cancel());   // Отмена → значения не меняются
                var closed = dlg.ShowDialog(this);
                await bg;
                dlg.Done();
                await closed;
            }
            if (await bg == ConstructorRepository.FileAutoResolveResult.FolderInaccessible)
                await ConfirmDialog.Info("Папка недоступна").ShowDialog(this);
        }

        Close(true);
    }
}
