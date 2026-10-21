using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Database;

/// <summary>Окно авто-привязки и отдельные ссылки файлов: сохранение, отмена и выбор пути кликом.</summary>
internal static class FileAutoChecks
{
    public static int Run(string outDir)
    {
        var folder = Path.Combine(outDir, $"fileauto-{Guid.NewGuid():N}");
        var path = Path.Combine(folder, "test.db");
        var filesFolder = Path.Combine(folder, "files");
        var checkedCount = 0;
        var failed = 0;
        DatabaseManager? manager = null;
        Window? owner = null;
        try
        {
            Directory.CreateDirectory(Path.Combine(filesFolder, "Архив"));
            var exact = Path.Combine(filesFolder, "Отчет.pdf");
            var comma = Path.Combine(filesFolder, "Отчет, март.pdf");
            var april = Path.Combine(filesFolder, "Отчет апрель.pdf");
            var archived = Path.Combine(filesFolder, "Архив", "Отчет архив.pdf");
            var expectedPaths = new[] { exact, comma, april, archived };
            foreach (var file in expectedPaths.Append(Path.Combine(filesFolder, "Отчет.xlsx")))
                File.WriteAllText(file, "данные стенда");

            manager = DatabaseManager.Open(9409, "file-auto", path);
            var repo = manager.Constructor;
            var collectionId = repo.AddCollection(new DbCollection { Name = "Авто-привязка" }).Id!.Value;
            // Правило из прежней базы: ключей enabled и partialMatch еще нет.
            var legacyConfig = JsonSerializer.Serialize(new
            {
                auto = new { source = FileAutoRule.TitleSource, folder = filesFolder, format = "pdf", recursive = true },
            });
            var field = repo.AddField(new DbField
            {
                CollectionId = collectionId, Name = "Документы", FieldType = "file",
                ConfigJson = legacyConfig, Position = 1000,
            });
            var fieldId = field.Id!.Value;
            var recordId = repo.AddRecord(new DbRecord { CollectionId = collectionId, Title = "Отчет" }).Id!.Value;
            repo.ResolveAutoFileField(fieldId);
            Expect(Value() == exact, "старое правило связывает только точное имя");
            var snapshot = Snapshot();

            owner = new Window { Width = 900, Height = 900 };
            owner.Show();
            Pump();

            var fresh = new PropertyEditorDialog(repo, collectionId);
            var freshClosed = fresh.ShowDialog<bool>(owner);
            Pump();
            var types = fresh.FindControl<ComboBox>("TypeCombo")!;
            types.SelectedItem = types.Items.OfType<FieldTypeItem>().Single(t => t.Type == "file");
            Pump();
            var freshToggle = fresh.FindControl<ToggleSwitch>("FileAutoToggle")!;
            var freshOptions = fresh.FindControl<StackPanel>("FileAutoOptions")!;
            Expect(freshToggle.IsChecked != true && !freshOptions.IsVisible,
                   "у нового файлового свойства автоподстановка выключена, настройки скрыты");
            fresh.FindControl<TextBox>("NameBox")!.Text = "Новые документы";
            Click(fresh, freshToggle);
            Expect(freshToggle.IsChecked == true && freshOptions.IsVisible && Snapshot() == snapshot,
                   "явное включение показывает источник, папку и формат без изменения базы");
            Click(fresh, fresh.FindControl<Button>("SaveBtn")!);
            Expect(!freshClosed.IsCompleted
                   && fresh.FindControl<NoticeBanner>("NoticeBanner")!.Message == "Выберите столбец-источник"
                   && Snapshot() == snapshot,
                   "включенное правило без источника не сохраняется как ручное поле");
            var sources = fresh.FindControl<ComboBox>("FileSourceCombo")!;
            sources.SelectedItem = sources.Items.OfType<PropertyEditorDialog.SourceFieldItem>()
                .Single(s => s.Key == FileAutoRule.TitleSource);
            Pump();
            var freshPartial = fresh.FindControl<CheckBox>("FilePartialMatchCheck")!;
            Expect(freshPartial.IsVisible && freshPartial.IsChecked != true,
                   "у нового файлового свойства частичное совпадение изначально выключено");
            Click(fresh, freshPartial);
            Expect(freshPartial.IsChecked == true && Snapshot() == snapshot,
                   "галочка в новом диалоге меняет только черновик и не затрагивает прежнюю привязку");
            fresh.FindControl<TextBox>("FileFolderText")!.Text = filesFolder;
            var freshFormat = fresh.FindControl<ComboBox>("FileFormatCombo")!;
            freshFormat.SelectedItem = freshFormat.Items.OfType<ComboBoxItem>().Single(i => Equals(i.Content, "pdf"));
            Click(fresh, freshToggle);
            Expect(!freshOptions.IsVisible && Snapshot() == snapshot,
                   "выключение до сохранения скрывает настройки и не меняет существующие файлы");
            Click(fresh, freshToggle);
            Expect(freshOptions.IsVisible && freshPartial.IsChecked == true
                   && fresh.FindControl<TextBox>("FileFolderText")!.Text == filesFolder
                   && sources.SelectedItem is PropertyEditorDialog.SourceFieldItem { Key: FileAutoRule.TitleSource },
                   "повторное включение сохраняет черновик источника, папки и частичного поиска");
            Click(fresh, fresh.FindControl<Button>("CancelBtn")!);
            Wait(() => freshClosed.IsCompleted, "новый диалог не закрылся по отмене");
            Expect(!freshClosed.Result && Snapshot() == snapshot,
                   "отмена нового свойства не создает поле и не меняет значения");

            var manual = new PropertyEditorDialog(repo, collectionId);
            var manualClosed = manual.ShowDialog<bool>(owner);
            Pump();
            var manualTypes = manual.FindControl<ComboBox>("TypeCombo")!;
            manualTypes.SelectedItem = manualTypes.Items.OfType<FieldTypeItem>().Single(t => t.Type == "file");
            manual.FindControl<TextBox>("NameBox")!.Text = "Ручные документы";
            Click(manual, manual.FindControl<Button>("SaveBtn")!);
            Wait(() => manualClosed.IsCompleted, "ручное поле без настроек поиска не сохранилось");
            var manualField = repo.GetFields(collectionId).Single(f => f.Name == "Ручные документы");
            var manualRule = FileAutoRule.ReadConfigured(manualField.ConfigJson);
            Expect(manualClosed.Result && manualRule is { Enabled: false, SourceKey: "", Folder: "", Format: "" }
                   && FileAutoRule.FromConfig(manualField.ConfigJson) is null && Value() == exact,
                   "новое ручное поле сохраняется без источника, папки и формата, прежняя привязка не меняется");
            repo.SetFieldValue(recordId, manualField.Id!.Value, exact);
            Expect(repo.GetFieldValues(recordId).GetValueOrDefault(manualField.Id.Value) == exact,
                   "выключенная автоподстановка оставляет поле доступным для ручного значения");
            snapshot = Snapshot();

            var edit = new PropertyEditorDialog(repo, collectionId, fieldId);
            var editClosed = edit.ShowDialog<bool>(owner);
            Pump();
            Expect(edit.FindControl<ToggleSwitch>("FileAutoToggle")!.IsChecked == true
                   && edit.FindControl<StackPanel>("FileAutoOptions")!.IsVisible,
                   "старое действующее правило без enabled открывается с включенной автоподстановкой");
            var partial = edit.FindControl<CheckBox>("FilePartialMatchCheck")!;
            Expect(partial.IsChecked != true, "старое правило без нового ключа открывается с выключенной галочкой");
            Click(edit, partial);
            Expect(partial.IsChecked == true && Snapshot() == snapshot,
                   "включение частичного поиска до сохранения не переписывает конфиг и старый путь");
            Click(edit, edit.FindControl<Button>("SaveBtn")!);
            Wait(() => edit.OwnedWindows.OfType<ConfirmDialog>().Any(),
                 "не появилось подтверждение замены существующего пути");
            Expect(Snapshot() == snapshot, "ожидание подтверждения сохранения также не меняет данные");
            var confirmation = edit.OwnedWindows.OfType<ConfirmDialog>().Single();
            Click(confirmation, confirmation.FindControl<Button>("ConfirmBtn")!);
            Wait(() => editClosed.IsCompleted, "сохранение и повторный поиск не завершились");
            Expect(editClosed.Result, "настоящее сохранение закрывает диалог с успешным результатом");
            var saved = FileAutoRule.FromConfig(repo.GetFields(collectionId).Single(f => f.Id == fieldId).ConfigJson);
            Expect(saved is { Enabled: true, PartialMatch: true, Recursive: true, Format: "pdf" }
                   && saved.SourceKey == FileAutoRule.TitleSource,
                   "сохранены частичный поиск, рекурсия, формат и столбец-источник");
            Expect(FileValue.ReadPaths(Value()).ToHashSet(StringComparer.Ordinal).SetEquals(expectedPaths),
                   "сохранение пересканировало папку: все четыре PDF найдены, чужой формат исключен");

            var beforeFileChanges = Value();
            var savedConfig = repo.GetFields(collectionId).Single(f => f.Id == fieldId).ConfigJson;
            var added = Path.Combine(filesFolder, "Отчет май.pdf");
            File.WriteAllText(added, "новый файл стенда");
            File.Delete(archived);
            var reopened = new PropertyEditorDialog(repo, collectionId, fieldId);
            var reopenedClosed = reopened.ShowDialog<bool>(owner);
            Pump();
            Expect(reopened.FindControl<CheckBox>("FilePartialMatchCheck")!.IsChecked == true,
                   "повторное открытие восстанавливает включенную галочку");
            Expect(Value() == beforeFileChanges,
                   "добавление и удаление файлов в папке до повторного сохранения не меняет старый список");
            Click(reopened, reopened.FindControl<Button>("SaveBtn")!);
            Wait(() => reopened.OwnedWindows.OfType<ConfirmDialog>().Any(),
                 "не появилось подтверждение повторного сохранения");
            var repeatConfirmation = reopened.OwnedWindows.OfType<ConfirmDialog>().Single();
            Click(repeatConfirmation, repeatConfirmation.FindControl<Button>("ConfirmBtn")!);
            Wait(() => reopenedClosed.IsCompleted, "повторное сохранение не завершило перескан");
            Expect(reopenedClosed.Result
                   && repo.GetFields(collectionId).Single(f => f.Id == fieldId).ConfigJson == savedConfig
                   && FileValue.ReadPaths(Value()).ToHashSet(StringComparer.Ordinal)
                       .SetEquals(expectedPaths.Where(p => p != archived).Append(added)),
                   "сохранение без изменения настроек добавляет новый PDF и убирает удаленный из привязки");
            var cancelReopened = new PropertyEditorDialog(repo, collectionId, fieldId);
            var cancelReopenedClosed = cancelReopened.ShowDialog<bool>(owner);
            Pump();
            var beforeCancel = Snapshot();
            Click(cancelReopened, cancelReopened.FindControl<ToggleSwitch>("FileAutoToggle")!);
            Expect(!cancelReopened.FindControl<StackPanel>("FileAutoOptions")!.IsVisible && Snapshot() == beforeCancel,
                   "выключение в открытом диалоге не останавливает сохраненное правило и не меняет список");
            Click(cancelReopened, cancelReopened.FindControl<Button>("CancelBtn")!);
            Wait(() => cancelReopenedClosed.IsCompleted, "повторный диалог не закрылся по отмене");
            Expect(!cancelReopenedClosed.Result && Snapshot() == beforeCancel
                   && FileAutoRule.FromConfig(repo.GetFields(collectionId).Single(f => f.Id == fieldId).ConfigJson) is not null,
                   "отмена выключения сохраняет активное правило и все пути");

            var disable = new PropertyEditorDialog(repo, collectionId, fieldId);
            var disableClosed = disable.ShowDialog<bool>(owner);
            Pump();
            var beforeDisable = Value();
            Click(disable, disable.FindControl<ToggleSwitch>("FileAutoToggle")!);
            Click(disable, disable.FindControl<Button>("SaveBtn")!);
            Wait(() => disableClosed.IsCompleted, "выключение автоподстановки не сохранилось");
            var disabledConfig = repo.GetFields(collectionId).Single(f => f.Id == fieldId).ConfigJson;
            var disabledRule = FileAutoRule.ReadConfigured(disabledConfig);
            Expect(disableClosed.Result && Value() == beforeDisable
                   && disabledRule is { Enabled: false, PartialMatch: true, Recursive: true, Format: "pdf" }
                   && disabledRule.SourceKey == FileAutoRule.TitleSource && disabledRule.Folder == filesFolder
                   && FileAutoRule.FromConfig(disabledConfig) is null,
                   "сохраненное выключение оставляет все пути, источник, папку, формат и флаги поиска");
            var whileOff = Path.Combine(filesFolder, "Отчет июнь.pdf");
            File.WriteAllText(whileOff, "файл появился при выключенном правиле");
            repo.ResolveAutoFileField(fieldId);
            repo.SetRecordTitle(recordId, "Другой отчет");
            Expect(Value() == beforeDisable,
                   "выключенное правило не пересчитывает файлы при запросе поиска и изменении источника");
            repo.SetRecordTitle(recordId, "Отчет");

            var disabledReopen = new PropertyEditorDialog(repo, collectionId, fieldId);
            var disabledReopenClosed = disabledReopen.ShowDialog<bool>(owner);
            Pump();
            Expect(disabledReopen.FindControl<ToggleSwitch>("FileAutoToggle")!.IsChecked != true
                   && !disabledReopen.FindControl<StackPanel>("FileAutoOptions")!.IsVisible
                   && disabledReopen.FindControl<TextBox>("FileFolderText")!.Text == filesFolder
                   && disabledReopen.FindControl<CheckBox>("FilePartialMatchCheck")!.IsChecked == true
                   && disabledReopen.FindControl<CheckBox>("FileRecursiveCheck")!.IsChecked == true
                   && disabledReopen.FindControl<ComboBox>("FileSourceCombo")!.SelectedItem
                       is PropertyEditorDialog.SourceFieldItem { Key: FileAutoRule.TitleSource }
                   && disabledReopen.FindControl<ComboBox>("FileFormatCombo")!.SelectedItem
                       is ComboBoxItem { Content: "pdf" },
                   "повторное открытие выключенного правила восстанавливает все настройки под скрытым блоком");
            var disabledSnapshot = Snapshot();
            Click(disabledReopen, disabledReopen.FindControl<ToggleSwitch>("FileAutoToggle")!);
            Expect(disabledReopen.FindControl<StackPanel>("FileAutoOptions")!.IsVisible
                   && Snapshot() == disabledSnapshot,
                   "включение после открытия остается черновиком и не добавляет новый файл до сохранения");
            Click(disabledReopen, disabledReopen.FindControl<Button>("CancelBtn")!);
            Wait(() => disabledReopenClosed.IsCompleted, "отмена повторного включения не закрыла диалог");
            Expect(!disabledReopenClosed.Result && Snapshot() == disabledSnapshot,
                   "отмена повторного включения оставляет правило выключенным и список прежним");

            var enable = new PropertyEditorDialog(repo, collectionId, fieldId);
            var enableClosed = enable.ShowDialog<bool>(owner);
            Pump();
            Click(enable, enable.FindControl<ToggleSwitch>("FileAutoToggle")!);
            Click(enable, enable.FindControl<Button>("SaveBtn")!);
            Wait(() => enable.OwnedWindows.OfType<ConfirmDialog>().Any(),
                 "включение сохраненного правила не запросило подтверждение замены путей");
            Expect(Snapshot() == disabledSnapshot, "ожидание подтверждения повторного включения не меняет базу");
            var enableConfirmation = enable.OwnedWindows.OfType<ConfirmDialog>().Single();
            Click(enableConfirmation, enableConfirmation.FindControl<Button>("ConfirmBtn")!);
            Wait(() => enableClosed.IsCompleted, "повторное включение не завершило поиск");
            Expect(enableClosed.Result
                   && FileAutoRule.FromConfig(repo.GetFields(collectionId).Single(f => f.Id == fieldId).ConfigJson)
                       is { Enabled: true, PartialMatch: true }
                   && FileValue.ReadPaths(Value()).ToHashSet(StringComparer.Ordinal)
                       .SetEquals(expectedPaths.Where(p => p != archived).Append(added).Append(whileOff)),
                   "после Сохранить правило вновь включено, все настройки применены и новый файл найден");

            var storage = FileValue.Serialize([comma, april]);
            var editor = new FileEditor { AutoFolder = filesFolder, Storage = storage, Margin = new Thickness(16) };
            owner.Content = editor;
            Pump();
            var links = editor.FindControl<StackPanel>("MultiLinks")!;
            var names = links.Children.OfType<TextBlock>().ToArray();
            Expect(links.IsVisible && names.Length == 3
                   && names[0].Text == "Отчет, март.pdf" && names[1].Text == ", " && names[2].Text == "Отчет апрель.pdf",
                   "два имени показаны отдельными ссылками, запятая внутри имени сохранена");

            // Смотрим выбранный путь до тика 250 мс; смена Storage сразу отменяет открытие ОС.
            var pending = typeof(FileEditor).GetField("_pendingPath", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var second = ClickedPath(names[2]);
            Expect(second == april && editor.Storage == storage,
                   "нажатие второго имени выбирает второй путь и не меняет список");
            names = links.Children.OfType<TextBlock>().ToArray();
            Expect(ClickedPath(names[1]) is null, "разделитель между ссылками не выбирает файл");

            var leadingSpace = " " + storage;
            editor.Storage = leadingSpace;
            Expect(editor.Storage == leadingSpace && !links.IsVisible,
                   "программное значение с пробелом перед JSON остается прежней одиночной строкой");
            editor.Storage = storage;
            editor.SyncStorage(leadingSpace);
            Expect(editor.Storage == leadingSpace && !links.IsVisible,
                   "дозагрузка также не обрезает пробел и не превращает старую строку в список");
            editor.Storage = storage;

            editor.AutoFolder = "";
            editor.BeginManualEdit();
            Expect(!editor.FindControl<TextBox>("Input")!.IsVisible && editor.Storage == storage,
                   "список после выключения авто-режима не открывается как сырой JSON для ручной правки");
            editor.Storage = april;
            editor.BeginManualEdit();
            Expect(editor.FindControl<TextBox>("Input") is { IsVisible: true, Text: var text } && text == april,
                   "одиночный путь по-прежнему доступен для ручной правки");
            editor.CancelManualEdit();

            Console.WriteLine($"Авто-привязка UI: проверок {checkedCount}, провалов {failed}.");
            return failed == 0 ? 0 : 2;

            string Value() => repo.GetFieldValues(recordId).GetValueOrDefault(fieldId, "");
            string Snapshot() => JsonSerializer.Serialize(new
            {
                Fields = repo.GetFields(collectionId).Select(f => new { f.Id, f.ConfigJson }).ToArray(),
                Record = repo.GetRecord(recordId),
                Values = repo.GetFieldValues(recordId).OrderBy(p => p.Key).ToArray(),
            });

            void Expect(bool ok, string message)
            {
                checkedCount++;
                Console.WriteLine($"  {(ok ? "ок    " : "ПРОВАЛ")} {message}");
                if (!ok) failed++;
            }

            void Pump()
            {
                Dispatcher.UIThread.RunJobs();
                foreach (var window in Windows(owner).ToArray())
                    if (window.IsVisible) window.UpdateLayout();
                // Захват кадра сам выполняет UI-задачи: сохранение может закрыть модальные окна.
                // Общий render-tick запускаем через владельца, который жив до конца проверки,
                // а не через снимок OwnedWindows с уже закрывшимися диалогами.
                Dispatcher.UIThread.RunJobs();
                if (owner.IsVisible)
                {
                    using var frame = owner.CaptureRenderedFrame();
                }
                Dispatcher.UIThread.RunJobs();
            }

            void Wait(Func<bool> ready, string message)
            {
                var watch = Stopwatch.StartNew();
                while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(10))
                {
                    Pump();
                    Thread.Sleep(1);
                }
                if (!ready()) throw new InvalidOperationException(message);
                Pump();
            }

            void Click(Window window, Control control)
            {
                Pump();
                ClickWithoutPump(window, control);
                Pump();
            }

            string? ClickedPath(Control control)
            {
                ClickWithoutPump(owner, control);
                var selected = pending.GetValue(editor) as string;
                editor.Storage = storage;   // таймер остановлен до запуска программы ОС
                Pump();
                return selected;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ПРОВАЛ проверки авто-привязки UI: {ex}");
            return 2;
        }
        finally
        {
            if (owner is not null)
                foreach (var window in Windows(owner).Reverse().ToArray()) window.Close();
            Dispatcher.UIThread.RunJobs();
            manager?.Dispose();
            AppDbContext.ResetPool(path);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    private static IEnumerable<Window> Windows(Window root) =>
        new[] { root }.Concat(root.OwnedWindows.SelectMany(Windows));

    private static void ClickWithoutPump(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("Контрол не прикреплен к окну стенда");
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }
}
