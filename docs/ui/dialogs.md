# Диалоги

## Назначение

Модальные окна для создания/редактирования сущностей и подтверждений. Лежат в
[Controls/Dialogs/](../../Controls/Dialogs/), открываются через `ShowDialog<T>(owner)`
и возвращают результат. Все диалоги — это `Window` и получают иконку приложения
через глобальный стиль (см. [../features/icons.md](../features/icons.md)).

## Список диалогов

| Диалог | Назначение | Возвращает |
|---|---|---|
| [RecordDialog](../../Controls/Dialogs/RecordDialog.axaml.cs) | создание/редактирование события календаря (заголовок, даты, all-day, описание, коллекция; «База данных» при мультибазе; «Дата записи» — выбор/перенос поля-даты при нескольких видимых датах); пресеты даты/времени | `RecordDialogResult?` (запись или флаг удаления) |
| [ConfirmDialog](../../Controls/Dialogs/ConfirmDialog.axaml.cs) | подтверждение действия (например, удаление N записей) | `bool` |
| [PropertyEditorDialog](../../Controls/Dialogs/PropertyEditorDialog.axaml.cs) | создание/редактирование свойства (поля) коллекции; для типа «Дата» — тумблер «Отображать в календаре» (`IsCalStart`); режим переименования системной колонки | `bool?` + `ResultName` |
| [DisplayDialog](../../Controls/Dialogs/DisplayDialog.axaml.cs) | «Отображение»: видимость и уровни колонок | `bool?` |
| [RecordDisplayDialog](../../Controls/Dialogs/RecordDisplayDialog.axaml.cs) | «Отображение записей»: видимость/порядок строк | `bool?` |
| [CollectionDialog](../../Controls/Dialogs/CollectionDialog.axaml.cs) | создание/правка коллекции (имя, иконка, цвет, группа) |  |
| [GroupDialog](../../Controls/Dialogs/GroupDialog.axaml.cs) | создание/правка группы |  |
| [CreateItemDialog](../../Controls/Dialogs/CreateItemDialog.axaml.cs) | выбор, что создать (группа/коллекция/…) |  |
| [LocationDialog](../../Controls/Dialogs/LocationDialog.axaml.cs) | «Расположение» коллекции/группы: выбор целевой БД и места (группа/корень), без переименования | `LocationDialogResult?` (целевые `GroupId`, `DbId`) |
| [RecordLocationDialog](../../Controls/Dialogs/RecordLocationDialog.axaml.cs) | «Расположение» записи: выбор целевой БД и коллекции + «Перенести все свойства» (создавать в цели отсутствующие свойства). Перенос данных грамотный — значения сопоставляются по имени поля с приведением типов (см. [../features/constructor-db/tables.md](../features/constructor-db/tables.md)) | `RecordLocationResult?` (`DbId`, `CollectionId`, `CreateMissingFields`) |
| [ImportExportDialog](../../Controls/Dialogs/ImportExportDialog.axaml.cs) | комбобокс «Режим»: импорт таблиц (Excel/CSV/JSON) в новые коллекции (файл/папка, стандартный/ручной тип, чекбоксы раскладки листов) или экспорт коллекций в `.xlsx`/`.xlsm`/`.csv`/`.json` (пикер коллекций с поиском и чекбоксами — [CollectionPickerFlyout](../../Controls/Components/CollectionPickerFlyout.cs)); пояснение стандартного типа меняется под формат | `ImportRequest?` \| `ExportRequest?`, см. [../features/import-export.md](../features/import-export.md) |
| [ProgressDialog](../../Controls/Dialogs/ProgressDialog.axaml.cs) | прогресс долгой фоновой операции (импорт, экспорт, дубль/удаление/перенос коллекций и групп, удаление/дубль свойств) | закрывается программно (`Done()`) |
| [CreateDatabaseDialog](../../Controls/Dialogs/CreateDatabaseDialog.axaml.cs) | «Создать базу данных»: имя + папка («Обзор…») вместо системного save-диалога — тот говорит на языке ОС и предлагает перезаписать существующий файл; здесь занятое имя даёт ошибку «уже существует», перезапись невозможна | `string?` (полный путь `…/имя.db`) |

`ProgressDialog` — не возвращает результат и не закрывается пользователем (крестик/Alt+F4
заблокированы до `Done()`). Управляется с UI-потока: `SetProgress(0..1, фаза)` — проценты,
`SetSaving(фаза)` — бегущая полоса без процентов (коммит большой транзакции, чтобы 100% не
выглядели зависшими), `SetInfo(текст)` — строка под заголовком. Сама тяжёлая работа идёт на
ОТДЕЛЬНОМ подключении в фоне, прогресс маршалится на UI через `IProgress<T>` →
`Dispatcher.UIThread.Post` (см. [[../architecture/performance]], принцип 5 и раздел «Сделано»).
Отменяемые операции (сейчас — импорт и экспорт) включают кнопку «Отмена» через
`EnableCancel(колбэк)`: по клику кнопка гаснет, полоса переходит в «Отмена…», дальнейшие
`SetProgress` игнорируются; окно по-прежнему закрывает только `Done()` — вызывающий делает
это после отката/очистки. Остальные операции кнопку не включают и не меняются.

## Шаблон использования

```csharp
var dlg = new ConfirmDialog("Удалить запись?");
if (await dlg.ShowDialog<bool>(ownerWindow) == true) { /* ... */ }
```

Контроллеры ([CalendarControllerView](../../Views/ConstructorDb/Calendar/CalendarControllerView.axaml.cs),
[ConstructorControllerView](../../Views/ConstructorDb/ConstructorControllerView.axaml.cs))
открывают диалоги и применяют результат к репозиторию.

## Ошибки валидации

Диалоги с проверкой ввода (`RecordDialog`, `CreateItemDialog`, `GroupDialog`,
`CollectionDialog`, `PropertyEditorDialog`, `ImportExportDialog`) показывают ошибки
через единый виджет [ErrorBanner](widgets.md) — красный баннер с иконкой и текстом.
Диалог зовёт `ErrorBanner.Show(msg)` при неудачной валидации и `Hide()` при правке
поля; длинный текст переносится, окно (`SizeToContent="Height"`) подстраивает высоту.

## Связанные документы

[[widgets]], [[../features/constructor-db/overview]], [[../features/calendar/overview]]
