using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using InputKey = Avalonia.Input.Key;
using Avalonia.Threading;
using SmartBase.Controls.Components;
using SmartBase.Controls.DrawnGrid;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Views.ConstructorDb;

/// <summary>Переключение пустых свойств на настоящей странице записи: ввод, фокус, индексы и БД.</summary>
internal static class EmptyPropertiesChecks
{
    public static int Run(string outDir)
    {
        var path = Path.Combine(outDir, $"empty-properties-{Guid.NewGuid():N}.db");
        var failed = 0;
        var checkedCount = 0;
        Window? window = null;
        DatabaseManager? manager = null;
        try
        {
            manager = DatabaseManager.Open(9408, "empty-properties", path);
            var repo = manager.Constructor;
            var collectionId = repo.AddCollection(new DbCollection { Name = "Пустые свойства" }).Id!.Value;
            var position = 0;
            DbField Field(string name, string type = "text", string config = "{}", bool description = false) =>
                repo.AddField(new DbField
                {
                    CollectionId = collectionId, Name = name, FieldType = type,
                    Position = position += 1000, ConfigJson = config, IsDescription = description,
                });

            var empty = Field("Пустое");
            var whitespace = Field("Пробелы");
            var description = Field("Описание", description: true);
            var zero = Field("Число ноль", "number");
            var check = Field("Галочка", "checkbox");
            var emptyChoice = Field("Пустой выбор", "select", "{\"options\":[\"Опция\"]}");
            var emptyDate = Field("Пустая дата", "date");
            var emptyMulti = Field("Пустой список", "multiselect");
            var multi = Field("Список", "multiselect");
            var relationConfig = new RelationConfig(collectionId, Single: false).ToConfigJson();
            var emptyRelation = Field("Пустая связь", "relation", relationConfig);
            var relation = Field("Связь", "relation", relationConfig);
            var text = Field("Текст");
            var recordId = repo.AddRecord(new DbRecord { CollectionId = collectionId, Title = "Запись" }).Id!.Value;
            var secondId = repo.AddRecord(new DbRecord { CollectionId = collectionId, Title = "" }).Id!.Value;
            var targetId = repo.AddRecord(new DbRecord { CollectionId = collectionId, Title = "Цель связи" }).Id!.Value;
            repo.SetFieldValue(recordId, whitespace.Id!.Value, " \t \n");
            repo.SetFieldValue(recordId, zero.Id!.Value, "0");
            repo.SetFieldValue(recordId, check.Id!.Value, "0");
            repo.SetFieldValue(recordId, emptyMulti.Id!.Value, "[]");
            repo.SetFieldValue(recordId, multi.Id!.Value, "[\"Опция\"]");
            repo.SetFieldValue(recordId, text.Id!.Value, "Заполнено");
            repo.SetLinks(relation.Id!.Value, recordId, [targetId]);
            repo.SetFieldValue(secondId, zero.Id!.Value, "0");
            repo.SetFieldValue(secondId, text.Id!.Value, "Вторая запись");

            var page = new RecordPageView();
            var host = new Window { Width = 600, Height = 900, Content = page };
            window = host;
            host.Show();
            Pump();
            page.Show(repo, recordId, collectionId);
            Pump();
            var footer = page.FindControl<FooterPage>("FooterBar")!;
            var toggle = footer.FindControl<ToggleButton>("HideEmptyToggle")!;
            var list = page.FindControl<PropertyList>("PropertiesHost")!;
            var editor = page.FindControl<TextBox>("MarkdownEditor")!;
            var crumb = page.FindControl<StackPanel>("BottomTitleHost")!;
            var changed = 0;
            IReadOnlyList<int>? deleted = null;
            page.RecordChanged += (_, _) => changed++;
            page.PropertiesDeleteRequested += (_, ids) => deleted = ids;
            var originalOrder = Names();
            var snapshot = Snapshot();

            Expect(!footer.HideEmptyProperties, "по умолчанию показаны все свойства");
            Expect(originalOrder.Length == 14 && originalOrder.Contains(empty.Name)
                   && originalOrder.Contains(emptyRelation.Name), "изначально видны все 12 свойств, название и добавление");
            Click(editor);
            Expect(editor.IsFocused && Crumb() == description.Name, "пустое описание открыто снизу и находится в фокусе");
            Click(toggle);
            Expect(footer.HideEmptyProperties, "нажатие на кнопку глаза включает скрытие");
            Expect(Names().SequenceEqual(new[] { "Название", zero.Name, check.Name, multi.Name, relation.Name, text.Name, "Добавить свойство" }),
                   "пустые значения скрыты, ноль, false, мультисписок и заполненная связь видны в исходном порядке");
            Expect(editor.IsFocused && editor.Text == "" && Crumb() == description.Name,
                   "скрытие карточки пустого описания сохраняет нижний редактор и его фокус");
            Expect(list.Cards.Any(c => c.Kind == PropertyCardKind.Title) && Names()[^1] == "Добавить свойство",
                   "название и добавление свойства остаются");
            Click(toggle);
            Expect(!footer.HideEmptyProperties && Names().SequenceEqual(originalOrder),
                   "повторное нажатие возвращает все карточки в прежнем порядке");
            Expect(Snapshot() == snapshot && changed == 0,
                   "переключение не пишет значения, связи, название и аудит в БД и не поднимает RecordChanged");

            ClickCard(empty.Name, value: false);
            Expect(IsSelected(empty.Name), "перед скрытием выделено пустое свойство");
            Click(toggle);
            list.Surface.Focus();
            Press(InputKey.Delete);
            Expect(deleted is null, "Delete после скрытия не удаляет невидимое выделенное свойство");
            Expect(Snapshot() == snapshot && changed == 0, "скрытие выделенного свойства также не меняет БД");

            Click(toggle);
            ClickCard(text.Name, value: false);
            Click(editor);
            editor.CaretIndex = 3;
            var caret = editor.CaretIndex;
            Click(toggle);
            Expect(editor.IsFocused && editor.CaretIndex == caret && editor.Text == "Заполнено" && Crumb() == text.Name,
                   "переключение сохраняет цель, текст, фокус и каретку нижнего редактора");
            ClickCard(text.Name, value: true);
            Press(InputKey.End);
            Type(" +");
            Expect(Value(text) == "Заполнено +" && editor.Text == Value(text),
                   $"редактор карточки после смены индексов меняет нужное свойство и синхронизирует нижний блок: БД «{Value(text)}», снизу «{editor.Text}», инлайн «{(list.CurrentEditor as TextBox)?.Text}», фокус {list.CurrentEditor?.IsFocused}");
            Expect(Value(zero) == "0" && Value(check) == "0" && Value(empty) == "",
                   "редактирование после фильтра не затрагивает соседние свойства");

            Press(InputKey.A, RawInputModifiers.Control);
            Press(InputKey.Back);
            Expect(Names().Contains(text.Name) && list.CurrentEditor?.IsKeyboardFocusWithin == true,
                   "очистка инлайн-поля не убирает карточку из-под каретки до завершения правки");
            Type("Снова");
            Expect(Value(text) == "Снова" && editor.Text == "Снова" && list.CurrentEditor?.IsKeyboardFocusWithin == true,
                   "после очистки можно продолжить набор в той же карточке");

            Click(editor);
            Press(InputKey.A, RawInputModifiers.Control);
            Press(InputKey.Back);
            Wait(() => !Names().Contains(text.Name));
            Expect(Value(text) == "" && !Names().Contains(text.Name), "очистка снизу скрывает карточку текстового свойства");
            Expect(editor.IsFocused && editor.Text == "" && Crumb() == text.Name,
                   "при исчезновении карточки нижний редактор сохраняет фокус, пустой текст и цель");

            page.Show(repo, recordId, collectionId);
            Pump();
            Expect(footer.HideEmptyProperties && !Names().Contains(description.Name) && Crumb() == description.Name,
                   "повторный Show сохраняет скрытие и открывает пустое описание снизу");

            repo.SetRecordTitle(targetId, "");
            page.ReloadFromDb();
            Pump();
            Expect(Names().Contains(relation.Name) && Card(relation.Name).Display?.Invoke() == ""
                   && repo.GetLinkEdges(relation.Id!.Value, recordId).Count == 1,
                   "связь с записью без названия остается видимой, даже когда ее текст показа пуст");

            Click(editor);
            // Мышь над другим свойством, но каретка остается в описании: незанятый редактор
            // под указателем не должен задерживать фильтрацию нижнего ввода до движения мыши.
            host.MouseMove(CardPoint(zero.Name, value: true));
            Pump();
            Expect(list.CurrentEditor is TextBox && !list.CurrentEditor.IsKeyboardFocusWithin && editor.IsFocused,
                   "наведение создало незанятый редактор другого свойства, фокус остался внизу");
            Type("Описание заполнено");
            Wait(() => Names().Contains(description.Name));
            Expect(Value(description) == "Описание заполнено" && Card(description.Name).Value() == Value(description),
                   "ввод в скрытое описание возвращает карточку без ухода мыши с другого свойства");
            Expect(editor.IsFocused && Crumb() == description.Name, "появление карточки не выдергивает фокус из описания");
            Press(InputKey.A, RawInputModifiers.Control);
            Press(InputKey.Back);
            Wait(() => !Names().Contains(description.Name));
            Expect(!Names().Contains(description.Name) && editor.IsFocused && editor.Text == "" && Crumb() == description.Name,
                   "повторная очистка описания скрывает карточку и сохраняет редактор");

            repo.SetFieldValue(recordId, empty.Id!.Value, "Из таблицы");
            repo.SetFieldValue(recordId, emptyMulti.Id!.Value, "[\"Опция\"]");
            repo.SetFieldValue(recordId, multi.Id!.Value, "[]");
            repo.SetLinks(relation.Id!.Value, recordId, []);
            repo.SetLinks(emptyRelation.Id!.Value, recordId, [targetId]);
            page.ReloadFromDb();
            Pump();
            Expect(footer.HideEmptyProperties && Names().Contains(empty.Name) && Names().Contains(emptyMulti.Name)
                   && !Names().Contains(multi.Name), "ReloadFromDb сохраняет фильтр и обновляет текст и мультисписки");
            Expect(Names().Contains(emptyRelation.Name) && !Names().Contains(relation.Name),
                   "ReloadFromDb обновляет видимость связей по ребрам, а не по пустой строке значения");
            Expect(editor.IsFocused && editor.Text == "" && Crumb() == description.Name,
                   "перечитывание при скрытом описании сохраняет нижний редактор");

            page.Show(repo, secondId, collectionId);
            Pump();
            Expect(footer.HideEmptyProperties && Names().SequenceEqual(new[] { "Название", zero.Name, text.Name, "Добавить свойство" }),
                   "переход к другой записи сохраняет режим, но фильтрует ее собственные значения");
            Expect(list.Cards.Any(c => c.Kind == PropertyCardKind.Title && c.Value() == ""),
                   "системное название остается даже у записи без названия");
            Click(toggle);
            Expect(!footer.HideEmptyProperties && Names().SequenceEqual(originalOrder),
                   "после перехода повторное нажатие по глазу возвращает все свойства");
            Console.WriteLine($"Пустые свойства: проверок {checkedCount}, провалов {failed}.");
            return failed == 0 ? 0 : 2;

            void Expect(bool ok, string message)
            {
                checkedCount++;
                Console.WriteLine($"  {(ok ? "ок    " : "ПРОВАЛ")} {message}");
                if (!ok) failed++;
            }

            // Свои карточки: блок ссылок на запись (подпись раздела и карточки связей коллекции на
            // саму себя) читается в фоне и проверяется стендом --only backlinks.
            string[] Names() => list.Cards.Where(c => c.Kind is not (PropertyCardKind.Section or PropertyCardKind.Backlink))
                .Select(c => c.Name).ToArray();
            string Crumb() => crumb.Children.OfType<TextBlock>().FirstOrDefault()?.Text ?? "";
            string Value(DbField field) => repo.GetFieldValues(recordId).GetValueOrDefault(field.Id!.Value, "");
            PropertyCard Card(string name) => list.Cards.First(c => c.Name == name);
            bool IsSelected(string name) => list.Surface.IsSelected(list.Cards.ToList().FindIndex(c => c.Name == name));
            string Snapshot()
            {
                var record = repo.GetRecord(recordId)!;
                return JsonSerializer.Serialize(new
                {
                    record.Title, record.UpdatedAt, record.UpdatedBy,
                    Values = repo.GetFieldValues(recordId).OrderBy(p => p.Key).ToArray(),
                    FilledLinks = repo.GetLinkEdges(relation.Id!.Value, recordId)
                        .Select(edge => new { edge.ToRecordId, edge.TargetDbId }).ToArray(),
                    EmptyLinks = repo.GetLinkEdges(emptyRelation.Id!.Value, recordId)
                        .Select(edge => new { edge.ToRecordId, edge.TargetDbId }).ToArray(),
                });
            }

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
                do { Pump(); } while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(2));
            }

            void Click(Control control)
            {
                Pump();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), host)
                    ?? throw new InvalidOperationException("Контрол не прикреплен к окну стенда");
                ClickAt(point);
            }

            void ClickAt(Point point)
            {
                host.MouseMove(point);
                Pump();   // редактор от наведения должен получить раскладку до нажатия
                host.MouseDown(point, MouseButton.Left);
                host.MouseUp(point, MouseButton.Left);
                Pump();
            }

            void ClickCard(string name, bool value)
            {
                ClickAt(CardPoint(name, value));
            }

            Point CardPoint(string name, bool value)
            {
                var index = list.Cards.ToList().FindIndex(c => c.Name == name);
                if (index < 0) throw new InvalidOperationException($"Нет карточки «{name}»");
                list.ScrollIntoView(index);
                Pump();
                var bounds = list.CardRect(index);
                var x = value ? bounds.X + 10 + list.NameColumnWidth + 18 + 24 : bounds.X + 30;
                var point = list.Surface.TranslatePoint(new Point(x, bounds.Y + bounds.Height / 2), host)
                    ?? throw new InvalidOperationException("Список не прикреплен к окну стенда");
                return point;
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
            Console.WriteLine($"ПРОВАЛ проверки пустых свойств: {ex}");
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
