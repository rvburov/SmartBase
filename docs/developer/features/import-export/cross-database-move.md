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

## Связи (`relation`) при переносе

Перенос **одиночной коллекции** SQLite↔SQLite связи переносит и переписывает: само-связи внутри
коллекции остаются внутрибазовыми (ремап `ToRecordId` по карте `_move_rmap` в
`CopyOneCollectionFromAttached`), ссылки на записи вне коллекции становятся межбазовыми на
базу-источник; конфиги полей-связей правятся согласованно (`RewriteMovedRelationConfigs`).

Если на записи переносимой коллекции ссылались **извне**, UI сперва показывает окно «Межбазовый
перенос оборвёт связи…» (`RelationDeleteImpact.ForCollectionExternalCrossMove`). При переносе
**внутри** одной базы окна нет — id не меняются, связи целы. Только само-связи внутри коллекции — тоже
без окна: они переписываются, а не рвутся.

Перенос **ветки-группы** между базами рёбра НЕ переносит (нужен межколлекционный ремап `ToRecordId` в
пределах ветки — отдельным заходом), но **конфиги полей-связей правит**
(`RewriteMovedGroupRelationConfigs`): цель внутри ветки → на новую коллекцию, цель снаружи → межбазовая
на источник. Иначе конфиг приезжал бы дословно и поле показывало бы ЧУЖИЕ записи целевой базы.

**PG-generic**-путь `CopyCollectionGeneric` не копирует ни рёбра, ни конфиги.

Для обоих путей (ветка группы и PG) предупреждение **строже обычного** —
`RelationDeleteImpact.ForCrossMoveDroppingAllLinks` срабатывает на ЛЮБЫХ связях переносимого, а не
только на внешних входящих. Иначе молча уехали бы два случая: коллекция с одними само-связями и ветка,
ссылающаяся только наружу. Текст окна прямо говорит, что теряются и связи ВНУТРИ переносимого.
Подробности — [data-types/relation.md](../../architecture/data-types/relation.md).

## Связанные документы

[overview](overview.md) · [import](import.md) · [export](export.md) · [architecture/database/multi-database](../../architecture/database/multi-database.md) · [architecture/data-types/relation](../../architecture/data-types/relation.md) · [features/constructor/table](../constructor/table.md)
