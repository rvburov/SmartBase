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
            var typePopupChild = typePopup.Child ?? throw new InvalidOperationException("Список типов не создал содержимое окна");
            var typeScroll = typePopupChild.GetVisualDescendants().OfType<ScrollViewer>().First();
            Console.WriteLine($"  Прокрутка списка типов до конца: extent={typeScroll.Extent}, "
                + $"viewport={typeScroll.Viewport}, offset={typeScroll.Offset}, panel={unavailableTypes.ItemsPanelRoot?.Bounds}");
            // Попап сначала получает viewport. Проверяем весь последний пункт в его
            // пределах после настоящей прокрутки до конца, прежде чем нажимать мышью.
            Wait(() =>
            {
                typeScroll.ScrollToEnd();
                typePopupChild.UpdateLayout();
                Pump();
                if (unavailableTypes.ContainerFromIndex(blockedIndex) is not ComboBoxItem item) return false;
                var position = item.TranslatePoint(default, typeScroll);
                return position is { } point && point.Y >= -1
                    && point.Y + item.Bounds.Height <= typeScroll.Viewport.Height + 1;
            }, "последний пункт типов не реализовался после прокрутки выпадающего списка");
            var blockedContainer = unavailableTypes.ContainerFromIndex(blockedIndex) as ComboBoxItem
                ?? throw new InvalidOperationException("Недоступный тип не создал контейнер после прокрутки");
            Console.WriteLine($"  Диагностика списка типов: open={unavailableTypes.IsDropDownOpen}, "
                + $"items={unavailableTypes.ItemCount}, index={blockedIndex}, panel={unavailableTypes.ItemsPanelRoot?.GetType().Name}, "
                + $"container={blockedContainer.GetType().Name}, bounds={unavailableTypes.Bounds}, "
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
            var inputRoot = TopLevel.GetTopLevel(blockedContainer)
                ?? throw new InvalidOperationException("Пункт списка не подключен к окну ввода");
            var itemOrigin = blockedContainer.TranslatePoint(default, inputRoot)!.Value;
            var blockedPoint = blockedContainer.TranslatePoint(new Point(blockedContainer.Bounds.Width / 2,
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
            var start = repository.AddField(new DbField { CollectionId = collection, Name = "План", FieldType = "date" }).Id!.Value;
            var end = repository.AddField(new DbField { CollectionId = collection, Name = "Факт", FieldType = "date" }).Id!.Value;
            repository.AddField(new DbField { CollectionId = collection, Name = "Изменено", FieldType = "updated_at" });
            repository.AddField(new DbField { CollectionId = empty, Name = "Чужая дата", FieldType = "date" });

            var dialog = new PropertyEditorDialog(repository, collection) { MaxHeight = 800 };
            var closed = dialog.ShowDialog<bool>(owner);
            Pump();
            SelectPeriod(dialog);
            var source = Find<ComboBox>(dialog, "DatePeriodStartCombo");
            Expect(dialog.FindControl<ComboBox>("DatePeriodEndCombo") is null
                && dialog.FindControl<ComboBox>("DatePeriodEndModeCombo") is null
                && dialog.GetVisualDescendants().OfType<TextBlock>().Any(label => label.Text == "Даты для расчета"),
                "настройка содержит один выбор «Даты для расчета», без списка «До» и второй даты");
            Expect(source.Items.OfType<PropertyEditorDialog.DatePeriodFieldItem>().Select(field => field.Id).Order()
                .SequenceEqual(new[] { start, end }.Order()), "источники содержат только обычные даты этой коллекции");
            Expect(Find<ToggleSwitch>(dialog, "DatePeriodAutomaticToggle").IsChecked == true
                && Find<TextBox>(dialog, "DatePeriodDayStart").Text == "00:00"
                && Find<TextBox>(dialog, "DatePeriodDayEnd").Text == "24:00"
                && Days(dialog).All(day => day.IsChecked == true), "стандартный расчет учитывает все дни целиком автоматически");
            Find<TextBox>(dialog, "NameBox").Text = "Рабочее время";
            source.SelectedIndex = -1;
            Click(Find<Button>(dialog, "SaveBtn"));
            Expect(!closed.IsCompleted && Find<NoticeBanner>(dialog, "NoticeBanner").IsVisible
                && !repository.GetFields(collection).Any(property => property.FieldType == "date_period"),
                "без выбранных дат расчет не сохраняется и не подменяет источник текущим временем");
            source.SelectedItem = source.Items.OfType<PropertyEditorDialog.DatePeriodFieldItem>().Single(field => field.Id == start);
            Find<ToggleSwitch>(dialog, "DatePeriodAutomaticToggle").IsChecked = false;
            Find<TextBox>(dialog, "DatePeriodDayStart").Text = "18:00";
            Find<TextBox>(dialog, "DatePeriodDayEnd").Text = "09:00";
            Click(Find<Button>(dialog, "SaveBtn"));
            Expect(!closed.IsCompleted && Find<NoticeBanner>(dialog, "NoticeBanner").IsVisible,
                "обратный суточный интервал не сохраняется и объясняется в окне");
            Find<TextBox>(dialog, "DatePeriodDayStart").Text = "24:00";
            Find<TextBox>(dialog, "DatePeriodDayEnd").Text = "24:00";
            Click(Find<Button>(dialog, "SaveBtn"));
            Expect(!closed.IsCompleted && Find<NoticeBanner>(dialog, "NoticeBanner").IsVisible,
                "24:00 недопустимо в начале рабочего интервала");
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
                && config.DateFieldId == start,
                "сохранение переносит единственный источник, неделю, часы и фиксированный режим в базу");

            var reopened = new PropertyEditorDialog(repository, collection, field.Id) { MaxHeight = 800 };
            var reopenedClosed = reopened.ShowDialog<bool>(owner);
            Pump();
            Expect(Find<ComboBox>(reopened, "DatePeriodStartCombo").SelectedItem is PropertyEditorDialog.DatePeriodFieldItem { Id: var sourceId }
                && sourceId == start && Find<TextBox>(reopened, "DatePeriodDayStart").Text == "09:00"
                && Find<TextBox>(reopened, "DatePeriodDayEnd").Text == "18:00"
                && Find<ToggleSwitch>(reopened, "DatePeriodAutomaticToggle").IsChecked == false
                && Days(reopened).Select((day, index) => day.IsChecked == (index < 5)).All(value => value),
                "повторное открытие восстанавливает выбранные даты, режим, дни и часы");
            Find<TextBox>(reopened, "DatePeriodDayEnd").Text = "24:00";
            Click(Find<Button>(reopened, "CancelBtn"));
            Wait(() => reopenedClosed.IsCompleted, "не закрылось редактирование периода");
            Expect(DatePeriodConfig.FromConfig(repository.GetFields(collection).Single(property => property.Id == field.Id).ConfigJson)?.DayEnd == "18:00",
                "отмена не сохраняет черновой интервал");

            var automaticDialog = new PropertyEditorDialog(repository, collection) { MaxHeight = 800 };
            var automaticClosed = automaticDialog.ShowDialog<bool>(owner);
            Pump();
            SelectPeriod(automaticDialog);
            Find<TextBox>(automaticDialog, "NameBox").Text = "Автоматический период";
            var automaticSource = Find<ComboBox>(automaticDialog, "DatePeriodStartCombo");
            automaticSource.SelectedItem = automaticSource.Items.OfType<PropertyEditorDialog.DatePeriodFieldItem>().Single(field => field.Id == start);
            Find<TextBox>(automaticDialog, "DatePeriodDayStart").Text = "09:00";
            Find<TextBox>(automaticDialog, "DatePeriodDayEnd").Text = "18:00";
            var automaticDays = Days(automaticDialog);
            for (var i = 0; i < automaticDays.Length; i++) automaticDays[i].IsChecked = i < 5;
            Click(Find<Button>(automaticDialog, "SaveBtn"));
            Wait(() => automaticClosed.IsCompleted, "не сохранился автоматический расчет");
            var automatic = repository.GetFields(collection).Single(property => property.Name == "Автоматический период");
            var automaticId = automatic.Id!.Value;
            var automaticKey = automaticId.ToString();
            Expect(automaticClosed.Result && DatePeriodConfig.FromConfig(automatic.ConfigJson)
                is { Automatic: true, DateFieldId: var automaticSourceId } && automaticSourceId == start,
                "автоматический расчет использует начало и конец одного выбранного свойства даты");
            var record = repository.AddRecord(new DbRecord { CollectionId = collection, Title = "Первый" }).Id!.Value;
            var second = repository.AddRecord(new DbRecord { CollectionId = collection, Title = "Второй" }).Id!.Value;
            foreach (var id in new[] { record, second })
            {
                repository.SetFieldValue(id, start, "2026-10-26T09:00/2026-10-27T10:00");
                repository.SetFieldValue(id, end, "2026-10-26T11:00/2026-10-26T16:00");
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
            var grid = Find<SmartBase.Views.ConstructorDb.DrawnGrid.GridPanel>(table, "RecordsGrid");
            Wait(() => grid.Rows?.FindCachedByRecordId(record) is not null, "строка таблицы не загрузилась");
            var properties = Find<PropertyList>(page, "PropertiesHost");
            Expect(Display(field.Name) == "10 ч" && Display(automatic.Name) == "10 ч",
                "страница считает десять рабочих часов из диапазона одного свойства даты");
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
            Wait(() => CachedPeriod("36000"), "автоматический период не загрузился в видимую колонку");
            grid.Rows!.FindCachedByRecordId(record)![start.ToString()] = "2026-10-26T09:00/2026-10-27T11:00";
            Wait(() => CachedPeriod("39600") && Display(automatic.Name) == "11 ч", "правка диапазона не обновила автоматический период");
            Expect(Display(field.Name) == "10 ч" && CachedPeriod("39600"),
                "правка диапазона в таблице обновляет автоматический расчет и сохраняет фиксированный снимок");
            var warmRows = grid.Rows;
            grid.Surface.OffsetX = 0;
            Pump();
            grid.ScrollToCell(0, automaticKey);
            Pump();
            Expect(ReferenceEquals(warmRows, grid.Rows) && CachedPeriod("39600") && grid.Rows?.Source is PlainRowSource,
                "прокрутка не заменяет теплые страницы и не запускает полный пересчет таблицы");

            var fixedRow = (PropertyRowVm)properties.Cards.Single(card => card.Name == field.Name).Tag!;
            var autoRow = (PropertyRowVm)properties.Cards.Single(card => card.Name == automatic.Name).Tag!;
            var fixedMenu = (ContextMenu)Invoke(page, "BuildPropertyMenu", fixedRow)!;
            var autoMenu = (ContextMenu)Invoke(page, "BuildPropertyMenu", autoRow)!;
            Expect(MenuAction(fixedMenu) is not null && MenuAction(autoMenu) is null,
                "ручной пересчет предлагается только фиксированному периоду");
            repository.SetFieldValue(record, start, "2026-10-26T09:00/2026-10-27T12:00");
            Click(MenuAction(fixedMenu)!);
            Wait(() => repository.GetFieldValues(record).GetValueOrDefault(field.Id.Value) == "43200",
                "пересчет свойства не обновил текущую запись");
            Expect(repository.GetFieldValues(second).GetValueOrDefault(field.Id.Value) == "36000",
                "одиночный пересчет не изменил соседнюю запись");

            repository.SetFieldValue(record, start, "2026-10-26T09:00/2026-10-27T13:00");
            repository.SetFieldValue(second, start, "2026-10-26T09:00/2026-10-27T11:00");
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
                "показ и обновление автоматического периода не записывают его результат в базу");

            grid.ScrollToCell(0, automaticKey);
            Wait(() => CachedPeriod("46800"), "период не загрузился перед сменой правила");
            var beforeRuleChange = grid.Rows;
            ChangeRule(new DatePeriodConfig(end, Weekdays: 31, DayStart: "09:00", DayEnd: "18:00"));
            Wait(() => CachedPeriod("18000") && Display(automatic.Name) == "5 ч", "новый источник не заменил старое значение в открытых видах");
            Expect(!ReferenceEquals(beforeRuleChange, grid.Rows) && grid.Rows?.Source is PlainRowSource,
                "смена источника обновляет видимые страницы без материализации полного списка записей");
            ChangeRule(new DatePeriodConfig(end, Weekdays: 96, DayStart: "09:00", DayEnd: "18:00"));
            Wait(() => CachedPeriod("0") && Display(automatic.Name) == "0 мин", "смена дней недели не обновила расчет");
            Expect(CachedPeriod("0"), "выбор только выходных дает ноль для понедельника");
            ChangeRule(new DatePeriodConfig(end, Weekdays: 31, DayStart: "12:00", DayEnd: "14:00"));
            Wait(() => CachedPeriod("7200") && Display(automatic.Name) == "2 ч", "смена рабочего интервала не обновила расчет");
            Expect(CachedPeriod("7200"), "рабочие часы обрезают диапазон выбранного свойства");
            ChangeRule(new DatePeriodConfig(end, Automatic: false, Weekdays: 31, DayStart: "09:00", DayEnd: "18:00"));
            Wait(() => CachedPeriod("") && Display(automatic.Name) == "", "фиксированный режим сохранил прежнее автоматическое значение");
            Expect(CachedPeriod(""), "фиксированный режим существующей записи ждет явного пересчета");
            Await(table.RecalculateDatePeriodsAsync([record], [automaticId]), "не пересчитался новый фиксированный режим");
            Wait(() => CachedPeriod("18000") && Display(automatic.Name) == "5 ч", "ручной снимок не появился в открытых видах");
            repository.SetFieldValue(record, end, "2026-10-26T11:00/2026-10-26T17:00");
            table.RefreshRecordRow(record);
            page.ReloadFromDb();
            Expect(CachedPeriod("18000") && Display(automatic.Name) == "5 ч",
                "после правки дат фиксированный снимок остается прежним до команды пересчета");
            ChangeRule(new DatePeriodConfig(end, Weekdays: 31, DayStart: "09:00", DayEnd: "18:00"));
            Wait(() => CachedPeriod("21600") && Display(automatic.Name) == "6 ч", "автоматический режим оставил фиксированный снимок");
            Expect(CachedPeriod("21600"), "возврат в автоматический режим считает актуальный диапазон");

            repository.SetFieldValue(record, end, "2026-09-01/2026-09-04");
            table.RefreshRecordRow(record);
            page.ReloadFromDb();
            Wait(() => CachedPeriod("97200") && Display(automatic.Name) == "1 д 3 ч", "диапазон дней не дал ожидаемые 27 рабочих часов");
            Expect(CachedPeriod("97200"), "01–04 сентября без времени считаются до начала четвертого числа: 27 рабочих часов");
            ChangeRule(new DatePeriodConfig(end));
            Wait(() => CachedPeriod("259200") && Display(automatic.Name) == "3 д", "целые сутки не дали трех дней");
            Expect(CachedPeriod("259200"), "тот же диапазон с 00:00–24:00 дает ровно три дня");
            repository.SetFieldValue(record, end, "2026-09-04");
            table.RefreshRecordRow(record);
            page.ReloadFromDb();
            Wait(() => CachedPeriod("0") && Display(automatic.Name) == "0 мин", "одиночная дата не дала нулевой период");
            Expect(CachedPeriod("0"), "одиночная дата не превращается в сутки и не считается до текущего времени");
            repository.SetFieldValue(record, end, "");
            table.RefreshRecordRow(record);
            page.ReloadFromDb();
            Wait(() => CachedPeriod("") && Display(automatic.Name) == "", "пустые исходные даты оставили старый расчет");
            Expect(CachedPeriod(""), "пустая дата показывает пустое значение, а не ноль");

            repository.SetFieldValue(record, end, "2026-10-26T11:00/2026-10-26T17:00");
            table.RefreshRecordRow(record);
            page.ReloadFromDb();
            Wait(() => CachedPeriod("21600"), "диапазон не восстановился перед проверкой смены типа");
            var unchangedRows = grid.Rows;
            var renamed = repository.GetFields(collection).Single(property => property.Id == automaticId);
            renamed.Name = "Переименованный период";
            repository.UpdateField(renamed);
            table.RebuildColumn(automaticKey);
            Pump();
            Expect(ReferenceEquals(unchangedRows, grid.Rows) && CachedPeriod("21600"),
                "переименование периода сохраняет теплые страницы и правильное значение");
            var sourceChanged = repository.GetFields(collection).Single(property => property.Id == end);
            sourceChanged.FieldType = "text";
            repository.UpdateField(sourceChanged);
            table.RebuildColumn(end.ToString());
            page.ReloadFromDb();
            Wait(() => CachedPeriod(""), "смена типа источника оставила рассчитанное значение в кеше");
            Expect(CachedPeriod(""), "период очищается после смены типа источника даты");
            CheckMissingSource(repository, owner, collection, automaticId, Expect);
            CheckSoleSource(repository, owner, Expect);
            using (var image = owner.CaptureRenderedFrame()) image?.Save(Path.Combine(output, "date-period-ui.png"));
            owner.Content = null;
            Pump();
            CheckLiveQueries(repository, owner, Expect);
            CheckLazyWindow(repository, owner, Expect);
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
                page.ReloadFromDb();
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

    /// <summary>Отбор и сортировка должны реагировать на реальные изменения диапазона,
    /// включая записи вне текущего результата и пустой вид со скрытой колонкой периода.</summary>
    private static void CheckLiveQueries(ConstructorRepository repository, Window owner, Action<bool, string> expect)
    {
        var collection = repository.AddCollection(new DbCollection { Name = "Диапазон в отборе" }).Id!.Value;
        var dates = repository.AddField(new DbField
        {
            CollectionId = collection, Name = "Даты", FieldType = "date",
        }).Id!.Value;
        var notes = repository.AddField(new DbField
        {
            CollectionId = collection, Name = "Примечание", FieldType = "text",
        }).Id!.Value;
        var period = repository.AddField(new DbField
        {
            CollectionId = collection, Name = "Длительность", FieldType = "date_period", IsSystem = true,
            ConfigJson = new DatePeriodConfig(dates).ToConfigJson(),
        }).Id!.Value;
        var first = repository.AddRecord(new DbRecord { CollectionId = collection, Title = "Первая" }).Id!.Value;
        var second = repository.AddRecord(new DbRecord { CollectionId = collection, Title = "Вторая" }).Id!.Value;
        repository.SetFieldValue(first, dates, "2026-10-28T07:00/2026-10-28T09:00");
        repository.SetFieldValue(second, dates, "2026-10-28T07:00/2026-10-28T08:00");
        var key = period.ToString();
        var table = new TableView();
        owner.Content = table;
        table.Load(repository, collection);
        Pump();
        var grid = Find<SmartBase.Views.ConstructorDb.DrawnGrid.GridPanel>(table, "RecordsGrid");
        Await(grid.QueryTask, "обычный вид периода не открылся");
        grid.ScrollToCell(0, key);
        Wait(() => grid.Rows?.FindCachedByRecordId(first)?[key] == "7200", "период первой записи не загрузился");
        var plainRows = grid.Rows;
        for (var i = 0; i < 5; i++) Pump();
        expect(ReferenceEquals(plainRows, grid.Rows) && grid.Rows?.Source is PlainRowSource,
            "отрисовка без правки данных сохраняет постраничный источник строк");

        // Тот же вход, что у подтверждения окна фильтра: фасад сохраняет выбор,
        // ApplyFilters передает его копию сетке и запускает настоящий фоновый запрос.
        var filters = Field<Dictionary<string, HashSet<string>>>(table, "_filters");
        filters[key] = ["7200"];
        Invoke(table, "ApplyFilters");
        Wait(() => HasIds(first), "начальный отбор должен выбрать только двухчасовой диапазон");
        expect(HasIds(first), "отбор по двум часам показывает подходящий диапазон");
        grid.ScrollToCell(0, key);
        Wait(() => grid.Rows?.FindCachedByRecordId(first) is not null, "отобранная строка не загрузилась");
        grid.Rows!.FindCachedByRecordId(first)![dates.ToString()] = "2026-10-28T07:00/2026-10-28T10:00";
        Wait(() => HasIds(), "правка исходной ячейки не убрала запись из активного отбора");
        expect(HasIds(), "правка даты в таблице сразу исключает несовпадающий рассчитанный период");

        repository.SaveColumnDisplay(collection, [(key, false, 1)]);
        table.ReloadColumns();
        Pump();
        expect(grid.Surface.Columns.IndexOf(key) < 0, "колонка периода скрыта перед проверкой пустого отбора");
        repository.SetFieldValue(second, dates, "2026-10-28T07:00/2026-10-28T09:00");
        table.RefreshRecordRow(second);
        Wait(() => HasIds(second), "из пустого вида не появилось новое совпадение после внешней правки");
        expect(HasIds(second), "внешняя правка возвращает ранее невидимую запись даже при скрытой колонке периода");
        repository.SetFieldValue(second, dates, "2026-10-28T07:00/2026-10-28T08:00");
        table.RefreshRecordRow(second);
        Wait(() => HasIds(), "внешняя правка не убрала переставший подходить диапазон");
        expect(HasIds(), "после внешней правки отбор корректно становится пустым");

        var rule = repository.GetFields(collection).Single(field => field.Id == period);
        rule.ConfigJson = new DatePeriodConfig(dates, DayStart: "07:00", DayEnd: "09:00").ToConfigJson(rule.ConfigJson);
        repository.UpdateField(rule);
        table.RebuildColumn(key);
        Wait(() => HasIds(first), "смена часов не обновила активный отбор");
        expect(HasIds(first), "изменение настроек периода заново проверяет весь активный отбор");
        filters.Clear();
        SetField(table, "_sortKey", key);
        SetField(table, "_sortAsc", true);
        Invoke(table, "ApplyFilters");
        Wait(() => HasIds(second, first), "не применилась сортировка по диапазону");
        expect(HasIds(second, first), "сортировка ставит часовой диапазон перед двухчасовым");
        repository.SaveColumnDisplay(collection, [(key, true, 1)]);
        table.ReloadColumns();
        Pump();
        grid.ScrollToCell(0, key);
        Wait(() => grid.Rows?.FindCachedByRecordId(first) is not null
            && grid.Rows.FindCachedByRecordId(second) is not null, "строки сортировки не загрузились");
        var beforeTitle = grid.Rows;
        grid.Rows!.FindCachedByRecordId(first)![TableRowVm.TitleKey] = "Переименованная первая";
        for (var i = 0; i < 5; i++) Pump();
        expect(ReferenceEquals(beforeTitle, grid.Rows) && HasIds(second, first),
            "правка названия не пересоздает строки при активной сортировке по периоду");
        grid.Rows!.FindCachedByRecordId(first)![notes.ToString()] = "Не относится к датам";
        for (var i = 0; i < 5; i++) Pump();
        expect(ReferenceEquals(beforeTitle, grid.Rows) && HasIds(second, first),
            "правка другого свойства не запускает повторную сортировку по периоду");
        grid.Rows!.FindCachedByRecordId(first)![dates.ToString()] = "2026-10-28T07:00/2026-10-28T07:30";
        Wait(() => HasIds(first, second), "правка ячейки не изменила порядок активной сортировки");
        expect(HasIds(first, second), "правка даты через модель ячейки пересортировывает зависимое свойство");
        repository.SetFieldValue(second, dates, "2026-10-28T08:00/2026-10-28T12:00");
        table.RefreshRecordRow(second);
        Wait(() => HasIds(first, second), "внешняя правка не сохранила ожидаемую сортировку");
        rule = repository.GetFields(collection).Single(field => field.Id == period);
        rule.ConfigJson = new DatePeriodConfig(dates, DayStart: "07:00", DayEnd: "08:00").ToConfigJson(rule.ConfigJson);
        repository.UpdateField(rule);
        table.RebuildColumn(key);
        Wait(() => HasIds(second, first), "смена часов не обновила активную сортировку");
        expect(HasIds(second, first), "смена рабочего интервала меняет порядок сортировки по вычисляемому свойству");
        owner.Content = null;
        Pump();

        bool HasIds(params int[] ids) => grid.Rows?.Source is IdListRowSource source && source.Ids.SequenceEqual(ids);
    }

    private static void CheckMissingSource(ConstructorRepository repository, Window owner, int collection, int field,
        Action<bool, string> expect)
    {
        var dialog = new PropertyEditorDialog(repository, collection, field) { MaxHeight = 800 };
        var closed = dialog.ShowDialog<bool>(owner);
        Pump();
        expect(Find<ComboBox>(dialog, "DatePeriodStartCombo").SelectedItem is PropertyEditorDialog.DatePeriodFieldItem { Missing: true },
            "изменившая тип исходная дата показана как недоступный сохраненный выбор");
        Click(Find<Button>(dialog, "SaveBtn"));
        expect(!closed.IsCompleted && Find<NoticeBanner>(dialog, "NoticeBanner").IsVisible,
            "недоступный источник нельзя сохранить без явного выбора другой даты");
        Click(Find<Button>(dialog, "CancelBtn"));
        Wait(() => closed.IsCompleted, "не закрылось окно с недоступным источником");
    }

    private static void CheckSoleSource(ConstructorRepository repository, Window owner, Action<bool, string> expect)
    {
        var collection = repository.AddCollection(new DbCollection { Name = "Единственная дата" }).Id!.Value;
        var dates = repository.AddField(new DbField { CollectionId = collection, Name = "Даты", FieldType = "date" }).Id!.Value;
        var legacy = repository.AddField(new DbField
        {
            CollectionId = collection, Name = "Старый период", FieldType = "date_period", IsSystem = true,
            ConfigJson = "{\"datePeriod\":{\"version\":1,\"startFieldId\":" + dates + ",\"endFieldId\":" + dates
                + ",\"automatic\":true,\"weekdays\":127,\"dayStart\":\"00:00\",\"dayEnd\":\"24:00\"}}",
        }).Id!.Value;
        var dialog = new PropertyEditorDialog(repository, collection) { MaxHeight = 800 };
        var closed = dialog.ShowDialog<bool>(owner);
        Pump();
        SelectPeriod(dialog);
        expect(Find<ComboBox>(dialog, "DatePeriodStartCombo").SelectedItem is PropertyEditorDialog.DatePeriodFieldItem { Id: var selected }
            && selected == dates, "единственное свойство даты выбирается автоматически");
        Click(Find<Button>(dialog, "CancelBtn"));
        Wait(() => closed.IsCompleted, "не закрылось окно с единственной датой");
        dialog = new PropertyEditorDialog(repository, collection, legacy) { MaxHeight = 800 };
        closed = dialog.ShowDialog<bool>(owner);
        Pump();
        expect(Find<ComboBox>(dialog, "DatePeriodStartCombo").SelectedItem is PropertyEditorDialog.DatePeriodFieldItem { Id: var restored }
            && restored == dates, "старое правило восстанавливает прежний источник как единственные даты для расчета");
        Click(Find<Button>(dialog, "SaveBtn"));
        Wait(() => closed.IsCompleted, "старое правило не сохранилось в новом формате");
        var saved = DatePeriodConfig.FromConfig(repository.GetFields(collection).Single(field => field.Id == legacy).ConfigJson);
        expect(closed.Result && saved is { DateFieldId: var savedDates, Automatic: true } && savedDates == dates,
            "повторное сохранение старого правила оставляет источник и автоматический режим");
    }

    private static void CheckLazyWindow(ConstructorRepository repository, Window owner, Action<bool, string> expect)
    {
        var collection = repository.AddCollection(new DbCollection { Name = "Ленивые периоды" }).Id!.Value;
        var dates = repository.AddField(new DbField { CollectionId = collection, Name = "Даты", FieldType = "date" }).Id!.Value;
        for (var i = 0; i < 20; i++)
            repository.AddField(new DbField { CollectionId = collection, Name = $"Текст {i}", FieldType = "text" });
        var period = repository.AddField(new DbField
        {
            CollectionId = collection, Name = "Период справа", FieldType = "date_period", IsSystem = true,
            ConfigJson = new DatePeriodConfig(dates, DayStart: "09:00", DayEnd: "18:00").ToConfigJson(),
        }).Id!.Value;
        // Системный тип по умолчанию размещается раньше обычных свойств. Здесь
        // явно ставим его справа, за видимым окном и его областью предзагрузки.
        repository.SaveColumnOrder(collection, ["__title__", .. repository.GetFields(collection)
            .Where(field => field.Id is not null && field.Id != period).OrderBy(field => field.Id)
            .Select(field => field.Id!.Value.ToString()), period.ToString()]);
        var ids = repository.BulkAddRecords(collection, [dates], Enumerable.Range(0, 1000)
            .Select(i => new BulkRecordRow($"Запись {i}", ["2026-09-01/2026-09-04"])), 1000, CancellationToken.None);
        var table = new TableView();
        owner.Content = table;
        table.Load(repository, collection);
        Pump();
        var grid = Find<SmartBase.Views.ConstructorDb.DrawnGrid.GridPanel>(table, "RecordsGrid");
        // При оценке длины 0 IsCellLoaded(0, ...) возвращает true: строка еще
        // вне среза. Ждем настоящую первую страницу после фонового счета.
        Wait(() => grid.Rows is { DataCount: 1000 } initialRows
            && initialRows.TryGetCached(0) is not null && initialRows.IsCellLoaded(0, dates),
            "не загрузилась первая страница тысячи записей с окном колонок");
        var periodColumn = grid.Surface.Columns.IndexOf(period.ToString());
        var firstWindow = grid.Surface.Columns.VisibleRange(grid.Surface.OffsetX, grid.Surface.ViewportWidth);
        Console.WriteLine($"  Ленивый показ: source={grid.Rows?.Source.GetType().Name}, rows={grid.Rows?.DataCount}, "
            + $"lastCached={grid.Rows?.TryGetCached(999) is not null}, periodLoaded={grid.Rows?.IsCellLoaded(0, period)}, "
            + $"fieldWindow={(grid.Rows?.FieldWindow is { } window ? string.Join(',', window) : "all")}, "
            + $"period={period}, column={periodColumn}, visible={firstWindow}, "
            + $"viewport={grid.Surface.ViewportWidth}, offset={grid.Surface.OffsetX}, "
            + $"columnOrder={string.Join(',', Enumerable.Range(0, grid.Surface.Columns.Count).Select(index => grid.Surface.Columns[index].Key))}");
        expect(grid.Rows?.Source is PlainRowSource && grid.Rows.TryGetCached(999) is null
            && !grid.Rows.IsCellLoaded(0, period),
            "первый показ не загружает дальнюю страницу и невидимую колонку периода из тысячи записей");
        var rows = grid.Rows;
        grid.ScrollToCell(0, period.ToString());
        Wait(() => grid.Rows?.IsCellLoaded(0, period) == true
            && grid.Rows.FindCachedByRecordId(ids[0])?[period.ToString()] == "97200", "период не дочитался при горизонтальной прокрутке");
        expect(ReferenceEquals(rows, grid.Rows) && grid.Rows?.TryGetCached(999) is null,
            "прокрутка дочитывает вычисляемую колонку в той же странице, не загружая конец таблицы");
        var warmed = grid.Rows;
        for (var i = 0; i < 5; i++) Pump();
        expect(ReferenceEquals(warmed, grid.Rows) && grid.Rows?.FindCachedByRecordId(ids[0])?[period.ToString()] == "97200",
            "повторная отрисовка без правок не сбрасывает страницы и расчет диапазона");
        owner.Content = null;
        Pump();
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
            // Даем завершиться настоящим отложенным событиям интерфейса и фоновым
            // чтениям страниц, не вызывая внутренние обработчики вместо работы окна.
            using var turn = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
            Dispatcher.UIThread.MainLoop(turn.Token);
        }
        Pump();
    }
    private static void Pump() { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
}
