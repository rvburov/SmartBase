using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;
using SmartBase.Extensions;
using SmartBase.Integrations;
using SmartBase.Utils;

namespace SmartBase.Tests.WindowPlacement;

/// <summary>Настоящие сценарные и служебные формы: размер меняется их обработчиками,
/// а не подменой Content окна. Нативный сдвиг при resize предоставляет общий стенд.</summary>
internal static class ScenarioWindowChecks
{
    public static void Run(Window owner, Action<bool, string> check, Action<Window> settle,
                           Func<Window, IDisposable> nativeShift)
    {
        CollectionChoice[] places = [new(9711, 1, "Геометрия", "Записи")];
        ActionChoice[] actions =
        [
            new("geometry", "Геометрия", new GeometryAction("plain", ExtensionActionKind.Transformer)),
            new("geometry", "Геометрия", new GeometryAction("operation", ExtensionActionKind.Operation,
                [new("title", "Название"), new("code", "Код"), new("date", "Дата")])),
            new("geometry", "Геометрия", new GeometryAction("file", ExtensionActionKind.Rows,
                [new("sheet", "Имя листа")], makesFile: true)),
        ];

        Exercise("Действие сценария", () => new ScenarioActionDialog(
            ScenarioStep.OfAction("geometry", "plain", 9711, 1), actions, places), (window, mark) =>
        {
            var combo = Find<ComboBox>(window, "ActionCombo");
            foreach (var index in new[] { 1, 2, 0 })
            {
                combo.SelectedIndex = index;
                mark($"action-{index}");
                check(Find<Control>(window, "PlaceCombo").IsVisible == (index == 1),
                    "Действие сценария: выбранный вид не изменил секцию коллекции.");
                check(Find<Control>(window, "FolderBox").IsVisible == (index == 2),
                    "Действие сценария: выбранный вид не изменил секцию файла.");
            }
        });

        Exercise("Создание записи сценарием", () => new ScenarioRecordDialog(
            ScenarioStep.OfRecord(9711, 1, new Dictionary<string, string>()), places), (window, mark) =>
        {
            var key = Find<TextBox>(window, "KeyBox");
            var create = Find<CheckBox>(window, "CreateMissingCheck");
            key.Text = "Код";
            create.IsChecked = true;
            mark("key-create");
            create.IsChecked = false;
            mark("key-skip");
            create.IsChecked = true;
            key.Text = "";
            mark("no-key");
            var fields = Find<StackPanel>(window, "FieldsPanel");
            AddAndRemove(window, "AddFieldBtn", fields, "fields", mark);
            check(fields.Children.Count == 1, "Создание записи сценарием: строки не вернулись к одной.");
        });

        Exercise("Отбор сценария", () => new ScenarioFilterDialog(ScenarioStep.OfFilter()), (window, mark) =>
        {
            var rows = Find<ConditionRows>(window, "TestsPanel");
            AddAndRemove(window, "AddTestBtn", rows, "conditions", mark);
            check(rows.Count == 1, "Отбор сценария: последняя пустая строка потерялась.");
        });

        Exercise("Экспорт сценария", () => new ScenarioExportDialog(
            ScenarioStep.OfExport(9711, 1, "", "csv"), places), (window, mark) =>
        {
            var rows = Find<ConditionRows>(window, "TestsPanel");
            AddAndRemove(window, "AddTestBtn", rows, "conditions", mark);
            check(rows.Count == 0, "Экспорт сценария: удаление не вернуло пустой отбор.");
        });

        Exercise("Исходящий отбор", () => new OutgoingFilterDialog(["Статус", "Название"], []), (window, mark) =>
        {
            var rows = Find<ConditionRows>(window, "Rows");
            AddAndRemove(window, "AddBtn", rows, "conditions", mark);
            check(rows.Count == 1, "Исходящий отбор: последняя пустая строка потерялась.");
        });

        Exercise("Вычисления сценария", () => new ScenarioComputeDialog(
            ScenarioStep.OfCompute(new ScenarioFormula("Итого", "1"))), (window, mark) =>
        {
            var columns = Find<StackPanel>(window, "ColumnsPanel");
            var first = columns.Children.OfType<Grid>().First();
            var expression = first.Children.OfType<TextBox>().Last();
            expression.Text = "1";
            Click(Find<Button>(window, "TryBtn"));
            mark("constant");
            AddAndRemove(window, "AddColumnBtn", columns, "columns", mark);
            expression.Text = "{{Цена}} * {{Количество}}";
            Click(Find<Button>(window, "TryBtn"));
            mark("sample");
            check(Find<Control>(window, "SampleBox").IsVisible
                && Find<StackPanel>(window, "SamplePanel").Children.Count == 2,
                "Вычисления сценария: проба не показала два настоящих поля.");
            expression.Text = "(";
            Click(Find<Button>(window, "TryBtn"));
            mark("bad-expression");
            expression.Text = "1";
            Click(Find<Button>(window, "TryBtn"));
            mark("constant-again");
            check(!Find<Control>(window, "SampleBox").IsVisible
                && Find<Control>(window, "ResultBox").IsVisible,
                "Вычисления сценария: постоянное выражение не скрыло пробные поля.");
        });

        Exercise("Получение файла сценарием", () => new ScenarioDownloadDialog(
            ScenarioStep.OfDownload(0, ""), [(17, "Файл из запроса", false)]), (window, mark) =>
        {
            var notice = Find<NoticeBanner>(window, "NoticeBanner");
            // В этом редакторе нет команды скрытия ошибки. Сбрасываем только баннер его
            // публичным API, затем каждый раз показываем его настоящим Save-обработчиком.
            notice.Hide();
            mark("no-error");
            Find<ComboBox>(window, "RequestCombo").SelectedIndex = -1;
            Find<TextBox>(window, "NameBox").Text = "";
            Click(Find<Button>(window, "SaveBtn"));
            mark("missing-request-and-name");
            check(notice.IsVisible && notice.Message!.Contains("запрос", StringComparison.OrdinalIgnoreCase),
                "Получение файла: сохранение не показало ошибку запроса.");
            Find<ComboBox>(window, "RequestCombo").SelectedIndex = 0;
            Click(Find<Button>(window, "SaveBtn"));
            mark("missing-name");
            check(window.IsVisible && notice.Message!.Contains("имя файла", StringComparison.OrdinalIgnoreCase),
                "Получение файла: неполная настройка закрыла редактор.");
        });

        var shortcutAction = ShortcutActions.All.First(action => action.Rebindable);
        var freeKey = new[] { Key.F12, Key.F11, Key.F10 }.First(key =>
            ShortcutSettings.ConflictOf(new ShortcutGesture(key,
                KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift), shortcutAction.Id) is null);
        Exercise("Захват сочетания", () => new ShortcutCaptureDialog(shortcutAction), (window, mark) =>
        {
            window.KeyPressQwerty(PhysicalKey.A, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.A, RawInputModifiers.None);
            mark("needs-modifier");
            check(Find<NoticeBanner>(window, "NoticeBanner").IsVisible,
                "Захват сочетания: клавиша без модификатора не вызвала отказ.");
            var modifiers = RawInputModifiers.Control | RawInputModifiers.Alt | RawInputModifiers.Shift;
            window.KeyPressQwerty(Enum.Parse<PhysicalKey>(freeKey.ToString()), modifiers);
            window.KeyReleaseQwerty(Enum.Parse<PhysicalKey>(freeKey.ToString()), modifiers);
            mark("free-shortcut");
            check(!Find<NoticeBanner>(window, "NoticeBanner").IsVisible
                && Find<Button>(window, "ConfirmBtn").IsEnabled,
                "Захват сочетания: свободное сочетание не убрало отказ.");
        });

        Exercise("Прогресс операции", () => new ProgressDialog(), (window, mark) =>
        {
            var progress = (ProgressDialog)window;
            progress.EnableCancel(() => { });
            progress.SetProgress(.1, "Чтение");
            progress.SetInfo("");
            mark("short-phase");
            progress.SetProgress(.7, "Проверка и подготовка записей коллекции с длинным пользовательским названием "
                + "перед сохранением результатов обработки в выбранную базу данных");
            progress.SetInfo("Обработано записей: 123456");
            mark("long-phase-and-info");
            progress.SetSaving("Запись");
            progress.SetInfo("");
            mark("saving");
            check(Find<Button>(window, "CancelBtn").IsVisible,
                "Прогресс операции: включение отмены не показало кнопку.");
        });

        void Exercise(string name, Func<Window> create, Action<Window, Action<string>> cycle)
        {
            var window = create();
            try
            {
                window.Show(owner);
                settle(window);
                using var native = nativeShift(window);
                var heights = new HashSet<double> { Math.Round(window.ClientSize.Height) };
                var width = window.ClientSize.Width;
                foreach (var manual in new[] { false, true })
                {
                    if (manual)
                    {
                        window.Position += new PixelVector(31, 17);
                        settle(window);
                    }
                    var anchor = window.Position;
                    var positions = new Dictionary<string, PixelPoint>(StringComparer.Ordinal);
                    for (var pass = 0; pass < 3; pass++)
                    {
                        cycle(window, state =>
                        {
                            settle(window);
                            var context = $"{name}, ручная позиция {manual}, обход {pass + 1}, {state}";
                            check(window.IsVisible, context + ": окно преждевременно закрылось.");
                            check(Math.Abs(window.ClientSize.Width - width) <= 1,
                                context + ": изменение содержимого изменило фиксированную ширину.");
                            heights.Add(Math.Round(window.ClientSize.Height));
                            if (manual)
                                check(window.Position == anchor,
                                    context + $": ручная позиция сдвинулась из {anchor} в {window.Position}.");
                            else
                                Centered(window, owner, check, context);
                            if (positions.TryGetValue(state, out var previous))
                                check(window.Position == previous,
                                    context + $": повторное состояние накопило сдвиг из {previous} в {window.Position}.");
                            else positions.Add(state, window.Position);
                        });
                    }
                }
                check(heights.Count > 1, name + ": переходы не изменили настоящий размер окна.");
            }
            finally
            {
                if (window is ProgressDialog progress) progress.Done();
                else window.Close();
                settle(window);
            }
        }
    }

    private static void AddAndRemove(Window window, string addName, Panel rows, string state,
                                     Action<string> mark)
    {
        for (var added = 1; added <= 2; added++)
        {
            Click(Find<Button>(window, addName));
            mark($"{state}-plus-{added}");
        }
        for (var removed = 1; removed <= 2; removed++)
        {
            var remove = rows.GetVisualDescendants().OfType<Button>()
                .Last(button => Equals(button.Content, "✕"));
            Click(remove);
            mark($"{state}-minus-{removed}");
        }
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        window.FindControl<T>(name) ?? throw new InvalidOperationException($"Не найден контрол {name}.");

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Centered(Window window, Window owner, Action<bool, string> check, string context)
    {
        var main = PixelSize.FromSize(owner.FrameSize ?? owner.ClientSize, owner.DesktopScaling);
        var child = PixelSize.FromSize(window.FrameSize ?? window.ClientSize, window.DesktopScaling);
        var expectedX = owner.Position.X + (main.Width - child.Width) / 2;
        var expectedY = owner.Position.Y + (main.Height - child.Height) / 2;
        var screen = window.Screens.ScreenFromWindow(owner) ?? window.Screens.Primary;
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            expectedX = Math.Clamp(expectedX, area.X, Math.Max(area.X, area.Right - child.Width));
            expectedY = Math.Clamp(expectedY, area.Y, Math.Max(area.Y, area.Bottom - child.Height));
        }
        check(Math.Abs(window.Position.X - expectedX) <= 1 && Math.Abs(window.Position.Y - expectedY) <= 1,
            context + $": авторазмер увел окно из ожидаемой точки ({expectedX}, {expectedY}) в {window.Position}.");
    }

    private sealed class GeometryAction(string key, ExtensionActionKind kind,
                                        IReadOnlyList<ExtensionActionField>? fields = null,
                                        bool makesFile = false) : IExtensionAction
    {
        public string Key => key;
        public string Name => key;
        public string Description => "Действие для проверки изменения формы без выполнения операции.";
        public ExtensionActionKind Kind => kind;
        public IReadOnlyList<ExtensionActionField> Fields => fields ?? [];
        public bool MakesFile => makesFile;
        public ActionResult Run(ActionContext context, ActionInput input) =>
            throw new InvalidOperationException("Проверка размещения не должна запускать действие.");
    }
}
