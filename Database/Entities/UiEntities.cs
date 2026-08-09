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

// Метаданные колонки таблицы — порядок, видимость/уровень, подпись — одной строкой на
// (CollectionId, Key). Key = TableRowVm.TitleKey ("__title__") либо
// строковый id поля. Каждый аспект nullable: null = дефолт (порядок по Position полей,
// видима/уровень 1, имя по умолчанию). Строки только для изменённых колонок; строка живёт,
// пока задан хоть один аспект (см. ConstructorRepository.RemoveColumnMetaIfEmpty).
// Объединяет прежние таблицы column_order + column_display + system_field_meta.
public class ColumnMetaEntity
{
    public int     CollectionId { get; set; }
    public string  Key          { get; set; } = "";
    public int?    Position     { get; set; }   // порядок колонки; null = дефолтный
    public bool?   Visible      { get; set; }   // false = скрыта; null = аспект «Отображение» не задан
    public int?    Level        { get; set; }   // пресет-уровень; задаётся вместе с Visible
    public string? DisplayName  { get; set; }   // подпись системной колонки (__title__)
}

// Отображение записей (окно «Отображение записей») — аналог аспекта Visible/Level в
// column_meta, но для строк-записей. Visible=false — запись скрыта в таблице (фильтруется
// на уровне SQL в постраничной загрузке, виртуализация сохраняется). Level — пресет-уровень
// записи; блок уровней массово включает видимость по уровню ≤ выбранного (только
// внутри окна, на отрисовку таблицы не влияет). Строки только для изменённых записей;
// отсутствие строки = видима, уровень 1.
public class RecordDisplayEntity
{
    public int  CollectionId { get; set; }
    public int  RecordId     { get; set; }
    public bool Visible      { get; set; } = true;
    public int  Level        { get; set; } = 1;
}
