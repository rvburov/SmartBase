using Avalonia.Controls;
using Avalonia.Interactivity;
using SmartBase.Core.Documents;
using SmartBase.Database.DocIndex;
using SmartBase.Utils;

namespace SmartBase.Views.Settings;

/// <summary>
/// Раздел «Настройки → Библиотека документов» (partial SettingsView, без ViewModel — как
/// «Календарь» и «Задачи»). Строки стартового вида и видимости видов обслуживает общий
/// InitAppearance (контролы просто живут в этой панели); остальные строки наполняются здесь и
/// пишут в живой сервис <see cref="LibrarySettings"/> — раздел перечитывается сразу, по событию
/// Changed.
/// </summary>
public partial class SettingsView
{
    // Пункты закрытого списка порога; индексы совпадают с ComboBox.SelectedIndex.
    // 0 означает «без порога» — осознанный выбор для тех, у кого лежат тяжелые чертежи.
    private static readonly int[] LibraryIndexMaxOptions = [16, 32, 64, 128, 256, 0];
    private static readonly string[] LibraryIndexMaxLabels =
        ["16 МБ", "32 МБ", "64 МБ", "128 МБ", "256 МБ", "Без ограничения"];

    // Размер карточки. Подписи называют не пиксели, а то, что человек увидит: число строк
    // найденного текста — единственное, что от размера зависит содержательно.
    private static readonly Views.Library.DocCardSize[] LibraryCardSizeOptions =
        [Views.Library.DocCardSize.Small, Views.Library.DocCardSize.Medium, Views.Library.DocCardSize.Large];
    private static readonly string[] LibraryCardSizeLabels =
        ["Маленькая — одна строка", "Средняя — три строки", "Большая — шесть строк"];

    // Гасит обработчики, пока контролы наполняются кодом (как _tasksBuilding у «Задач»).
    private bool _libraryBuilding;

    // ── Построение раздела ────────────────────────────────────────────────────

    private void RebuildLibraryPanel()
    {
        _libraryBuilding = true;

        LibrarySidebarOnlyToggle.IsChecked = LibrarySettings.SidebarOnlyInSection;
        LibraryThumbsToggle.IsChecked      = LibrarySettings.Thumbnails;

        LibraryCardSizeCombo.ItemsSource   = LibraryCardSizeLabels;
        LibraryCardSizeCombo.SelectedIndex =
            Math.Max(0, Array.IndexOf(LibraryCardSizeOptions, LibrarySettings.CardSize));

        LibraryIndexMaxCombo.ItemsSource   = LibraryIndexMaxLabels;
        var idx = Array.IndexOf(LibraryIndexMaxOptions, LibrarySettings.IndexMaxMb);
        LibraryIndexMaxCombo.SelectedIndex = idx >= 0 ? idx : Array.IndexOf(LibraryIndexMaxOptions, 64);

        UpdateLibraryCacheDesc();

        _libraryBuilding = false;
    }

    // Сводка кеша: сколько разобрано и сколько это занимает на диске. Числа читаются с диска,
    // поэтому обновляются при заходе в раздел и после очистки, а не по таймеру.
    private void UpdateLibraryCacheDesc()
    {
        var thumbs = DocThumbnail.Size(AppSettings.DocThumbsDir);

        if (DocIndexService.Current is not { } service)
        {
            LibraryCacheDesc.Text =
                "Кеш документов не открылся — поиск по содержимому недоступен. " +
                "Подробности в разделе «Диагностика».";
            LibraryClearCacheBtn.IsEnabled = thumbs > 0;
            return;
        }

        try
        {
            var (indexed, noText, failed, bytes) = service.Index.Stats();
            // Форматы перечисляем прямо здесь: без этого «разобрано 1 190» не объясняет, почему
            // часть документов так и не находится по содержимому, а это главный источник
            // недоумения. Раньше это стояло у тумблера автоматической индексации, а тумблера
            // больше нет — чтение запускает кнопка в самом разделе.
            LibraryCacheDesc.Text =
                $"Разобрано документов: {indexed}. Без текстового слоя: {noText}. " +
                $"Не прочитано: {failed}. Занято на диске: {Mb(bytes + thumbs)}. " +
                $"Текст читается у {DocTextExtractor.SupportedText}; распознавания картинок нет, " +
                "поэтому сканы без текстового слоя в поиск по содержимому не попадают. " +
                "Очистка удаляет только кеш — сами документы и записи не трогаются, " +
                "а прочитать их заново можно кнопкой в разделе.";
            LibraryClearCacheBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Настройки: сводка кеша документов не прочитана");
            LibraryCacheDesc.Text = "Сводку кеша прочитать не удалось.";
            LibraryClearCacheBtn.IsEnabled = true;
        }
    }

    private static string Mb(long bytes) =>
        bytes < 1024L * 1024 ? $"{Math.Max(1, bytes / 1024)} КБ"
                             : $"{bytes / (1024.0 * 1024):0.#} МБ";

    // ── Обработчики ───────────────────────────────────────────────────────────

    private void OnLibrarySidebarOnlyToggled(object? sender, RoutedEventArgs e)
    {
        if (_libraryBuilding) return;
        LibrarySettings.SetSidebarOnlyInSection(LibrarySidebarOnlyToggle.IsChecked == true);
    }

    private void OnLibraryThumbsToggled(object? sender, RoutedEventArgs e)
    {
        if (_libraryBuilding) return;
        LibrarySettings.SetThumbnails(LibraryThumbsToggle.IsChecked == true);
    }

    private void OnLibraryCardSizeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_libraryBuilding) return;
        var i = LibraryCardSizeCombo.SelectedIndex;
        if (i < 0 || i >= LibraryCardSizeOptions.Length) return;
        LibrarySettings.SetCardSize(LibraryCardSizeOptions[i]);
    }

    private void OnLibraryIndexMaxChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_libraryBuilding) return;
        var i = LibraryIndexMaxCombo.SelectedIndex;
        if (i < 0 || i >= LibraryIndexMaxOptions.Length) return;
        LibrarySettings.SetIndexMaxMb(LibraryIndexMaxOptions[i]);
    }

    private async void OnLibraryClearCacheClick(object? sender, RoutedEventArgs e)
    {
        var owner = VisualRoot as Window;
        if (owner is null) return;

        var ok = await new Controls.Dialogs.ConfirmDialog(
            "Очистить кеш документов и миниатюр?\n\n" +
            "Сами документы и записи не изменятся. Текст разберется заново при следующем " +
            "заходе в раздел — на большой папке это займет время.",
            "Очистить").ShowDialog<bool>(owner);
        if (!ok) return;

        // Очередь могла идти прямо сейчас — иначе она дописала бы в кеш уже после очистки.
        DocIndexService.Current?.Stop();
        try
        {
            DocIndexService.Current?.Index.Clear();
            DocThumbnail.Clear(AppSettings.DocThumbsDir);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Настройки: кеш документов не очищен");
        }
        UpdateLibraryCacheDesc();
    }
}
