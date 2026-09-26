using Avalonia.Controls;
using SmartBase.Core.Models;

namespace SmartBase.ViewModels.ConstructorDb;

/// <summary>Тон текста ячейки служебной таблицы (кисти токенов темы, см. GridSurface).</summary>
public enum CellTone { Primary, Secondary, Green, Red }

/// <summary>
/// Описание колонки виртуальной таблицы.
/// Хранит тип, настройки отображения и хуки для кликабельного функционала. У служебных таблиц
/// (ServiceGrid) колонка описывает еще и вид: иконку шапки, тон текста, доступность ячейки; типы
/// ячеек там — «text», «checkbox», «choice» (список) и «button» (кнопка с иконкой).
/// </summary>
public sealed class TableColumnDef
{
    public const string NumKey     = "__num__";
    public const string AddPropKey = "__add_property__";

    // ── Идентификация ──────────────────────────────────────────────────────────
    public required string Key      { get; init; }
    public required string Header   { get; init; }
    public string FieldType         { get; init; } = "text";

    // ── Геометрия ──────────────────────────────────────────────────────────────
    public bool    IsStarWidth { get; init; } = true;
    public double  FixedWidth  { get; init; }
    public double  MinWidth    { get; init; } = 160;
    public double? MaxWidth    { get; init; }
    public bool    CanResize   { get; init; } = true;
    public bool    IsReadOnly  { get; init; }

    // Данные для select-поля
    public IReadOnlyList<string> Options { get; init; } = [];

    // Строка-склейка значений «Мультисписка» (из ConfigJson поля: {"separator":"..."}); по умолчанию «, ».
    public string Separator { get; init; } = ", ";

    // Формат показа поля «Число» (из ConfigJson поля); по умолчанию «Обычное число» (показ = сырому вводу).
    public NumberFormat NumberFormat { get; init; } = NumberFormat.Default;

    // Папка авто-привязки поля «Файл» (из ConfigJson: {"auto":{folder}}); пусто — ручной режим.
    // Непусто → ячейка файла read-only (правило ведет значения), нужна для статуса «папка недоступна».
    public string FileAutoFolder { get; init; } = "";

    // Настройка поля-связи «relation» (целевая коллекция + one/many) из ConfigJson; null — не связь
    // или цель не выбрана. Непусто → ячейка рисует чипы связанных записей (read-only колонка).
    public RelationConfig? Relation { get; init; }

    // Произвольный тег для идентификации (например, field.Id)
    public object? Tag { get; init; }

    // ── Служебные таблицы (ServiceGrid): вид шапки и ячеек задается колонкой, а не типом поля ──

    /// <summary>Иконка шапки по ключу Icons.axaml (у служебных таблиц колонка не поле — иконка ее
    /// смысла: часы, пользователь, замок); null — иконка типа поля.</summary>
    public string? HeaderIconKey { get; init; }

    /// <summary>Есть ли у колонки кнопка фильтра и сортировки в шапке.</summary>
    public bool Filterable { get; init; } = true;

    /// <summary>Рисовать ли правую линию в шапке колонки. У последней колонки некоторых служебных
    /// таблиц ее нет: край и так очерчен краем таблицы, а вторая линия давала двойную.</summary>
    public bool HeaderRightLine { get; init; } = true;

    /// <summary>Подсказка к заголовку (строка или готовый контрол); null — без подсказки.</summary>
    public object? HeaderTip { get; init; }

    /// <summary>Подсказка к кнопке фильтра; null — «Фильтр и сортировка».</summary>
    public string? FilterTip { get; init; }

    /// <summary>Тон текста ячейки по строке (серый у вторичных сведений, зеленый и красный у
    /// исхода); null — обычный текст. Только у служебных таблиц: у таблицы коллекции цвет ячейки
    /// идет каскадом оформления.</summary>
    public Func<TableRowVm, CellTone>? CellTone { get; init; }

    /// <summary>Доступна ли ячейка-галочка, список или кнопка в этой строке; null — доступна.</summary>
    public Func<TableRowVm, bool>? CellEnabled { get; init; }

    /// <summary>Подсказка к ячейке по строке; null — без подсказки.</summary>
    public Func<TableRowVm, string?>? CellTip { get; init; }

    /// <summary>Пункты ячейки-списка («choice») по строке; null — <see cref="Options"/>.</summary>
    public Func<TableRowVm, IReadOnlyList<string>>? CellOptions { get; init; }

    /// <summary>Что написано в ячейке-списке, когда значения нет (тон — из <see cref="CellTone"/>).</summary>
    public string Placeholder { get; init; } = "";

    /// <summary>Иконка ячейки-кнопки («button») по ключу Icons.axaml.</summary>
    public string? CellIconKey { get; init; }

    // ── Расширяемость: заголовок ───────────────────────────────────────────────
    public Action<TableColumnDef>?             OnHeaderClick   { get; set; }
    public Func<TableColumnDef, ContextMenu?>? BuildHeaderMenu { get; set; }

    // ── Расширяемость: ячейка ──────────────────────────────────────────────────
    public Action<TableRowVm, TableColumnDef>?             OnCellClick   { get; set; }
    public Func<TableRowVm, TableColumnDef, ContextMenu?>? BuildCellMenu { get; set; }
}
