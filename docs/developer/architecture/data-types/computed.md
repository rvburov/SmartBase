# Свойство: Вычисляемые (`created_*` / `updated_*`)

Часть [реестра типов свойств](overview.md). Единый источник классификации — [`FieldTypeInfo`](../../../../Core/Models/FieldTypeInfo.cs).


**Read-only**, значения в `db_field_values` нет — берутся из аудит-колонок `db_records`
(`CreatedAt`/`UpdatedAt`/`CreatedBy`/`UpdatedBy`). Автор — снимок `CurrentUser.DisplayName` на момент
операции. Даты форматируются как `дд.MM.гггг чч:мм` round-trip из ISO (`FieldTypeInfo.FormatComputedDisplay`;
доли секунды/смещение `DateValue` не парсит). Стамп: создание — при `AddRecord`/`SaveChanges`; правка —
`TouchUpdated` (название/описание/дата) и сырой `UPDATE db_records` в `SetFieldValue`, но **только при
реальной смене значения**. Ограничения: фильтр/сортировка по этим колонкам пока не функциональны (значений
нет в `db_field_values`); у старых записей автор может быть неизвестен (метаданных нет → пусто).

## Связанные документы

[overview](overview.md) · [features/constructor/table](../../features/constructor/table.md) · [features/constructor/record-page](../../features/constructor/record-page.md)
