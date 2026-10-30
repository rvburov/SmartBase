using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using InputKey = Avalonia.Input.Key;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using SmartBase.Controls.Components;
using SmartBase.Controls.DrawnGrid;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.Utils;
using SmartBase.Views.ConstructorDb;

/// <summary>Блок «Ссылаются на эту запись» на настоящей странице записи: место в списке, пустые
/// карточки, список источников с подгрузкой и поиском, переход, выделение и ответ во время ввода.</summary>
internal static class BacklinkChecks
{
    public static int Run(string outDir)
    {
        var path = Path.Combine(outDir, $"backlinks-{Guid.NewGuid():N}.db");
        var failed = 0;
        var checkedCount = 0;
        Window? window = null;
        DatabaseManager? manager = null;
        try
        {
            manager = DatabaseManager.Open(9431, "backlinks", path);
            var repo = manager.Constructor;
            var clients = repo.AddCollection(new DbCollection { Name = "Клиенты" }).Id!.Value;
            var note = repo.AddField(new DbField { CollectionId = clients, Name = "Заметка", FieldType = "text", Position = 1000 });
            var client = repo.AddRecord(new DbRecord { CollectionId = clients, Title = "ООО Ромашка" }).Id!.Value;
            repo.SetFieldValue(client, note.Id!.Value, "Звонил");
            var lonely = repo.AddRecord(new DbRecord { CollectionId = clients, Title = "Без ссылок" }).Id!.Value;
            (int Collection, int Field) Relation(string collection, string name)
            {
                var id = repo.AddCollection(new DbCollection { Name = collection }).Id!.Value;
                var field = repo.AddField(new DbField
                {
                    CollectionId = id, Name = name, FieldType = "relation",
                    ConfigJson = new RelationConfig(clients, false).ToConfigJson(),
                });
                return (id, field.Id!.Value);
            }
            var (bills, billField) = Relation("Счета", "Клиент");
            Relation("Сделки", "Клиент");
            var billIds = repo.BulkAddRecords(bills, [], Enumerable.Range(1, 450)
                .Select(i => new BulkRecordRow(i == 77 ? "Акт сверки 77" : $"Счет {i}", [])), 450, default);
            repo.SetLinksBulk(bills, billIds.Select(b => new ConstructorRepository.LinkWrite(b, billField, [client])).ToList());

            var page = new RecordPageView();
            var host = new Window { Width = 600, Height = 900, Content = page };
            window = host;
            host.Show();
            Pump();
            var list = page.FindControl<PropertyList>("PropertiesHost")!;
            var footer = page.FindControl<FooterPage>("FooterBar")!;
            var toggle = footer.FindControl<ToggleButton>("HideEmptyToggle")!;
            var related = new List<int>();
            var crossDb = new List<(int DbId, int RecordId)>();
            IReadOnlyList<int>? deleted = null;
            page.RelatedRecordRequested += (_, id) => related.Add(id);
            page.CrossDbRelatedRecordRequested += (_, t) => crossDb.Add(t);
            page.PropertiesDeleteRequested += (_, ids) => deleted = ids;

            page.Show(repo, client, clients);
            var own = Names();
            Wait(() => !page.BacklinksLoading);
            Expect(Names().Take(own.Length).SequenceEqual(own) && own[^1] == "Добавить свойство",
                   "ответ ссылок не сдвигает свои карточки: блок встает после «Добавить свойство»");
            Expect(Names().Skip(own.Length).SequenceEqual(new[] { "Ссылаются на эту запись", "Сделки · Клиент", "Счета · Клиент" }),
                   $"подпись раздела и карточки по свойствам-связям: {string.Join(" | ", Names().Skip(own.Length))}");
            Expect(Card("Счета · Клиент").Display?.Invoke() == "450 записей: Счет 450, Счет 449, Счет 448…",
                   $"число и первые названия, сначала новые: «{Card("Счета · Клиент").Display?.Invoke()}»");
            Expect(Card("Сделки · Клиент").Display?.Invoke() == "" && Card("Сделки · Клиент").Placeholder == "ссылок нет",
                   "связь без ссылок — «ссылок нет»");
            var section = Index("Ссылаются на эту запись");
            var sectionRect = list.CardRect(section);
            Expect(list.Surface.HitTest(sectionRect.Center).Zone == PropertyZone.None,
                   "подпись раздела не отвечает на наведение и нажатие");

            Click(toggle);
            Expect(!Names().Contains("Сделки · Клиент") && Names().Contains("Счета · Клиент") && Names().Contains("Ссылаются на эту запись"),
                   "«Скрыть пустые свойства» прячет карточку без ссылок");
            Click(toggle);
            Expect(Names().Contains("Сделки · Клиент"), "повторное нажатие возвращает пустую карточку");

            // Выделенное свойство и нажатие на карточку ссылок: Delete не удаляет ни ее, ни свойство.
            ClickCard(note.Name, value: false);
            Expect(IsSelected(note.Name), "свойство выделено перед нажатием на ссылки");
            ClickCard("Счета · Клиент", value: false);
            var box = list.CurrentEditor as RelationBacklinkBox;
            Expect(box is { IsListOpen: true } && !IsSelected(note.Name) && !IsSelected("Счета · Клиент"),
                   "нажатие на название карточки ссылок открывает список и снимает выделение");
            Wait(() => box is { IsLoading: false } && box.HintText.Contains("из 450"));
            Expect(box!.ShownSources.Count == RelationBacklinkBox.PageSize && box.ShownSources[0].Title == "Счет 450",
                   $"первая страница — 200 новых записей: {box.ShownSources.Count}");
            Expect(box.HintText == "Показаны 200 из 450", $"подсказка с точным числом: «{box.HintText}»");
            box.LoadMore();
            Wait(() => !box.IsLoading && box.ShownSources.Count == 400);
            box.LoadMore();
            Wait(() => !box.IsLoading && box.ShownSources.Count == 450);
            Expect(box.ShownSources.Select(s => s.RecordId).Distinct().Count() == 450 && box.HintText == "",
                   "подгрузка доводит до конца без повторов, подсказка уходит");
            // Чужая правка перечитывает панель — открытый список не закрывается, перечитывание ждет.
            var stamp = page.DataStamp;
            page.ReloadFromDb();
            Pump();
            Expect(box.IsListOpen && box.ShownSources.Count == 450 && page.DataStamp == stamp,
                   "перечитывание панели ждет, пока открыт список источников");
            box.Search("АКТ СВЕРКИ");
            Wait(() => !box.IsLoading && box.ShownSources.Count == 1);
            Expect(box.ShownSources.SingleOrDefault().Title == "Акт сверки 77", "поиск по названию без учета регистра");
            Press(InputKey.Enter);
            Expect(related.SequenceEqual(new[] { billIds[76] }) && !box.IsListOpen, "Enter переходит к записи-источнику и закрывает список");
            Wait(() => page.DataStamp > stamp);
            Expect(page.DataStamp > stamp, "закрытый список отпускает отложенное перечитывание");
            // Перечитанная панель построила карточки заново: список открывается на новой.
            ClickCard("Счета · Клиент", value: false);
            box = list.CurrentEditor as RelationBacklinkBox;
            Wait(() => box is { IsLoading: false, ShownSources.Count: > 0 });
            var popup = box.GetLogicalDescendants().OfType<Popup>().Single();
            // Строка первой записи списка: контейнеры строк идут не по порядку, часть — за краем.
            var first = box!.ShownSources[0].RecordId;
            var row = popup.Child!.GetVisualDescendants().OfType<Grid>().First(g => g.IsEffectivelyVisible
                && g.DataContext?.GetType().GetProperty("Id")?.GetValue(g.DataContext) is int id && id == first);
            ClickPopup(popup, row);
            Expect(related.Count == 2 && related[1] == first && !box.IsListOpen,
                   "клик по строке списка переходит к записи-источнику");
            Press(InputKey.Delete);
            Expect(deleted is null, "Delete после карточки ссылок не удаляет свойств");

            RightClickCard("Счета · Клиент");
            Expect(list.Surface.ContextMenu is null, "правый клик по карточке ссылок меню не дает");
            RightClickCard(note.Name);
            Expect(list.Surface.ContextMenu is not null, "правый клик по свойству дает его меню");
            list.Surface.ContextMenu?.Close();
            Pump();
            // Поверхность ловит нажатие только там, где рисует: подпись раздела — серый текст у
            // нижнего края места карточки.
            var sectionIndex = Index("Ссылаются на эту запись");
            list.ScrollIntoView(sectionIndex);
            Pump();
            var label = list.CardRect(sectionIndex);
            ClickAt(list.Surface.TranslatePoint(new Point(label.X + 24, label.Y + 29), host)!.Value, MouseButton.Right);
            Expect(list.Surface.ContextMenu is null, "правый клик по подписи раздела не открывает меню прежнего свойства");

            // Набор снизу при скрытых пустых: фильтр пересчитывается на каждую букву, а карточки — те же.
            Click(toggle);
            ClickCard(note.Name, value: false);
            var bottom = page.FindControl<TextBox>("MarkdownEditor")!;
            Click(bottom);
            bottom.CaretIndex = bottom.Text?.Length ?? 0;
            var cardsBefore = list.Cards;
            Type(" вчера");
            Expect(ReferenceEquals(list.Cards, cardsBefore) && NoteValue() == "Звонил вчера",
                   "набор текста при показанных ссылках не пересоздает карточки");
            Click(toggle);

            // Ответ во время ввода в карточке ждет, пока поле освободится.
            ClickCard(note.Name, value: true);
            Press(InputKey.End);
            Type(", ждет");
            var extra = repo.AddRecord(new DbRecord { CollectionId = bills, Title = "Счет новый" }).Id!.Value;
            repo.SetLinks(billField, extra, [client]);
            page.RefreshBacklinks();
            Wait(() => !page.BacklinksLoading);
            Pump();
            Expect(list.CurrentEditor is TextBox { IsKeyboardFocusWithin: true } tb && tb.Text == "Звонил вчера, ждет"
                   && Card("Счета · Клиент").Display?.Invoke()?.StartsWith("450 ", StringComparison.Ordinal) == true,
                   "ответ во время ввода не трогает поле и фокус и ждет");
            list.Surface.Focus();
            Wait(() => Card("Счета · Клиент").Display?.Invoke()?.StartsWith("451 запись: Счет новый", StringComparison.Ordinal) == true);
            Expect(Card("Счета · Клиент").Display?.Invoke()?.StartsWith("451 запись: Счет новый", StringComparison.Ordinal) == true
                   && NoteValue() == "Звонил вчера, ждет",
                   "после ухода фокуса ответ встает на место, набранное сохранено");

            // Панель, открытая межбазовым переходом, ведет к источнику в базе записи.
            page.Show(repo, client, clients, dbId: 1_000_777);
            Wait(() => !page.BacklinksLoading);
            ClickCard("Счета · Клиент", value: true);
            box = list.CurrentEditor as RelationBacklinkBox;
            Wait(() => box is { IsLoading: false, ShownSources.Count: > 0 });
            Press(InputKey.Enter);
            Expect(crossDb.LastOrDefault() == (1_000_777, extra), "переход из межбазовой панели — в базу записи");

            // Другая запись без ссылок: раздел с пустыми карточками, при скрытии пустых — ничего.
            page.Show(repo, lonely, clients);
            Wait(() => !page.BacklinksLoading);
            Expect(Names().Contains("Сделки · Клиент") && Card("Счета · Клиент").Display?.Invoke() == "",
                   "у записи без ссылок все карточки пустые");
            Click(toggle);
            Expect(Names()[^1] == "Добавить свойство", "без ссылок и со скрытием пустых раздела нет");
            Click(toggle);

            Console.WriteLine($"Ссылки на запись: проверок {checkedCount}, провалов {failed}.");
            return failed == 0 ? 0 : 2;

            void Expect(bool ok, string message)
            {
                checkedCount++;
                Console.WriteLine($"  {(ok ? "ок    " : "ПРОВАЛ")} {message}");
                if (!ok) failed++;
            }

            string[] Names() => list.Cards.Select(c => c.Name).ToArray();
            int Index(string name) => list.Cards.ToList().FindIndex(c => c.Name == name);
            PropertyCard Card(string name) => list.Cards.First(c => c.Name == name);
            bool IsSelected(string name) => list.Surface.IsSelected(Index(name));
            string NoteValue() => repo.GetFieldValues(client).GetValueOrDefault(note.Id!.Value, "");

            void Pump()
            {
                Dispatcher.UIThread.RunJobs();
                host.UpdateLayout();
                using var frame = host.CaptureRenderedFrame();
                Dispatcher.UIThread.RunJobs();
            }

            void Wait(Func<bool> ready)
            {
                var watch = Stopwatch.StartNew();
                do { Pump(); Thread.Sleep(5); } while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(5));
            }

            void Click(Control control)
            {
                Pump();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), host)
                    ?? throw new InvalidOperationException("Контрол не прикреплен к окну стенда");
                ClickAt(point);
            }

            void ClickAt(Point point, MouseButton button = MouseButton.Left)
            {
                host.MouseMove(point);
                Pump();
                host.MouseDown(point, button);
                host.MouseUp(point, button);
                Pump();
            }

            void ClickCard(string name, bool value) => ClickAt(CardPoint(name, value));

            // Клик внутри выпадающего списка — в его собственном окне.
            void ClickPopup(Popup popup, Control control)
            {
                Pump();
                var top = TopLevel.GetTopLevel(control) ?? throw new InvalidOperationException("Список не прикреплен к окну");
                top.UpdateLayout();
                using (top.CaptureRenderedFrame()) { }
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)
                    ?? throw new InvalidOperationException("Нет позиции строки списка");
                top.MouseMove(point);
                top.MouseDown(point, MouseButton.Left);
                if (popup.IsOpen) top.MouseUp(point, MouseButton.Left);
                else host.MouseUp(new Point(2, 2), MouseButton.Left);
                Pump();
            }

            void RightClickCard(string name) => ClickAt(CardPoint(name, value: false), MouseButton.Right);

            Point CardPoint(string name, bool value)
            {
                var index = Index(name);
                if (index < 0) throw new InvalidOperationException($"Нет карточки «{name}»");
                list.ScrollIntoView(index);
                Pump();
                var bounds = list.CardRect(index);
                var x = value ? bounds.X + 10 + list.NameColumnWidth + 18 + 24 : bounds.X + 30;
                return list.Surface.TranslatePoint(new Point(x, bounds.Y + bounds.Height / 2), host)
                    ?? throw new InvalidOperationException("Список не прикреплен к окну стенда");
            }

            void Press(InputKey key, RawInputModifiers modifiers = RawInputModifiers.None)
            {
                host.KeyPress(key, modifiers);
                host.KeyRelease(key, modifiers);
                Pump();
            }

            void Type(string textValue)
            {
                host.KeyTextInput(textValue);
                Pump();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ПРОВАЛ проверки ссылок на запись: {ex}");
            return 2;
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            manager?.Dispose();
            AppDbContext.ResetPool(path);
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }
}
