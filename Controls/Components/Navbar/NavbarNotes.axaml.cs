using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;

namespace SmartBase.Controls.Components;

/// <summary>
/// Шапка раздела «Заметки»: имя открытого файла с правкой по клику. Прием тот же, что у названия
/// коллекции в <see cref="NavbarConstructor"/> — два наложенных элемента в одной ячейке, клик
/// показывает поле.
///
/// Правится имя БЕЗ расширения: иначе достаточно стереть «.md», и заметка перестанет открываться.
/// Расширение подставляет сама операция переименования.
/// </summary>
public partial class NavbarNotes : UserControl
{
    /// <summary>Новое имя (после blur или Enter). Пустое и неизменившееся сюда не доходит.</summary>
    public event EventHandler<string>? TitleEdited;

    private string _committedTitle = "";
    private string _placeholder = "Выберите заметку";

    public NavbarNotes()
    {
        InitializeComponent();

        TitleDisplay.PointerPressed += (_, _) =>
        {
            if (!TitleBox.IsEnabled) return;
            TitleDisplay.IsVisible = false;
            TitleBox.IsVisible     = true;
            TitleBox.Focus();
            TitleBox.SelectAll();
        };

        TitleBox.LostFocus += (_, _) => { CommitTitle(); ExitTitleEdit(); };
        TitleBox.KeyDown   += (_, e) =>
        {
            if (e.Key == Key.Enter)  { CommitTitle(); ExitTitleEdit(); e.Handled = true; }
            if (e.Key == Key.Escape) { TitleBox.Text = _committedTitle; ExitTitleEdit(); e.Handled = true; }
        };
    }

    /// <summary>Идет ли сейчас правка имени: пока да, автосохранение текста не трогает файл.</summary>
    public bool IsEditing => TitleBox.IsVisible;

    /// <summary>Имя открытой вещи. Пустое — заметка не открыта, поле неактивно.</summary>
    public void SetTitle(string text)
    {
        _committedTitle    = text ?? "";
        TitleBox.Text      = _committedTitle;
        TitleBox.IsEnabled = _committedTitle.Length > 0;
        ExitTitleEdit();
    }

    /// <summary>Разрешить правку имени. У файлов, которые раздел не открывает, ее нет: менять им
    /// имя можно из дерева, где видно расширение целиком.</summary>
    public void SetEditable(bool on)
    {
        TitleBox.IsEnabled = on && _committedTitle.Length > 0;
        if (!on) ExitTitleEdit();
    }

    /// <summary>Подпись пустого состояния — меняется вместе с состоянием раздела.</summary>
    public void SetPlaceholder(string text)
    {
        _placeholder = text;
        if (_committedTitle.Length == 0) ExitTitleEdit();
    }

    /// <summary>Всплывашка-ошибка у заголовка. Через Post — чтобы показать после завершения
    /// текущего события commit/focus.</summary>
    public void ShowTitleError(string message) =>
        Dispatcher.UIThread.Post(() => Toast.Error(TitleDisplay, message));

    private void ExitTitleEdit()
    {
        TitleBox.IsVisible     = false;
        TitleDisplay.IsVisible = true;

        if (_committedTitle.Length == 0)
        {
            TitleDisplay.Text       = _placeholder;
            TitleDisplay.Foreground = Brush("BrushSecondaryText", "#8E8E93");
        }
        else
        {
            // Шапка — одна строка: показываем первую строку имени. Полное остается в TitleBox.
            TitleDisplay.Text       = TitleText.FirstLine(_committedTitle);
            TitleDisplay.Foreground = Brush("BrushPrimaryText", "#1C1C1E");
        }
    }

    // Ищем кисть у приложения, а не у контрола: раздел ставит состояние еще в своем конструкторе,
    // когда шапка к дереву окна не привязана и локальный поиск ресурса возвращает UnsetValue.
    private static IBrush Brush(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var res) && res is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallbackHex));

    // Коммит по blur/Enter, а не по символу: переименование — это операция файловой системы,
    // и по нажатию каждой клавиши она означала бы File.Move на каждую букву.
    private void CommitTitle()
    {
        var newName = (TitleBox.Text ?? "").Trim();
        if (newName.Length == 0 || newName == _committedTitle)
        {
            // Откатываем визуально, если попытались очистить или ничего не поменялось.
            TitleBox.Text = _committedTitle;
            return;
        }

        _committedTitle = newName;
        TitleBox.Text   = newName;
        TitleEdited?.Invoke(this, newName);
    }
}
