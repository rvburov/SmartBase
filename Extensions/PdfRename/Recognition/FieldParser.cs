using SmartBase.Extensions.PdfRename.Ocr;

namespace SmartBase.Extensions.PdfRename.Recognition;

/// <summary>Значение поля, прочитанное одним движком, или причина, почему его нет.</summary>
internal readonly record struct Parsed(string? Value, string? Problem)
{
    public static Parsed Ok(string value) => new(value, null);
    public static Parsed Fail(string problem) => new(null, problem);
    public bool IsOk => Value is not null;
}

/// <summary>Что один движок прочитал в полях документа.</summary>
internal sealed record EngineFields(string Engine, Parsed Designation, Parsed Name);

/// <summary>
/// Разбор текста областей в обозначение и наименование — отдельно по каждому движку.
/// Ничего не додумывается: нет обозначения по правилам — нет обозначения.
/// </summary>
internal static class FieldParser
{
    public const int MaxNameLength = 180;
    public const int MaxNameLines = 5;

    /// <summary>Синяя область: ровно одно обозначение и ничего, кроме него.</summary>
    public static Parsed ParseDesignation(OcrReading reading, double minConfidence)
    {
        if (reading.IsEmpty) return Parsed.Fail("область обозначения пустая или текст не читается");
        if (reading.MinConfidence < minConfidence) return Parsed.Fail("низкая уверенность распознавания обозначения");

        var prepared = Designations.Prepare(reading.Text);
        var found = Designations.FindAll(prepared);
        if (found.Count == 0) return Parsed.Fail("обозначение не прошло проверку формата");
        if (found.Select(f => f.Value).Distinct().Count() > 1 || found.Count > 1)
            return Parsed.Fail("в области обозначения несколько обозначений");

        var match = found[0];
        var rest = prepared.Remove(match.Index, match.Length);
        if (TextRules.HasAlnum(rest)) return Parsed.Fail("в области обозначения кроме обозначения есть другой текст");
        return Parsed.Ok(match.Value);
    }

    /// <summary>Зеленая область: весь текст — наименование.</summary>
    public static Parsed ParseName(OcrReading reading, double minConfidence)
    {
        if (reading.IsEmpty) return Parsed.Fail("наименование отсутствует (область пустая или текст не читается)");
        if (reading.MinConfidence < minConfidence) return Parsed.Fail("низкая уверенность распознавания наименования");
        return CheckName(reading.Lines.Select(l => l.Text).ToList());
    }

    /// <summary>
    /// Красно-оранжевая область: ровно одно обозначение; наименование — текст по одну сторону
    /// от него (строки выше или ниже, либо остаток той же строки). Текст по обе стороны —
    /// разделение неоднозначно.
    /// </summary>
    public static (Parsed Designation, Parsed Name) ParseCombined(OcrReading reading, double minConfidence)
    {
        if (reading.IsEmpty)
        {
            var empty = Parsed.Fail("общая область пустая или текст не читается");
            return (empty, empty);
        }
        if (reading.MinConfidence < minConfidence)
        {
            var low = Parsed.Fail("низкая уверенность распознавания");
            return (low, low);
        }

        var lines = reading.Lines.Select(l => TextRules.NormalizeName(l.Text)).Where(l => l.Length > 0).ToList();
        var hits = new List<(int Line, DesignationMatch Match, int Start, int End)>();
        for (var i = 0; i < lines.Count; i++)
        {
            var squeezed = Designations.Squeeze(lines[i], out var map);
            foreach (var m in Designations.FindAll(squeezed))
                hits.Add((i, m, map[m.Index], map[m.Index + m.Length - 1] + 1));
        }

        if (hits.Count == 0)
        {
            var none = Parsed.Fail("в общей области не найдено обозначение по правилам формата");
            return (none, Parsed.Fail("наименование не отделено: нет обозначения"));
        }
        if (hits.Count > 1)
        {
            var many = Parsed.Fail("в общей области несколько обозначений");
            return (many, Parsed.Fail("наименование не отделено: несколько обозначений"));
        }

        var (lineIndex, match, start, end) = hits[0];
        var line = lines[lineIndex];
        var before = line[..start].Trim();
        var after = line[end..].Trim();

        var above = lines.Take(lineIndex).Append(before).Where(TextRules.HasAlnum).ToList();
        var below = new[] { after }.Concat(lines.Skip(lineIndex + 1)).Where(TextRules.HasAlnum).ToList();

        var designation = Parsed.Ok(match.Value);
        if (above.Count > 0 && below.Count > 0)
            return (designation, Parsed.Fail("общую область нельзя однозначно разделить: текст и до, и после обозначения"));
        var nameLines = above.Count > 0 ? above : below;
        if (nameLines.Count == 0) return (designation, Parsed.Fail("наименование отсутствует (в общей области только обозначение)"));
        return (designation, CheckName(nameLines));
    }

    /// <summary>Проверка наименования: буквы есть, строк немного, нет обозначения внутри,
    /// длина разумная.</summary>
    public static Parsed CheckName(IReadOnlyList<string> lines)
    {
        var meaningful = lines.Where(TextRules.HasAlnum).ToList();
        if (meaningful.Count == 0) return Parsed.Fail("наименование отсутствует");
        if (meaningful.Count > MaxNameLines)
            return Parsed.Fail($"в области наименования {meaningful.Count} строк — похоже на таблицу, а не на наименование");

        var name = TextRules.ResolveMixedWords(TextRules.JoinLines(meaningful));
        if (name.Count(char.IsLetter) < 2) return Parsed.Fail("наименование не содержит слов");
        if (name.Length > MaxNameLength) return Parsed.Fail($"наименование длиннее {MaxNameLength} символов — похоже на случайный текст");
        if (Designations.ContainsDesignation(name)) return Parsed.Fail("в области наименования найдено обозначение");
        return Parsed.Ok(name);
    }
}
