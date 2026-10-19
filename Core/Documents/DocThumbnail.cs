using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using SkiaSharp;

namespace SmartBase.Core.Documents;

/// <summary>
/// Миниатюра документа — первая страница PDF, отрисованная в PNG и сохраненная на диск.
///
/// Рендер идет через <c>Docnet.Core</c> (PDFium) — тот же движок, которым расширение «Водяной
/// знак» переводит страницы в пиксели. Для остальных форматов миниатюры нет: рисовать DOCX и
/// XLSX нечем, и карточка честно показывает бейдж формата.
///
/// <b>Кеш — файлы на диске, а не память.</b> Лента прокручивается через тысячи документов, и
/// держать их картинки в памяти нельзя. Имя файла кеша содержит время правки и размер исходного
/// документа, поэтому инвалидация бесплатна: изменился документ — другое имя, старая картинка
/// просто перестает запрашиваться, а уборка по возрасту сотрет ее позже.
///
/// <b>PDFium не потокобезопасен.</b> <c>DocLib.Instance</c> — синглтон на процесс, поэтому
/// рендер идет через общий <see cref="PdfGate"/>. Замок именно ОБЩИЙ, а не свой: страницы
/// рисуют еще двое — панель чтения и расширение «Водяной знак», — и два независимых замка
/// пропускали бы друг друга.
/// </summary>
public static class DocThumbnail
{
    /// <summary>
    /// Ширина миниатюры в пикселях. Считается от САМОГО БОЛЬШОГО окошка карточки (126 px при
    /// большом размере), с запасом на экран с двойной плотностью и зум: 126 × 2 × 1,3 ≈ 328.
    /// Меньше — и на большой карточке шапка страницы расплывется.
    /// </summary>
    public const int Width = 336;

    /// <summary>
    /// Пропорции окошка под миниатюру на плитке карточки — 7:5. Числа даны в маленьком размере
    /// (56×40); средний и большой держат те же пропорции, поэтому картинка в кеше ОДНА на все
    /// три размера, а не три разных (см. DocTiles). Готовая страница ПОДРЕЗАЕТСЯ под них СВЕРХУ —
    /// показывается шапка первой страницы, то есть как раз то, по чему документ узнают. Вписывать
    /// страницу целиком нельзя: лист А4 в таком окошке съежился бы до половины ширины с белыми
    /// полями по бокам.
    /// </summary>
    public const int BoxWidth  = 56;
    public const int BoxHeight = 40;

    /// <summary>
    /// Отметка формы картинки в имени файла кеша. Ключ кеша — путь, время правки и размер; форма
    /// в него не входит, поэтому при смене подрезки старые картинки молча подошли бы с неверными
    /// пропорциями. Меняете <see cref="BoxWidth"/>/<see cref="BoxHeight"/>, <see cref="Width"/>
    /// или сам способ отрисовки — меняйте и это: старые файлы просто перестанут запрашиваться и
    /// уйдут с уборкой по возрасту.
    ///
    /// <c>t4</c> — после починки перекошенных сканов: в кеше у людей лежат картинки, разъехавшиеся
    /// по диагонали, и отличить их от здоровых по имени нечем.
    /// </summary>
    private const string ShapeTag = "t4";

    /// <summary>Сколько дней держим неиспользуемую картинку, прежде чем уборка ее сотрет.</summary>
    public const int KeepDays = 60;

    /// <summary>Имя подпапки кеша в папке данных приложения.</summary>
    public const string FolderName = "thumbs";

    /// <summary>Умеем ли нарисовать миниатюру такого файла (по расширению, файл не трогаем).</summary>
    public static bool IsSupported(string path) =>
        DocTextExtractor.ExtensionOf(path) == "pdf";

    /// <summary>
    /// Путь к файлу миниатюры в кеше — он же ключ: имя содержит хеш пути, время правки и размер.
    /// Файла может еще не быть; создает его <see cref="Render"/>.
    /// </summary>
    public static string CachePath(string cacheDir, string docPath, long mtime, long size)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(docPath)))[..16];
        return System.IO.Path.Combine(cacheDir, $"{hash}-{mtime}-{size}-{ShapeTag}.png");
    }

    /// <summary>
    /// Готовая миниатюра из кеша либо <c>null</c>, если ее еще нет. Ничего не рисует, но
    /// спрашивает у диска отметку самого документа — а он может лежать на сервере. Карточка
    /// ленты поэтому ходит другим путем, с отметкой, снятой в фоне вместе со страницей.
    /// </summary>
    public static string? Cached(string cacheDir, string docPath)
    {
        var (mtime, size) = DocStamps.Of(docPath);
        return Cached(cacheDir, docPath, mtime, size);
    }

    /// <summary>
    /// Готовая миниатюра по уже известной отметке документа (<see cref="DocStamps"/>): из всех
    /// обращений к диску остается одно — к локальной папке кеша. Этим путем ходит карточка
    /// ленты, и в UI-потоке ей нельзя ждать ответа сервера.
    /// </summary>
    public static string? Cached(string cacheDir, string docPath, long mtime, long size)
    {
        if (!IsSupported(docPath) || size < 0) return null;

        var file = CachePath(cacheDir, docPath, mtime, size);
        return File.Exists(file) ? file : null;
    }

    /// <summary>
    /// Рисует миниатюру и кладет ее в кеш; возвращает путь к картинке либо <c>null</c>, если
    /// нарисовать не удалось. Исключений не бросает: в ленте попадаются битые и занятые файлы,
    /// и для карточки это рядовой исход — она покажет бейдж.
    /// </summary>
    public static string? Render(string cacheDir, string docPath, CancellationToken ct = default)
    {
        if (!IsSupported(docPath)) return null;
        var (mtime, size) = DocStamps.Of(docPath);
        if (size < 0) return null;

        var file = CachePath(cacheDir, docPath, mtime, size);
        if (File.Exists(file)) return file;

        try
        {
            Directory.CreateDirectory(cacheDir);
            ct.ThrowIfCancellationRequested();

            var png = PdfGate.Run(() =>
            {
                // Размер задается по ширине; высота считается по пропорции страницы — PageDimensions
                // с одним измерением этого не умеет, поэтому сначала читаем размер страницы.
                using var probe = DocLib.Instance.GetDocReader(docPath, new PageDimensions(Width, Width));
                if (probe.GetPageCount() == 0) return [];

                using var first  = probe.GetPageReader(0);
                var pageW = first.GetPageWidth();
                var pageH = first.GetPageHeight();
                if (pageW <= 0 || pageH <= 0) return [];

                // PageDimensions — это РАМКА, в которую страница вписывается целиком, и Docnet
                // требует, чтобы первое число не превышало второго: на альбомной странице
                // (чертеж, разворот) высота по пропорции меньше ширины, и рамка 336×237 роняла
                // отрисовку ArgumentException. Ошибку глотал catch ниже, поэтому альбомные
                // документы просто никогда не получали миниатюру — молча, без следа в журнале.
                // Квадратная рамка вписывает такую страницу по ширине, ровно как нам и нужно.
                var height = Math.Max(1, (int)Math.Round(Width * (double)pageH / pageW));
                using var reader = DocLib.Instance.GetDocReader(docPath,
                                       new PageDimensions(Width, Math.Max(Width, height)));
                using var page   = reader.GetPageReader(0);
                return Encode(page);
            });

            if (png.Length == 0) return null;
            // Через временный файл: прерванная запись не должна оставить в кеше обрезанный PNG,
            // который потом молча не откроется.
            var temp = file + ".tmp";
            File.WriteAllBytes(temp, png);
            File.Move(temp, file, overwrite: true);
            return file;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static byte[] Encode(Docnet.Core.Readers.IPageReader page)
    {
        // Размеры берутся У СТРАНИЦЫ, а не те, что просили. PDFium вписывает страницу в заданную
        // рамку с сохранением пропорций и округляет: там, где просили 336 пикселей ширины, он
        // отдает 335. Холст на 336 при таких данных разъезжает ровно на пиксель в КАЖДОЙ строке —
        // картинка уходит по диагонали, снизу остается черный клин. На сплошном скане это видно
        // сразу, на обычном текстовом листе почти нет, потому дефект и дожил до ленты.
        //
        // Тот же порядок у расширения «Водяной знак» (WatermarkStamp.StampPage) — там холст
        // изначально строится по GetPageWidth/GetPageHeight.
        var width  = page.GetPageWidth();
        var height = page.GetPageHeight();
        if (width <= 0 || height <= 0) return [];

        // PDFium отдает страницу как BGRA, и незакрашенные места в ней ПРОЗРАЧНЫ, а не белы:
        // без подмены фона обычный текстовый лист уехал бы в картинку черным прямоугольником.
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var raw = page.GetImage(new NaiveTransparencyRemover(255, 255, 255));
        if (raw.Length == 0) return [];
        // Не больше, чем влезает в холст: лишний байт здесь — это запись в чужую память.
        System.Runtime.InteropServices.Marshal.Copy(raw, 0, bitmap.GetPixels(),
                                                    Math.Min(raw.Length, bitmap.ByteCount));

        using var full = SKImage.FromBitmap(bitmap);

        // Подрезка под окошко плитки — сверху. Страница выше окошка почти всегда (лист А4 против
        // 56×40), но проверка нужна: широкий чертеж в альбомной ориентации бывает и ниже, и тогда
        // резать нечего.
        var crop = Math.Max(1, (int)Math.Round(width * (double)BoxHeight / BoxWidth));
        using var cut = crop < height ? full.Subset(SKRectI.Create(0, 0, width, crop)) : null;

        using var data = (cut ?? full).Encode(SKEncodedImageFormat.Png, 90);
        return data?.ToArray() ?? [];
    }

    /// <summary>
    /// Уборка кеша: стирает старые картинки по времени записи и ограничивает объем папки. Зовется раз в запуск,
    /// ошибки глотает — недоступный кеш это не повод падать.
    /// </summary>
    public static int Tidy(string cacheDir, int keepDays = KeepDays,
                           long maxBytes = DocLibraryLimits.ThumbnailCacheBytes)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(cacheDir)) return 0;
            var edge = DateTime.UtcNow.AddDays(-Math.Max(1, keepDays));
            var files = new DirectoryInfo(cacheDir).EnumerateFiles("*.png")
                .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
            var bytes = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (file.LastWriteTimeUtc >= edge && bytes <= Math.Max(0, maxBytes)) continue;
                try
                {
                    var size = file.Length;
                    file.Delete();
                    bytes -= size;
                    removed++;
                }
                catch { /* занят или пропал — пропускаем */ }
            }
        }
        catch { /* нет доступа — уборка не мешает чтению */ }
        return removed;
    }

    /// <summary>Полностью очищает кеш миниатюр — кнопка на экране настроек.</summary>
    public static void Clear(string cacheDir)
    {
        try
        {
            if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, recursive: true);
        }
        catch { /* занят — сотрется при следующей уборке */ }
    }

    /// <summary>Суммарный размер кеша в байтах — для экрана настроек.</summary>
    public static long Size(string cacheDir)
    {
        try
        {
            if (!Directory.Exists(cacheDir)) return 0;
            return Directory.EnumerateFiles(cacheDir, "*.png").Sum(f =>
            {
                try { return new FileInfo(f).Length; } catch { return 0L; }
            });
        }
        catch { return 0; }
    }
}
