using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using SmartBase.Controls.DrawnGrid;
using SmartBase.Core.Models;
using SmartBase.Utils;
using SmartBase.ViewModels.ConstructorDb;

/// <summary>Настоящие нажатия по видимым ссылкам файлов; свое малое содержимое, без базы wide.</summary>
internal static class FileLinksChecks
{
    public static int Run(string outDir)
    {
        var folder = Path.Combine(outDir, "file-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var first = Path.Combine(folder, "Акт, часть.pdf");
        var second = Path.Combine(folder, "Смета.pdf");
        var longName = Path.Combine(folder, "MMMMMMMMMMMM.pdf");
        foreach (var path in new[] { first, second, longName }) File.WriteAllText(path, "файл стенда");
        var syncBefore = FileProbe.Synchronous;
        FileProbe.Synchronous = true;
        var checks = 0;
        var failed = 0;
        Window? window = null;
        try
        {
            foreach (var scale in new[] { 1.0, 2.0 })
            {
                var raw = FileValue.Serialize([first, second]);
                var rowStyle = RowStyle.None;
                var surface = new GridSurface();
                var row = TableRowVm.Static(1, _ => raw);
                TableColumnDef def = null!;
                var width = 760.0;
                void Configure(string autoFolder, double nextWidth = 760)
                {
                    width = nextWidth;
                    def = new TableColumnDef
                    {
                        Key = "files", Header = "Файлы", FieldType = "file", FileAutoFolder = autoFolder,
                        IsStarWidth = false, FixedWidth = width,
                    };
                    var columns = ColumnLayout.Build([def], new Dictionary<string, double>(),
                                                      new HashSet<string>(), 880, 160, 1);
                    surface.Attach([row], columns, _ => rowStyle, null, null);
                }
                Configure(folder);
                var host = new Window { Width = 880, Height = 160, Content = surface };
                window = host;
                if (scale != 1)
                {
                    var impl = host.PlatformImpl!;
                    var field = impl.GetType().GetField("<RenderScaling>k__BackingField",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        ?? throw new InvalidOperationException("HeadlessWindowImpl сменил поле масштаба");
                    field.SetValue(impl, scale);
                }
                host.Show();
                Pump();
                var tokens = SurfaceStyle.Load(surface, ConstructorSettings.RowHeightResourceKey);
                string? pressedPath = null;
                surface.Pressed += (_, e) => pressedPath = surface.FilePathAt(e.Hit, e.Pointer.GetPosition(surface));
                var names = "Акт, часть.pdf, Смета.pdf";

                foreach (var align in new[] { CellHAlign.Left, CellHAlign.Center, CellHAlign.Right })
                {
                    rowStyle = new RowStyle(null, null, align);
                    surface.InvalidateVisual();
                    Pump();
                    var cell = surface.CellRect(0, 0);
                    var layout = tokens.Text.Get(names, width - 13, TextAlignment.Left, tokens.Primary, "p", 1,
                                                  tokens.LineHeight, trimming: TextTrimming.None);
                    var start = align switch
                    {
                        CellHAlign.Center => Math.Round((width - Math.Ceiling(layout.WidthIncludingTrailingWhitespace - 1e-6)) * scale / 2) / scale,
                        CellHAlign.Right => width - 7 - layout.WidthIncludingTrailingWhitespace,
                        _ => 6,
                    };
                    Point At(int offset, int length = 1)
                    {
                        var rect = layout.HitTestTextRange(offset, length).First();
                        return new Point(cell.X + start + rect.Center.X, cell.Center.Y);
                    }
                    Click(At(1));
                    Expect(pressedPath == first, $"x{scale} {align}: первое имя открывает первый путь");
                    Click(At(names.IndexOf(',')));
                    Expect(pressedPath == first, $"x{scale} {align}: запятая внутри имени остается частью первой ссылки");
                    Click(At("Акт, часть.pdf, ".Length + 1));
                    Expect(pressedPath == second, $"x{scale} {align}: второе имя открывает второй путь");
                    Click(At("Акт, часть.pdf".Length));
                    Expect(pressedPath is null, $"x{scale} {align}: запятая между именами не является ссылкой");
                    Click(new Point(cell.X + 2, cell.Center.Y));
                    Expect(pressedPath is null, $"x{scale} {align}: пустое место не открывает файл");
                }

                rowStyle = RowStyle.None;
                raw = FileValue.Serialize([longName, second]);
                var prefix = tokens.Text.Get("MMMMM", 500, TextAlignment.Left, tokens.Primary, "p", 1,
                                              tokens.LineHeight, trimming: TextTrimming.None).WidthIncludingTrailingWhitespace;
                var dots = tokens.Text.Get("…", 500, TextAlignment.Left, tokens.Primary, "p", 1,
                                            tokens.LineHeight, trimming: TextTrimming.None).WidthIncludingTrailingWhitespace;
                Configure(folder, Math.Ceiling(prefix + dots) + 13);
                Pump();
                var narrow = surface.CellRect(0, 0);
                Click(new Point(narrow.X + 6 + prefix + dots / 2, narrow.Center.Y));
                Expect(pressedPath is null, $"x{scale}: явное многоточие не открывает скрытый файл");
                var hidden = Enumerable.Range(1, (int)width - 2).Select(x =>
                {
                    var point = new Point(narrow.X + x, narrow.Center.Y);
                    return surface.FilePathAt(surface.HitTest(point), point);
                });
                Expect(hidden.All(path => path != second), $"x{scale}: второе невидимое имя не имеет области ссылки");

                raw = first;
                Configure("");
                Pump();
                var manual = surface.CellRect(0, 0);
                Click(new Point(manual.X + 15, manual.Center.Y));
                Expect(pressedPath == first && surface.FileButtonRect(manual, def) is not null,
                       $"x{scale}: одиночный путь и скрепка ручного режима сохранены");
                Configure(Path.Combine(folder, "папки-нет"));
                Pump();
                var unavailable = surface.CellRect(0, 0);
                Click(new Point(unavailable.X + 25, unavailable.Center.Y));
                Expect(pressedPath == "" && surface.FileButtonRect(unavailable, def) is null,
                       $"x{scale}: недоступная авто-папка дает плашку, без скрепки");
                host.Close();
                window = null;

                void Pump()
                {
                    Dispatcher.UIThread.RunJobs();
                    host.UpdateLayout();
                    using var frame = host.CaptureRenderedFrame();
                    Dispatcher.UIThread.RunJobs();
                }
                void Click(Point point)
                {
                    pressedPath = "не было нажатия";
                    host.MouseMove(point); Pump();
                    host.MouseDown(point, MouseButton.Left); Pump();
                    host.MouseUp(point, MouseButton.Left); Pump();
                }
            }
            Console.WriteLine($"Ссылки файлов: проверок {checks}, провалов {failed}.");
            return failed == 0 ? 0 : 3;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ПРОВАЛ стенда ссылок файлов: {ex}");
            return 3;
        }
        finally
        {
            window?.Close();
            FileProbe.Synchronous = syncBefore;
            try { Directory.Delete(folder, recursive: true); } catch { }
        }

        void Expect(bool ok, string message)
        {
            checks++;
            if (!ok) failed++;
            Console.WriteLine($"  {(ok ? "ок" : "ПРОВАЛ")} {message}");
        }
    }
}
