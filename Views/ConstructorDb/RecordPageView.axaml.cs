using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DataBase.Controls.Widgets;
using DataBase.Core.Models;
using DataBase.Database.Repositories;
using DataBase.ViewModels.ConstructorDb;

namespace DataBase.Views.ConstructorDb;

/// <summary>
/// Правая панель «Страница записи» (Obsidian-стиль): свойства сверху + markdown снизу.
/// Сохранение значений идёт сразу в БД при изменении (как в TableView).
/// </summary>
public partial class RecordPageView : UserControl
{
    public event EventHandler<int>? RecordChanged;
    public event EventHandler<bool>? CollectionPanelToggled;

    private RecordPageViewModel? _vm;
    private ConstructorRepository? _repo;   // для перечитывания записи из БД (ReloadFromDb)

    // Гасит авто-сейв на время служебной установки текста (Show/ReloadFromDb), чтобы
    // программное TitleBox.Text/MarkdownEditor.Text не приняли за правку пользователя
    // (иначе перечитанное из БД значение тут же писалось бы обратно и дёргало таблицу).
    private bool _suppressAutoSave;

    public RecordPageView()
    {
        InitializeComponent();

        PreviewToggle.IsCheckedChanged += (_, _) => UpdatePreviewVisibility();

        TitleDisplay.PointerPressed += (_, _) =>
        {
            if (_vm is null) return;
            TitleDisplay.IsVisible = false;
            TitleBox.IsVisible     = true;
            TitleBox.Focus();
            TitleBox.SelectAll();
        };

        TitleBox.LostFocus += (_, _) => ExitTitleEdit();
        TitleBox.KeyDown   += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape) { ExitTitleEdit(); e.Handled = true; }
        };

        // Живая запись по символу — как ячейка таблицы: правка имени/описания в панели сразу
        // видна в строке таблицы (RecordChanged → RefreshRecordRow). Обработчики висят на
        // постоянных контролах и читают актуальный _vm, поэтому вешаем их один раз в ctor
        // (а не в Show — иначе при каждом открытии записи копились бы дубли).
        TitleBox.TextChanged += (_, _) =>
        {
            if (_vm is null || _suppressAutoSave) return;
            var entered = TitleBox.Text?.Trim() ?? "";
            _vm.Title = string.IsNullOrEmpty(entered) ? "Без названия" : entered;
            _vm.SaveTitle();
            RecordChanged?.Invoke(this, _vm.RecordId);
        };
        MarkdownEditor.TextChanged += (_, _) =>
        {
            if (_vm is null || _suppressAutoSave) return;
            _vm.MarkdownText = MarkdownEditor.Text ?? "";
            _vm.SaveMarkdown();
            RecordChanged?.Invoke(this, _vm.RecordId);
        };

        CollectionPanelBtn.IsCheckedChanged += (_, _) =>
        {
            UpdateCollectionPanelIcon();
            CollectionPanelToggled?.Invoke(this, CollectionPanelBtn.IsChecked == true);
        };

    }

    /// <summary>Синхронизирует визуальное состояние кнопки с фактической видимостью блока коллекции.</summary>
    public void SetCollectionPanelOpen(bool isOpen)
    {
        if (CollectionPanelBtn.IsChecked == isOpen) return;
        CollectionPanelBtn.IsChecked = isOpen;
    }

    private void UpdateCollectionPanelIcon()
    {
        var open = CollectionPanelBtn.IsChecked == true;
        CollectionOpenIcon.IsVisible  = !open;
        CollectionCloseIcon.IsVisible = open;
        ToolTip.SetTip(CollectionPanelBtn, open ? "Закрыть коллекцию" : "Открыть коллекцию");
    }

    public void Show(ConstructorRepository repository, int recordId, int collectionId)
    {
        _repo = repository;
        _vm = new RecordPageViewModel(repository, recordId, collectionId);
        DataContext = _vm;

        _suppressAutoSave = true;
        TitleBox.Text        = _vm.Title == "Без названия" ? "" : _vm.Title;
        ExitTitleEdit();
        MarkdownEditor.Text  = _vm.MarkdownText;
        _suppressAutoSave = false;
        PreviewToggle.IsChecked = false;
        UpdatePreviewVisibility();

        BuildProperties();
        ApplyPropsHeight();
    }

    /// <summary>
    /// Перечитать ТЕКУЩУЮ запись из БД и обновить панель на месте — когда ту же запись поправили
    /// прямо в таблице. Обновляются значения свойств, имя и описание. Поле, которое пользователь
    /// сейчас редактирует в панели (заголовок в режиме правки или markdown-редактор в фокусе),
    /// не трогаем — чтобы не затереть набираемый текст; режим просмотра markdown сохраняется.
    /// WireAutoSave повторно НЕ вызываем (его LostFocus-подписки читают актуальный _vm).
    /// </summary>
    public void ReloadFromDb()
    {
        if (_vm is null || _repo is null) return;

        var recordId     = _vm.RecordId;
        var collectionId = _vm.CollectionId;
        var preview      = PreviewToggle.IsChecked == true;

        _vm = new RecordPageViewModel(_repo, recordId, collectionId);
        DataContext = _vm;

        // Перечитанные из БД значения ставим под guard'ом — иначе TextChanged принял бы их
        // за правку и тут же записал обратно (+ лишний RefreshRecordRow по таблице).
        _suppressAutoSave = true;

        // Заголовок — только если он не в режиме редактирования.
        if (!TitleBox.IsVisible)
        {
            TitleBox.Text = _vm.Title == "Без названия" ? "" : _vm.Title;
            ExitTitleEdit();
        }

        // Описание — только если редактор не в фокусе (иначе затёрли бы набираемый текст).
        if (!MarkdownEditor.IsFocused)
            MarkdownEditor.Text = _vm.MarkdownText;

        _suppressAutoSave = false;

        BuildProperties();
        ApplyPropsHeight();

        PreviewToggle.IsChecked = preview;
        UpdatePreviewVisibility();
    }

    public void Clear()
    {
        _vm = null;
        _repo = null;
        DataContext = null;
        TitleBox.Text        = "";
        ExitTitleEdit();
        MarkdownEditor.Text  = "";
        MarkdownPreview.Markdown = "";
        PropertiesList.ItemsSource = null;
    }

    private void ExitTitleEdit()
    {
        TitleBox.IsVisible     = false;
        TitleDisplay.IsVisible = true;
        var title = TitleBox.Text ?? "";
        if (string.IsNullOrEmpty(title))
        {
            TitleDisplay.Text       = "Без названия";
            TitleDisplay.Foreground = (IBrush)this.FindResource("BrushSecondaryText")!;
        }
        else
        {
            TitleDisplay.Text       = title;
            TitleDisplay.Foreground = (IBrush)this.FindResource("BrushPrimaryText")!;
        }
    }

    private void UpdatePreviewVisibility()
    {
        var preview = PreviewToggle.IsChecked == true;
        MarkdownPreviewBox.IsVisible = preview;
        MarkdownEditor.IsVisible     = !preview;

        // В режиме редактора кнопка предлагает перейти к динамическому просмотру (карандаш);
        // в режиме просмотра — вернуться к исходному тексту (code-xml).
        IconToPreview.IsVisible = !preview;
        IconToSource.IsVisible  = preview;
        ToolTip.SetTip(PreviewToggle, preview ? "Просмотр исходного текста" : "Динамический просмотр");

        if (preview)
            MarkdownPreview.Markdown = MarkdownEditor.Text ?? "";
    }

    // ── Перетаскивание ручки между свойствами и описанием ──────────────────────
    //
    // По умолчанию блок свойств занимает высоту контента (строка Auto), ручка-пилюля
    // сидит ровно на границе с описанием. Перетаскивание задаёт строке пиксельную
    // высоту: тянем вниз — свойства растут (описание сжимается, оно в Star-строке),
    // тянем вверх — свойства сжимаются, и если их больше, чем влезает, ScrollViewer
    // даёт прокрутку. Выбранная высота помнится в пределах сессии и переносится
    // между записями (на диск не пишем — у разных записей разное число свойств).

    private bool    _propsGripDragging;
    private double  _propsGripStartY;
    private double  _propsGripStartHeight;
    private double? _propsHeight;   // выбранная пользователем высота блока свойств (null = Auto)

    private const double PropsHeightMin = 48;

    // Строка свойств в PageGrid (Auto,Auto,Auto,*,Auto): индекс 1.
    private RowDefinition PropsRow => PageGrid.RowDefinitions[1];

    private void ApplyPropsHeight() =>
        PropsRow.Height = _propsHeight is double h
            ? new GridLength(h, GridUnitType.Pixel)
            : GridLength.Auto;

    private void OnPropsGripPressed(object? sender, PointerPressedEventArgs e)
    {
        _propsGripDragging    = true;
        _propsGripStartY      = e.GetPosition(this).Y;
        _propsGripStartHeight = PropsRow.ActualHeight;   // текущая высота (при Auto = высота контента)
        e.Pointer.Capture((IInputElement?)sender);
        e.Handled = true;
    }

    private void OnPropsGripMoved(object? sender, PointerEventArgs e)
    {
        if (!_propsGripDragging) return;

        var dy = e.GetPosition(this).Y - _propsGripStartY;
        // Не даём свойствам съесть всё описание: оставляем ему запас (шапка+футер+минимум описания).
        var max = Math.Max(PropsHeightMin, PageGrid.Bounds.Height - 260);
        var newHeight = Math.Clamp(_propsGripStartHeight + dy, PropsHeightMin, max);

        PropsRow.Height = new GridLength(newHeight, GridUnitType.Pixel);
        _propsHeight = newHeight;
    }

    private void OnPropsGripReleased(object? sender, PointerReleasedEventArgs e)
    {
        _propsGripDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void BuildProperties()
    {
        if (_vm is null) { PropertiesList.ItemsSource = null; return; }

        PropertiesList.ItemTemplate = new FuncDataTemplate<PropertyRowVm>((row, _) => BuildPropertyRow(row));
        PropertiesList.ItemsSource  = _vm.Properties;
    }

    private Control BuildPropertyRow(PropertyRowVm row)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("140,*"),
            Margin = new Thickness(0, 0, 0, 6),
        };

        var nameLabel = new TextBlock
        {
            Text                = row.Field.Name,
            VerticalAlignment   = VerticalAlignment.Center,
            Foreground          = (IBrush)this.FindResource("BrushSecondaryText")!,
            FontSize            = 13,
        };
        Grid.SetColumn(nameLabel, 0);

        var input = MakeInputForField(row);
        Grid.SetColumn(input, 1);

        grid.Children.Add(nameLabel);
        grid.Children.Add(input);
        return grid;
    }

    // Сохранить значение свойства И сообщить наверх (RecordChanged) — чтобы таблица обновила ЭТУ
    // строку точечно (RefreshRecordRow), а не перезагружалась целиком. Дёшево: одна строка на месте.
    private void PersistProperty(PropertyRowVm row)
    {
        row.Persist();
        if (_vm is not null) RecordChanged?.Invoke(this, _vm.RecordId);
    }

    private Control MakeInputForField(PropertyRowVm row)
    {
        var f = row.Field;
        Control input;

        switch (f.FieldType)
        {
            case "number":
            {
                var tb = new TextBox { Text = row.Value, BorderThickness = new Thickness(1), Height = 32 };
                // Тот же фильтр ввода, что и в ячейке таблицы: пускаем только цифры, один
                // разделитель и ведущий минус — иначе живая запись по символу клала бы в БД
                // промежуточный мусор («-», «12,»).
                tb.AddHandler(InputElement.TextInputEvent, OnNumberTextInput, RoutingStrategies.Tunnel);
                tb.TextChanged += (_, _) => { row.Value = tb.Text ?? ""; PersistProperty(row); };
                input = tb;
                break;
            }
            case "date":
            {
                // Тот же редактор, что и в таблице: хранит/отдаёт канон DateValue (диапазоны,
                // дата+время, «весь день»). Прежний CalendarDatePicker понимал только «dd.MM.yyyy»
                // и терял богатые значения (диапазон/время «пропадал» в панели).
                var editor = new DateEditor
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Storage             = row.Value,
                };
                editor.Changed += (_, _) => { row.Value = editor.Storage ?? ""; PersistProperty(row); };
                input = editor;
                break;
            }
            case "select":
            {
                var combo = new ComboBox
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Height              = 32,
                    ItemsSource         = SelectOptions(f),
                    SelectedItem        = row.Value,
                };
                combo.SelectionChanged += (_, _) =>
                {
                    row.Value = combo.SelectedItem?.ToString() ?? "";
                    PersistProperty(row);
                };
                input = combo;
                break;
            }
            case "checkbox":
            {
                var cb = new CheckBox
                {
                    IsChecked         = row.Value is "1" or "true" or "True",
                    VerticalAlignment = VerticalAlignment.Center,
                };
                cb.IsCheckedChanged += (_, _) =>
                {
                    row.Value = cb.IsChecked == true ? "1" : "0";
                    PersistProperty(row);
                };
                input = cb;
                break;
            }
            case "url":
            {
                var tb = new TextBox { Text = row.Value, Watermark = "https://", Height = 32 };
                tb.TextChanged += (_, _) => { row.Value = tb.Text ?? ""; PersistProperty(row); };
                input = tb;
                break;
            }
            default:  // text
            {
                var tb = new TextBox { Text = row.Value, Height = 32 };
                tb.TextChanged += (_, _) => { row.Value = tb.Text ?? ""; PersistProperty(row); };
                input = tb;
                break;
            }
        }

        return input;
    }

    // Фильтр ввода числового поля (как в TableView): символ принимается только если итоговый
    // текст остаётся допустимым (частичным) числом — цифры, один разделитель, ведущий минус.
    private static void OnNumberTextInput(object? sender, TextInputEventArgs e)
    {
        if (sender is not TextBox tb || string.IsNullOrEmpty(e.Text)) return;

        var text  = tb.Text ?? "";
        var start = Math.Min(tb.SelectionStart, tb.SelectionEnd);
        var end   = Math.Max(tb.SelectionStart, tb.SelectionEnd);
        var prospective = text[..start] + e.Text + text[end..];

        if (!IsPartialNumber(prospective)) e.Handled = true;
    }

    // Допускает незавершённый ввод: "", "-", "12", "12,", "-3.5".
    private static bool IsPartialNumber(string s)
    {
        if (s.Length == 0) return true;
        var i = s[0] == '-' ? 1 : 0;
        var seps = 0;
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '.' or ',') { if (++seps > 1) return false; }
            else if (!char.IsDigit(c)) return false;
        }
        return true;
    }

    private static List<string> SelectOptions(DbField field)
    {
        try
        {
            using var doc = JsonDocument.Parse(field.ConfigJson ?? "{}");
            if (doc.RootElement.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                return opts.EnumerateArray().Select(o => o.ToString()).ToList();
        }
        catch { /* ignore bad json */ }
        return [];
    }
}
