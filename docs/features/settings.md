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

`Initialize(registry, isMounted, queueStatus)` даёт настройкам реестр, способ узнать,
смонтирована ли БД, и статус фоновой очереди монтирования. Список
(`RefreshDatabaseList`) показывает все БД из реестра со статусом: дефолтная /
смонтирована / серым живой статус очереди («в очереди…», «Обновление · 45% · ≈ 2 мин») /
красным «недоступна…» с кнопкой «Подключить заново».

Операции:

- **Создать БД** — собственное русское окно
  [CreateDatabaseDialog](../../Controls/Dialogs/CreateDatabaseDialog.axaml.cs) (имя + папка
  через «Обзор…») вместо системного save-диалога: тот говорит на языке ОС и предлагает
  ПЕРЕЗАПИСАТЬ существующий файл — здесь занятое имя даёт ошибку «уже существует»,
  перезапись невозможна в принципе. Регистрируется и монтируется (схема создаётся при
  монтировании).
- **Подключить** существующий `.db` — `OpenFilePicker` (в т.ч. из сетевой папки); уже
  подключённый файл даёт уведомление «уже подключена», а не тихий no-op.
- **Отключить** — с подтверждением ([ConfirmDialog](../../Controls/Dialogs/ConfirmDialog.axaml.cs)); **файл на диске не удаляется**, дефолтную отключить нельзя.
- **Переподключить** недоступную БД.

Изменения реестра уходят наверх событиями `DatabaseAdded` / `DatabaseRemoved` /
`DatabaseReconnectRequested`; MainWindow ставит базу в фоновую очередь
[MountQueue](../../Database/MountQueue.cs) (в хвост — даже если сейчас мигрирует
другая), размонтирует в [Workspace](../../Database/Workspace.cs) и обновляет сайдбар.
Модального окна нет: прогресс виден в статусе строки списка и в заглушке узла
сайдбара, по готовности база оживает сама. Подробнее —
[../architecture/multi-database.md](../architecture/multi-database.md).

## В планах

Автозапуск при входе в систему ([Utils/Autostart.cs](../../Utils/Autostart.cs)) пока
не реализован (файл-заглушка).

## Связанные документы

[[../architecture/multi-database]], [[../architecture/databases]], [[../ui/theming]]
