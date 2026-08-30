using System.Globalization;

namespace SmartBase.Extensions.PdfWatermark;

/// <summary>
/// Номер экземпляра и имена файлов. Чистая логика: ни базы, ни UI, ни PDF.
///
/// Номер уникален В ПРЕДЕЛАХ ДОКУМЕНТА, а документ — это имя файла без расширения. У каждого
/// документа своя нумерация с единицы: занятый «экз. 1» сборочного чертежа никак не мешает
/// выдать «экз. 1» спецификации. Отсюда и устройство методов — они ничего не знают про то, где
/// хранятся номера, и получают уже отобранный набор занятых.
///
/// Один и тот же документ можно выдать одному и тому же человеку сколько угодно раз: экземпляры
/// различаются номером, и это норма, а не дубль.
/// </summary>
public static class CopyNumber
{
    /// <summary>Первый и последний номер. Верхний предел — защита от разбора мусора вроде
    /// «2026» в чужой колонке, а не настоящее ограничение на выдачу.</summary>
    public const int MinNumber = 1;
    public const int MaxNumber = 999_999;

    /// <summary>Документ = имя файла без расширения. «АБВГ.301121.001 СБ.pdf» → «АБВГ.301121.001 СБ».</summary>
    public static string DocumentOf(string path) => Path.GetFileNameWithoutExtension(path);

    /// <summary>
    /// Номер из ячейки «Экземпляр»; <c>null</c> — не номер, ничего не занимает.
    ///
    /// Читается ведущий прогон цифр, хвост игнорируется: «3 (копия)» занимает 3. Выдать номер
    /// дважды хуже, чем пропустить один, — на бумаге два экземпляра с одним номером уже не
    /// различить.
    /// </summary>
    public static int? NumberIn(string? value)
    {
        var text   = (value ?? "").Trim();
        var digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits])) digits++;
        if (digits == 0) return null;

        // Больше шести цифр — заведомо не наш номер (дата, артикул, чужие данные в колонке).
        if (digits > MaxNumber.ToString(CultureInfo.InvariantCulture).Length) return null;

        var number = int.Parse(text[..digits], CultureInfo.InvariantCulture);
        return number is >= MinNumber and <= MaxNumber ? number : null;
    }

    /// <summary>
    /// Первый свободный номер документа. Дырки заполняются: заняты 1 и 3 — вернется 2.
    /// <c>null</c> — диапазон исчерпан.
    /// </summary>
    public static int? FirstFree(IEnumerable<int> taken, int max = MaxNumber)
    {
        var busy = taken as ISet<int> ?? new HashSet<int>(taken);

        for (var n = MinNumber; n <= max; n++)
            if (!busy.Contains(n)) return n;

        return null;
    }

    /// <summary>Имя результата: «АБВГ.301121.001 СБ экз. 3.pdf».</summary>
    public static string FileNameFor(string document, int number) => $"{document} экз. {number}.pdf";

    /// <summary>
    /// Свободный путь в папке результата: занято — приписывается « (2)», « (3)» и так далее.
    ///
    /// Счетчик, а не перезапись: имя уже несет номер экземпляра, значит совпадение означает
    /// повторный прогон по почищенному журналу — и затирать чужой результат в этом случае
    /// нельзя, о нем можно было уже отчитаться.
    ///
    /// <paramref name="forbidden"/> — путь исходного файла: если папка результата совпала с
    /// исходной, писать поверх источника нельзя ни при каких условиях.
    /// </summary>
    public static string UniquePath(string folder, string fileName, string? forbidden = null)
    {
        var stem      = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var copy = 1; ; copy++)
        {
            var candidate = Path.Combine(folder, copy == 1 ? fileName : $"{stem} ({copy}){extension}");

            var clash = File.Exists(candidate) ||
                        (forbidden is not null &&
                         string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(forbidden),
                                       StringComparison.OrdinalIgnoreCase));

            if (!clash) return candidate;
        }
    }

    /// <summary>
    /// Текст знака ТРЕМЯ строками: кому выдан · экземпляр и дата · предупреждение.
    ///
    /// Три строки, а не одна: длина строки задает кегль (знак вписывается в диагональ листа), и
    /// одна длинная надпись получалась мелкой — мельче текста самого документа. Разбитая на
    /// строки, она занимает тот же лист буквами вдвое-втрое крупнее.
    ///
    /// Пустое предупреждение и пустое ФИО просто отбрасываются: знак остается осмысленным и без
    /// них, а пустая строка забрала бы себе место в блоке.
    /// </summary>
    public static IReadOnlyList<string> StampText(string holder, int number, DateTime date, string warning)
    {
        var lines = new List<string>(3);

        if (holder.Trim() is { Length: > 0 } who) lines.Add($"Выдан: {who}");

        lines.Add($"Экз. № {number} · {date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}");

        if (warning.Trim() is { Length: > 0 } text) lines.Add(text);

        return lines;
    }
}
