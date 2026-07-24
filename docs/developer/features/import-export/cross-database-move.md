# Перенос коллекций между БД

Часть [обзора импорта/экспорта](overview.md).

## Перенос между БД

Перенос коллекций/групп в другую БД (контекстное меню «Расположение», смена БД в окне
«Изменить») идёт **быстрым ATTACH-путём** — `MoveCollectionIsolated`/`MoveGroupIsolated`:
на подключении целевой БД присоединяется файл-источник и данные копируются bulk-ом
(`INSERT … SELECT`), затем источник удаляется. Этот путь **переносит флаги `IsCalStart`
и пересобирает проекцию календаря** `calendar_index` целевой коллекции. Подробнее о механике
и индикаторе прогресса — в [[../../architecture/performance/overview]] и
[../architecture/multi-database.md](../../architecture/database/multi-database.md).

> **Легаси (не используется UI).** Старые EF-методы `ExportCollection`/`ImportCollection`,
> `ExportGroup`/`ImportGroup`, `MoveCollection`/`MoveGroup` (снимок `CollectionSnapshot`/
> `GroupSnapshot` в память → запись по одной записи) ни одной кнопкой не вызываются — их
> заменил ATTACH-путь, а экспорт коллекций **в файл** теперь делает отдельный потоковый
> конвейер Core/Exporters (см. «Экспорт таблиц»). Если будешь их подключать, добавь
> пересборку проекции `calendar_index` — сейчас они её не делают, и скопированные события
> не попали бы в календарь.

## Связанные документы

[overview](overview.md) · [import](import.md) · [export](export.md) · [architecture/database/multi-database](../../architecture/database/multi-database.md) · [features/constructor/table](../constructor/table.md)
