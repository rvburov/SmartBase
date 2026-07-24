# Импорт и экспорт

## Назначение

Перенос данных извне и наружу: **импорт таблиц** (Excel/CSV/JSON → новые коллекции,
включая пакетный импорт листов и папок) и **экспорт коллекций** (одна, несколько или
все — в файлы `.xlsx`/`.xlsm`/`.csv`/`.json`, включая книгу «лист на коллекцию»).

## Где в коде

Импорт:

- Типы и контракты: [Importer.cs](../../../../Core/Importers/Importer.cs) (`ImportSpec`,
  `ImportSchema`, `ImportRow`, `SheetMode`, `ImportException`)
- Источники форматов: [Core/Importers/TableSources/](../../../../Core/Importers/TableSources/) —
  `ExcelTableSource`/`ExcelMergedSource` (ExcelDataReader), `CsvTableSource`,
  `JsonTableSource`, общие правила в `SourceHelpers`, контракт `ITableSource`
- Планировщик юнитов: [ImportPlanner.cs](../../../../Core/Importers/ImportPlanner.cs)
- Файловые ошибки: [ImportErrors.cs](../../../../Core/Importers/ImportErrors.cs)
- Запись в БД: `ConstructorRepository.ImportStreamedCollection(Isolated)`
  в [ConstructorRepository](../../../../Database/Repositories/ConstructorRepository.cs)
- Проверки и замеры: [Tests/ImportPerf](../../../../Tests/ImportPerf/Program.cs)

Экспорт:

- Типы и контракты: [Exporter.cs](../../../../Core/Exporters/Exporter.cs) (`ExportSpec`,
  `ExportColumn`, `ExportFormat`, `ExportException`)
- Писатели форматов: [Core/Exporters/TableWriters/](../../../../Core/Exporters/TableWriters/) —
  `XlsxTableWriter` (OpenXmlWriter, SAX), `CsvTableWriter`, `JsonTableWriter`,
  общие правила типизации в `WriterHelpers`, контракт `ITableWriter`
- Планировщик юнитов, санитайз и уникализация имён:
  [ExportPlanner.cs](../../../../Core/Exporters/ExportPlanner.cs)
- Файловые ошибки записи: [ExportErrors.cs](../../../../Core/Exporters/ExportErrors.cs)
- Чтение из БД: `ConstructorRepository.GetExportColumnsIsolated` /
  `ReadExportRowsIsolated` / `CountRecordsIsolated` / `LogExportIsolated`
- Пикер коллекций: [CollectionPickerFlyout](../../../../Controls/Components/CollectionPickerFlyout.cs)
- Проверки и замеры: [Tests/ExportPerf](../../../../Tests/ExportPerf/Program.cs)

Общее:

- UI: [ImportExportDialog](../../../../Controls/Dialogs/ImportExportDialog.axaml.cs) — возвращает
  `ImportRequest` или `ExportRequest`; оркестрация — `SidebarTree.OpenImportExport`
  (общий retry-цикл) → `RunImport`/`RunExport`
- Перенос коллекций/групп между БД (реальный путь): `MoveCollectionIsolated`/`MoveGroupIsolated`
  в [ConstructorRepository](../../../../Database/Repositories/ConstructorRepository.cs) (через `ATTACH`)


> Разбито на подстраницы: [импорт](import.md), [экспорт](export.md), [перенос между БД](cross-database-move.md).

## В планах

Экспорт/импорт календаря как календаря (например, iCal) не реализован.
См. [../ROADMAP.md](../../../ROADMAP.md).

## Связанные документы

[[../constructor/overview]], [[../../architecture/database/multi-database]], [[../../architecture/performance/overview]]
