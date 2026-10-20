namespace SmartBase.Core.Models;

/// <summary>
/// Значение чекбокса. Отмеченным считается «1», «true» и «True» — так галочку пишут таблица,
/// окно записи, вставка и старые файлы; все остальное, пустое тоже, — «не отмечено». Снятая в
/// таблице галочка пишется «0», а нетронутая ячейка пуста, после смены типа столбца в ячейках
/// остается прежний текст — для человека это одно и то же «не отмечено», поэтому фильтр
/// колонки делит чекбокс ровно на две группы по этому правилу.
/// </summary>
public static class CheckboxValue
{
    /// <summary>Значение отмеченной галочки, которое пишет программа.</summary>
    public const string Checked = "1";

    /// <summary>Все записи «отмечено», которые встречаются в хранении.</summary>
    public static readonly string[] CheckedSpellings = ["1", "true", "True"];

    public static bool IsChecked(string? raw) => raw is "1" or "true" or "True";
}
