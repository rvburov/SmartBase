using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Spreadsheet;

namespace SmartBase.Extensions.ProductCompare;

/// <summary>
/// Стили книги-результата: добавление шрифтов, заливок, рамок, форматов чисел и форматов
/// ячеек без повторов. Одинаковый элемент второй раз не добавляется — иначе на сотнях строк
/// из нескольких файлов таблица стилей разрастается до предела Excel (64 000 форматов).
/// </summary>
internal sealed class TargetStyles
{
    private const uint FirstCustomNumberFormat = 164;   // номера ниже — встроенные форматы Excel

    private readonly Stylesheet _sheet;
    private readonly Dictionary<string, uint> _fonts   = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> _fills   = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> _borders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> _formats = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> _numbers = new(StringComparer.Ordinal);

    public TargetStyles(Stylesheet sheet)
    {
        _sheet = sheet;
        Index(Fonts,   _fonts);
        Index(Fills,   _fills);
        Index(Borders, _borders);
        Index(Formats, _formats);
        foreach (var format in _sheet.NumberingFormats?.Elements<NumberingFormat>() ?? [])
            if (format.FormatCode?.Value is { } code && format.NumberFormatId?.Value is { } id)
                _numbers.TryAdd(code, id);
    }

    private Fonts         Fonts   => _sheet.Fonts         ??= new Fonts(new Font());
    private Fills         Fills   => _sheet.Fills         ??= new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }));
    private Borders       Borders => _sheet.Borders       ??= new Borders(new Border());
    private CellFormats   Formats => _sheet.CellFormats   ??= new CellFormats(new CellFormat());

    public uint Font(Font font)               => Add(Fonts,   _fonts,   font);
    public uint Fill(Fill fill)               => Add(Fills,   _fills,   fill);
    public uint Border(Border border)         => Add(Borders, _borders, border);
    public uint Format(CellFormat format)     => Add(Formats, _formats, format);

    public uint NumberFormat(string code)
    {
        if (_numbers.TryGetValue(code, out var id)) return id;

        var formats = _sheet.NumberingFormats;
        if (formats is null)
        {
            formats = new NumberingFormats();
            _sheet.InsertAt(formats, 0);                 // numFmts — первый по схеме
        }
        id = Math.Max(FirstCustomNumberFormat,
                      (formats.Elements<NumberingFormat>().Max(f => f.NumberFormatId?.Value) ?? 0) + 1);
        formats.Append(new NumberingFormat { NumberFormatId = id, FormatCode = code });
        formats.Count = (uint)formats.ChildElements.Count;
        _numbers[code] = id;
        return id;
    }

    private static void Index(OpenXmlCompositeElement list, Dictionary<string, uint> index)
    {
        uint i = 0;
        foreach (var item in list.ChildElements) index.TryAdd(Key(item), i++);
    }

    private static uint Add(OpenXmlCompositeElement list, Dictionary<string, uint> index, OpenXmlElement item)
    {
        var key = Key(item);
        if (index.TryGetValue(key, out var id)) return id;

        id = (uint)list.ChildElements.Count;
        list.Append(item.Parent is null ? item : item.CloneNode(true));
        index[key] = id;

        var count = (uint)list.ChildElements.Count;
        switch (list)
        {
            case Fonts f:       f.Count = count; break;
            case Fills f:       f.Count = count; break;
            case Borders b:     b.Count = count; break;
            case CellFormats c: c.Count = count; break;
        }
        return id;
    }

    // Сравнение по разметке отсоединенной копии: у элемента внутри дерева и у вынутого объявления
    // пространств имен печатаются по-разному.
    private static string Key(OpenXmlElement item) => item.CloneNode(true).OuterXml;
}

/// <summary>
/// Перенос оформления ячеек одной книги-источника в книгу-результат: формат ячейки источника
/// пересобирается из шрифта, заливки, рамки и формата числа, добавленных в стили результата.
/// Сопоставление номеров запоминается — каждый формат источника переносится один раз.
/// </summary>
internal sealed class StyleCopier(Stylesheet? source, TargetStyles target)
{
    private readonly List<CellFormat>     _formats = source?.CellFormats?.Elements<CellFormat>().ToList() ?? [];
    private readonly List<Font>           _fonts   = source?.Fonts?.Elements<Font>().ToList() ?? [];
    private readonly List<Fill>           _fills   = source?.Fills?.Elements<Fill>().ToList() ?? [];
    private readonly List<Border>         _borders = source?.Borders?.Elements<Border>().ToList() ?? [];
    private readonly Dictionary<uint, uint> _map   = [];

    public uint Map(uint style)
    {
        if (style == 0 && _formats.Count == 0) return 0;
        if (_map.TryGetValue(style, out var mapped)) return mapped;
        if (style >= _formats.Count) return _map[style] = 0;

        var format = (CellFormat)_formats[(int)style].CloneNode(true);
        format.FontId   = Pick(format.FontId,   _fonts,   target.Font);
        format.FillId   = Pick(format.FillId,   _fills,   target.Fill);
        format.BorderId = Pick(format.BorderId, _borders, target.Border);
        format.NumberFormatId = Number(format.NumberFormatId?.Value ?? 0);
        format.FormatId = 0;                                   // стиль «Обычный» результата

        return _map[style] = target.Format(format);
    }

    private static UInt32Value Pick<T>(UInt32Value? id, List<T> items, Func<T, uint> add)
        where T : OpenXmlElement
    {
        var index = id?.Value ?? 0;
        return index < items.Count ? add((T)items[(int)index].CloneNode(true)) : 0;
    }

    private uint Number(uint id)
    {
        if (id < 164) return id;                               // встроенный — один номер везде
        var code = source?.NumberingFormats?.Elements<NumberingFormat>()
            .FirstOrDefault(f => f.NumberFormatId?.Value == id)?.FormatCode?.Value;
        return code is null ? 0 : target.NumberFormat(code);
    }
}
