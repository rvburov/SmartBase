using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SmartBase.Database.Entities;

// ── Производные индексы и UI-настройки коллекций ───────────────────────────────

// Индексная проекция календаря: по строке на каждое видимое значение поля-даты
// (запись × поле-дата с IsCalStart). Денормализованный кэш под быстрый запрос диапазона —
// календарь читает отсюда, а не сканирует db_field_values. Start/End — ISO-строки
// (yyyy-MM-ddTHH:mm:ss), сортируются лексикографически. FK-каскады чистят строки при
// удалении записи/поля (как у db_field_values). Содержимое целиком derived из
// db_field_values + DbFields.IsCalStart. Физически таблица WITHOUT ROWID (см. миграцию
// SchemaCompact) — кластеризована по PK (RecordId, FieldId).
public class CalendarIndexEntity
{
    public int    RecordId     { get; set; }
    public int    FieldId      { get; set; }
    public int    CollectionId { get; set; }
    public string StartIso     { get; set; } = "";   // ISO "o"-формат, сортируется лексикографически
    public string EndIso       { get; set; } = "";
    public bool   AllDay       { get; set; }

    [ForeignKey(nameof(RecordId))]
    public DbRecordEntity Record { get; set; } = null!;
    [ForeignKey(nameof(FieldId))]
    public DbFieldEntity  Field  { get; set; } = null!;
}

// ЛЕГАСИ: ширины колонок таблицы переехали в column_meta (аспект Width). Ключ здесь —
// числовой FieldId, поэтому системную колонку «Название» ("__title__") он покрыть не может,
// а без нее ручной ресайз неполон. Таблица оставлена, чтобы не ломать старые базы; таблица
// конструктора ее больше не читает и не пишет.
public class UiColWidthEntity
{
    public int CollectionId { get; set; }
    public int FieldId      { get; set; }
    public int Width        { get; set; }
}

public class UiRowHeightEntity
{
    public int CollectionId { get; set; }
    public int RecordId     { get; set; }
    public int Height       { get; set; }
}

public class UiCollectionSettingEntity
{
    [Key] public int    CollectionId { get; set; }
    public bool   ColResize    { get; set; } = false;
    public bool   RowResize    { get; set; } = false;
    public string ViewMode     { get; set; } = "table";
}

// Аспекты оформления таблицы (RowStyle) — одни и те же у колонки (column_meta), строки
// (record_display) и ячейки (cell_style): фон, цвет текста и расположение текста. Общий
// интерфейс дает одну запись аспекта и одну проверку «оформления не осталось» на все три уровня.
public interface ITableStyleRow
{
    string? Bg     { get; set; }
    string? Fg     { get; set; }
    string? HAlign { get; set; }
    string? VAlign { get; set; }
}

// Метаданные колонки таблицы — порядок, видимость/уровень, подпись, оформление, ручная ширина —
// одной строкой на (CollectionId, Key). Key = TableRowVm.TitleKey ("__title__") либо
// строковый id поля. Каждый аспект nullable: null = дефолт (порядок по Position полей,
// видима/уровень 1, имя по умолчанию, ширина не задана). Строки только для измененных
// колонок; строка живет, пока задан хоть один аспект (см.
// ConstructorRepository.RemoveColumnMetaIfEmpty).
// Объединяет прежние таблицы column_order + column_display + system_field_meta, а с
// аспектом Width — и ui_col_widths (там ключ числовой и «__title__» не помещается).
public class ColumnMetaEntity : ITableStyleRow
{
    public int     CollectionId { get; set; }
    public string  Key          { get; set; } = "";
    public int?    Position     { get; set; }   // порядок колонки; null = дефолтный
    public bool?   Visible      { get; set; }   // false = скрыта; null = аспект «Отображение» не задан
    public int?    Level        { get; set; }   // пресет-уровень; задается вместе с Visible
    public string? DisplayName  { get; set; }   // подпись системной колонки (__title__)
    public string? Bg           { get; set; }   // фон всех ячеек колонки, hex; null = без фона
    public string? Fg           { get; set; }   // цвет текста колонки, hex; null = стандартный
    public int?    Width        { get; set; }   // ручная ширина колонки в DIP; null = не задана
    public string? HAlign       { get; set; }   // текст по горизонтали: left/center/right; null = по типу
    public string? VAlign       { get; set; }   // текст по вертикали: top/center/bottom; null = по центру
}

// Метаданные записи в таблице — аналог column_meta, но для строк. Visible=false — запись
// скрыта в таблице (фильтруется на уровне SQL в постраничной загрузке, виртуализация
// сохраняется). Level — пресет-уровень записи; блок уровней массово включает видимость по
// уровню ≤ выбранного (только внутри окна «Отображение записей», на отрисовку таблицы не
// влияет). Bg/Fg — фон и цвет текста строки в ЭТОЙ коллекции.
//
// Цвет строки — настройка таблицы, а не свойство записи: за пределами таблицы он не виден
// и в журнал не пишется, как ширины колонок и высоты строк. Сквозной цвет записи
// (DbRecordEntity.Color) был отдельным уровнем и убран — см. комментарий там.
//
// Аспекты независимы, как в column_meta: строка живет, пока задан хоть один, отсутствие
// строки = видима, уровень 1, без своего цвета. Поэтому запись аспектов — точечный
// upsert (SetRowStyle / SaveRecordDisplay), а не «удалить все и переписать»: иначе
// сохранение видимости стирало бы цвет.
public class RecordDisplayEntity : ITableStyleRow
{
    public int     CollectionId { get; set; }
    public int     RecordId     { get; set; }
    public bool    Visible      { get; set; } = true;
    public int     Level        { get; set; } = 1;
    public string? Bg           { get; set; }   // фон строки, hex; null = без фона
    public string? Fg           { get; set; }   // цвет текста строки, hex; null = стандартный
    public string? HAlign       { get; set; }   // текст строки по горизонтали; null = не задан
    public string? VAlign       { get; set; }   // текст строки по вертикали; null = не задан
}

// Оформление ОДНОЙ ячейки таблицы: (коллекция, запись, колонка) → фон, цвет текста и
// расположение текста. Key — как в column_meta: "__title__" либо строковый id поля (числовым
// FieldId нельзя — системную колонку «Название» тогда не оформить). Таблица разрежена: строка
// появляется только у оформленной ячейки, поэтому у большинства страниц выборка вернет пусто.
//
// FK на запись с каскадом (как у calendar_index) — удаление записи чистит ее ячейки само.
// Каскада по полю нет: Key строковый, поэтому чистку при удалении свойства делает
// прикладной код там же, где чистится column_meta.
public class CellStyleEntity : ITableStyleRow
{
    public int     CollectionId { get; set; }
    public int     RecordId     { get; set; }
    public string  Key          { get; set; } = "";
    public string? Bg           { get; set; }
    public string? Fg           { get; set; }
    public string? HAlign       { get; set; }
    public string? VAlign       { get; set; }

    [ForeignKey(nameof(RecordId))]
    public DbRecordEntity Record { get; set; } = null!;
}
