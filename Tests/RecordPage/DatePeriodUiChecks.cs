using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.DrawnGrid;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Database.Repositories;
using SmartBase.ViewModels.ConstructorDb;
using SmartBase.Views.ConstructorDb;

/// <summary>Настройка периода через настоящее окно и ручной/автоматический расчет в открытых видах.</summary>
internal static class DatePeriodUiChecks
{
    public static int Run(string output)
    {
        var path = Path.Combine(output, $"date-period-ui-{Guid.NewGuid():N}.db");
        DatabaseManager? manager = null;
        Window? owner = null;
        var checks = 0;
        var failures = 0;
        try
        {
            manager = DatabaseManager.Open(9458, "Периоды", path);
            var repository = manager.Constructor;
            var empty = repository.AddCollection(new DbCollection { Name = "Без обычной даты" }).Id!.Value;
            repository.AddField(new DbField { CollectionId = empty, Name = "Создано", FieldType = "created_at" });
            owner = new Window { Width = 1300, Height = 850 };
            owner.Show();
            // Headless рисует Popup в OverlayLayer своего окна. Обычный нативный popup
            // может выходить за его границы, а overlay обрезается: даем списку место внутри
            // реального input/render root, чтобы проверять попадание мыши, а не клик мимо окна.
            var unavailable = new PropertyEditorDialog(repository, empty)
            {
                SizeToContent = SizeToContent.Manual, Height = 700, MinHeight = 700, MaxHeight = 800,
            };
            var unavailableClosed = unavailable.ShowDialog<bool>(owner);
            Pump();
            var unavailableTypes = Find<ComboBox>(unavailable, "TypeCombo");
            var blocked = unavailableTypes.Items.OfType<FieldTypeItem>().Single(type => type.Type == "date_period");
            Expect(blocked.Label == "Дата период времени" && !blocked.IsEnabled && blocked.Hint?.Contains("Дата") == true,
                "пятый вычисляемый тип объясняет недоступность без обычной даты");
            unavailableTypes.IsDropDownOpen = true;
            Pump();
            var blockedIndex = unavailableTypes.Items.IndexOf(blocked);
            var typePopup = unavailableTypes.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Single(popup => popup.IsOpen);
            var typeScroll = typePopup.Child!.GetVisualDescendants().OfType<ScrollViewer>().First();
            Console.WriteLine($"  Прокрутка списка типов до конца: extent={typeScroll.Extent}, "
                + $"viewport={typeScroll.Viewport}, offset={typeScroll.Offset}, panel={unavailableTypes.ItemsPanelRoot?.Bounds}");
            // Попап сначала получает viewport. Проверяем весь последний пункт в его
            // пределах после настоящей прокрутки до конца, прежде чем нажимать мышью.
            Wait(() =>
            {
                typeScroll.ScrollToEnd();
                typePopup.Child.UpdateLayout();
                Pump();
                if (unavailableTypes.ContainerFromIndex(blockedIndex) is not ComboBoxItem item) return false;
                var position = item.TranslatePoint(default, typeScroll);
                return position is { } point && point.Y >= -1
                    && point.Y + item.Bounds.Height <= typeScroll.Viewport.Height + 1;
            }, "последний пункт типов не реализовался после прокрутки выпадающего списка");
            var blockedContainer = unavailableTypes.ContainerFromIndex(blockedIndex) as ComboBoxItem;
            Console.WriteLine($"  Диагностика списка типов: open={unavailableTypes.IsDropDownOpen}, "
                + $"items={unavailableTypes.ItemCount}, index={blockedIndex}, panel={unavailableTypes.ItemsPanelRoot?.GetType().Name}, "
                + $"container={blockedContainer?.GetType().Name ?? "null"}, bounds={unavailableTypes.Bounds}, "
                + $"visible={unavailableTypes.IsEffectivelyVisible}, enabled={unavailableTypes.IsEffectivelyEnabled}");
            foreach (var item in unavailableTypes.GetRealizedContainers().OfType<ComboBoxItem>())
                Console.WriteLine($"    item index={unavailableTypes.IndexFromContainer(item)}, "
                    + $"content={item.Content?.GetType().Name}:{(item.Content as FieldTypeItem)?.Type}, "
                    + $"context={item.DataContext?.GetType().Name}:{(item.DataContext as FieldTypeItem)?.Type}, "
                    + $"enabled={item.IsEnabled}, effective={item.IsEffectivelyEnabled}, "
                    + $"hasValue={item.IsSet(InputElement.IsEnabledProperty)}, bounds={item.Bounds}");
            foreach (var popup in unavailableTypes.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>())
                Console.WriteLine($"    popup open={popup.IsOpen}, child={popup.Child?.GetType().Name}, bounds={popup.Child?.Bounds}");
            Expect(blockedContainer is { IsEnabled: false },
                "запрет типа применен к настоящему пункту выпадающего списка");
            using (var image = unavailable.CaptureRenderedFrame()) image?.Save(Path.Combine(output, "date-period-type-menu.png"));
            var inputRoot = TopLevel.GetTopLevel(blockedContainer!)!;
            var itemOrigin = blockedContainer!.TranslatePoint(default, inputRoot)!.Value;
            var blockedPoint = blockedContainer!.TranslatePoint(new Point(blockedContainer.Bounds.Width / 2,
                blockedContainer.Bounds.Height / 2), inputRoot)!.Value;
            var withinInputRoot = itemOrigin.X >= 0 && itemOrigin.Y >= 0
                && itemOrigin.X + blockedContainer.Bounds.Width <= inputRoot.ClientSize.Width + 1
                && itemOrigin.Y + blockedContainer.Bounds.Height <= inputRoot.ClientSize.Height + 1
                && blockedPoint.X >= 0 && blockedPoint.X < inputRoot.ClientSize.Width
                && blockedPoint.Y >= 0 && blockedPoint.Y < inputRoot.ClientSize.Height;
            Expect(withinInputRoot, "последний пункт и точка клика находятся внутри настоящего окна ввода и отрисовки");
            if (!withinInputRoot)
                throw new InvalidOperationException($"Проверка клика недействительна: root={inputRoot.ClientSize}, "
                    + $"item={itemOrigin}/{blockedContainer.Bounds.Size}, pointer={blockedPoint}");
            inputRoot.MouseMove(blockedPoint);
            inputRoot.MouseDown(blockedPoint, MouseButton.Left);
            inputRoot.MouseUp(blockedPoint, MouseButton.Left);
            Pump();
            Expect(!ReferenceEquals(unavailableTypes.SelectedItem, blocked),
                "настоящий клик по недоступному пункту не выбирает тип");
            unavailableTypes.IsDropDownOpen = false;
            Click(Find<Button>(unavailable, "CancelBtn"));
            Wait(() => unavailableClosed.IsCompleted, "не закрылось окно без даты");

            var collection = repository.AddCollection(new DbCollection { Name = "Рабочие периоды" }).Id!.Value;
            var start = repository.AddField(new DbField { CollectionId = collection, Name = "Начало", FieldType = "date" }).Id!.Value;
            var end = repository.AddField(new DbField { CollectionId = collection, Name = "Окончание", FieldType = "date" }).Id!.Value;
            repository.AddField(new DbField { CollectionId = collection, Name = "Изменено", FieldType = "updated_at" });
            repository.AddField(new DbField { CollectionId = empty, Name = "Чужая дата", FieldType = "date" });

            var dialog = new PropertyEditorDialog(repository, collection) { MaxHeight = 800 };
            var closed = dialog.ShowDialog<bool>(owner);
            Pump();
            SelectPeriod(dialog);
            var source = Find<ComboBox>(dialog, "DatePeriodStartCombo");
            var ending = Find<ComboBox>(dialog, "DatePeriodEndCombo");
            var endMode = Find<ComboBox>(dialog, "DatePeriodEndModeCombo");
            Expect(endMode.SelectedIndex == 1 && ending.IsVisible,
                "новый период по умолчанию считает до другого свойства даты");
            Expect(source.Items.OfType<PropertyEditorDialog.DatePeriodFieldItem>().Select(field => field.Id).Order()
                .SequenceEqual(new[] { start, end }.Order()), "источники содержат только обычные даты этой коллекции");
            Expect(Find<ToggleSwitch>(dialog, "DatePeriodAutomaticToggle").IsChecked == true
                && Find<TextBox>(dialog, "DatePeriodDayStart").Text == "00:00"
                && Find<TextBox>(dialog, "DatePeriodDayEnd").Text == "24:00"
                && Days(dialog).All(day => day.IsChecked == true), "стандартный расчет учитывает все дни целиком автоматически");
            Find<TextBox>(dialog, "NameBox").Text = "Рабочее время";
            source.SelectedItem = source.Items.OfType<PropertyEditorDialog.DatePeriodFieldItem>().Single(field => field.Id == start);
            Click(Find<Button>(dialog, "SaveBtn"));
            Expect(!closed.IsCompleted && Find<NoticeBanner>(dialog, "NoticeBanner").Message?.Contains("даты окончания") == true
                && !repository.GetFields(collection).Any(property => property.FieldType == "date_period"),
                "без выбранного окончания новый период не подменяет его текущим временем");
            ending.SelectedItem = ending.Items.OfType<PropertyEditorDialog.DatePeriodFieldItem>().Single(field => field.Id == end);
            endMode.SelectedIndex = 0;
            Pump();
            Expect(!ending.IsVisible, "режим текущего времени скрывает ненужный выбор конечной даты");
            endMode.SelectedIndex = 1;
            Pump();
            Expect(ending.IsVisible && ending.SelectedItem is PropertyEditorDialog.DatePeriodFieldItem { Id: var keptEnd } && keptEnd == end,
                "возврат к другой дате сохраняет выбранное поле окончания");
            Find<ToggleSwitch>(dialog, "DatePeriodAutomaticToggle").IsChecked = false;
            Find<TextBox>(dialog, "DatePeriodDayStart").Text = "18:00";
            Find<TextBox>(dialog, "DatePeriodDayEnd").Text = "09:00";
            Click(Find<Button>(dialog, "SaveBtn"));
            Expect(!closed.IsCompleted && Find<NoticeBanner>(dialog, "NoticeBanner").IsVisible,
                "обратный суточный интервал не сохраняется и объясняется в окне");
            Find<TextBox>(dialog, "DatePeriodDayStart").Text = "09:00";
            Find<TextBox>(dialog, "DatePeriodDayEnd").Text = "18:00";
            foreach (var day in Days(dialog)) day.IsChecked = false;
            Click(Find<Button>(dialog, "SaveBtn"));
            Expect(!closed.IsCompleted && Find<NoticeBanner>(dialog, "NoticeBanner").Message?.Contains("день недели") == true,
                "пустая неделя не создает бессмысленное свойство");
            var weekdays = Days(dialog);
            for (var i = 0; i < weekdays.Length; i++) weekdays[i].IsChecked = i < 5;
            Click(Find<Button>(dialog, "SaveBtn"));
            Wait(() => closed.IsCompleted, "настройки периода не сохранились");
            var field = repository.GetFields(collection).Single(property => property.Name == "Рабочее время");
            var config = DatePeriodConfig.FromConfig(field.ConfigJson);
            Expect(closed.Result && config is { Automatic: false, Weekdays: 31, DayStart: "09:00", DayEnd: "18:00" }
                && config.StartFieldId == start && config.EndFieldId == end,
                "сохранение переносит источник, конец, неделю, часы и фиксированный режим в базу");

            var reopened = new PropertyEditorDialog(repository, collection, field.Id) { MaxHeight = 800 };
            var reopenedClosed = reopened.ShowDialog<bool>(owner);
            Pump();
            Expect(Find<ComboBox>(reopened, "DatePeriodStartCombo").SelectedItem is PropertyEditorDialog.DatePeriodFieldItem { Id: var sourceId }
                && sourceId == start && Find<ComboBox>(reopened, "DatePeriodEndModeCombo").SelectedIndex == 1
                && Find<ComboBox>(reopened, "DatePeriodEndCombo").SelectedItem is PropertyEditorDialog.DatePeriodFieldItem { Id: var endId }
                && endId == end && Find<TextBox>(reopened, "DatePeriodDayStart").Text == "09:00"
                && Find<TextBox>(reopened, "DatePeriodDayEnd").Text == "18:00"
                && Find<ToggleSwitch>(reopened, "DatePeriodAutomaticToggle").IsChecked == false
                && Days(reopened).Select((day, index) => day.IsChecked == (index < 5)).All(value => value),
                "повторное открытие восстанавливает все параметры");
            Find<TextBox>(reopened, "DatePeriodDayEnd").Text = "24:00";
            Click(Find<Button>(reopened, "CancelBtn"));
            Wait(() => reopenedClosed.IsCompleted, "не закрылось редактирование периода");
            Expect(DatePeriodConfig.FromConfig(repository.GetFields(collection).Single(property => property.Id == field.Id).ConfigJson)?.DayEnd == "18:00",
                "отмена не сохраняет черновой интервал");

            var now = new DateTime(2026, 10, 27, 10, 0, 0);
            repository.DatePeriodClock = () => now;
            var currentDialog = new PropertyEditorDialog(repository, collection) { MaxHeight = 800 };
            var currentClosed = currentDialog.ShowDialog<bool>(owner);
            Pump();
            SelectPeriod(currentDialog);
            Find<TextBox>(currentDialog, "NameBox").Text = "До настоящего времени";
            var currentSource = Find<ComboBox>(currentDialog, "DatePeriodStartCombo");
            currentSource.SelectedItem = currentSource.Items.OfType<PropertyEditorDialog.DatePeriodFieldItem>().Single(field => field.Id == start);
            // В скрытом списке остается другое поле: сохранение текущего режима не должно
            // случайно использовать его вместо часов. Проверяем реальный путь окна.
            var currentEnd = Find<ComboBox>(currentDialog, "DatePeriodEndCombo");
            currentEnd.SelectedItem = currentEnd.Items.OfType<PropertyEditorDialog.DatePeriodFieldItem>().Single(field => field.Id == end);
            Find<ComboBox>(currentDialog, "DatePeriodEndModeCombo").SelectedIndex = 0;
            Find<TextBox>(currentDialog, "DatePeriodDayStart").Text = "09:00";
            Find<TextBox>(currentDialog, "DatePeriodDayEnd").Text = "18:00";
            var currentDays = Days(currentDialog);
            for (var i = 0; i < currentDays.Length; i++) currentDays[i].IsChecked = i < 5;
            Click(Find<Button>(currentDialog, "SaveBtn"));
            Wait(() => currentClosed.IsCompleted, "не сохранился режим текущего времени");
            var automatic = repository.GetFields(collection).Single(property => property.Name == "До настоящего времени");
            var automaticId = automatic.Id!.Value;
            var automaticKey = automaticId.ToString();
            Expect(currentClosed.Result && DatePeriodConfig.FromConfig(automatic.ConfigJson)
                is { Automatic: true, EndFieldId: null, StartFieldId: var currentStart } && currentStart == start,
                "выбор текущего времени сохраняется без ссылки на скрытую конечную дату");
            var currentReopened = new PropertyEditorDialog(repository, collection, automatic.Id) { MaxHeight = 800 };
            var currentReopenedClosed = currentReopened.ShowDialog<bool>(owner);
            Pump();
            Expect(Find<ComboBox>(currentReopened, "DatePeriodEndModeCombo").SelectedIndex == 0
                && !Find<ComboBox>(currentReopened, "DatePeriodEndCombo").IsVisible,
                "повторное открытие существующего расчета до текущего времени не применяет новое умолчание");
            Click(Find<Button>(currentReopened, "SaveBtn"));
            Wait(() => currentReopenedClosed.IsCompleted, "не закрылось сохранение текущего режима");
            Expect(DatePeriodConfig.FromConfig(repository.GetFields(collection).Single(property => property.Id == automatic.Id).ConfigJson)
                is { EndFieldId: null, Automatic: true },
                "повторное сохранение текущего режима не заменяет его другим полем даты");
            var record = repository.AddRecord(new DbRecord { CollectionId = collection, Title = "Первый" }).Id!.Value;
            var second = repository.AddRecord(new DbRecord { CollectionId = collection, Title = "Второй" }).Id!.Value;
            foreach (var id in new[] { record, second })
            {
                repository.SetFieldValue(id, start, "2026-10-26T09:00");
                repository.SetFieldValue(id, end, "2026-10-27T10:00");
            }
            repository.RecalculateDatePeriods([record, second], [field.Id!.Value]);
            var page = new RecordPageView();
            var table = new TableView();
            // Тот же мост, что у MainWindow: измененная из таблицы показанная запись
            // перечитывается в панели, в том числе после пакетного пересчета.
            table.RecordValueEdited += (_, id) => { if (id == record && page.Shows(repository)) page.ReloadFromDb(); };
            var host = new Grid { ColumnDefinitions = new ColumnDefinitions("*,560") };
            host.Children.Add(table);
            Grid.SetColumn(page, 1);
            host.Children.Add(page);
            owner.Content = host;
            page.Show(repository, record, collection);
            table.Load(repository, collection);
            Pump();
            var grid = table.FindControl<SmartBase.Views.ConstructorDb.DrawnGrid.GridPanel>("RecordsGrid")!;
            Wait(() => grid.Rows?.FindCachedByRecordId(record) is not null, "строка таблицы не загрузилась");
            var properties = Find<PropertyList>(page, "PropertiesHost");
            Expect(Display(field.Name) == "10 ч" && Display(automatic.Name) == "10 ч",
                "страница показывает длительность в часах вместо сырых секунд");
            var fixedCard = properties.Cards.ToList().FindIndex(card => card.Name == field.Name);
            properties.ScrollIntoView(fixedCard);
            Pump();
            properties.OpenEditor(fixedCard);
            Expect(properties.CurrentEditor is TextBox { IsReadOnly: true, Text: "10 ч" },
                "рассчитанное значение открывается только для чтения");
            owner.KeyTextInput("999");
            Pump();
            Expect(repository.GetFieldValues(record).GetValueOrDefault(field.Id.Value) == "36000",
                "набор в вычисленное свойство не меняет сохраненный результат");

            grid.ScrollToCell(0, automaticKey);
            Wait(() => CachedPeriod("36000"), "период не загрузился перед проверкой невидимой колонки");
            grid.Surface.OffsetX = 0;
            Pump();
            Expect(grid.Surface.Columns.Left(grid.Surface.Columns.IndexOf(automaticKey)) >= grid.Surface.ViewportWidth,
                "текущий период находится за горизонтальной границей окна перед проверкой обновления");
            now = now.AddHours(1);
            Await(InvokeTask(page, "RefreshDatePeriodsForTimeAsync"), "не обновился период в панели");
            Await(InvokeTask(table, "RefreshDatePeriodsForTimeAsync"), "не завершилась проверка невидимого периода");
            Expect(CachedPeriod("36000") && Display(automatic.Name) == "11 ч",
                "минутное обновление таблицы пропускает невидимую колонку, а открытая страница считает свой период");
            grid.ScrollToCell(0, automaticKey);
            Pump();
            Wait(() => CachedPeriod("39600"), "прокрутка к периоду не обновила устаревшую ячейку без ожидания минутного таймера");
            Expect(CachedPeriod("39600"),
                "возвращение к колонке само обновляет период без ручного вызова обновления и ожидания минуты");
            Expect(Display(automatic.Name) == "11 ч" && Display(field.Name) == "10 ч",
                "минутное обновление меняет автоматический период и сохраняет фиксированный");
            Expect(grid.Rows?.FindCachedByRecordId(record)?[automatic.Id!.Value.ToString()] == "39600"
                && grid.Rows?.FindCachedByRecordId(record)?[field.Id.Value.ToString()] == "36000",
                "обновление таблицы заменяет только автоматическое значение в кеше видимой строки");
            Expect(properties.CurrentEditor is TextBox { IsReadOnly: true, Text: "10 ч" },
                "обновление часов не закрывает редактор другого свойства");

            var fixedRow = (PropertyRowVm)properties.Cards.Single(card => card.Name == field.Name).Tag!;
            var autoRow = (PropertyRowVm)properties.Cards.Single(card => card.Name == automatic.Name).Tag!;
            var fixedMenu = (ContextMenu)Invoke(page, "BuildPropertyMenu", fixedRow)!;
            var autoMenu = (ContextMenu)Invoke(page, "BuildPropertyMenu", autoRow)!;
            Expect(MenuAction(fixedMenu) is not null && MenuAction(autoMenu) is null,
                "ручной пересчет предлагается только фиксированному периоду");
            repository.SetFieldValue(record, end, "2026-10-27T12:00");
            Click(MenuAction(fixedMenu)!);
            Wait(() => repository.GetFieldValues(record).GetValueOrDefault(field.Id.Value) == "43200",
                "пересчет свойства не обновил текущую запись");
            Expect(repository.GetFieldValues(second).GetValueOrDefault(field.Id.Value) == "36000",
                "одиночный пересчет не изменил соседнюю запись");

            repository.SetFieldValue(record, end, "2026-10-27T13:00");
            repository.SetFieldValue(second, end, "2026-10-27T11:00");
            Wait(() => grid.Rows?.FindCachedByRecordId(record) is not null, "строка таблицы не загрузилась");
            table.SelectRecords([record, second]);
            var rowMenu = (ContextMenu)Invoke(table, "BuildRowContextMenu", (Func<TableRowVm?>)(() => grid.Rows?.FindCachedByRecordId(record)))!;
            Expect(MenuAction(rowMenu) is not null, "меню строки предлагает пересчет выбранных записей");
            Click(MenuAction(rowMenu)!);
            Wait(() => repository.GetFieldValues(record).GetValueOrDefault(field.Id.Value) == "46800"
                && repository.GetFieldValues(second).GetValueOrDefault(field.Id.Value) == "39600",
                "пересчет строки не применился ко всему выделению");
            Wait(() => Display(field.Name) == "13 ч", "событие пакетного пересчета не обновило открытую панель записи");
            Expect(!manager.Context.DbFieldValues.Any(value => value.FieldId == automatic.Id),
                "показ и обновление автоматического периода не записали его результат в базу");

            grid.ScrollToCell(0, automaticKey);
            Wait(() => CachedPeriod("39600"), "период не загрузился в видимую колонку перед сменой правила");
            var beforeRuleChange = grid.Rows;
            ChangeRule(new DatePeriodConfig(start, end, Weekdays: 31, DayStart: "09:00", DayEnd: "18:00"));
            Wait(() => CachedPeriod("46800"), "после перехода от текущего времени к другому полю кеш сохранил старый период");
            Expect(!ReferenceEquals(beforeRuleChange, grid.Rows) && grid.Rows?.Source is PlainRowSource,
                "изменение правила перечитывает страницы без материализации полного списка записей");

            var otherEnd = repository.AddField(new DbField
            {
                CollectionId = collection, Name = "Другое окончание", FieldType = "date",
            }).Id!.Value;
            repository.SetFieldValue(record, otherEnd, "2026-10-27T15:00");
            table.AddColumnForField(otherEnd);
            ChangeRule(new DatePeriodConfig(start, otherEnd, Weekdays: 31, DayStart: "09:00", DayEnd: "18:00"));
            Wait(() => CachedPeriod("54000"), "новое конечное поле не заменило старое значение в открытой таблице");
            Expect(CachedPeriod("54000"), "смена конечного свойства сразу отражается в той же видимой ячейке");

            ChangeRule(new DatePeriodConfig(start, otherEnd, Weekdays: 96, DayStart: "09:00", DayEnd: "18:00"));
            Wait(() => CachedPeriod("0"), "смена дней недели не обновила значение видимой ячейки");
            Expect(CachedPeriod("0"), "смена расписания не оставляет кеш от прежней рабочей недели");
            ChangeRule(new DatePeriodConfig(start, otherEnd, Automatic: false, Weekdays: 31, DayStart: "09:00", DayEnd: "18:00"));
            Wait(() => CachedPeriod(""), "переход к фиксированному режиму сохранил старое автоматическое значение");
            Expect(CachedPeriod(""), "фиксированный режим существующей записи не выдает автоматический результат за снимок");
            Await(table.RecalculateDatePeriodsAsync([record], [automaticId]), "не пересчитался новый фиксированный режим");
            Wait(() => CachedPeriod("54000"), "снимок после ручного пересчета не появился в таблице");
            Expect(CachedPeriod("54000"), "пересчет нового фиксированного режима показывает сохраненный снимок");

            ChangeRule(new DatePeriodConfig(start, Weekdays: 31, DayStart: "09:00", DayEnd: "18:00"));
            Wait(() => CachedPeriod("39600"), "переход обратно к текущему времени оставил фиксированный снимок");
            Expect(CachedPeriod("39600"), "возврат из фиксированного режима к текущему времени заново считает видимую ячейку");
            var unchangedRows = grid.Rows;
            var renamed = repository.GetFields(collection).Single(property => property.Id == automaticId);
            renamed.Name = "Переименованный период";
            repository.UpdateField(renamed);
            table.RebuildColumn(automaticKey);
            Pump();
            Expect(ReferenceEquals(unchangedRows, grid.Rows) && CachedPeriod("39600"),
                "переименование периода сохраняет теплые страницы и правильное значение");

            var sourceChanged = repository.GetFields(collection).Single(property => property.Id == start);
            sourceChanged.FieldType = "text";
            repository.UpdateField(sourceChanged);
            table.RebuildColumn(start.ToString());
            Wait(() => CachedPeriod(""), "смена типа даты-источника оставила рассчитанное значение в кеше");
            Expect(CachedPeriod(""), "период очищается в открытой таблице после смены типа источника даты");

            using (var image = owner.CaptureRenderedFrame()) image?.Save(Path.Combine(output, "date-period-ui.png"));

            owner.Content = null;
            Pump();
            Expect(!Field<DispatcherTimer>(table, "_datePeriodTimer").IsEnabled
                && !Field<DispatcherTimer>(table, "_datePeriodViewportTimer").IsEnabled
                && !Field<DispatcherTimer>(page, "_datePeriodTimer").IsEnabled,
                "после снятия видов минутные таймеры и отложенное обновление окна остановлены");
            CheckLiveQueries(repository, owner, Expect);
            return failures == 0 ? 0 : 1;

            string Display(string name)
            {
                var card = properties.Cards.Single(item => item.Name == name);
                return card.Display?.Invoke() ?? card.Value();
            }

            bool CachedPeriod(string expected) => grid.Rows?.IsCellLoaded(0, automaticId) == true
                && grid.Rows.FindCachedByRecordId(record)?[automaticKey] == expected;

            void ChangeRule(DatePeriodConfig rule)
            {
                var changed = repository.GetFields(collection).Single(property => property.Id == automaticId);
                changed.ConfigJson = rule.ToConfigJson(changed.ConfigJson);
                repository.UpdateField(changed);
                table.RebuildColumn(automaticKey);
                Pump();
            }
        }
        catch (Exception error)
        {
            Expect(false, error.ToString());
            return 1;
        }
        finally
        {
            owner?.Close();
            Pump();
            manager?.Dispose();
            AppDbContext.ResetPool(path);
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
            Console.WriteLine($"Периоды времени UI: проверок {checks}, провалов {failures}.");
        }

        void Expect(bool condition, string text)
        {
            checks++;
            if (!condition) failures++;
            Console.WriteLine($"  {(condition ? "OK" : "ПРОВАЛ")}: {text}");
        }
    }

    /// <summary>Меняется сам набор строк, включая прежде невидимые и пустой вид:
    /// одного обновления уже нарисованных значений для текущего периода недостаточно.</summary>
    private static void CheckLiveQueries(ConstructorRepository repository, Window owner, Action<bool, string> expect)
    {
        var savedClock = repository.DatePeriodClock;
        var now = new DateTime(2026, 10, 28, 9, 0, 0);
        repository.DatePeriodClock = () => now;
        try
        {
            var collection = repository.AddCollection(new DbCollection { Name = "Текущий период в отборе" }).Id!.Value;
            var start = repository.AddField(new DbField
            {
                CollectionId = collection, Name = "Начало", FieldType = "date",
            }).Id!.Value;
            var period = repository.AddField(new DbField
            {
                CollectionId = collection, Name = "Прошло времени", FieldType = "date_period", IsSystem = true,
                ConfigJson = new DatePeriodConfig(start).ToConfigJson(),
            }).Id!.Value;
            var first = repository.AddRecord(new DbRecord { CollectionId = collection, Title = "Первая" }).Id!.Value;
            var second = repository.AddRecord(new DbRecord { CollectionId = collection, Title = "Вторая" }).Id!.Value;
            repository.SetFieldValue(first, start, "2026-10-28T07:00");
            repository.SetFieldValue(second, start, "2026-10-28T07:01");
            var key = period.ToString();
            var table = new TableView();
            owner.Content = table;
            table.Load(repository, collection);
            Pump();
            var grid = Find<SmartBase.Views.ConstructorDb.DrawnGrid.GridPanel>(table, "RecordsGrid");
            Await(grid.QueryTask, "обычный вид периода не открылся");
            grid.ScrollToCell(0, key);
            Wait(() => grid.Rows?.FindCachedByRecordId(first)?[key] == "7200",
                "период первой записи не загрузился в обычном виде");
            var plainRows = grid.Rows;
            Await(InvokeTask(table, "RefreshDatePeriodsForTimeAsync"), "не обновился обычный вид периода");
            expect(ReferenceEquals(plainRows, grid.Rows) && grid.Rows?.Source is PlainRowSource,
                "обновление часов без фильтра и сортировки сохраняет постраничный источник и его строки");

            // Тот же вход, что у подтверждения окна фильтра: фасад сохраняет выбор,
            // ApplyFilters передает его копию сетке и запускает настоящий фоновый запрос.
            var filters = Field<Dictionary<string, HashSet<string>>>(table, "_filters");
            filters[key] = ["7200"];
            Invoke(table, "ApplyFilters");
            Await(grid.QueryTask, "не применился начальный отбор по текущему периоду");
            Wait(() => HasIds(first), "первый отбор должен выбрать только запись от 07:00");
            expect(HasIds(first), "при 09:00 отбор по двум часам показывает запись от 07:00");

            now = now.AddMinutes(1);
            RefreshQuery("не обновился отбор периода после смены минуты");
            Wait(() => HasIds(second), "после 09:01 прежнее совпадение осталось или новое не появилось");
            grid.ScrollToCell(0, key);
            Wait(() => grid.Rows?.FindCachedByRecordId(second)?[key] == "7200",
                "новое совпадение не показывает значение, соответствующее отбору");
            expect(HasIds(second) && grid.Rows?.FindCachedByRecordId(second)?[key] == "7200",
                "следующая минута убирает прежнее совпадение и показывает прежде невидимую подходящую запись");

            var sameMinuteRows = grid.Rows;
            RefreshQuery("повторная проверка той же минуты не завершилась");
            expect(ReferenceEquals(sameMinuteRows, grid.Rows) && HasIds(second),
                "повторное обновление в той же минуте не пересоздает отфильтрованный список строк");

            repository.SaveColumnDisplay(collection, [(key, false, 1)]);
            table.ReloadColumns();
            Pump();
            expect(grid.Surface.Columns.IndexOf(key) < 0,
                "колонка периода действительно скрыта перед проверкой зависимости отбора");

            now = now.AddMinutes(1);
            RefreshQuery("не обновился отбор без совпадений");
            Wait(() => HasIds(), "при 09:02 ни один период не равен двум часам");
            expect(HasIds(), "текущий отбор может стать пустым после смены минуты даже при скрытой колонке периода");
            now = now.AddMinutes(-1);
            RefreshQuery("пустой вид не обновился при возвращении подходящего периода");
            Wait(() => HasIds(second), "из пустого вида не появилось новое совпадение");
            expect(HasIds(second), "пересчет текущего отбора работает без видимых записей и показанной колонки периода");
            repository.SaveColumnDisplay(collection, [(key, true, 1)]);
            table.ReloadColumns();
            Pump();

            // Будущий старт сначала дает пустое значение, затем ноль. Его место должно
            // поменяться даже при неизменных исходных ячейках и прежнем порядке записей.
            now = new DateTime(2026, 10, 28, 9, 0, 0);
            repository.SetFieldValue(second, start, "2026-10-28T09:01");
            filters.Clear();
            SetField(table, "_sortKey", key);
            SetField(table, "_sortAsc", true);
            Invoke(table, "ApplyFilters");
            Await(grid.QueryTask, "не применилась сортировка по текущему периоду");
            Wait(() => HasIds(first, second), "будущая дата должна быть в пустом хвосте сортировки");
            expect(HasIds(first, second), "до наступления даты пустой будущий период находится после положительного");
            now = now.AddMinutes(1);
            RefreshQuery("не обновилась сортировка после наступления даты");
            Wait(() => HasIds(second, first), "нулевой период не переместился перед положительным");
            expect(HasIds(second, first), "при наступлении даты нулевой период перемещается из пустого хвоста в начало сортировки");

            bool HasIds(params int[] ids) => grid.Rows?.Source is IdListRowSource source
                && source.Ids.SequenceEqual(ids);

            void RefreshQuery(string error)
            {
                Await(InvokeTask(table, "RefreshDatePeriodsForTimeAsync"), error);
                Await(grid.QueryTask, error);
            }
        }
        finally
        {
            owner.Content = null;
            Pump();
            repository.DatePeriodClock = savedClock;
        }
    }

    private static MenuItem? MenuAction(ContextMenu menu) =>
        menu.Items.OfType<MenuItem>().SingleOrDefault(item => Equals(item.Header, "Пересчитать"));
    private static void SelectPeriod(PropertyEditorDialog dialog)
    {
        var combo = Find<ComboBox>(dialog, "TypeCombo");
        combo.SelectedItem = combo.Items.OfType<FieldTypeItem>().Single(type => type.Type == "date_period");
        Pump();
    }
    private static CheckBox[] Days(PropertyEditorDialog dialog) =>
        Find<WrapPanel>(dialog, "DatePeriodWeekdays").Children.OfType<CheckBox>().ToArray();
    private static T Find<T>(Control parent, string name) where T : Control => parent.FindControl<T>(name)!;
    private static object? Invoke(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);
    private static Task InvokeTask(object target, string name) => (Task)Invoke(target, name)!;
    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    // Присваивание Text у TextBox ставит TextChanged в очередь. Перед следующим действием
    // человека даем ему завершиться: иначе отложенный HideError скрывал только что
    // показанную валидацию, хотя в обычном вводе это два разных прохода диспетчера.
    private static void Click(Button button) { Pump(); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    private static void Click(MenuItem item) { Pump(); item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump(); }
    private static void Await(Task task, string error)
    {
        Wait(() => task.IsCompleted, error);
        task.GetAwaiter().GetResult();
    }
    private static void Wait(Func<bool> condition, string error)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new InvalidOperationException(error);
            Pump();
            // RunJobs разбирает готовую очередь, но сам не продвигает отложенные
            // DispatcherTimer. Настоящий короткий цикл нужен для проверки прокрутки,
            // которая в приложении запускает обновление через debounce, без private-вызова.
            using var turn = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
            Dispatcher.UIThread.MainLoop(turn.Token);
        }
        Pump();
    }
    private static void Pump() { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
}
