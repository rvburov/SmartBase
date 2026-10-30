using Avalonia.Controls;

namespace SmartBase.Extensions;

/// <summary>
/// Одна настройка расширения — строка, которую расширению негде хранить самому: путь к
/// файлу-шаблону, адрес сервера и подобное. Расширение перечисляет свои настройки в
/// <see cref="IExtension.Settings"/>, а раздел «Расширения» в настройках строит по этому
/// списку поля ввода.
///
/// Значение хранит оболочка (<c>AppSettings.LoadExtensionSetting</c> / <c>Save…</c>) под
/// ключом <c>ext.&lt;Id расширения&gt;.&lt;Key&gt;</c> и НЕ знает, что оно означает —
/// поэтому добавление и удаление расширения не задевают ни настройки, ни их экран.
/// </summary>
/// <param name="Key">Имя настройки внутри расширения (уникально в его пределах).</param>
/// <param name="Label">Подпись над полем.</param>
/// <param name="Watermark">Серая подсказка в пустом поле (пример значения).</param>
/// <param name="Button">Кнопка рядом с полем или под ним; <c>null</c> — только поле.</param>
/// <param name="Editor">Составной редактор самого расширения; <c>null</c> — обычная строка.</param>
public sealed record ExtensionSetting(
    string                  Key,
    string                  Label,
    string                  Watermark = "",
    ExtensionSettingButton? Button    = null,
    Func<ExtensionSettingEditorContext, Control>? Editor = null);

/// <summary>
/// Кнопка под полем настройки: действие над значением, которое выполняет само расширение, —
/// например, разослать что-то тем, кто записан в поле. Оболочка только рисует кнопку и
/// передает ей значение поля; что кнопка делает и что говорит человеку, решает расширение.
/// </summary>
/// <param name="Text">Подпись кнопки.</param>
/// <param name="Run">Что сделать по нажатию. Значение поля к этому моменту уже сохранено.</param>
/// <param name="Inline">Поставить кнопку справа от поля на одной линии; иначе — под полем.</param>
public sealed record ExtensionSettingButton(string Text, Func<ExtensionSettingRun, Task> Run, bool Inline = false);

/// <summary>Составной редактор настроек получает только общие возможности оболочки.</summary>
/// <param name="Read">Актуальное значение ключа этого расширения, включая еще не сохраненный ввод.</param>
/// <param name="Save">Сохранить значение ключа этого расширения.</param>
/// <param name="Owner">Текущее окно для диалогов; до прикрепления к окну — <c>null</c>.</param>
/// <param name="Replies">Текущий путь отправки ответов; <c>null</c> — интеграции недоступны.</param>
public sealed record ExtensionSettingEditorContext(
    Func<string, string> Read, Action<string, string> Save,
    Func<Window?> Owner, Func<IActionReplies?> Replies);

/// <summary>Что оболочка дает кнопке настройки.</summary>
/// <param name="Owner">Окно настроек — владелец диалогов кнопки.</param>
/// <param name="Value">Значение поля в момент нажатия.</param>
/// <param name="Replies">Отправка сообщения путем ответов сценариев (см.
/// <see cref="IActionReplies"/>); <c>null</c> — интеграции недоступны.</param>
public sealed record ExtensionSettingRun(Window Owner, string Value, IActionReplies? Replies);
