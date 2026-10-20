using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.EntityFrameworkCore;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Utils;
using SmartBase.Views.ConstructorDb;

/// <summary>Настройка связей настоящим диалогом: черновик, сохранение, предварительный просмотр и отмена.</summary>
internal static class RelationChecks
{
    public static int Run(string outDir)
    {
        var folder = Path.Combine(outDir, $"relations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "test.db");
        DatabaseManager? manager = null;
        Window? owner = null;
        var checks = 0;
        var failed = 0;
        try
        {
            manager = DatabaseManager.Open(9418, "relations", path);
            var repo = manager.Constructor;
            var target = repo.AddCollection(new DbCollection { Name = "Клиенты" }).Id!.Value;
            var source = repo.AddCollection(new DbCollection { Name = "Заказы" }).Id!.Value;
            DbField Field(int col, string name, string type = "text", string config = "{}") =>
                repo.AddField(new DbField { CollectionId = col, Name = name, FieldType = type, ConfigJson = config });
            var sku = Field(target, "Артикул");
            var targetInn = Field(target, "ИНН");
            var balance = Field(target, "Баланс", "number");
            var sourceInn = Field(source, "ИНН заказа");
            var legacyJson = JsonSerializer.Serialize(new { targetCollectionId = target, single = false, separator = "comma" });
            var relation = Field(source, "Клиент", "relation", legacyJson);
            var a = repo.AddRecord(new DbRecord { CollectionId = target, Title = "Компания А" }).Id!.Value;
            var b = repo.AddRecord(new DbRecord { CollectionId = target, Title = "Компания Б" }).Id!.Value;
            repo.SetFieldValue(a, sku.Id!.Value, "А-01");
            repo.SetFieldValue(b, sku.Id.Value, "Б-02");
            repo.SetFieldValue(a, targetInn.Id!.Value, "100");
            repo.SetFieldValue(b, targetInn.Id.Value, "200");
            repo.SetFieldValue(a, balance.Id!.Value, "10,5");
            repo.SetFieldValue(b, balance.Id.Value, "20,25");
            var record = repo.AddRecord(new DbRecord { CollectionId = source, Title = "Заказ" }).Id!.Value;
            repo.SetFieldValue(record, sourceInn.Id!.Value, "100");
            repo.SetLinks(relation.Id!.Value, record, [b]);
            owner = new Window { Width = 850, Height = 700 };
            owner.Show();
            Pump();
            var before = Snapshot();

            var legacy = new PropertyEditorDialog(repo, source, relation.Id.Value) { MaxHeight = 600 };
            var legacyClosed = legacy.ShowDialog<bool>(owner);
            Pump();
            Expect(Combo(legacy, "RelationModeCombo").SelectedIndex == 0
                && SelectedKey(Combo(legacy, "RelationDisplayCombo")) == "__title__"
                && SelectedKey(Combo(legacy, "RelationSearchCombo")) == "__title__",
                "старое поле открылось ручным, поиск и показ остались по названию");
            Choose(Combo(legacy, "RelationDisplayCombo"), "Key", sku.Id.Value.ToString());
            Choose(Combo(legacy, "RelationSearchCombo"), "Key", targetInn.Id.Value.ToString());
            Expect(Snapshot() == before, "переключение свойств меняет только черновик диалога");
            Click(legacy, legacy.FindControl<Button>("CancelBtn")!);
            Wait(() => legacyClosed.IsCompleted, "отмена старого поля не закрыла окно");
            Expect(!legacyClosed.Result && Snapshot() == before, "отмена не изменила конфиг, записи или ребра");

            var manual = new PropertyEditorDialog(repo, source, relation.Id.Value) { MaxHeight = 600 };
            var manualClosed = manual.ShowDialog<bool>(owner);
            Pump();
            Choose(Combo(manual, "RelationDisplayCombo"), "Key", sku.Id.Value.ToString());
            Choose(Combo(manual, "RelationSearchCombo"), "Key", targetInn.Id.Value.ToString());
            Click(manual, manual.FindControl<Button>("SaveBtn")!);
            Wait(() => manualClosed.IsCompleted, "ручное поле не сохранилось");
            var manualCfg = RelationConfig.FromConfig(repo.GetFields(source).Single(f => f.Id == relation.Id).ConfigJson);
            Expect(manualClosed.Result && manualCfg?.DisplayField == sku.Id.Value.ToString()
                && manualCfg.SearchField == targetInn.Id.Value.ToString() && manualCfg.Auto is null,
                "настоящее сохранение записало отдельные свойства показа и поиска");
            Expect(repo.GetLinks(relation.Id.Value, record).Single().Title == "Б-02"
                && repo.SearchRelationRecords(target, targetInn.Id.Value.ToString(), "100").Single().RecordId == a,
                "показ идет по артикулу, поиск по ИНН, сохраненная цель остается прежней");

            before = Snapshot();
            var cancelled = CreateAuto("Авто отмена");
            var cancelledClosed = cancelled.ShowDialog<bool>(owner);
            Pump();
            ConfigureAuto(cancelled);
            var save = cancelled.FindControl<Button>("SaveBtn")!;
            var saveAt = save.TranslatePoint(new Point(0, 0), cancelled);
            Expect(cancelled.FindControl<StackPanel>("RelationAutoOptions")!.IsVisible
                && saveAt is { } position && position.Y >= 0 && position.Y + save.Bounds.Height <= cancelled.Bounds.Height + 1,
                "в коротком окне автоматические настройки прокручиваются, кнопка сохранения остается видна");
            using (var frame = cancelled.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "relations-auto-dialog.png"));
            Click(cancelled, save);
            Wait(() => cancelled.OwnedWindows.OfType<ConfirmDialog>().Any(), "не появился предварительный результат автоподбора");
            Expect(Snapshot() == before, "до подтверждения предварительного результата поле и ребра не создаются");
            var preview = cancelled.OwnedWindows.OfType<ConfirmDialog>().Single();
            Click(preview, preview.FindControl<Button>("CancelBtn")!);
            Wait(() => !cancelled.OwnedWindows.OfType<ConfirmDialog>().Any(), "предварительный результат не закрылся по отмене");
            Expect(!cancelledClosed.IsCompleted && Snapshot() == before, "отмена предварительного результата сохранила черновик без записи в базу");
            Click(cancelled, cancelled.FindControl<Button>("CancelBtn")!);
            Wait(() => cancelledClosed.IsCompleted, "создание автосвязи не закрылось по отмене");
            Expect(!cancelledClosed.Result && Snapshot() == before, "полная отмена создания автосвязи не оставила поля или ссылок");

            var auto = CreateAuto("Авто клиент");
            var autoClosed = auto.ShowDialog<bool>(owner);
            Pump();
            ConfigureAuto(auto);
            SaveWithPreview(auto, autoClosed);
            var saved = repo.GetFields(source).Single(f => f.Name == "Авто клиент");
            var autoCfg = RelationConfig.FromConfig(saved.ConfigJson);
            Expect(autoClosed.Result && autoCfg?.Auto == new RelationAutoRule(sourceInn.Id.Value.ToString(), targetInn.Id.Value.ToString())
                && autoCfg.DisplayField == sku.Id.Value.ToString() && !autoCfg.Single,
                "сохранение нового автополя записало оба ключа сопоставления и режим нескольких целей");
            Expect(repo.GetLinkTargetIds(saved.Id!.Value, record).SequenceEqual([a]),
                "подтвержденное сохранение связало заказ с клиентом по ИНН");

            var configBefore = saved.ConfigJson;
            manager.Context.Database.ExecuteSqlInterpolated($"UPDATE db_field_values SET Value = '100' WHERE RecordId = {b} AND FieldId = {targetInn.Id.Value}");
            var repeated = new PropertyEditorDialog(repo, source, saved.Id.Value) { MaxHeight = 600 };
            var repeatedClosed = repeated.ShowDialog<bool>(owner);
            Pump();
            Expect(Combo(repeated, "RelationModeCombo").SelectedIndex == 1
                && SelectedKey(Combo(repeated, "RelationAutoSourceCombo")) == sourceInn.Id.Value.ToString()
                && SelectedKey(Combo(repeated, "RelationAutoTargetCombo")) == targetInn.Id.Value.ToString(),
                "повторное открытие восстанавливает автоправило и оба выбранных свойства");
            SaveWithPreview(repeated, repeatedClosed);
            Expect(repeatedClosed.Result && repo.GetFields(source).Single(f => f.Id == saved.Id).ConfigJson == configBefore
                && repo.GetLinkTargetIds(saved.Id.Value, record).ToHashSet().SetEquals([a, b]),
                "повторное сохранение без изменения настроек увидело внешнее изменение данных и обновило все совпадения");

            var lookupDialog = CreateComputed("Артикул клиента", "lookup", saved.Id.Value, sku.Id.Value.ToString());
            var lookupClosed = lookupDialog.ShowDialog<bool>(owner);
            Pump();
            ConfigureComputed(lookupDialog, saved.Id.Value, sku.Id.Value.ToString());
            Choose(Combo(lookupDialog, "ComputedRelationCombo"), "Id", relation.Id.Value);
            Expect(SelectedKey(Combo(lookupDialog, "ComputedTargetCombo")) == "__title__",
                "смена исходной связи сбрасывает выбранное свойство цели, даже если такой ключ есть в новой связи");
            Choose(Combo(lookupDialog, "ComputedRelationCombo"), "Id", saved.Id.Value);
            Choose(Combo(lookupDialog, "ComputedTargetCombo"), "Key", sku.Id.Value.ToString());
            Click(lookupDialog, lookupDialog.FindControl<Button>("SaveBtn")!);
            Wait(() => lookupClosed.IsCompleted, "lookup не сохранился");
            var lookup = repo.GetFields(source).Single(f => f.Name == "Артикул клиента");
            Expect(lookupClosed.Result && RelationComputedConfig.FromConfig(lookup.ConfigJson) == new RelationComputedConfig(saved.Id.Value, sku.Id.Value.ToString())
                && repo.GetFieldValues(record).GetValueOrDefault(lookup.Id!.Value) == "А-01, Б-02",
                "lookup создается диалогом и выводит выбранное свойство обеих связанных записей");

            var rollupDialog = CreateComputed("Общий баланс", "rollup", saved.Id.Value, balance.Id.Value.ToString());
            var rollupClosed = rollupDialog.ShowDialog<bool>(owner);
            Pump();
            ConfigureComputed(rollupDialog, saved.Id.Value, balance.Id.Value.ToString());
            Combo(rollupDialog, "ComputedAggregateCombo").SelectedIndex = 1;
            Pump();
            Click(rollupDialog, rollupDialog.FindControl<Button>("SaveBtn")!);
            Wait(() => rollupClosed.IsCompleted, "rollup не сохранился");
            var rollup = repo.GetFields(source).Single(f => f.Name == "Общий баланс");
            Expect(rollupClosed.Result && RelationComputedConfig.FromConfig(rollup.ConfigJson)?.Aggregate == "sum"
                && repo.GetFieldValues(record).GetValueOrDefault(rollup.Id!.Value) == "30.75",
                "числовой итог создается диалогом и считает сырые числа связанных записей");

            var filterDisplay = typeof(TableView).GetMethod("DisplayValue", BindingFlags.NonPublic | BindingFlags.Static)!;
            Expect((string?)filterDisplay.Invoke(null, ["1.5", "rollup", ", ", null]) == "1,5"
                && (string?)filterDisplay.Invoke(null, ["1.5", "lookup", ", ", null]) == "1.5",
                "фильтр показывает итог как число с запятой, а текст lookup оставляет без преобразования");

            var autoPicker = new RelationSelectBox(repo, saved.Id.Value, autoCfg!, editing: true) { RecordId = record, Height = 38 };
            owner.Content = autoPicker;
            Pump();
            autoPicker.OpenSearch("");
            Wait(() => SearchReady(autoPicker) && Shown(autoPicker).Length == 2, "автоматический список не загрузился");
            var autoPopup = Private<Popup>(autoPicker, "_popup");
            LayoutPopup(autoPopup);
            var autoBoxes = autoPopup.Child!.GetVisualDescendants().OfType<CheckBox>().ToArray();
            Expect(autoBoxes.Length == 2 && autoBoxes.All(cb => !cb.IsEnabled),
                "чекбоксы автоматической связи отключены, но найденные цели видны");
            var autoEdges = repo.GetLinkEdges(saved.Id.Value, record).ToArray();
            var firstVm = Shown(autoPicker)[0];
            firstVm.GetType().GetProperty("Checked")!.SetValue(firstVm, false);
            Pump();
            Expect(repo.GetLinkEdges(saved.Id.Value, record).SequenceEqual(autoEdges),
                "попытка переключить автоматическую связь не меняет сохраненные ребра");
            (int DbId, int RecordId)? navigated = null;
            autoPicker.Navigate += (_, targetId) => navigated = targetId;
            var pencil = autoPopup.Child.GetVisualDescendants().OfType<Border>().First(control =>
                Grid.GetColumn(control) == 1 && control.Child is Viewbox
                && control.DataContext?.GetType().Name == "RelationOptionVm");
            ClickPopup(autoPopup, pencil);
            Expect(navigated is { DbId: 0 } && new[] { a, b }.Contains(navigated.Value.RecordId),
                "переход к доступной записи работает и у автоматической связи только для чтения");

            manager.Context.Database.ExecuteSqlInterpolated($"UPDATE db_field_values SET Value = '200' WHERE RecordId = {b} AND FieldId = {targetInn.Id.Value}");
            var permissions = manager.Permissions;
            permissions.SetMask(permissions.CurrentUserId, null, (int)PermAction.All);
            permissions.SetMask(permissions.CurrentUserId, source, (int)(PermAction.All & ~PermAction.EditRecord));
            var me = manager.Context.DbUsers.First(user => user.UserKey == CurrentUser.Key);
            me.IsAdmin = false;
            me.IsApproved = true;
            manager.Context.SaveChanges();
            permissions.Reload(notify: false);
            var deniedPicker = new RelationSelectBox(repo, relation.Id.Value, manualCfg!, editing: true) { RecordId = record, Height = 38 };
            owner.Content = deniedPicker;
            Pump();
            deniedPicker.OpenSearch("");
            Wait(() => SearchReady(deniedPicker) && Shown(deniedPicker).Length == 2, "список без права изменения не загрузился");
            var deniedPopup = Private<Popup>(deniedPicker, "_popup");
            LayoutPopup(deniedPopup);
            var deniedBoxes = deniedPopup.Child!.GetVisualDescendants().OfType<CheckBox>().ToArray();
            Expect(deniedBoxes.Length == 2 && deniedBoxes.All(cb => !cb.IsEnabled),
                "без права изменения записи ручной список также доступен только для просмотра");
            var deniedEdges = repo.GetLinkEdges(relation.Id.Value, record).ToArray();
            var deniedVm = Shown(deniedPicker).Single(item => (int)item.GetType().GetProperty("Id")!.GetValue(item)! == a);
            deniedVm.GetType().GetProperty("Checked")!.SetValue(deniedVm, true);
            Pump();
            Expect(repo.GetLinkEdges(relation.Id.Value, record).SequenceEqual(deniedEdges),
                "отказ права изменения не оставляет частично измененных связей");
            deniedPicker.OpenSearch("100");
            Pump();
            var generation = Private<int>(deniedPicker, "_searchGeneration");
            deniedPicker.OpenSearch("200");
            Wait(() => SearchReady(deniedPicker) && Shown(deniedPicker) is { Length: 1 } rows
                    && (int)rows[0].GetType().GetProperty("Id")!.GetValue(rows[0])! == b,
                "последний запрос не заменил предыдущий результат поиска");
            Expect(Private<int>(deniedPicker, "_searchGeneration") > generation && Shown(deniedPicker).Length == 1
                && (int)Shown(deniedPicker)[0].GetType().GetProperty("Id")!.GetValue(Shown(deniedPicker)[0])! == b,
                "быстрый поиск 100 затем 200 оставляет только последний результат, отмененный запрос его не затирает");
            deniedPopup.IsOpen = false;
            Console.WriteLine($"Настройки связей UI: проверок {checks}, провалов {failed}.");
            return failed == 0 ? 0 : 2;

            PropertyEditorDialog CreateAuto(string name) => new(repo, source) { MaxHeight = 600, Tag = name };
            void ConfigureAuto(PropertyEditorDialog dialog)
            {
                dialog.FindControl<TextBox>("NameBox")!.Text = (string)dialog.Tag!;
                var type = Combo(dialog, "TypeCombo");
                type.SelectedItem = type.Items.OfType<FieldTypeItem>().Single(t => t.Type == "relation");
                Pump();
                Choose(Combo(dialog, "RelationTargetCombo"), "Id", target);
                Choose(Combo(dialog, "RelationDisplayCombo"), "Key", sku.Id.Value.ToString());
                Choose(Combo(dialog, "RelationSearchCombo"), "Key", targetInn.Id.Value.ToString());
                Combo(dialog, "RelationModeCombo").SelectedIndex = 1;
                Combo(dialog, "RelationCardinalityCombo").SelectedIndex = 0;
                Pump();
                Choose(Combo(dialog, "RelationAutoSourceCombo"), "Key", sourceInn.Id.Value.ToString());
                Choose(Combo(dialog, "RelationAutoTargetCombo"), "Key", targetInn.Id.Value.ToString());
            }

            PropertyEditorDialog CreateComputed(string name, string type, int relationId, string key) =>
                new(repo, source) { MaxHeight = 600, Tag = (name, type) };
            void ConfigureComputed(PropertyEditorDialog dialog, int relationId, string key)
            {
                var (name, typeKey) = ((string, string))dialog.Tag!;
                dialog.FindControl<TextBox>("NameBox")!.Text = name;
                var type = Combo(dialog, "TypeCombo");
                type.SelectedItem = type.Items.OfType<FieldTypeItem>().Single(t => t.Type == typeKey);
                Pump();
                Choose(Combo(dialog, "ComputedRelationCombo"), "Id", relationId);
                Choose(Combo(dialog, "ComputedTargetCombo"), "Key", key);
            }

            void SaveWithPreview(PropertyEditorDialog dialog, Task<bool> closed)
            {
                Click(dialog, dialog.FindControl<Button>("SaveBtn")!);
                Wait(() => dialog.OwnedWindows.OfType<ConfirmDialog>().Any(), "не появился результат подбора связей");
                var confirmation = dialog.OwnedWindows.OfType<ConfirmDialog>().Single();
                Click(confirmation, confirmation.FindControl<Button>("ConfirmBtn")!);
                Wait(() => closed.IsCompleted, "сохранение автоматических связей не завершилось");
            }

            string Snapshot() => JsonSerializer.Serialize(new
            {
                Fields = repo.GetFields(source).Select(f => new { f.Id, f.Name, f.FieldType, f.ConfigJson }),
                Record = repo.GetRecord(record), Links = repo.GetLinkEdges(relation.Id.Value, record),
                Values = repo.GetFieldValues(record).OrderBy(x => x.Key),
            });
            void Expect(bool ok, string message)
            {
                checks++;
                if (!ok) failed++;
                Console.WriteLine($"  {(ok ? "ок    " : "ПРОВАЛ")} {message}");
            }
            void Pump()
            {
                Dispatcher.UIThread.RunJobs();
                foreach (var window in Windows(owner).ToArray()) if (window.IsVisible) window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                if (owner.IsVisible) { using var frame = owner.CaptureRenderedFrame(); }
                Dispatcher.UIThread.RunJobs();
            }
            void Wait(Func<bool> ready, string message)
            {
                var watch = Stopwatch.StartNew();
                while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(15)) { Pump(); Thread.Sleep(1); }
                if (!ready()) throw new InvalidOperationException(message);
                Pump();
            }
            void Choose(ComboBox combo, string property, object value)
            {
                combo.SelectedItem = combo.Items.Cast<object>().Single(item =>
                    Equals(item.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(item), value));
                Pump();
            }
            void Click(Window window, Control control)
            {
                Pump();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                    ?? throw new InvalidOperationException("Контрол не прикреплен к окну");
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Pump();
            }
            bool SearchReady(RelationSelectBox picker) => Private<TextBlock>(picker, "_moreHint").Text != "Поиск…";
            object[] Shown(RelationSelectBox picker) => ((System.Collections.IEnumerable)Private<object>(picker, "_shown")).Cast<object>().ToArray();
            void LayoutPopup(Popup popup)
            {
                Pump();
                if (popup.Child is { } child && TopLevel.GetTopLevel(child) is { } top)
                {
                    top.UpdateLayout();
                    using var frame = top.CaptureRenderedFrame();
                }
                Pump();
            }
            void ClickPopup(Popup popup, Control control)
            {
                LayoutPopup(popup);
                var top = TopLevel.GetTopLevel(control) ?? throw new InvalidOperationException("Список не прикреплен к окну");
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)
                    ?? throw new InvalidOperationException("Нет позиции кнопки перехода");
                top.MouseMove(point);
                top.MouseDown(point, MouseButton.Left);
                if (popup.IsOpen) top.MouseUp(point, MouseButton.Left);
                else owner.MouseUp(new Point(2, 2), MouseButton.Left);
                Pump();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ПРОВАЛ проверки настроек связей: {ex}");
            return 2;
        }
        finally
        {
            owner?.Close();
            Dispatcher.UIThread.RunJobs();
            manager?.Dispose();
            AppDbContext.ResetPool(path);
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private static ComboBox Combo(Window window, string name) => window.FindControl<ComboBox>(name)!;
    private static T Private<T>(object owner, string field) =>
        (T)owner.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
    private static string? SelectedKey(ComboBox combo) => combo.SelectedItem?.GetType().GetProperty("Key")?.GetValue(combo.SelectedItem) as string;
    private static IEnumerable<Window> Windows(Window window)
    {
        yield return window;
        foreach (var child in window.OwnedWindows)
            foreach (var descendant in Windows(child)) yield return descendant;
    }
}
