using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SmartBase.Core.Notes;

namespace SmartBase.Controls.Dialogs;

/// <summary>Что вернул диалог: имя (пустое в режиме переноса) и относительный путь целевой папки.</summary>
public sealed record NoteItemResult(NoteKind Kind, string Name, string ParentRelativePath);

/// <summary>
/// Одно окно на три действия раздела «Заметки»: создать, переименовать, переместить. В конструкторе
/// под это заведены четыре разных окна (CreateItem/Group/Collection/Location) — здесь хватает
/// одного: лишние поля прячутся статическими фабриками, как это делает <c>ConfirmDialog.Info</c>.
/// </summary>
public partial class NoteItemDialog : Window
{
    private readonly bool _askName;
    private readonly bool _askType;
    private readonly NoteKind _fixedKind;
    private List<(string Label, string RelativePath)> _locations = [];

    /// <summary>Создание: тип, название и папка-родитель.</summary>
    public static NoteItemDialog ForCreate(IEnumerable<NoteNode> folderRoots,
                                           string parentRelativePath,
                                           NoteKind presetKind = NoteKind.File) =>
        new(folderRoots, parentRelativePath, presetKind, askType: true, askName: true,
            title: "Создать", confirm: "Создать", excludeSubtree: null, presetName: "");

    /// <summary>Переименование: только название, папка остается прежней.</summary>
    public static NoteItemDialog ForRename(NoteNode node) =>
        new([], VaultRules.ParentOf(node.RelativePath), node.Kind, askType: false, askName: true,
            title: node.IsFolder ? "Изменение папки" : "Изменение заметки", confirm: "Сохранить",
            excludeSubtree: null, presetName: node.Stem, askLocation: false);

    /// <summary>Перенос: только папка-получатель. Поддерево самой папки в список не попадает —
    /// внутрь себя ее не переместить.</summary>
    public static NoteItemDialog ForMove(IEnumerable<NoteNode> folderRoots, NoteNode node) =>
        new(folderRoots, VaultRules.ParentOf(node.RelativePath), node.Kind,
            askType: false, askName: false,
            title: "Расположение", confirm: "Переместить",
            excludeSubtree: node.IsFolder ? node.RelativePath : null, presetName: node.Stem);

    private NoteItemDialog(IEnumerable<NoteNode> folderRoots,
                           string parentRelativePath,
                           NoteKind kind,
                           bool askType,
                           bool askName,
                           string title,
                           string confirm,
                           string? excludeSubtree,
                           string presetName,
                           bool askLocation = true)
    {
        InitializeComponent();

        _askType   = askType;
        _askName   = askName;
        _fixedKind = kind;

        Title            = title;
        ConfirmBtn.Content = confirm;

        TypeCombo.ItemsSource   = new[] { "Заметка", "Папка" };
        TypeCombo.SelectedIndex = kind == NoteKind.Folder ? 1 : 0;
        TypeLabel.IsVisible = TypeCombo.IsVisible = askType;

        NameBox.Text = presetName;
        NameLabel.IsVisible = NameBox.IsVisible = askName;

        LocationLabel.IsVisible = LocationCombo.IsVisible = askLocation;
        if (askLocation)
        {
            _locations = VaultLocations.Build(folderRoots, excludeSubtree);
            LocationCombo.ItemsSource = _locations.Select(l => l.Label).ToList();
            var idx = _locations.FindIndex(l => string.Equals(l.RelativePath,
                                                              VaultRules.Normalize(parentRelativePath),
                                                              VaultRules.Comparison));
            LocationCombo.SelectedIndex = idx >= 0 ? idx : 0;
        }
        else
        {
            // Переименование не двигает вещь с места — папку берем ту же, в которой она лежит.
            _locations = [("", VaultRules.Normalize(parentRelativePath))];
            LocationCombo.SelectedIndex = -1;
        }

        CancelBtn.Click     += (_, _) => Close(null);
        ConfirmBtn.Click    += (_, _) => TryConfirm();
        NameBox.KeyDown     += (_, e) => { if (e.Key == Key.Enter) TryConfirm(); };
        NameBox.TextChanged += (_, _) => HideError();

        // Курсор сразу в поле имени: чаще всего окно открывают именно чтобы напечатать название.
        if (askName) Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private NoteKind SelectedKind =>
        _askType ? (TypeCombo.SelectedIndex == 1 ? NoteKind.Folder : NoteKind.File) : _fixedKind;

    private string SelectedLocation
    {
        get
        {
            if (_locations.Count == 0) return "";
            var i = LocationCombo.SelectedIndex;
            // −1 бывает в режиме переименования: список скрыт, папка ровно одна.
            return _locations[i >= 0 && i < _locations.Count ? i : 0].RelativePath;
        }
    }

    private void TryConfirm()
    {
        var name = _askName ? NameBox.Text?.Trim() ?? "" : "";

        if (_askName && VaultNames.Validate(name) is { } error)
        {
            ShowError(error);
            NameBox.Focus();
            return;
        }

        Close(new NoteItemResult(SelectedKind, name, SelectedLocation));
    }

    private void ShowError(string message)
    {
        var pos = Position;
        NoticeBanner.Show(message);
        KeepWindowPosition(pos);
    }

    private void HideError()
    {
        if (!NoticeBanner.IsVisible) return;
        var pos = Position;
        NoticeBanner.Hide();
        KeepWindowPosition(pos);
    }

    // SizeToContent пересчитывает окно после показа баннера — без этого оно «прыгает».
    private void KeepWindowPosition(PixelPoint pos)
    {
        void Restore(object? _, EventArgs __)
        {
            LayoutUpdated -= Restore;
            if (Position != pos) Position = pos;
        }
        LayoutUpdated += Restore;
    }
}
