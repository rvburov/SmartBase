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
            // Правило из прежней базы: ключа partialMatch еще нет.
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
            Click(fresh, fresh.FindControl<Button>("CancelBtn")!);
            Wait(() => freshClosed.IsCompleted, "новый диалог не закрылся по отмене");
            Expect(!freshClosed.Result && Snapshot() == snapshot,
                   "отмена нового свойства не создает поле и не меняет значения");

            var edit = new PropertyEditorDialog(repo, collectionId, fieldId);
            var editClosed = edit.ShowDialog<bool>(owner);
            Pump();
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
            Expect(saved is { PartialMatch: true, Recursive: true, Format: "pdf" }
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
            Click(cancelReopened, cancelReopened.FindControl<Button>("CancelBtn")!);
            Wait(() => cancelReopenedClosed.IsCompleted, "повторный диалог не закрылся по отмене");

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
