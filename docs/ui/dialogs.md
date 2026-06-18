# Диалоги

## Назначение

Модальные окна для создания/редактирования сущностей и подтверждений. Лежат в
[Controls/Dialogs/](../../Controls/Dialogs/), открываются через `ShowDialog<T>(owner)`
и возвращают результат. Все диалоги — это `Window` и получают иконку приложения
через глобальный стиль (см. [../features/icons.md](../features/icons.md)).

## Список диалогов

| Диалог | Назначение | Возвращает |
|---|---|---|
| [RecordDialog](../../Controls/Dialogs/RecordDialog.axaml.cs) | создание/редактирование события календаря (заголовок, даты, all-day, цвет, описание); поддерживает пресеты даты/времени | `RecordDialogResult?` (запись или флаг удаления) |
| [ConfirmDialog](../../Controls/Dialogs/ConfirmDialog.axaml.cs) | подтверждение действия (например, удаление N записей) | `bool` |
| [SelectDatabaseDialog](../../Controls/Dialogs/SelectDatabaseDialog.axaml.cs) | выбор целевой БД при мультибазе | `int?` (DbId) |
| [PropertyEditorDialog](../../Controls/Dialogs/PropertyEditorDialog.axaml.cs) | создание/редактирование свойства (поля) коллекции; режим переименования системной колонки | `bool?` + `ResultName` |
| [PropertyManagerDialog](../../Controls/Dialogs/PropertyManagerDialog.axaml.cs) | управление свойствами коллекции |  |
| [DisplayDialog](../../Controls/Dialogs/DisplayDialog.axaml.cs) | «Отображение»: видимость и уровни колонок | `bool?` |
| [RecordDisplayDialog](../../Controls/Dialogs/RecordDisplayDialog.axaml.cs) | «Отображение записей»: видимость/порядок строк | `bool?` |
| [CollectionDialog](../../Controls/Dialogs/CollectionDialog.axaml.cs) | создание/правка коллекции (имя, иконка, цвет, группа) |  |
| [GroupDialog](../../Controls/Dialogs/GroupDialog.axaml.cs) | создание/правка группы |  |
| [CreateItemDialog](../../Controls/Dialogs/CreateItemDialog.axaml.cs) | выбор, что создать (группа/коллекция/…) |  |
| [ImportExportDialog](../../Controls/Dialogs/ImportExportDialog.axaml.cs) | импорт/экспорт данных | см. [../features/import-export.md](../features/import-export.md) |

## Шаблон использования

```csharp
var dlg = new ConfirmDialog("Удалить запись?");
if (await dlg.ShowDialog<bool>(ownerWindow) == true) { /* ... */ }
```

Контроллеры ([CalendarControllerView](../../Views/ConstructorDb/Calendar/CalendarControllerView.axaml.cs),
[ConstructorControllerView](../../Views/ConstructorDb/ConstructorControllerView.axaml.cs))
открывают диалоги и применяют результат к репозиторию.

## Связанные документы

[[widgets]], [[../features/constructor-db/overview]], [[../features/calendar/overview]]
