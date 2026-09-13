using Avalonia.Controls;
using SmartBase.Core.Models;
using SmartBase.Utils;

namespace SmartBase.Views.Settings;

/// <summary>
/// Раздел «Настройки → Конструктор базы данных» (partial SettingsView, без ViewModel — как
/// «Календарь» и «Задачи»). Строки стартового вида и видимости видов обслуживает общий
/// InitAppearance (контролы просто живут в этой панели); остальные строки наполняются здесь
/// и пишут в живой сервис <see cref="ConstructorSettings"/> — таблица и панель записи
/// перечитываются сразу, по событию Changed.
/// </summary>
public partial class SettingsView
{
    // Пункты закрытых списков; индексы совпадают с ComboBox.SelectedIndex.
    //
    // Типы свойств: нарочно НЕ весь список окна «Создание свойства». «Связь» и «Пользователь»
    // без выбранной цели не создаются, а вычисляемые (дата создания, изменение пользователем
    // и т. п.) заполняются сами и руками не вводятся, — стандартным типом ни то ни другое
    // осмысленным не будет. Подписи и ключи совпадают с окном создания свойства.
    private static readonly string[] ConstructorFieldTypeKeys =
        ["text", "number", "date", "checkbox", "select", "status", "multiselect", "url", "file"];

    private static readonly string[] ConstructorFieldTypeLabels =
        ["Текст", "Число", "Дата", "Чекбокс", "Список", "Статус", "Мультисписок", "Ссылка", "Файл"];

    // Цвет новой коллекции: значение «без цвета» плюс палитра приложения (та же, что в меню
    // цвета коллекции). Первый пункт — это #007AFF, он же EventColors.Default: в интерфейсе
    // такая коллекция метки не красит и показывается серой, поэтому и подпись «Без цвета»,
    // а не «Синий» — иначе выбранный «синий» давал бы серую метку в дереве.
    private static readonly string[] ConstructorColorKeys =
        [AppSettings.ConstructorNewCollectionColorDefault, .. AppPalette.Colors.Select(c => c.Hex)];

    private static readonly string[] ConstructorColorLabels =
        ["Без цвета (стандартный)", .. AppPalette.Colors.Select(c => c.Name)];

    private static readonly string[] ConstructorRowHeightLabels =
        ["Компактная", "Стандартная", "Просторная"];

    // Ширины столбца: список, а не поле ввода, — как у всех числовых строк раздела. Значения
    // берем из AppSettings (там же их проверяют Load и Set), здесь только подписи: разойдись
    // эти два списка, выбранный пункт перестал бы отвечать действующему значению.
    private static readonly string[] ConstructorColWidthLabels  =
        ["120 пикселей", "160 пикселей (стандарт)", "200 пикселей", "240 пикселей", "300 пикселей"];

    private static readonly bool[]   ConstructorOpenClickOptions = [false, true];
    private static readonly string[] ConstructorOpenClickLabels  = ["Одинарным кликом", "Двойным кликом"];

    private static readonly bool[]   ConstructorDescriptionOptions = [false, true];
    private static readonly string[] ConstructorDescriptionLabels  = ["В редакторе разметки", "В готовом виде"];

    private static readonly string[] ConstructorPollLabels =
        ["Часто (3 секунды)", "Умеренно (10 секунд)", "Редко (30 секунд)"];

    // Гасит обработчики, пока контролы наполняются кодом (как _tasksBuilding у «Задач»).
    private bool _constructorBuilding;

    // ── Построение раздела ────────────────────────────────────────────────────

    private void RebuildConstructorPanel()
    {
        _constructorBuilding = true;

        ConstructorNewFieldTypeCombo.ItemsSource   = ConstructorFieldTypeLabels;
        ConstructorNewFieldTypeCombo.SelectedIndex =
            Math.Max(0, Array.IndexOf(ConstructorFieldTypeKeys, ConstructorSettings.NewFieldType));

        ConstructorOpenNewRecordToggle.IsChecked = ConstructorSettings.OpenNewRecord;
        ConstructorConfirmDeleteToggle.IsChecked = ConstructorSettings.ConfirmDelete;

        ConstructorColorCombo.ItemsSource   = ConstructorColorLabels;
        ConstructorColorCombo.SelectedIndex =
            Math.Max(0, Array.IndexOf(ConstructorColorKeys, ConstructorSettings.NewCollectionColor));

        ConstructorRowHeightCombo.ItemsSource   = ConstructorRowHeightLabels;
        ConstructorRowHeightCombo.SelectedIndex =
            Math.Max(0, Array.IndexOf(AppSettings.ConstructorRowHeights, ConstructorSettings.RowHeight));

        ConstructorColWidthCombo.ItemsSource   = ConstructorColWidthLabels;
        ConstructorColWidthCombo.SelectedIndex =
            Math.Max(0, Array.IndexOf(AppSettings.ConstructorColWidths, ConstructorSettings.ColWidth));

        ConstructorColResizeToggle.IsChecked = ConstructorSettings.ColResize;
        ConstructorRowResizeToggle.IsChecked = ConstructorSettings.RowResize;

        ConstructorOpenClickCombo.ItemsSource   = ConstructorOpenClickLabels;
        ConstructorOpenClickCombo.SelectedIndex =
            Math.Max(0, Array.IndexOf(ConstructorOpenClickOptions, ConstructorSettings.OpenByDoubleClick));

        ConstructorDescriptionCombo.ItemsSource   = ConstructorDescriptionLabels;
        ConstructorDescriptionCombo.SelectedIndex =
            Math.Max(0, Array.IndexOf(ConstructorDescriptionOptions, ConstructorSettings.DescriptionPreview));

        ConstructorRestoreRecordToggle.IsChecked  = ConstructorSettings.RestoreRecord;
        ConstructorRememberLayoutToggle.IsChecked = ConstructorSettings.RememberLayout;
        ConstructorRememberFiltersToggle.IsChecked = ConstructorSettings.RememberFilters;

        ConstructorPollCombo.ItemsSource   = ConstructorPollLabels;
        ConstructorPollCombo.SelectedIndex =
            Math.Max(0, Array.IndexOf(AppSettings.ConstructorPollSeconds, ConstructorSettings.PollSeconds));

        _constructorBuilding = false;
    }

    // ── Обработчики ───────────────────────────────────────────────────────────

    private void OnConstructorNewFieldTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_constructorBuilding) return;
        var i = ConstructorNewFieldTypeCombo.SelectedIndex;
        if (i >= 0 && i < ConstructorFieldTypeKeys.Length)
            ConstructorSettings.SetNewFieldType(ConstructorFieldTypeKeys[i]);
    }

    private void OnConstructorOpenNewRecordToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_constructorBuilding) return;
        ConstructorSettings.SetOpenNewRecord(ConstructorOpenNewRecordToggle.IsChecked == true);
    }

    private void OnConstructorConfirmDeleteToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_constructorBuilding) return;
        ConstructorSettings.SetConfirmDelete(ConstructorConfirmDeleteToggle.IsChecked == true);
    }

    private void OnConstructorColorChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_constructorBuilding) return;
        var i = ConstructorColorCombo.SelectedIndex;
        if (i >= 0 && i < ConstructorColorKeys.Length)
            ConstructorSettings.SetNewCollectionColor(ConstructorColorKeys[i]);
    }

    private void OnConstructorRowHeightChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_constructorBuilding) return;
        var i = ConstructorRowHeightCombo.SelectedIndex;
        if (i >= 0 && i < AppSettings.ConstructorRowHeights.Length)
            ConstructorSettings.SetRowHeight(AppSettings.ConstructorRowHeights[i]);
    }

    private void OnConstructorColWidthChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_constructorBuilding) return;
        var i = ConstructorColWidthCombo.SelectedIndex;
        if (i >= 0 && i < AppSettings.ConstructorColWidths.Length)
            ConstructorSettings.SetColWidth(AppSettings.ConstructorColWidths[i]);
    }

    private void OnConstructorColResizeToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_constructorBuilding) return;
        ConstructorSettings.SetColResize(ConstructorColResizeToggle.IsChecked == true);
    }

    private void OnConstructorRowResizeToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_constructorBuilding) return;
        ConstructorSettings.SetRowResize(ConstructorRowResizeToggle.IsChecked == true);
    }

    private void OnConstructorOpenClickChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_constructorBuilding) return;
        var i = ConstructorOpenClickCombo.SelectedIndex;
        if (i >= 0 && i < ConstructorOpenClickOptions.Length)
            ConstructorSettings.SetOpenByDoubleClick(ConstructorOpenClickOptions[i]);
    }

    private void OnConstructorDescriptionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_constructorBuilding) return;
        var i = ConstructorDescriptionCombo.SelectedIndex;
        if (i >= 0 && i < ConstructorDescriptionOptions.Length)
            ConstructorSettings.SetDescriptionPreview(ConstructorDescriptionOptions[i]);
    }

    private void OnConstructorRestoreRecordToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_constructorBuilding) return;
        ConstructorSettings.SetRestoreRecord(ConstructorRestoreRecordToggle.IsChecked == true);
    }

    private void OnConstructorRememberLayoutToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_constructorBuilding) return;
        ConstructorSettings.SetRememberLayout(ConstructorRememberLayoutToggle.IsChecked == true);
    }

    private void OnConstructorRememberFiltersToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_constructorBuilding) return;
        ConstructorSettings.SetRememberFilters(ConstructorRememberFiltersToggle.IsChecked == true);
    }

    private void OnConstructorPollChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_constructorBuilding) return;
        var i = ConstructorPollCombo.SelectedIndex;
        if (i >= 0 && i < AppSettings.ConstructorPollSeconds.Length)
            ConstructorSettings.SetPollSeconds(AppSettings.ConstructorPollSeconds[i]);
    }
}
