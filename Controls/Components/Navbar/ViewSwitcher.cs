using Avalonia.Controls;
using SmartBase.Settings;

namespace SmartBase.Controls.Components;

/// <summary>
/// Переключатель видов раздела в навбаре: выпадающий список видимых видов одной области
/// <see cref="ViewCatalog"/>. Общий для календаря, задач и конструктора; внутри — ключи
/// каталога, номера (у календаря глобальный номер вида) переводит сам навбар.
///
/// Набор видимых перечитывает <see cref="Refresh"/> — главное окно зовет его, как только в
/// настройках сменили «Отображаемые виды». Раньше каждый навбар читал набор один раз, при
/// создании, и скрытый вид уходил из переключателя только после перезапуска.
///
/// <see cref="Changed"/> — показанный вид сменился не по просьбе раздела: человек выбрал пункт
/// или открытый вид скрыли. Выбор кодом (<see cref="Show"/>, пересборка списка) событие не
/// поднимает: обработчик сверяет выбранный пункт с показанным видом, и ему все равно, кто и
/// когда сменил выбор. Раньше выбор кодом поднимал событие, и раздел переключался второй раз —
/// из обработчика переключателя: календарь перечитывал вид дважды, а конструктор при переходе
/// между коллекциями с разными видами дважды читал таблицу.
/// </summary>
internal sealed class ViewSwitcher
{
    private readonly ComboBox     _box;
    private readonly string       _scope;
    private readonly Func<string> _default;
    private string[] _keys = [];

    /// <param name="defaultKey">Вид по умолчанию — настройка «Отображение по умолчанию»: на
    /// него раздел уходит со скрытого вида.</param>
    public ViewSwitcher(ComboBox box, string scope, Func<string> defaultKey)
    {
        _box     = box;
        _scope   = scope;
        _default = defaultKey;
        Refresh();

        _box.SelectionChanged += (_, _) =>
        {
            var pos = _box.SelectedIndex;
            if (pos < 0 || pos >= _keys.Length || _keys[pos] == Current) return;
            Current = _keys[pos];
            Changed?.Invoke(Current);
        };
    }

    /// <summary>Вид, который показывает раздел (ключ каталога).</summary>
    public string Current { get; private set; } = "";

    /// <summary>Показанный вид сменился не по просьбе раздела — выбор человека или скрытие
    /// открытого вида. Раздел отвечает тем же, чем на выбор: показывает этот вид.</summary>
    public event Action<string>? Changed;

    /// <summary>Есть ли вид в переключателе — не скрыт ли он.</summary>
    public bool Has(string key) => _keys.Contains(key);

    /// <summary>
    /// Показать вид, без <see cref="Changed"/>: вид открывает сам раздел. Скрытый не
    /// показывается — вместо него вид по умолчанию, а скрыт и тот — первый видимый
    /// (<see cref="ViewCatalog.Resolve"/>). Возвращает ключ показанного.
    /// </summary>
    public string Show(string? key)
    {
        Current = ViewCatalog.Resolve(_keys, key, _default());
        _box.SelectedIndex = Array.IndexOf(_keys, Current);
        return Current;
    }

    /// <summary>
    /// Перечитать набор видимых. Показанный вид остается выбранным; скрыли его — переключатель
    /// уходит на вид по умолчанию и поднимает <see cref="Changed"/>: раздел обязан сменить
    /// содержимое так же, как при выборе человеком.
    /// </summary>
    public void Refresh()
    {
        var shown   = Current;
        var visible = ViewCatalog.Visible(_scope);

        // Пока меняется список, ключей нет: смена ItemsSource трогает выбор и может поднять
        // SelectionChanged, а это не выбор человека.
        _keys = [];
        _box.ItemsSource = visible.Select(v => v.Label).ToList();
        _keys = [.. visible.Select(v => v.Key)];

        if (Show(shown) != shown && shown.Length > 0) Changed?.Invoke(Current);
    }
}
