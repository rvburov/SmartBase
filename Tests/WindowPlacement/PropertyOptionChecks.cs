using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SmartBase.Controls.Dialogs;
using SmartBase.Controls.Widgets;
using SmartBase.Database.Repositories;

namespace SmartBase.Tests.WindowPlacement;

/// <summary>Вложенные параметры настоящего свойства: оба направления изменения формы,
/// повтор состояния и сохранение настоящей ручной позиции при нативном resize.</summary>
internal static class PropertyOptionChecks
{
    internal static void Run(Window owner, ConstructorRepository repo, int collection,
        Action<bool, string> check, Action<Window> settle, Func<Window, IDisposable> nativeShift)
    {
        var fieldsBefore = repo.GetFields(collection).Select(field => (field.Id, field.FieldType, field.ConfigJson)).ToArray();
        foreach (var manual in new[] { false, true })
        {
            Case("форматы числа", "number", Number);
            Case("автоподбор файла", "file", File);
            Case("параметры связи", "relation", Relation);
            Case("пункты и поиск списка", "status", Options);
            Case("настройки периода", "date_period", Period);
            Case("ошибка названия", "text", Validation);

            void Case(string name, string type, Action<Session> scenario)
            {
                using var session = new Session(owner, repo, collection, type, manual, name, check, settle, nativeShift);
                scenario(session);
            }
        }
        check(fieldsBefore.SequenceEqual(repo.GetFields(collection).Select(field => (field.Id, field.FieldType, field.ConfigJson))),
            "Проверка вложенных параметров сохранила черновик свойства в базу.");
    }

    private static void Number(Session s)
    {
        var format = s.Find<ComboBox>("NumberFormatCombo");
        for (var cycle = 0; cycle < 2; cycle++)
        {
            s.Step("деньги", () => format.SelectedIndex = 2,
                () => s.Find<Control>("NumberOptions").IsVisible && s.Find<Control>("CurrencySection").IsVisible);
            s.Step("символ перед числом", () => s.Find<ComboBox>("SymbolPosCombo").SelectedIndex = 1,
                () => s.Find<ComboBox>("SymbolPosCombo").SelectedIndex == 1);
            s.Step("проценты", () => format.SelectedIndex = 3,
                () => s.Find<Control>("NumberOptions").IsVisible && !s.Find<Control>("CurrencySection").IsVisible);
            s.Step("разряды", () => format.SelectedIndex = 1,
                () => s.Find<Control>("NumberOptions").IsVisible && !s.Find<Control>("CurrencySection").IsVisible);
            s.Step("обычное число", () => format.SelectedIndex = 0,
                () => !s.Find<Control>("NumberOptions").IsVisible);
        }
        s.RequireResize();
    }

    private static void File(Session s)
    {
        var automatic = s.Find<ToggleSwitch>("FileAutoToggle");
        var format = s.Find<ComboBox>("FileFormatCombo");
        for (var cycle = 0; cycle < 2; cycle++)
        {
            s.Step("включен подбор", () => automatic.IsChecked = true,
                () => s.Find<Control>("FileAutoOptions").IsVisible);
            s.Step("свое расширение", () => format.SelectedItem = format.Items.OfType<ComboBoxItem>()
                    .Single(item => item.Content?.ToString() == "Свой формат…"),
                () => s.Find<Control>("FileFormatCustom").IsVisible);
            s.Step("готовое расширение", () => format.SelectedIndex = 0,
                () => !s.Find<Control>("FileFormatCustom").IsVisible);
            s.Step("выключен подбор", () => automatic.IsChecked = false,
                () => !s.Find<Control>("FileAutoOptions").IsVisible);
        }
        s.RequireResize();
    }

    private static void Relation(Session s)
    {
        var target = s.Find<ComboBox>("RelationTargetCombo");
        target.SelectedIndex = 0;
        s.Settle();
        var mode = s.Find<ComboBox>("RelationModeCombo");
        for (var cycle = 0; cycle < 2; cycle++)
        {
            s.Step("автоматическая связь", () => mode.SelectedIndex = 1,
                () => s.Find<Control>("RelationAutoOptions").IsVisible);
            s.Step("добавлена пара", () => Click(s.Find<Button>("AddAutoPairBtn")),
                () => s.Find<Panel>("RelationAutoExtra").Children.Count == 1);
            s.Step("убрана пара", () => DeleteRow(s.Find<Panel>("RelationAutoExtra").Children.Single()),
                () => s.Find<Panel>("RelationAutoExtra").Children.Count == 0);
            s.Step("добавлено условие", () => Click(s.Find<Button>("AddConditionBtn")),
                () => s.Find<Panel>("RelationConditionList").Children.Count == 1);
            s.Step("убрано условие", () =>
            {
                var row = s.Find<Panel>("RelationConditionList").Children.Single();
                Click(row.GetVisualDescendants().OfType<Button>().Single(button => ToolTip.GetTip(button)?.ToString() == "Убрать условие"));
            }, () => s.Find<Panel>("RelationConditionList").Children.Count == 0);
            s.Step("ручная связь", () => mode.SelectedIndex = 0,
                () => !s.Find<Control>("RelationAutoOptions").IsVisible);
        }
    }

    private static void Options(Session s)
    {
        var list = s.Find<Panel>("OptionsList");
        var initial = list.Children.Count;
        var size = s.ClientSize;
        for (var cycle = 0; cycle < 2; cycle++)
        {
            Control? added = null;
            s.Step("пункт добавлен", () =>
            {
                Click(s.Find<Button>("AddOptionBtn"));
                added = list.Children.Last();
                added.GetVisualDescendants().OfType<TextBox>().Single().Text = "Дополнительный пункт";
                s.Find<TextBox>("NameBox").Focus();
            }, () => list.Children.Count == initial + 1 && s.ClientSize == size);
            s.Step("поиск скрыл пункты", () => s.Find<TextBox>("OptionsSearch").Text = "__нет_такого_пункта__",
                () => list.Children.All(row => !row.IsVisible) && s.ClientSize == size);
            s.Step("поиск очищен", () => s.Find<TextBox>("OptionsSearch").Text = "",
                () => list.Children.All(row => row.IsVisible) && s.ClientSize == size);
            s.Step("пункт удален", () => DeleteRow(added!), () => list.Children.Count == initial && s.ClientSize == size);
        }
    }

    private static void Period(Session s)
    {
        var automatic = s.Find<ToggleSwitch>("DatePeriodAutomaticToggle");
        var monday = s.Find<Panel>("DatePeriodWeekdays").Children.OfType<CheckBox>().First();
        for (var cycle = 0; cycle < 2; cycle++)
        {
            s.Step("фиксированный период", () => automatic.IsChecked = false,
                () => s.Find<TextBlock>("DatePeriodModeHint").Text?.Contains("фиксируется", StringComparison.Ordinal) == true);
            s.Step("день выключен", () => monday.IsChecked = false, () => monday.IsChecked == false);
            s.Step("день включен", () => monday.IsChecked = true, () => monday.IsChecked == true);
            s.Step("автоматический период", () => automatic.IsChecked = true,
                () => s.Find<TextBlock>("DatePeriodModeHint").Text?.Contains("пересчитывается", StringComparison.Ordinal) == true);
        }
    }

    private static void Validation(Session s)
    {
        var name = s.Find<TextBox>("NameBox");
        for (var cycle = 0; cycle < 2; cycle++)
        {
            s.Step("ошибка показана", () =>
            {
                name.Text = "";
                s.Settle();
                Click(s.Find<Button>("SaveBtn"));
            }, () => s.Find<NoticeBanner>("NoticeBanner").IsVisible);
            s.Step("ошибка скрыта", () => name.Text = "Проверка геометрии",
                () => !s.Find<NoticeBanner>("NoticeBanner").IsVisible);
        }
        s.RequireResize();
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void DeleteRow(Control row)
    {
        var parent = row as Panel ?? row.GetVisualDescendants().OfType<Panel>()
            .First(panel => panel.Children.OfType<Button>().Any());
        Click(parent.Children.OfType<Button>().Single());
    }

    private sealed class Session : IDisposable
    {
        private readonly PropertyEditorDialog _dialog;
        private readonly bool _manual;
        private readonly string _name;
        private readonly Action<bool, string> _check;
        private readonly Action<Window> _settle;
        private readonly IDisposable _shift;
        private readonly EventHandler<SizeChangedEventArgs> _sizeChanged;
        private readonly PixelPoint _anchor;
        private readonly Dictionary<string, (PixelPoint Position, Size Size)> _states = [];
        private int _resizes;

        public Session(Window owner, ConstructorRepository repo, int collection, string type, bool manual,
            string name, Action<bool, string> check, Action<Window> settle, Func<Window, IDisposable> nativeShift)
        {
            _manual = manual;
            _name = name;
            _check = check;
            _settle = settle;
            _dialog = new PropertyEditorDialog(repo, collection);
            Find<TextBox>("NameBox").Text = "Проверка геометрии";
            _dialog.Show(owner);
            Settle();
            var types = Find<ComboBox>("TypeCombo");
            var selected = types.Items.OfType<FieldTypeItem>().Single(item => item.Type == type);
            check(selected.IsEnabled, $"Для проверки вложенных параметров недоступен тип {type}.");
            types.SelectedItem = selected;
            Settle();
            if (manual)
            {
                _dialog.Position += new PixelVector(39, 23);
                Settle();
            }
            _anchor = _dialog.Position;
            _shift = nativeShift(_dialog);
            _sizeChanged = (_, _) => _resizes++;
            _dialog.SizeChanged += _sizeChanged;
        }

        public T Find<T>(string name) where T : Control =>
            _dialog.FindControl<T>(name) ?? throw new InvalidOperationException($"Не найден контрол {name}.");

        public void Settle() => _settle(_dialog);

        public Size ClientSize => _dialog.ClientSize;

        public void Step(string state, Action change, Func<bool> expected)
        {
            change();
            Settle();
            var label = $"{_name}, {state}, ручная позиция {_manual}";
            _check(expected(), label + ": ожидаемое состояние формы не наступило.");
            if (_manual)
                _check(_dialog.Position == _anchor,
                    label + $": нативное изменение размера сдвинуло окно из {_anchor} в {_dialog.Position}.");
            else if (_states.TryGetValue(state, out var previous))
                _check(_dialog.Position == previous.Position && _dialog.ClientSize == previous.Size,
                    label + $": повторное состояние накопило смещение {previous.Position} → {_dialog.Position}.");
            else _states.Add(state, (_dialog.Position, _dialog.ClientSize));
        }

        public void RequireResize() => _check(_resizes > 0, $"{_name}: вложенные параметры не изменили размер окна, нативный сценарий не проверен.");

        public void Dispose()
        {
            _dialog.SizeChanged -= _sizeChanged;
            _shift.Dispose();
            _dialog.Close();
        }
    }
}
