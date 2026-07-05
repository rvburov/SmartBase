# Настройки

## Назначение

Экран настроек с разделами: **Оформление**, **Управление базой данных**,
**О программе**. Главная функциональная часть — управление списком подключённых БД.

## Где в коде

- View: [SettingsView.axaml.cs](../../Views/Settings/SettingsView.axaml.cs)
- Строка списка БД: [DatabaseRow.cs](../../Views/Settings/DatabaseRow.cs)
- Боковая навигация: [SettingsSidebar](../../Controls/Components/SettingsSidebar.axaml)
- Хранилище значений: app.db через [AppSettings](../../Utils/AppSettings.cs) (см. [../architecture/databases.md](../architecture/databases.md))

## Разделы

`ShowCategory(key)` переключает панель и заголовок:

- **Оформление** (`appearance`) — масштаб интерфейса (1.0–1.3), хранится как `scale` в app.db (`AppSettings.LoadScale/SaveScale`)
- **Управление базой данных** (`database`) — список и операции над БД
- **О программе** (`about`) — версия (из сборки)

## Управление базой данных

`Initialize(registry, isMounted)` даёт настройкам реестр и способ узнать, смонтирована
ли БД. Список (`RefreshDatabaseList`) показывает все БД из реестра со статусом
(дефолтная / смонтирована).

Операции:

- **Создать БД** — `SaveFilePicker` (`*.db`), регистрируется и монтируется (схема создаётся при монтировании).
- **Подключить** существующий `.db` — `OpenFilePicker` (в т.ч. из сетевой папки).
- **Отключить** — с подтверждением ([ConfirmDialog](../../Controls/Dialogs/ConfirmDialog.axaml.cs)); **файл на диске не удаляется**, дефолтную отключить нельзя.
- **Переподключить** недоступную БД.

Изменения реестра уходят наверх событиями `DatabaseAdded` / `DatabaseRemoved` /
`DatabaseReconnectRequested`; MainWindow монтирует/размонтирует БД в
[Workspace](../../Database/Workspace.cs) и обновляет сайдбар. Монтирование (подключение и
переподключение) идёт **в фоне** (`MountWithProgressAsync`): большой файл открывается
секунды (миграции/WAL/бэкафилл), UI не блокируется; если дольше 400 мс — модальное окно
«Подключение базы данных» с бегущей полосой ([ProgressDialog](../../Controls/Dialogs/ProgressDialog.axaml.cs)),
статус в списке обновляется по завершении. Подробнее —
[../architecture/multi-database.md](../architecture/multi-database.md).

## В планах

Автозапуск при входе в систему ([Utils/Autostart.cs](../../Utils/Autostart.cs)) пока
не реализован (файл-заглушка).

## Связанные документы

[[../architecture/multi-database]], [[../architecture/databases]], [[../ui/theming]]
