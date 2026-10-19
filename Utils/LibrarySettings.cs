using SmartBase.Core.Documents;
using SmartBase.Views.Library;

namespace SmartBase.Utils;

/// <summary>
/// Живые настройки раздела «Библиотека документов» (по образцу <see cref="TaskSettings"/> и
/// <see cref="CalendarSettings"/>): кеш значений из <see cref="AppSettings"/> + событие
/// <see cref="Changed"/> — раздел слушает и перечитывается без перезапуска. Set* сохраняют
/// дельту и публикуют изменение. Мусор в хранилище (правка app.db руками, откат версии)
/// читается как стандарт.
/// </summary>
public static class LibrarySettings
{
    /// <summary>Общая сортировка; по умолчанию сохраняется порядок коллекций.</summary>
    public static DocSort Sort { get; private set; } = DocSort.Collections;

    /// <summary>Боковая панель показывает только коллекции с файловым свойством библиотеки
    /// (false — все коллекции).</summary>
    public static bool SidebarOnlyInSection { get; private set; } =
        AppSettings.LibrarySidebarOnlyInSectionDefault;

    // Настройки «разбирать текст автоматически» здесь НЕТ и не будет. Раньше она была, и
    // включена по умолчанию: раздел при каждом заходе молча запускал разбор всего отмеченного —
    // часы процессора и диска, которых никто не просил. Чтение начинает человек кнопкой, и
    // второй дороги к нему нет: настройка, включающая тяжелую фоновую работу, — это та же мина,
    // только спрятанная.

    /// <summary>Порог размера файла для индексации, МБ: тяжелее — не разбираем.</summary>
    public static int IndexMaxMb { get; private set; } = AppSettings.LibraryIndexMaxMbDefault;

    /// <summary>
    /// С какого времени чтение спрашивает подтверждение. Минута — граница, за которой человек
    /// перестает ждать и уходит заниматься другим: об этом стоит предупредить, о четырех
    /// секундах — нет.
    /// </summary>
    public static readonly TimeSpan ConfirmReadFrom = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Сколько документов считать «многими», пока скорость не измерена.
    ///
    /// Начальная оценка — 1 с на документ: прогретый разбор большого официального
    /// руководства RISC-V с записью FTS занял около 0,7 с, округляем вверх. До собственного
    /// замера предупреждаем с 60 документов; после разогрева действует скорость человека.
    /// Набор и результаты: docs/developer/architecture/performance/library-calibration.md.
    /// </summary>
    public const double InitialSecondsPerDocument = 1;
    public const int ConfirmReadFromCount = (int)(60 / InitialSecondsPerDocument);

    /// <summary>
    /// Спрашивать ли подтверждение перед чтением <paramref name="count"/> документов.
    ///
    /// Пока скорость не измерена, решаем по числу документов; после первого прогона — по
    /// времени, и это уже не догадка, а замер на файлах самого человека.
    /// </summary>
    public static bool NeedsReadConfirmation(int count) =>
        SecondsPerDocument is { } perDoc && perDoc > 0
            ? TimeSpan.FromSeconds(perDoc * count) >= ConfirmReadFrom
            : count >= ConfirmReadFromCount;

    /// <summary>
    /// Сколько секунд уходило на документ в прошлых прогонах; <c>null</c> — еще не мерили.
    ///
    /// Замер по НАСТОЯЩИМ файлам человека, а не оценка: документы у всех разные, и угадать
    /// скорость заранее нельзя. Из него считается время в окне подтверждения — в самый первый
    /// раз его там просто нет.
    /// </summary>
    public static double? SecondsPerDocument { get; private set; }

    /// <summary>Показывать миниатюру первой страницы PDF (false — всегда бейдж формата).</summary>
    public static bool Thumbnails { get; private set; } = AppSettings.LibraryThumbnailsDefault;

    /// <summary>
    /// Размер карточки документа в ленте. Управляет заодно и тем, сколько строк найденного
    /// текста видно: маленькая — одна, средняя — три, большая — шесть. Геометрия — в
    /// <see cref="DocTiles"/>.
    /// </summary>
    public static DocCardSize CardSize { get; private set; } =
        DocTiles.Parse(AppSettings.LibraryCardSizeDefault);

    /// <summary>Настройки изменились — разделу пора перечитаться.</summary>
    public static event Action? Changed;

    /// <summary>Счетчик изменений — «догоняющая» перерисовка, как у остальных разделов.</summary>
    public static long Stamp { get; private set; }

    /// <summary>Старт: считать сохраненные значения. Вызывать после AppSettings.AttachStore.</summary>
    public static void Init()
    {
        Sort = Enum.TryParse<DocSort>(AppSettings.LoadLibrarySort(), out var sort) && Enum.IsDefined(sort)
            ? sort : DocSort.Collections;
        SidebarOnlyInSection = AppSettings.LoadLibrarySidebarOnlyInSection();
        IndexMaxMb           = ParseMaxMb(AppSettings.LoadLibraryIndexMaxMb());
        Thumbnails           = AppSettings.LoadLibraryThumbnails();
        CardSize             = DocTiles.Parse(AppSettings.LoadLibraryCardSize());
        SecondsPerDocument   = AppSettings.LoadLibrarySecondsPerDoc();
    }

    public static void SetSort(DocSort sort)
    {
        if (!Enum.IsDefined(sort) || Sort == sort) return;
        Sort = sort;
        AppSettings.SaveLibrarySort(sort.ToString());
        Publish();
    }

    public static void SetSidebarOnlyInSection(bool on)
    {
        if (on == SidebarOnlyInSection) return;
        SidebarOnlyInSection = on;
        AppSettings.SaveLibrarySidebarOnlyInSection(on);
        Publish();
    }

    /// <summary>
    /// Запомнить измеренную скорость разбора. Событие НЕ публикуется: от этого числа зависит
    /// только текст окна подтверждения, а перечитывать из-за него ленту незачем.
    ///
    /// Скорость сглаживается: новый замер весит треть, прежний — две трети. Один прогон по
    /// папке сканов иначе выдал бы за правду скорость, которой в остальной библиотеке нет.
    /// </summary>
    public static void RememberSpeed(double secondsPerDoc)
    {
        if (secondsPerDoc <= 0 || double.IsNaN(secondsPerDoc)) return;

        SecondsPerDocument = SecondsPerDocument is { } old && old > 0
            ? old * 2 / 3 + secondsPerDoc / 3
            : secondsPerDoc;

        AppSettings.SaveLibrarySecondsPerDoc(SecondsPerDocument.Value);
    }

    public static void SetIndexMaxMb(int mb)
    {
        mb = ParseMaxMb(mb);
        if (mb == IndexMaxMb) return;
        IndexMaxMb = mb;
        AppSettings.SaveLibraryIndexMaxMb(mb);
        Publish();
    }

    public static void SetThumbnails(bool on)
    {
        if (on == Thumbnails) return;
        Thumbnails = on;
        AppSettings.SaveLibraryThumbnails(on);
        Publish();
    }

    public static void SetCardSize(DocCardSize size)
    {
        if (size == CardSize) return;
        CardSize = size;
        AppSettings.SaveLibraryCardSize(DocTiles.KeyOf(size));
        Publish();
    }

    private static void Publish()
    {
        Stamp++;
        Changed?.Invoke();
    }

    // 0 и меньше = «без порога»: осознанный выбор, а не мусор. Верхняя граница — чтобы опечатка
    // в app.db не заставила очередь читать гигабайтные файлы.
    private static int ParseMaxMb(int mb) => mb <= 0 ? 0 : Math.Min(mb, 1024);

    /// <summary>Понятная человеку строка поддержанных форматов — для подсказок раздела.</summary>
    public static string SupportedFormats => DocTextExtractor.SupportedText;
}
