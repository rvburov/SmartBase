using Avalonia.Controls;
using SmartBase.Core.Models;

namespace SmartBase.ViewModels.ConstructorDb;

/// <summary>
/// Описание колонки виртуальной таблицы.
/// Хранит тип, настройки отображения и хуки для кликабельного функционала.
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

    // ── Расширяемость: заголовок ───────────────────────────────────────────────
    public Action<TableColumnDef>?             OnHeaderClick   { get; set; }
    public Func<TableColumnDef, ContextMenu?>? BuildHeaderMenu { get; set; }

    // ── Расширяемость: ячейка ──────────────────────────────────────────────────
    public Action<TableRowVm, TableColumnDef>?             OnCellClick   { get; set; }
    public Func<TableRowVm, TableColumnDef, ContextMenu?>? BuildCellMenu { get; set; }
}
