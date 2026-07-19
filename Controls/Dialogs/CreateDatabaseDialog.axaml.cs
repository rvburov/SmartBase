using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Собственное окно «Создать базу данных» вместо системного save-диалога. Причины:
///  • системный диалог предлагает ПЕРЕЗАПИСАТЬ существующий файл («already exists —
///    Replace?») — для базы данных это опасно и запрещено: здесь занятое имя даёт
///    ошибку «уже существует», перезаписи нет в принципе;
///  • системные диалоги говорят на языке ОС — у нас всё окно по-русски на любой ОС
///    (нативным остаётся только выбор папки по «Обзор…»: файловый браузер — часть ОС).
/// Возвращает полный путь нового файла (…/имя.db) или null при отмене.
/// </summary>
public partial class CreateDatabaseDialog : Window
{
    private string _folder;

    public CreateDatabaseDialog(string initialFolder)
    {
        InitializeComponent();
        _folder         = initialFolder;
        FolderText.Text = _folder;
        NameBox.Text    = "database";

        BrowseBtn.Click     += async (_, _) => await PickFolder();
        CancelBtn.Click     += (_, _) => Close(null);
        ConfirmBtn.Click    += (_, _) => TryConfirm();
        NameBox.KeyDown     += (_, e) => { if (e.Key == Key.Enter) TryConfirm(); };
        NameBox.TextChanged += (_, _) => HideError();

        // Имя предвыделено — ввод сразу заменяет «database» на своё.
        Opened += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private async Task PickFolder()
    {
        var start   = await StorageProvider.TryGetFolderFromPathAsync(_folder);
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title                  = "Выберите папку для базы данных",
            AllowMultiple          = false,
            SuggestedStartLocation = start,
        });
        var folder = folders.FirstOrDefault();
        if (folder is null) return;
        _folder         = folder.Path.LocalPath;
        FolderText.Text = _folder;
        HideError();
    }

    private void TryConfirm()
    {
        var name = NameBox.Text?.Trim() ?? "";
        if (name.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            name = name[..^3].TrimEnd();   // расширение добавляем сами — введённое не дублируем

        if (name.Length == 0)
        {
            ShowError("Укажите название базы данных");
            NameBox.Focus();
            return;
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/') || name.Contains('\\'))
        {
            ShowError("Название содержит недопустимые символы");
            NameBox.Focus();
            return;
        }
        if (!Directory.Exists(_folder))
        {
            ShowError("Выбранная папка недоступна — укажите другую");
            return;
        }

        var path = Path.Combine(_folder, name + ".db");
        if (File.Exists(path))
        {
            // Главное правило: существующую базу НЕ перезаписываем — никогда.
            ShowError($"База данных «{name}» уже существует в этой папке. Укажите другое название " +
                      "или подключите существующий файл кнопкой «Подключить существующую».");
            NameBox.Focus();
            return;
        }

        Close(path);
    }

    private void ShowError(string message)
    {
        var pos = Position;            // баннер увеличит высоту — держим позицию
        ErrorBanner.Show(message);
        KeepWindowPosition(pos);
    }

    private void HideError()
    {
        if (!ErrorBanner.IsVisible) return;
        var pos = Position;
        ErrorBanner.Hide();
        KeepWindowPosition(pos);
    }

    // Держим позицию окна при пересчёте высоты (SizeToContent), чтобы оно не «прыгало».
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
