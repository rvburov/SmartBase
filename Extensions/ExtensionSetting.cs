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
public sealed record ExtensionSetting(
    string Key,
    string Label,
    string Watermark = "");
