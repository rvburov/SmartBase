using OpenCvSharp;
using PdfSharp.Drawing;

namespace SmartBase.Tests.Extensions.PdfRename.Samples;

/// <summary>Как размечен шаблон.</summary>
public enum Marking
{
    /// <summary>Синяя и зеленая рамки нарисованы по картинке скана.</summary>
    Separate,
    /// <summary>Одна красно-оранжевая рамка по картинке.</summary>
    Combined,
    /// <summary>Синяя и зеленая рамки — векторные прямоугольники поверх скана (как в редакторе PDF).</summary>
    SeparateVector,
}

/// <summary>Цвета рамок по умолчанию (BGR) и их «плохие» оттенки для тестов.</summary>
public static class FrameColors
{
    public static readonly Scalar Blue = new(255, 0, 0);
    public static readonly Scalar Green = new(0, 170, 0);
    public static readonly Scalar RedOrange = new(0, 64, 255);
}

/// <summary>
/// Временная папка с шаблонами и исходными PDF для тестов. Удаляется вместе со всем
/// содержимым по окончании теста.
/// </summary>
public sealed class Workbench : IDisposable
{
    public const int TemplateDpi = 200;

    public string Root { get; }
    public string Templates { get; }
    public string Source { get; }

    public Workbench()
    {
        Root = Path.Combine(Path.GetTempPath(), "sb-pdfrename-" + Guid.NewGuid().ToString("N")[..10]);
        Templates = Path.Combine(Root, "Шаблоны");
        Source = Path.Combine(Root, "Сканы");
        Directory.CreateDirectory(Templates);
        Directory.CreateDirectory(Source);
    }

    /// <summary>Шаблон: лист с образцом и цветными рамками на графах.</summary>
    public string AddTemplate(string typeName, FormKind kind, Marking marking = Marking.Separate,
        Scalar? blue = null, Scalar? green = null, Scalar? red = null, string? folder = null,
        Action<Mat, FormFields>? customize = null)
    {
        var fields = SyntheticForms.Fields(kind);
        using var page = SyntheticForms.Render(kind, "ОБРЗ.000000.000", "Образец\nдокумента", TemplateDpi, seed: 7);
        var path = Path.Combine(folder ?? Templates, typeName + ".pdf");

        if (marking == Marking.SeparateVector)
        {
            SyntheticForms.SavePdf(path, [page], TemplateDpi, 90, (gfx, _, _) =>
            {
                void Box(RectMm r, XColor color)
                {
                    var pen = new XPen(color, 1.5);
                    gfx.DrawRectangle(pen, XUnit.FromMillimeter(r.X).Point, XUnit.FromMillimeter(r.Y).Point,
                        XUnit.FromMillimeter(r.Width).Point, XUnit.FromMillimeter(r.Height).Point);
                }
                Box(fields.Designation.Inflate(-1), XColor.FromArgb(30, 60, 230));
                Box(fields.Name.Inflate(-1), XColor.FromArgb(20, 160, 40));
            });
            return path;
        }

        if (marking == Marking.Combined)
            SyntheticForms.DrawFrame(page, fields.Both.Inflate(-1), red ?? FrameColors.RedOrange, TemplateDpi);
        else
        {
            SyntheticForms.DrawFrame(page, fields.Designation.Inflate(-1), blue ?? FrameColors.Blue, TemplateDpi);
            SyntheticForms.DrawFrame(page, fields.Name.Inflate(-1), green ?? FrameColors.Green, TemplateDpi);
        }
        customize?.Invoke(page, fields);
        SyntheticForms.SavePdf(path, [page], TemplateDpi, 92);
        return path;
    }

    /// <summary>Входной «скан» без рамок.</summary>
    public string AddDocument(string relativePath, FormKind kind, string designation, string name,
        ScanOptions? options = null, int seed = 1)
    {
        var o = options ?? new ScanOptions();
        var path = Path.Combine(Source, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var page = SyntheticForms.Render(kind, designation, name, o.Dpi, seed);
        using var scan = SyntheticForms.Scan(page, o, SyntheticForms.PageSizeMm(kind).W);
        SyntheticForms.SavePdf(path, [scan], o.Dpi, o.JpegQuality);
        return path;
    }

    /// <summary>Многостраничный «скан»: страницы задаются готовыми картинками.</summary>
    public string AddPages(string relativePath, IReadOnlyList<Mat> pages, int dpi)
    {
        var path = Path.Combine(Source, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SyntheticForms.SavePdf(path, pages, dpi);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
