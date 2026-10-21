using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using SmartBase.Core.Models;

namespace SmartBase.Controls.DrawnGrid;

/// <summary>Имена файлов одной строкой: разделитель и многоточие не являются ссылками.
/// Рисование и попадание используют одну раскладку, без контролов в ячейках.</summary>
internal sealed class FileLinksLayout
{
    private const int PreviewPaths = 8;
    private const int PreviewCharacters = 512;
    private sealed record Part(int Start, int Length, string? Path, bool Clickable,
                               IBrush Brush, string BrushId, bool Underline, bool Ellipsis = false);
    private sealed record Run(TextLayout Layout, Rect[] Bounds, string? Path);

    private readonly Point _origin;
    private readonly List<Run> _runs;

    private FileLinksLayout(Point origin, List<Run> runs) => (_origin, _runs) = (origin, runs);

    public static FileLinksLayout Create(SurfaceStyle style, string storage, string autoFolder,
                                         Rect area, TextAlignment align, CellVAlign valign,
                                         double pad, double scale, bool label = false,
                                         IBrush? foreground = null, string foregroundId = "")
    {
        if (area.Width <= pad * 2 || area.Height <= 0) return new(default, []);
        var width = Math.Max(1, area.Width - pad * 2);
        var lineHeight = label ? double.NaN : style.LineHeight;
        var dots = style.Text.Get("…", width, TextAlignment.Left, style.Secondary, "s", 1,
                                  lineHeight, trimming: TextTrimming.None);
        var paths = FileValue.ReadPaths(storage);
        var parts = new List<Part>();
        var text = new StringBuilder();
        var more = false;

        // Недоступная папка — один статус для всего свойства, а не статус у каждого файла.
        if (width <= dots.WidthIncludingTrailingWhitespace)
            more = true;
        else if (paths.Count == 0 || autoFolder.Length > 0 && FileProbe.FolderExists(autoFolder) == false)
        {
            var look = style.FileLook("", autoFolder, foreground, foregroundId);
            parts.Add(new(0, look.Text.Length, null, false, look.Brush, look.Id, false));
            text.Append(look.Text);
        }
        else
        {
            for (var i = 0; i < paths.Count; i++)
            {
                if (i >= PreviewPaths || text.Length >= PreviewCharacters) { more = true; break; }
                var path = paths[i];
                var name = PreviewName(path, Math.Max(1, PreviewCharacters - text.Length - 2), out var cutName);
                if (name.Length == 0) { more = true; break; }
                // До очереди диска убеждаемся, что хотя бы начало этой ссылки попадет в кадр.
                // Скрытые за предыдущими именами пути не измеряются целиком и не проверяются.
                var first = char.IsHighSurrogate(name[0]) && name.Length > 1 ? name[..2] : name[..1];
                var start = text.ToString() + (text.Length > 0 ? ", " : "") + first;
                var startMeasure = style.Text.Get(start, width, TextAlignment.Left, style.Primary, "p", 1,
                                                  lineHeight, trimming: TextTrimming.None);
                if (startMeasure.WidthIncludingTrailingWhitespace > width - dots.WidthIncludingTrailingWhitespace)
                {
                    var lastFits = i == paths.Count - 1 && !cutName
                        && style.Text.Get(text.ToString() + (text.Length > 0 ? ", " : "") + name,
                            width, TextAlignment.Left, style.Primary, "p", 1,
                            lineHeight, trimming: TextTrimming.None).WidthIncludingTrailingWhitespace <= width;
                    if (!lastFits) { more = true; break; }
                }
                if (text.Length > 0)
                {
                    parts.Add(new(text.Length, 2, null, false, foreground ?? style.Primary,
                                  foreground is null ? "p" : foregroundId, false));
                    text.Append(", ");
                }
                var exists = FileProbe.FileExists(path) != false;
                var caption = name + (exists ? "" : " — файл не найден");
                var brush = exists ? foreground ?? style.Accent : style.Secondary;
                var brushId = exists ? foreground is null ? "a" : foregroundId : "s";
                parts.Add(new(text.Length, caption.Length, path, autoFolder.Length == 0 || exists,
                              brush, brushId, exists));
                text.Append(caption);
                var shown = style.Text.Get(text.ToString(), width, TextAlignment.Left, style.Primary, "p", 1,
                                           lineHeight, trimming: TextTrimming.None);
                if (cutName || shown.WidthIncludingTrailingWhitespace > width)
                {
                    more = cutName || i + 1 < paths.Count;
                    break;
                }
            }
        }

        var full = text.ToString();
        var measure = style.Text.Get(full, width, TextAlignment.Left, style.Primary, "p", 1,
                                     lineHeight, trimming: TextTrimming.None);
        var prefix = full.Length;
        var ellipsis = more || measure.WidthIncludingTrailingWhitespace > width;
        if (ellipsis)
        {
            var available = width - dots.WidthIncludingTrailingWhitespace;
            prefix = 0;
            if (available > 0 && measure.TextLines.Count > 0)
            {
                var line = measure.TextLines[0];
                var hit = line.GetCharacterHitFromDistance(available);
                prefix = Math.Clamp(hit.FirstCharacterIndex + hit.TrailingLength, 0, full.Length);
                while (prefix > 0 && line.GetDistanceFromCharacterHit(new CharacterHit(prefix, 0)) > available)
                {
                    var previous = line.GetPreviousCaretCharacterHit(new CharacterHit(prefix, 0));
                    var next = previous.FirstCharacterIndex + previous.TrailingLength;
                    if (next >= prefix) { prefix = 0; break; }
                    prefix = next;
                }
            }
        }

        // Явное многоточие имеет собственный диапазон: попадание по нему никогда не попадает
        // в невидимый хвост последней ссылки (автоматическое схлопывание текста этого не обещает).
        var visible = ellipsis ? full[..prefix] + "…" : full;
        if (ellipsis)
            parts.Add(new(prefix, 1, null, false, style.Secondary, "s", false, Ellipsis: true));
        var layout = style.Text.Get(visible, width, TextAlignment.Left, style.Primary, "p", 1,
                                    lineHeight, trimming: TextTrimming.None);
        var textWidth = Math.Min(Math.Ceiling(layout.WidthIncludingTrailingWhitespace - 1e-6), width);
        var x = align switch
        {
            TextAlignment.Center => area.X + SurfaceStyle.Snap((area.Width - textWidth) / 2, scale),
            TextAlignment.Right => area.X + pad + width - layout.WidthIncludingTrailingWhitespace,
            _ => area.X + pad,
        };
        var y = label
            ? area.Y + SurfaceStyle.Snap((area.Height - Math.Ceiling(layout.Height - 1e-6)) / 2, scale)
            : SurfaceStyle.Snap(area.Y + CellAlign.TextTop(valign, area.Height, layout.Height, style.TextEdge), scale);
        var origin = new Point(x, y);
        var clip = new Rect(area.X + pad, area.Y, width, area.Height);
        var runs = new List<Run>();
        foreach (var part in parts)
        {
            // У добавленного многоточия Start == prefix; остальные части заканчиваются перед ним.
            var length = part.Ellipsis ? part.Length : Math.Min(part.Length, prefix - part.Start);
            if (length <= 0) continue;
            var bounds = new List<Rect>();
            foreach (var rect in layout.HitTestTextRange(part.Start, length))
            {
                var absolute = rect.Translate(new Vector(origin.X, origin.Y));
                var left = Math.Max(clip.Left, absolute.Left);
                var top = Math.Max(clip.Top, absolute.Top);
                var right = Math.Min(clip.Right, absolute.Right);
                var bottom = Math.Min(clip.Bottom, absolute.Bottom);
                if (right > left && bottom > top) bounds.Add(new(left, top, right - left, bottom - top));
            }
            if (bounds.Count == 0) continue;
            var colored = style.Text.Get(visible, width, TextAlignment.Left, part.Brush, part.BrushId, 1,
                                          lineHeight, part.Underline ? SurfaceStyle.Underline : null, TextTrimming.None);
            runs.Add(new(colored, [.. bounds], part.Clickable ? part.Path : null));
        }
        return new(origin, runs);
    }

    public void Draw(DrawingContext context)
    {
        foreach (var run in _runs)
        foreach (var rect in run.Bounds)
            using (context.PushClip(rect)) run.Layout.Draw(context, _origin);
    }

    public string? PathAt(Point point)
    {
        foreach (var run in _runs)
            if (run.Path is not null && run.Bounds.Any(rect => rect.Contains(point))) return run.Path;
        return null;
    }

    private static string PreviewName(string path, int limit, out bool truncated)
    {
        var start = path.LastIndexOfAny(['/', '\\']) + 1;
        if (start == path.Length) start = 0;
        var name = path.AsSpan(start);
        truncated = name.Length > limit;
        if (!truncated) return name.ToString();
        if (char.IsHighSurrogate(name[limit - 1])) limit--;
        return name[..limit].ToString();
    }
}
