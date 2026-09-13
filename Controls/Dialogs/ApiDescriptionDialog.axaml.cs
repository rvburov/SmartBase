using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно «Описание для отправителя»: собранное из состава коллекций описание своего API —
/// адрес, вид сообщения и список колонок с типами (docs/ROADMAP-API.md, шаг 14).
///
/// <b>Почему это отдельное окно, а не сообщение.</b> Раньше описание показывало окно
/// подтверждения: текста в нем на несколько экранов, а такое окно растет по содержимому и
/// упирается в край экрана — прочитать описание целиком было нечем, и оставалось сохранять его
/// в файл, чтобы посмотреть. Здесь текст живет в моноширинной ячейке с обеими полосами
/// прокрутки, окно тянется и помнит свой размер.
///
/// Своих данных окно не собирает: готовый текст приходит параметром. Собрать его — дело
/// вкладки «Доступ извне», которая знает и адрес слушателя, и схему коллекций.
/// </summary>
public partial class ApiDescriptionDialog : Window
{
    private readonly string _text;
    private readonly string _fileName;

    public ApiDescriptionDialog() : this("", "описание.txt", "") { }

    /// <param name="text">Готовое описание.</param>
    /// <param name="fileName">Что предложить в окне сохранения.</param>
    /// <param name="hint">Строка над текстом: чем это описание является и чего в нем нет.</param>
    public ApiDescriptionDialog(string text, string fileName, string hint)
    {
        InitializeComponent();

        _text     = text ?? "";
        _fileName = fileName;

        BodyText.Text = _text;
        HintText.Text = hint;

        // Подсказки нет — и строки нет: пустая строка над текстом выглядела бы обрезанной.
        HintText.IsVisible = hint.Length > 0;

        CopyBtn.Click  += async (_, _) => await CopyAsync();
        SaveBtn.Click  += async (_, _) => await SaveAsync();
        CloseBtn.Click += (_, _) => Close();
        KeyDown        += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private async Task CopyAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;

        await clipboard.SetTextAsync(_text);

        // Ответ кнопки — на самой кнопке: второе окно поверх этого ради слова «скопировано»
        // человек закрывает, не читая.
        CopyBtn.Content = "Скопировано";
    }

    private async Task SaveAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title               = "Куда сохранить описание",
            SuggestedFileName   = _fileName,
            DefaultExtension    = "txt",
            ShowOverwritePrompt = true,
            FileTypeChoices     = [new FilePickerFileType("Текстовый файл") { Patterns = ["*.txt"] }],
        });

        if (file?.TryGetLocalPath() is not { Length: > 0 } path) return;

        try
        {
            // С меткой порядка байтов: файл открывают Блокнотом, а он без метки читает русский
            // текст как набор знаков вопроса.
            await File.WriteAllTextAsync(path, _text, new System.Text.UTF8Encoding(true));
            SaveBtn.Content = "Сохранено";
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // Отказ файловой системы — не повод ронять окно: описание осталось на экране, и его
            // можно скопировать.
            SaveBtn.Content = "Не сохранилось";
        }
    }
}
