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
using SmartBase.Database.Repositories;
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

            var multi = new PropertyEditorDialog(repo, source, relation.Id.Value) { MaxHeight = 600 };
            var multiClosed = multi.ShowDialog<bool>(owner);
            Pump();
            var addDisplay = multi.FindControl<Button>("AddDisplayBtn")!;
            addDisplay.BringIntoView();
            Click(multi, addDisplay);
            Choose(multi.FindControl<StackPanel>("RelationDisplayExtra")!.GetVisualDescendants().OfType<ComboBox>().Single(), "Key", "__title__");
            var addSearch = multi.FindControl<Button>("AddSearchBtn")!;
            addSearch.BringIntoView();
            Click(multi, addSearch);
            Choose(multi.FindControl<StackPanel>("RelationSearchExtra")!.GetVisualDescendants().OfType<ComboBox>().Single(), "Key", sku.Id.Value.ToString());
            Combo(multi, "RelationDeleteCombo").SelectedIndex = 1;   // «Запретить удаление»
            using (var frame = multi.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "relations-multi-dialog.png"));
            Click(multi, multi.FindControl<Button>("SaveBtn")!);
            Wait(() => multiClosed.IsCompleted, "показ по нескольким свойствам не сохранился");
            var multiCfg = RelationConfig.FromConfig(repo.GetFields(source).Single(f => f.Id == relation.Id).ConfigJson)!;
            Expect(multiClosed.Result && multiCfg.DisplayKeys.SequenceEqual([sku.Id.Value.ToString(), "__title__"])
                && multiCfg.SearchKeys.SequenceEqual([targetInn.Id.Value.ToString(), sku.Id.Value.ToString()]),
                "дополнительные свойства показа и поиска сохранились в порядке строк окна");
            Expect(multiCfg.RestrictDelete && Throws<RelationDeleteRestrictedException>(() => repo.DeleteRecord(b))
                && repo.GetRecord(b) is not null,
                "«Запретить удаление» сохранилось и не дает удалить связанную запись");
            Expect(repo.GetRelationLinks(relation.Id.Value, record).Single().Display == "Б-02 · Компания Б"
                && repo.SearchRelationRecords(target, multiCfg.EffectiveSearchField, "А-01").Single().RecordId == a
                && repo.SearchRelationRecords(target, multiCfg.EffectiveSearchField, "200").Single().RecordId == b,
                "ячейка показывает оба свойства, поиск находит запись по любому из выбранных");
            var reopened = new PropertyEditorDialog(repo, source, relation.Id.Value) { MaxHeight = 600 };
            var reopenedClosed = reopened.ShowDialog<bool>(owner);
            Pump();
            Expect(SelectedKey(reopened.FindControl<StackPanel>("RelationDisplayExtra")!.GetVisualDescendants().OfType<ComboBox>().Single()) == "__title__"
                && SelectedKey(reopened.FindControl<StackPanel>("RelationSearchExtra")!.GetVisualDescendants().OfType<ComboBox>().Single()) == sku.Id.Value.ToString(),
                "повторное открытие восстановило дополнительные свойства строками");
            Expect(Combo(reopened, "RelationDeleteCombo").SelectedIndex == 1,
                "повторное открытие показало запрет удаления");
            Click(reopened, reopened.FindControl<Button>("CancelBtn")!);
            Wait(() => reopenedClosed.IsCompleted, "окно связи не закрылось");

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
            Expect(autoClosed.Result
                && autoCfg?.Auto == new RelationAutoRule([new(sourceInn.Id.Value.ToString(), targetInn.Id.Value.ToString())], fold: true)
                && autoCfg.DisplayField == sku.Id.Value.ToString() && !autoCfg.Single,
                "сохранение нового автополя записало оба ключа сопоставления, сравнение без учета регистра и режим нескольких целей");
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

            // Данные из связанных записей настраиваются внутри «Связи»: отдельных типов в списке нет.
            var typeChoice = new PropertyEditorDialog(repo, source) { MaxHeight = 600 };
            var typeClosed = typeChoice.ShowDialog<bool>(owner);
            Pump();
            var typeKeys = Combo(typeChoice, "TypeCombo").Items.OfType<FieldTypeItem>().Select(t => t.Type).ToList();
            Expect(typeKeys.Contains("relation") && !typeKeys.Contains("lookup") && !typeKeys.Contains("rollup"),
                "в списке типов одна «Связь»: данные из связанных записей — ее настройка");
            Click(typeChoice, typeChoice.FindControl<Button>("CancelBtn")!);
            Wait(() => typeClosed.IsCompleted, "окно нового свойства не закрылось");

            var dataDialog = new PropertyEditorDialog(repo, source, relation.Id.Value) { MaxHeight = 600 };
            var dataClosed = dataDialog.ShowDialog<bool>(owner);
            Pump();
            var addData = dataDialog.FindControl<Button>("AddRelationDataBtn")!;
            addData.BringIntoView();
            Click(dataDialog, addData);
            addData.BringIntoView();
            Click(dataDialog, addData);
            var rows = DataRows(dataDialog);
            Choose(RowCombo(rows[0], "Property"), "Key", sku.Id.Value.ToString());
            Expect(RowText(rows[0]) == "Клиент · Артикул" && SelectedAggregate(rows[0]) == "lookup",
                "новая строка показывает значения свойства и сама называет столбец «Связь · свойство»");
            Choose(RowCombo(rows[1], "Property"), "Key", balance.Id.Value.ToString());
            Choose(RowCombo(rows[1], "Kind"), "Aggregate", "sum");
            Expect(RowText(rows[1]) == "Клиент · сумма Баланс", "итог по числу назван по виду итога и свойству");
            Choose(RowCombo(rows[0], "Property"), "Key", "__title__");
            Expect(!Kinds(rows[0]).Contains("sum"), "у текстового свойства числовых итогов нет");
            Choose(RowCombo(rows[0], "Property"), "Key", sku.Id.Value.ToString());
            RowName(rows[1]).Text = "Общий баланс";
            Pump();
            using (var frame = dataDialog.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "relations-data-dialog.png"));
            Click(dataDialog, dataDialog.FindControl<Button>("SaveBtn")!);
            Wait(() => dataClosed.IsCompleted, "связь со столбцами данных не сохранилась");
            var lookup = repo.GetFields(source).Single(f => f.Name == "Клиент · Артикул");
            var rollup = repo.GetFields(source).Single(f => f.Name == "Общий баланс");
            var order = repo.GetColumnOrder(source);
            Expect(dataClosed.Result && lookup.FieldType == "lookup"
                && RelationComputedConfig.FromConfig(lookup.ConfigJson) == new RelationComputedConfig(relation.Id.Value, sku.Id.Value.ToString())
                && repo.GetFieldValues(record).GetValueOrDefault(lookup.Id!.Value) == "Б-02",
                "сохранение связи создало столбец значений свойства связанной записи");
            Expect(rollup.FieldType == "rollup" && RelationComputedConfig.FromConfig(rollup.ConfigJson)?.Aggregate == "sum"
                && repo.GetFieldValues(record).GetValueOrDefault(rollup.Id!.Value) == "20.25",
                "итог создан тем же сохранением и считает сырые числа связанных записей");
            Expect(order[lookup.Id.Value.ToString()] == order[relation.Id.Value.ToString()] + 1000
                && order[rollup.Id.Value.ToString()] == order[lookup.Id.Value.ToString()] + 1000,
                "столбцы данных встали сразу за связью в порядке строк окна");

            var childDialog = new PropertyEditorDialog(repo, source, rollup.Id.Value) { MaxHeight = 600 };
            var childClosed = childDialog.ShowDialog<bool>(owner);
            Pump();
            var childRows = DataRows(childDialog);
            Expect(childDialog.FindControl<TextBox>("NameBox")!.Text == "Клиент"
                && childDialog.FindControl<StackPanel>("RelationSection")!.IsVisible && childRows.Count == 2
                && RowText(childRows[1]) == "Общий баланс" && SelectedAggregate(childRows[1]) == "sum",
                "окно столбца данных открывает настройки его связи со всеми столбцами");
            var deleteRow = ((Border)childRows[1].GetType().GetProperty("Root")!.GetValue(childRows[1])!)
                .GetVisualDescendants().OfType<Button>().Single();
            deleteRow.BringIntoView();
            Click(childDialog, deleteRow);
            Click(childDialog, childDialog.FindControl<Button>("SaveBtn")!);
            Wait(() => childDialog.OwnedWindows.OfType<ConfirmDialog>().Any(), "удаление столбца данных не спросило подтверждения");
            var removal = childDialog.OwnedWindows.OfType<ConfirmDialog>().Single();
            Click(removal, removal.FindControl<Button>("ConfirmBtn")!);
            Wait(() => childClosed.IsCompleted, "связь без удаленного столбца не сохранилась");
            Expect(childClosed.Result && repo.GetFields(source).All(f => f.Id != rollup.Id)
                && repo.GetFields(source).Any(f => f.Id == lookup.Id),
                "удаленная строка удалила только свой столбец");

            // Условие выбора записей: строки окна, сохранение, переспрос пустого условия, список выбора.
            var state = Field(target, "Состояние", "select", JsonSerializer.Serialize(new { options = new[] { "Активен", "Закрыт" } }));
            var stateKey = state.Id!.Value.ToString();
            repo.SetFieldValue(a, state.Id.Value, "Активен");
            repo.SetFieldValue(b, state.Id.Value, "Закрыт");
            var conditioned = Field(source, "Активный клиент", "relation", legacyJson);
            repo.SetLinks(conditioned.Id!.Value, record, [b]);
            var conditionDialog = new PropertyEditorDialog(repo, source, conditioned.Id.Value) { MaxHeight = 600 };
            var conditionClosed = conditionDialog.ShowDialog<bool>(owner);
            Pump();
            Expect(conditionDialog.FindControl<TextBlock>("RelationConditionEmpty")!.IsVisible && ConditionRows(conditionDialog).Count == 0,
                "у связи без условия блок пишет, что выбрать можно любую запись");
            var addCondition = conditionDialog.FindControl<Button>("AddConditionBtn")!;
            addCondition.BringIntoView();
            Click(conditionDialog, addCondition);
            var conditionRow = ConditionRows(conditionDialog).Single();
            Choose(RowCombo(conditionRow, "Property"), "Key", stateKey);
            var choices = ((WrapPanel)conditionRow.GetType().GetProperty("Choices")!.GetValue(conditionRow)!).Children.OfType<CheckBox>().ToList();
            Expect(SelectedOp(conditionRow) == RelationCondition.Equal && choices.Select(c => c.Tag).SequenceEqual(["Активен", "Закрыт"])
                && !((TextBox)conditionRow.GetType().GetProperty("Text")!.GetValue(conditionRow)!).IsVisible
                && !conditionDialog.FindControl<TextBlock>("RelationConditionEmpty")!.IsVisible,
                "у списка условие «равно» предлагает пункты галочками, а не поле для текста");
            choices[0].IsChecked = true;
            ((Control)conditionRow.GetType().GetProperty("Root")!.GetValue(conditionRow)!).BringIntoView();
            Pump();
            using (var frame = conditionDialog.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "relations-condition-dialog.png"));
            Click(conditionDialog, conditionDialog.FindControl<Button>("SaveBtn")!);
            Wait(() => conditionClosed.IsCompleted, "связь с условием не сохранилась");
            var conditionCfg = RelationConfig.FromConfig(repo.GetFields(source).Single(f => f.Id == conditioned.Id).ConfigJson)!;
            Expect(conditionClosed.Result && conditionCfg.Where is { Count: 1 } savedWhere
                && savedWhere[0] == new RelationCondition(stateKey, RelationCondition.Equal, ["Активен"])
                && repo.GetLinkTargetIds(conditioned.Id.Value, record).SequenceEqual([b]),
                "сохранение записало условие, прежняя связь вне условия осталась");

            var emptyDialog = new PropertyEditorDialog(repo, source, conditioned.Id.Value) { MaxHeight = 600 };
            var emptyClosed = emptyDialog.ShowDialog<bool>(owner);
            Pump();
            var restoredRow = ConditionRows(emptyDialog).Single();
            var restoredChoices = ((WrapPanel)restoredRow.GetType().GetProperty("Choices")!.GetValue(restoredRow)!).Children.OfType<CheckBox>().ToList();
            Expect(SelectedKey(RowCombo(restoredRow, "Property")) == stateKey && SelectedOp(restoredRow) == RelationCondition.Equal
                && restoredChoices.Select(c => c.IsChecked == true).SequenceEqual([true, false]),
                "повторное открытие восстановило условие строкой с отмеченным пунктом");
            Choose(RowCombo(restoredRow, "Op"), "Op", RelationCondition.Empty);
            Expect(!((WrapPanel)restoredRow.GetType().GetProperty("Choices")!.GetValue(restoredRow)!).IsVisible
                && ((TextBlock)restoredRow.GetType().GetProperty("NoValue")!.GetValue(restoredRow)!).IsVisible,
                "у «пусто» значения нет, окно так и пишет");
            Click(emptyDialog, emptyDialog.FindControl<Button>("SaveBtn")!);
            Wait(() => emptyDialog.OwnedWindows.OfType<ConfirmDialog>().Any(), "условие, под которое ничего не подходит, не переспросило");
            var emptyAsk = emptyDialog.OwnedWindows.OfType<ConfirmDialog>().Single();
            Expect(emptyAsk.FindControl<TextBlock>("MessageLabel")!.Text!.Contains("ни одна запись коллекции «Клиенты»", StringComparison.Ordinal),
                "переспрос называет коллекцию, в которой ничего не подходит");
            Click(emptyAsk, emptyAsk.FindControl<Button>("CancelBtn")!);
            Wait(() => !emptyDialog.OwnedWindows.OfType<ConfirmDialog>().Any(), "переспрос не закрылся");
            Expect(!emptyClosed.IsCompleted
                && RelationConfig.FromConfig(repo.GetFields(source).Single(f => f.Id == conditioned.Id).ConfigJson)!.Where![0].Op == RelationCondition.Equal,
                "«Вернуться» оставляет окно открытым и прежнее условие в базе");
            Click(emptyDialog, emptyDialog.FindControl<Button>("CancelBtn")!);
            Wait(() => emptyClosed.IsCompleted, "окно условия не закрылось");

            var conditionPicker = new RelationSelectBox(repo, conditioned.Id.Value, conditionCfg, editing: true) { RecordId = record, Height = 38 };
            owner.Content = conditionPicker;
            Pump();
            conditionPicker.OpenSearch("");
            Wait(() => SearchReady(conditionPicker) && Shown(conditionPicker).Length == 2, "список связи с условием не загрузился");
            var conditionPopup = Private<Popup>(conditionPicker, "_popup");
            LayoutPopup(conditionPopup);
            var hint = Private<TextBlock>(conditionPicker, "_conditionHint");
            Expect(hint.IsVisible && hint.Text == "Только записи, где «Состояние» равно «Активен»",
                "под поиском написано условие выбора");
            var outsideMarks = conditionPopup.Child!.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.Text == "не подходит под условие выбора").ToList();
            Expect(Shown(conditionPicker).Select(VmId).SequenceEqual([b, a]) && outsideMarks.Count(t => t.IsVisible) == 1
                && (bool)Shown(conditionPicker)[0].GetType().GetProperty("Outside")!.GetValue(Shown(conditionPicker)[0])!,
                "в списке только подходящая запись и прежняя связь с пометкой в строке");
            var outsideVm = Shown(conditionPicker)[0];
            outsideVm.GetType().GetProperty("Checked")!.SetValue(outsideVm, false);
            Wait(() => owner.OwnedWindows.OfType<ConfirmDialog>().Any(), "снятие связи вне условия не переспросило");
            var lossAsk = owner.OwnedWindows.OfType<ConfirmDialog>().Single();
            Expect(lossAsk.FindControl<TextBlock>("MessageLabel")!.Text
                    == "Запись «Компания Б» не подходит под условие выбора: снятую связь выбрать снова будет нельзя.",
                "переспрос говорит, что снятую связь вернуть будет нельзя");
            Click(lossAsk, lossAsk.FindControl<Button>("CancelBtn")!);
            Wait(() => !owner.OwnedWindows.OfType<ConfirmDialog>().Any(), "переспрос снятия не закрылся");
            Expect(repo.GetLinkTargetIds(conditioned.Id.Value, record).SequenceEqual([b]), "отказ от снятия оставил связь");
            var matchingVm = Shown(conditionPicker).Single(item => VmId(item) == a);
            matchingVm.GetType().GetProperty("Checked")!.SetValue(matchingVm, true);
            Pump();
            Expect(repo.GetLinkTargetIds(conditioned.Id.Value, record).SequenceEqual([b, a]),
                "подходящая запись выбирается, прежняя связь вне условия остается");
            conditionPopup.IsOpen = false;

            var filterDisplay = typeof(TableView).GetMethod("DisplayValue", BindingFlags.NonPublic | BindingFlags.Static)!;
            Expect((string?)filterDisplay.Invoke(null, ["1.5", "rollup", ", ", null]) == "1,5"
                && (string?)filterDisplay.Invoke(null, ["1.5", "lookup", ", ", null]) == "1.5",
                "фильтр показывает итог как число с запятой, а текст lookup оставляет без преобразования");

            // Несколько пар: ИНН совпадает у обоих клиентов, артикул — только у второго, и то без учета
            // регистра и пробелов по краям.
            var orderSku = Field(source, "Артикул заказа");
            repo.SetFieldValue(record, orderSku.Id!.Value, "  б-02 ");
            var pairs = CreateAuto("Авто по паре");
            var pairsClosed = pairs.ShowDialog<bool>(owner);
            Pump();
            ConfigureAuto(pairs);
            var fold = pairs.FindControl<CheckBox>("RelationAutoFoldCheck")!;
            Expect(fold.IsChecked == true && fold.IsEnabled, "у нового правила по тексту сравнение без учета регистра включено");
            var addPair = pairs.FindControl<Button>("AddAutoPairBtn")!;
            addPair.BringIntoView();
            Click(pairs, addPair);
            var pairRow = AutoRows(pairs).Single();
            Choose(RowCombo(pairRow, "Source"), "Key", orderSku.Id.Value.ToString());
            Choose(RowCombo(pairRow, "Target"), "Key", sku.Id.Value.ToString());
            ((Control)pairRow.GetType().GetProperty("Root")!.GetValue(pairRow)!).BringIntoView();
            Pump();
            using (var frame = pairs.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, "relations-auto-pairs.png"));
            Click(pairs, pairs.FindControl<Button>("SaveBtn")!);
            Wait(() => pairs.OwnedWindows.OfType<ConfirmDialog>().Any(), "не появился результат подбора по двум парам");
            var pairsAsk = pairs.OwnedWindows.OfType<ConfirmDialog>().Single();
            Expect(pairsAsk.FindControl<TextBlock>("MessageLabel")!.Text!.Contains("Совпали только без учета регистра и пробелов — 1.", StringComparison.Ordinal),
                "итог подбора называет записи, совпавшие только без учета регистра");
            Click(pairsAsk, pairsAsk.FindControl<Button>("ConfirmBtn")!);
            Wait(() => pairsClosed.IsCompleted, "связь по двум парам не сохранилась");
            var pairsField = repo.GetFields(source).Single(f => f.Name == "Авто по паре");
            Expect(pairsClosed.Result && RelationConfig.FromConfig(pairsField.ConfigJson)?.Auto == new RelationAutoRule(
                    [new(sourceInn.Id.Value.ToString(), targetInn.Id.Value.ToString()), new(orderSku.Id.Value.ToString(), sku.Id.Value.ToString())], fold: true)
                && repo.GetLinkTargetIds(pairsField.Id!.Value, record).SequenceEqual([b]),
                "правило из двух пар сохранено, связана только запись, у которой совпали обе пары");

            var pairsAgain = new PropertyEditorDialog(repo, source, pairsField.Id.Value) { MaxHeight = 600 };
            var pairsAgainClosed = pairsAgain.ShowDialog<bool>(owner);
            Pump();
            var restoredPair = AutoRows(pairsAgain).Single();
            Expect(SelectedKey(RowCombo(restoredPair, "Source")) == orderSku.Id.Value.ToString()
                && SelectedKey(RowCombo(restoredPair, "Target")) == sku.Id.Value.ToString()
                && pairsAgain.FindControl<CheckBox>("RelationAutoFoldCheck")!.IsChecked == true,
                "повторное открытие восстановило вторую пару и флажок");
            var addAgain = pairsAgain.FindControl<Button>("AddAutoPairBtn")!;
            addAgain.BringIntoView();
            Click(pairsAgain, addAgain);
            var third = AutoRows(pairsAgain)[1];
            Choose(RowCombo(third, "Source"), "Key", sourceInn.Id.Value.ToString());
            Choose(RowCombo(third, "Target"), "Key", targetInn.Id.Value.ToString());
            Expect(!addAgain.IsVisible, "на третьей паре кнопка «+ Еще пара» скрыта");
            Click(pairsAgain, pairsAgain.FindControl<Button>("SaveBtn")!);
            Expect(!pairsAgainClosed.IsCompleted && pairsAgain.FindControl<NoticeBanner>("NoticeBanner")!.Message == "Пара 3 повторяет пару 1.",
                "повтор пары не сохраняется и назван номером");
            Click(pairsAgain, pairsAgain.FindControl<Button>("CancelBtn")!);
            Wait(() => pairsAgainClosed.IsCompleted, "окно правила из двух пар не закрылось");

            // Правило более новой версии программы: строки неактивны, переименование его не меняет.
            var newerJson = $"{{\"targetCollectionId\":{target},\"single\":false,\"separator\":\"comma\"," +
                            "\"auto\":{\"sourceField\":\"__title__\",\"targetField\":\"2147483647\",\"format\":3,\"pairs\":[]}}";
            manager.Context.Database.ExecuteSqlRaw("UPDATE db_fields SET ConfigJson = {0} WHERE Id = {1}", newerJson, pairsField.Id.Value);
            var newer = new PropertyEditorDialog(repo, source, pairsField.Id.Value) { MaxHeight = 600 };
            var newerClosed = newer.ShowDialog<bool>(owner);
            Pump();
            Expect(newer.FindControl<NoticeBanner>("RelationAutoNote")!.Message?.StartsWith("Правило автоподбора создано более новой версией", StringComparison.Ordinal) == true
                && !Combo(newer, "RelationAutoSourceCombo").IsEnabled && !newer.FindControl<Button>("AddAutoPairBtn")!.IsVisible,
                "правило более новой версии показано плашкой, пары не меняются");
            newer.FindControl<TextBox>("NameBox")!.Text = "Авто новой версии";
            Click(newer, newer.FindControl<Button>("SaveBtn")!);
            Wait(() => newerClosed.IsCompleted, "переименование связи с правилом новой версии не сохранилось");
            var renamedNewer = repo.GetFields(source).Single(f => f.Id == pairsField.Id);
            Expect(newerClosed.Result && renamedNewer.Name == "Авто новой версии"
                && RelationConfig.FromConfig(renamedNewer.ConfigJson)?.Auto?.State == RelationAutoState.Newer
                && repo.GetLinkTargetIds(pairsField.Id.Value, record).SequenceEqual([b]),
                "переименование оставило правило новой версии и связи как есть");

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
            var autoHint = Private<TextBlock>(autoPicker, "_autoHint");
            Expect(autoHint.IsVisible && autoHint.Text == "Подбирается автоматически: «ИНН заказа».",
                "список автосвязи пишет, по каким свойствам она подбирается");
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
            static bool Throws<T>(Action action) where T : Exception
            {
                try { action(); return false; }
                catch (T) { return true; }
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
    private static List<object> DataRows(PropertyEditorDialog dialog) =>
        Private<System.Collections.IEnumerable>(dialog, "_relationData").Cast<object>().ToList();
    private static List<object> AutoRows(PropertyEditorDialog dialog) =>
        Private<System.Collections.IEnumerable>(dialog, "_autoExtra").Cast<object>().ToList();
    private static List<object> ConditionRows(PropertyEditorDialog dialog) =>
        Private<System.Collections.IEnumerable>(dialog, "_conditionRows").Cast<object>().ToList();
    private static string? SelectedOp(object row) =>
        RowCombo(row, "Op").SelectedItem?.GetType().GetProperty("Op")?.GetValue(RowCombo(row, "Op").SelectedItem) as string;
    private static int VmId(object item) => (int)item.GetType().GetProperty("Id")!.GetValue(item)!;
    private static ComboBox RowCombo(object row, string name) => (ComboBox)row.GetType().GetProperty(name)!.GetValue(row)!;
    private static TextBox RowName(object row) => (TextBox)row.GetType().GetProperty("Name")!.GetValue(row)!;
    private static string? RowText(object row) => RowName(row).Text;
    private static string? SelectedAggregate(object row) =>
        RowCombo(row, "Kind").SelectedItem?.GetType().GetProperty("Aggregate")?.GetValue(RowCombo(row, "Kind").SelectedItem) as string;
    private static List<string> Kinds(object row) => RowCombo(row, "Kind").Items.Cast<object>()
        .Select(k => (string)k.GetType().GetProperty("Aggregate")!.GetValue(k)!).ToList();
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
