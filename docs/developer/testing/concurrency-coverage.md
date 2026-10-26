# Матрица многопользовательского покрытия

Инвентарь пользовательских функций и тестов на 26.10.2026. Матрица связывает разделы
программы с конкретными наборами проверок и показывает, какой вид нагрузки к ним применим.
Это карта покрытия, а не заявление, что все перечисленные проверки прошли на одной машине.
Команды, результаты первого нагрузочного этапа и UI-наблюдателя — в
[concurrency.md](concurrency.md); итоги расширенных интеграционных прогонов — ниже.
После исправлений последние результаты всех 24 нагрузочных вариантов успешны;
[сводка повторных прогонов](#нагрузка-после-исправлений-26102026) отделена от исходных падений.

## Текущее состояние после исправлений

Последний полный `Integration`: **2200 случаев, 2194 PASS, 0 FAIL, 6 известных пропусков**.
Источник — `/workspace/.setup/concurrency-fixed-final-checks/Integration/Integration.trx`.
Шесть пропусков остаются пропусками: четыре относятся к платформенной среде, два — к ранее
отмеченным отдельным дефектам системных колонок/пользовательских полей задачи.
Полный Unit завершился **2933/2933 PASS**. Последние результаты **24/24** нагрузочных
вариантов успешны: в каждом 1000 сессий готовы и завершили работу, итоговые данные проверены.
Полные наборы расширений завершились **1068/1068 PASS**, без пропусков: все девять движков
и отдельный реестр. `ConcurrentUi` прошел все три режима, `BackupUi` и `DocReader` — без
расхождений. В `DocReader` потребовалось обновить проверку владельца подписки: объединенный
обработчик захватывает панель через closure; проверка сохраненных delegate по-прежнему
ловит дубли и утечки после detach. Production для этого повтора не менялся.
Восемь самостоятельных `RecordPage`-режимов прошли: `empty`, `fileauto`, `fileprobe`,
`relations`, `relationcomputed`, `relationintegrity`, `backlinks`, `fileload --files 100000`.
Числа старых неуспешных прогонов ниже сохранены как история обнаружения
ошибок, а не описание текущего исхода исправленных регрессий.

Итоговые журналы и TRX находятся в `/workspace/.setup/concurrency-fixed-final-checks/`.
Первый `DocReader.log` сохраняет несовместимость старого оракула с новым handler;
успешный повтор — `/workspace/.setup/concurrency-fixed-docreader-retest.log`.
Финальный UI: `jobs-live` — 642,3 мс, максимальный heartbeat gap 437,7 мс;
`jobs-snapshot` — 2556,0 мс / 2214,3 мс; `notifications` — 46,6 мс / 37,2 мс.
Живое обновление дополнительно проверяет пакет 1000 отчетов: те же контролы, десять
обновлений текста и одно кольца вместо тысячи перестроений. Построение 1000 строк
проверено функционально; пауза этого первого построения сохраняется в метриках и не
объявляется выполнением какого-либо SLA.

## Как читать матрицу

- **Функциональные** — команды проверяются на настоящих временных БД/файлах либо как чистая
  логика. Само по себе это не проверка одновременной работы.
- **Сосед** — второй контекст или управляемое вмешательство между шагами первой операции.
  Такой тест доказывает конкретный конфликт, но не обслуживание 1000 участников.
- **Параллельно** — несколько независимых участников действительно выполняют операции
  одновременно; число участников и барьеры должны быть видны в тесте.
- **1000** — есть сохраненный результат прогона на 1000 сессий. Готовность, полезная работа
  и полное завершение всех сессий учитываются отдельно; отказавшие не исключаются из знаменателя.
- **UI** — есть стенд настоящего интерфейса без экрана. Наличие такого стенда не означает,
  что он уже проверен одновременно с 1000 писателями.
- **Сбой** — в тесте создается конкретная ошибка, отмена, потеря файла либо завершение
  процесса; моделирование одного отказа не покрывает все аппаратные и сетевые отказы.

В таблицах «есть» означает наличие указанного сценария в исходниках. Его фактический исход
нужно брать из журнала/TRX/JSON соответствующего прогона. Поля «не проверено» нельзя заменять
на «пройдено» на основании соседнего теста, зеленой сборки или наличия набора.

## Модель пользователей

Для общей SQLite/PostgreSQL-базы участник — собственный `AppDbContext`, подключение,
синтетический ключ пользователя и снимок прав. После общего старта проверяются результаты
каждого участника, значения, аудит, журнал и производные индексы. Тысяча итераций одного
последовательного контекста этой модели не соответствует.

Интерфейс одного приложения работает на своем UI-потоке. Для проверки реакции на соседей
нужен UI-наблюдатель на фоне независимых писателей: обновление строк/видов, выделение,
прокрутка, переходы, уведомления, частота перечитываний и задержки UI. Прямой вызов методов
одного окна из 1000 потоков нарушает контракт интерфейса и не моделирует 1000 пользователей.

Личные настройки, реестр подключений, кеш документов и папка данных приложения обычно
раздельные у пользователей. Их многопроцессная модель — отдельные папки/хранилища, а не
1000 потоков, одновременно меняющих `AppSettings.AttachStore` одного процесса. Общая папка
заметок или выходных документов требует отдельных тестов конфликта имен и сохранности.
Чистые вычисления и обработку независимых файлов можно проверять параллельными вызовами
на синтетических данных с ограниченным числом исполнителей и явным учетом очереди.

## Наборы расширенного этапа

Таблица перечисляет дополнительные интеграционные наборы помимо 27 случаев
`ConcurrentLifecycleTests`. Числа учитывают варианты `InlineData`/`MemberData`, а не результаты запуска.
Наличие сценария для PG не отменяет обязательной проверки, что он не был пропущен.

| Набор в `Tests/Integration` | Случаев | Модель и проверяемое поведение |
|---|---:|---|
| `ConcurrentFieldTypesTests` | 34 | 10 хранимых типов × 3 профиля с 8 редакторами, 3 смешанных запуска десяти разных типов и 1 контроль состава; set/read/update/read/duplicate/move, точные значения/кодеки и календарь |
| `ConcurrentStaleRecoveryTests` | 12 | Тот же контекст после устаревшей правки удаленной записи/свойства продолжает работу; отложенные история/события не должны попасть в следующую успешную операцию |
| `ConcurrentStructureTests` | 27 | 8 контекстов; создание свойств, устаревшая схема/источник, два переноса одной записи, общая недостающая схема, массовое удаление с читателями, связь против удаления цели |
| `ConcurrentPlanningTests` | 9 | 8 контекстов; задачи/роли/сроки/стадии, перенос задач и календарные изменения с читателем |
| `ConcurrentLibraryTests` | 13 | 8 DB-участников и отдельная очередь файлового автоподбора; общие FTS/чтение, отмена/удаление и 1000 фоновых работ, в том числе большие счетчики |
| `ConcurrentAccessTests` | 18 | 8 контекстов; регистрация одинаковых/разных ключей, явная перезагрузка отозванных прав, события и отдельные курсоры получателей |
| `ConcurrentIntegrationTests` | 12 | 8 контекстов; повтор привязки/шага по независимым ключам, подтвержденный префикс при отмене и захват одного файла обмена с повторной доставкой |
| `ConcurrentBackupTests` | 12 | Живые транзакционные писатели SQLite/PG, очередь ручных копий, удаление готовой копии/каталога, kill/commit дочернего клиента; настоящий PG dump/restore |
| `ConcurrentConnectionRecoveryTests` | 2 | Принудительный разрыв только собственных тестовых PG-сессий; отказ старого подключения и чтение/запись через новое |
| `ConcurrentPersonalFilesTests` | 4 | 32 независимые папки/конфликта имен/редактора; 1000 личных хранилищ через 8 исполнителей, без переключения глобального хранилища из рабочих потоков |
| `AtomicWriteBoundaryTests` | 8 | PG закрепляет тип/формат свойства; вложенный отказ восстанавливает навигации и только исходные pending-правки владельца внешней транзакции |
| `SqliteTransactionAdmissionTests` | 10 | Одиночный EF SaveChanges настроек/ширин, sync/async BEGIN, отмена, dispose при открытом подключении, отказ native BEGIN, один lease внешнего владельца и обход очереди native read snapshot |
| `ConcurrentTableStyleTests` | 15 | Стили строки/колонки/ячейки ждут очередь SQLite; общий откат с внешней транзакцией, повтор после чужой очистки и атомарный отказ сброса ячеек/колонки |
| `ConcurrentMoveAtomicityTests` | 6 | Поздний отказ переноса откатывает схему, копию, проекцию и удаление; два межбазовых переноса захватывают один источник |
| `ConcurrentAutoRelationParentTests` | 3 | Удаление локальной цели после предварительного автоподбора не оставляет нового ребра без живого родителя |
| `CalendarReadSnapshotTests` | 4 | Управляемый перенос даты между чтениями дней и сохранение владения внешней транзакцией на трех профилях |
| `ConcurrentExchangeAtomicityTests` | 24 | Поздний отказ поля/журнала откатывает полную строку и допускает повтор; конкурирующий upsert общего ключа с кириллицей, пробелами и символами шаблона |

`MultiUserTestDatabase` создает собственную временную SQLite-базу или отдельную PG-базу
с уникальным именем. `RunClients` подготавливает контексты до общего старта и учитывает
исключения и незавершенные потоки. Малые тесты не становятся нагрузкой на 1000 клиентов
только потому, что используют ту же обвязку.

`ConcurrentFieldTypesTests` покрывает хранимые `text/number/date/select/status/multiselect/checkbox/url/file/user`.
Числовая нормализация и кодеки списков/путей проверяются отдельно от равенства сырых строк;
перенос использует готовую целевую схему. `relation` хранит ребра, а `lookup/rollup` и
аудит вычисляются, поэтому их нельзя считать покрытыми этим путем `SetFieldValue`:
для них нужны настоящие API связей/вычислений и указанные ниже отдельные наборы.

Нагрузочный `multiuser` дополнительно принимает `planning`, `library`, `integrations`,
`access` и `structure`. У этих режимов собственные журналы подтвержденных результатов:
задачи/события/проекция, библиотечные карточки/автоподбор/FTS, привязки/повтор/JSON,
разрешенные и запрещенные действия, структура/дерево/очистка метаданных соответственно.
В `access` половина клиентов имеет право записи, половина — только просмотра:
ожидаемый запрет не считается успешно выполненной записью. Режимы поддерживают `--clients 1000`;
факт обслуживания всех участников подтверждается только итоговым JSON конкретного прогона.

`Tests/ConcurrentUi` содержит три режима одного настоящего UI-наблюдателя: `jobs-live`
(1000 сообщений для 10 работ), `jobs-snapshot` (1000 работ при открытии панели) и
`notifications` (1000 результатов копирования через fake notifier/badge). Проверяются
очередь UI, итоговые строки/значки, удаление завершенной ошибки и маршрутизация уведомления.
Исходный прогон трех режимов до исправлений прошел функциональные проверки 26.10.2026;
его задержки и пауза живого обновления описаны в
[concurrency.md](concurrency.md#ui-наблюдатель-расширенного-этапа). Финальный повтор после
исправлений также прошел; новые метрики приведены в текущей сводке выше.
Это не 1000 окон и не проверка видимости всех строк в физическом viewport. Очередь уведомлений
ограничена продуктовым лимитом, поэтому проверка маршрута последнего уведомления не обещает
сохранение истории всех 1000 уведомлений.

## Ядро конструктора и данные

Все пути в столбце проверок относятся к `Tests/Integration/`, если не указано другое.
Большие объемы, число клиентов, провайдер, продолжительность и исходы должны сопровождать
каждый отчет; не следует переносить результат маленькой двухколоночной фикстуры на большую БД.

| Раздел и действия | Имеющиеся проверки | Совместная работа, UI и сбои | Оставшаяся проверка |
|---|---|---|---|
| Записи: создать, заполнить, изменить, прочитать, дублировать, удалить | `ConstructorRepositoryCrudTests`, `RecordLifecycleTests`, `ConcurrentLifecycleTests` | Параллельно 8 участников; 1000 в `multiuser lifecycle` на WAL, DELETE+FULL и PG | Длинная смешанная нагрузка, большая исходная БД и распределенные процессы |
| Одна ячейка: первое значение и перезапись | `ContentionTests`, `PgProviderTests.Concurrent_writes_last_write_wins`, `ConcurrentLifecycleTests` | Два писателя и 8 участников; 1000 в `multiuser hotcell` | Неодинаковые задержки/клиенты и обрыв соединения между commit и ответом |
| Запись удалена соседом перед сохранением | `SharedDbResilienceTests`, `ConcurrentLifecycleTests`, `ConcurrentStaleRecoveryTests` | Свежие родители вместо устаревшего трекера; текст/дата, отсоединенное значение и следующая здоровая правка тем же контекстом | UI-сценарий с открытым редактором, чужими обновлениями и несохраненным вводом |
| Неуспешная составная правка и откат | `PgWriteTransactionTests`, `ConcurrentLifecycleTests`, `AtomicWriteBoundaryTests`, `ConcurrentExchangeAtomicityTests` | Отказ аудита/последней колонки/журнала, savepoint, восстановление навигаций и pending владельца; 1000 явных rollback | Смерть клиента между этапами пользовательской команды, потерянное подтверждение commit и безопасный повтор |
| Порядок записей, вставка после якоря, перемещение вверх/вниз | `RecordPositionTests`, `KeysetPaginationTests` | Функциональные; часть CRUD имеет соседа | Одновременная вставка после одного якоря, сдвиг порядка во время удаления и пагинации |
| Коллекции и группы: создание, вложенность, копирование, удаление | `ConstructorRepositoryCrudTests`, `DuplicateCollectionTests`, `BulkDeleteTests`, `SectionCollectionsTests`, `ConcurrentStructureTests` | 8 создателей/редакторов схемы, удаленная коллекция; `multiuser structure` и UI `Sections --only sidebarops` | Копирование полной общей ветки во время правок потомков и UI-наблюдатель |
| Свойства: добавление, тип, флаги, изменение, массовое удаление | `FieldLifecycleTests`, `FieldBulkOpsTests`, `FieldTypeMatrixTests`, `ConcurrentFieldTypesTests`, `RoleAdoptionTests`, `SectionSetupPlanTests`, `ConcurrentStructureTests`, `AtomicWriteBoundaryTests` | 8 редакторов, удаленное свойство, конкурентное создание схемы при переносе; PG закрепляет тип/формат до конца правки; `multiuser structure` | Смена роли коллекции при активном UI, устаревший редактор и несохраненный ввод |
| Перенос записей между коллекциями одной БД | `CrossMoveTests`, `RecordLifecycleTests`, `ConcurrentLifecycleTests`, `ConcurrentStructureTests`, `ConcurrentMoveAtomicityTests` | Разные источники и два переноса одного источника, устаревший дубль, полный откат при позднем отказе; 1000 через `multiuser lifecycle` | Длинный поток переносов с удалением/сменой схемы и UI-наблюдателем |
| Перенос коллекций/групп между БД и провайдерами | `CrossMoveTests`, `CrossMoveBatchTests`, `PendingCopyTests`, `CopyTraceTests`, `PgProviderTests`, `PgCalendarSummaryTests`, `ConcurrentMoveAtomicityTests` | Пакетные копии, отказ удаления и маркеры незавершенного переноса; два межбазовых переноса одной записи | Чужие правки полной ветки во время cross-DB копии, смерть клиента между коммитами разных БД, одновременная смена схемы |
| Таблица: keyset, страницы, список значений, фильтр, поиск, сортировка | `GridPagesTests`, `ColumnValueListTests`, `ColumnFilterCascadeTests`, `ColorQueryFilterTests`, `FileQueryTests`, `PgSearchTests` | Чтение страниц/значений под 1000 писателями входит в `lifecycle`; остальные фильтры функциональные | `ConcurrentStructureTests`: 7 читателей страниц/значений/фильтра при массовом удалении; остается длинный поток переносов и полный проход keyset |
| Таблица: диапазоны, копирование, вырезание, вставка, очистка, отмена | `CellRangeWriteTests`, `CellLinksWriteTests`, `PasteGrowthTests`; Unit `CellRangeTests`, `TableClipboardFormatTests`; `GridProto --keys/--edit/--ops` | Функциональные и headless UI | Сосед меняет/удаляет ячейки между чтением буфера и записью/отменой; 1000 фоновых сессий и UI-наблюдатель |
| Таблица: стили, ширины, выравнивание, видимость и раскладка | `ConstructorUiSettingsTests`, `TableStyleOrderTests`, `TableColorTests`, `TableTextAlignTests`, `ConcurrentTableStyleTests`, `SqliteTransactionAdmissionTests`; `EditGeometry`, `WideGui` | Стили строки/колонки/ячейки атомарны, повтор после чужой очистки, откат внешней транзакции; одиночные EF-сохранения настроек/ширин проходят FIFO благодаря SQLite Always | Живой UI при потоке чужих стилей/схемы, разные личные хранилища, платформенная геометрия |
| Страница записи и редакторы всех типов | `RecordPageTitleTests`, `DescriptionFieldTests`, `FieldTypeMatrixTests`; `RecordPage --only behave/empty/relations/relationcomputed/relationintegrity/backlinks` | Функциональные и headless UI; отдельные stale-editor регрессии | Открытая карточка при потоке чужих обновлений, удалении и смене свойств; фокус/несохраненный ввод |
| Системные авторы/даты и поля пользователя | `UserValueTests`, `UserOptionsTests`, `ProfileFieldsTests` | Функциональные; аудит переноса проверяется параллельно в `ConcurrentLifecycleTests` | Полный путь фильтра/сортировки/экспорта вычисляемых колонок и различие задач со сроком/без — открытые дефекты Roadmap |

## Связи и вычисляемые данные

| Раздел и действия | Имеющиеся проверки | Совместная работа, UI и сбои | Оставшаяся проверка |
|---|---|---|---|
| Ручные связи, множественные цели, условия выбора, обратные ссылки | `RelationLinkTests`, `RelationMultiPropertyTests`, `RelationConditionTests`, `RelationBacklinkTests`, `ConcurrentStructureTests` | Функциональные и headless `RecordPage`; локальная цель согласована с удалением при записи ребра | Одновременная замена одной ячейки несколькими авторами, изменение условия/доступа во время выбора |
| Запрет удаления и сохранность ссылок при переносе/копии | `RelationRestrictDeleteTests`, `RelationCopyLifecycleTests`, `RelationCrossMoveLifecycleTests`, `RelationCalendarMoveSafetyTests` | Функциональные и управляемые отказы; PG-варианты в `RelationBulkPgTests` | `ConcurrentStructureTests` задает добавление ребра после проверки цели против ее удаления; остается поток связей и запретов удаления под нагрузкой |
| Автосопоставление и пересчет производных полей | `RelationAutoTests`, `RelationAutoPairsTests`, `RelationReactiveTests`, `RelationGlobalRefreshTests`, `RelationComputedTests`, `RelationComputedColumnTests`, `ConcurrentAutoRelationParentTests` | Устаревший предварительный просмотр, локальная цель удалена перед записью, перекрывающиеся версии `RelationPropertyTests`; UI computed | 1000 производителей изменений источников, отмена старого пересчета, справедливость очереди пересчета и отсутствие потерянных пересчетов |
| Межбазовая идентичность и переносимые форматы | `RelationBaseIdentityTests`, `RelationPortableJsonTests`, `RelationPortableExportSafetyTests`, `RelationCopyProjectionTests` | Функциональные на отдельных БД | Параллельные изменения реестра баз/доступа при разрешении межбазовой ссылки |
| Сканирование/починка целостности и I/O целей | `RelationIntegrityScanTests`, `RelationIntegrityFieldScanTests`, `RelationIntegrityRepairTests`, `RelationProjectionIoTests`, `RelationIoSafetyTests`, `RelationIntegrityBackupTests` | Отмена, недоступность и отказ резервной копии; UI проверки целостности | Сканирование/починка на фоне живых писателей, права изменены во время прохода |

## Планирование, документы и фон

| Раздел и действия | Имеющиеся проверки | Совместная работа, UI и сбои | Оставшаяся проверка |
|---|---|---|---|
| Календарь: создание, перенос, растяжение, удаление и семь видов | `CalendarProjectionTests`, `CalendarWindowTests`, `CalendarSpansTests`, `ConcurrentPlanningTests`, `CalendarReadSnapshotTests`; `Sections --only calops/frames` | Управляемый перенос между чтениями дней, один снимок и внешний владелец транзакции; 1000 `planning`/`lifecycle` | Живой UI всех видов при потоке изменений, удержание прокрутки/выделения |
| Сводки дней/длительности и календарные счетчики | `CalendarDayCountsTests`, `PgCalendarSummaryTests` | Сводки проверяются после 1000 сессий; PG триггеры и отказ cross-DB переноса | `ConcurrentPlanningTests` и `multiuser planning` сверяют сводки/проекцию; остается длинный поток пересекающихся протяженных событий |
| Задачи: исполнитель, назначающий, стадии, фильтры и страницы | `TaskRecordsTests`, `TaskStagesTests`, `TaskRoleGuardTests`, `TaskPagingTests`, `TaskFilterCountsTests`, `TaskEventWriteTests` | Функциональные, роли и события; UI в `Sections` | `ConcurrentPlanningTests`: задачи разных авторов/ролей и перенос; `multiuser planning`; остаются конкурирующие назначения одной задачи и UI фильтров |
| События задач, курсоры и уведомления календаря/копий | `TaskEventReaderTests`, `NotificationServiceTests`, `BackupNotificationTests`, `LinuxNotificationsTests`; Unit schedulers | Источники настоящие, показ через fake notifier/badge; `BackupUi` | `ConcurrentAccessTests` сверяет курсоры получателей; `ConcurrentUi notifications` принимает 1000 результатов копии; остаются restart task-reader и native доставка по ОС |
| Библиотека: file-роли, лента, страницы, сортировка/поиск | `DocRolesTests`, `DocPagingTests`, `DocStampsTests`, `PgLibraryTests`; `Sections --only libraryops` | Функциональные; UI-сосед добавляет документы без перезахода | `ConcurrentLibraryTests` проверяет одновременные правки/удаления файлов и курсоры; `multiuser library`; остается смена ролей с живым UI |
| Чтение документов, кеш текста, FTS, миниатюры и просмотр | `DocTextExtractorTests`, `DocTextIndexTests`, `DocIndexServiceTests`, `DocPageSourceTests`, `DocSheetSourceTests`, `DocReadTextTests`, `DocThumbnailTests`; `DocReader` | Отмена, очистка при незавершенной работе, изменившийся файл, поврежденный кеш и UI | `ConcurrentLibraryTests` проверяет общую очередь, удаленный файл и stop/clear/restart; `multiuser library`; остается закрытие настоящего окна во время очереди |
| Файловые свойства: ручные ссылки, автоподбор, каталог, фоновые проверки | `FileAutoCreationTests`, `FileAutoPermissionTests`, `FileAutoCatalogTests`, `FileAutoLinkTests`, `FileAutoSafetyTests`; `RecordPage --only fileauto/fileprobe/fileload` | Commit/rollback, отмена, отзыв прав, недоступная папка; есть UI и стенд больших метаданных | `ConcurrentLibraryTests`: общий каталог, синхронный и очередной автоподбор, latest/cancel/delete; `multiuser library`; остаются смена правил во время сканирования и сетевой I/O |
| Фоновые работы: этап, прогресс, ошибка, удаление из панели | Unit `BackgroundJobsTests`; `MountBackgroundTests`; `BackupUi` | Функциональные; фоновые mount/backup связываются с UI | `ConcurrentLibraryTests`: 1000 работ/большие счетчики; `ConcurrentUi jobs-live/jobs-snapshot`: производители, строки/значки/ошибка/уборка; остаются поздние события после dispose |
| Копирование SQLite, расписание, ротация и прерванные попытки | `BackupServiceTests`, `BackupInterruptionTests`; Unit `BackupSettingsTests`, `BackupScheduleTests`, `BackupRotationTests`; `BackupUi` | Реальный дочерний процесс, kill до/после публикации, delete/corrupt `.part`, отказ переименования, restart | `ConcurrentBackupTests`: живые писатели, последовательные снимки при двух запросах, удаление копии/каталога; остаются аппаратные и реальные сетевые отказы |
| Дамп/восстановление PostgreSQL и контроль копий связей | `PgBackupTests`, `RelationIntegrityBackupTests` | Требуются сервер и нативные утилиты; часть отказов моделируется | `ConcurrentBackupTests`: нативный дамп при писателе, восстановление собственной БД и проверка содержимого; нужны password-auth и утилиты, остаются реальные сбои сервера |

## Обмен, локальные файлы и оболочка

| Раздел и действия | Имеющиеся проверки | Совместная работа, UI и сбои | Оставшаяся проверка |
|---|---|---|---|
| Импорт/экспорт коллекций: CSV, книги, JSON, переносимые ссылки | `ImportMatrixTests`, `ExportMatrixTests`, `CsvExportTests`, `ExportTraceTests`, `JsonTableSourceTests`, `PgBulkWriteTests`, `BigBookMemoryTests` | Функциональные, потоковые объемы, ограничения памяти; benchmarks import/export | Снимок экспорта при чужих правках/удалениях, параллельные импорты, отмена/сбой внутри пакета без частично подтвержденного результата |
| Соединения/запросы, переменные, привязки и исходящие действия | `IntegrationsRegistryTests`, `RequestRunnerTests`, `RequestPreviewTests`, `CollectionBindingTests`, `OutgoingSourceTests`, `EgressHttpTests`, `EgressMirrorTests`, `ConcurrentExchangeAtomicityTests` | Локальные HTTP-ответы, запреты политики и ошибки сети; атомарная строка привязки, поздний отказ журнала, общий ключ и 1000 `integrations` | Шторм внешних HTTP-запросов/повторов, лимиты сетевой очереди и одновременная правка конфигурации привязки |
| Входящие запросы, команды и папки обмена | `IngressHandlerTests`, `IngressReadTests`, `IngressRepliesTests`, `InboxReceiverTests`, `InboxCommandTests`, `InboxServiceTests`, `InboxModesTests`, `InboxFolderTests`, `InboxStreamTests` | Функциональные, допуск, квитанции и управляемые отказы | `ConcurrentIntegrationTests`: один файл у 8 приемников и повтор по квитанции; остаются разные файлы с одним MessageId, смерть получателя и конкурентные команды одного ключа |
| Сценарии: вычисления, условие, запись, копия/перенос, вызов действия | `ScenarioRunnerTests`, `ScenarioActionTests`, `ScenarioComputeTests`, `ScenarioDeedsTests`, `ScenarioTransferTests`, `ScenarioAutomationTests`, `ScenarioIntakeTests`, `ConcurrentIntegrationTests`, `ConcurrentExchangeAtomicityTests` | Шаг записи атомарен, общий/независимые ключи и повтор; 1000 `integrations`; отдельные регрессии резервирования номеров расширений | Полный ScenarioRunner под нагрузкой со stop/restart, повтором внешней доставки и цепочкой нескольких действий |
| Расписание сценариев и потоки | `ScenarioServiceTests`, `StreamServiceTests`, `ScenariosRegistryTests`, `StreamsRegistryTests` | Функциональные lifecycle и ошибки | Start/stop/restart во время входящих событий, два экземпляра планировщика, доставка без задвоения |
| Заметки: создание/переименование/перенос/копия/сохранение/удаление файлов | Unit `VaultOperationsTests`, `VaultScannerTests`, `NoteAutosaveTests`, `NoteFormatTests`, `NoteMarkupScannerTests` | Настоящие временные файлы, BOM/переносы строк/отказ перезаписи соседа | `ConcurrentPersonalFilesTests`: 32 конфликта имен/сохранения и независимые файловые операции; остается настоящий UI autosave/удаление, отзыв доступа и сетевой обрыв |
| Профиль, допуск, пароли базы, права и матрица действий | `DbAccessLifecycleTests`, `PermissionServiceTests`, `PermissionMatrixTests`, `PgAccessTests`, `ConfidentialityRepositoryTests`, `FileAutoPermissionTests` | Управляемая чужая правка, удаление пользователя/разрешения, отказ доступа | `ConcurrentAccessTests`: регистрация и запись после явной перезагрузки отзыва; `multiuser access`: авторы/читатели; остается отзыв между проверкой и commit, открытый UI |
| Менеджер паролей, мастер-пароль обмена и защищенное хранилище | Unit `SecretsInventoryTests`, `SecretFieldConventionTests`, `ListenerPermissionsTests`; `DbSettingsRegistryTests`, `IntegrationsRegistryTests` | Инвентарь/допуск/маскировка и замена тестового секрета; это не проверка всех нативных хранилищ | Параллельная смена/удаление своих секретов в отдельных профилях, отказ системного хранилища; UI не должен показывать значение после отзыва прав |
| История изменений, диагностика и журналы запусков/безопасности | `ChangeLogTests`, `LogReaderTests`, `LogReaderFilterTests`, `DiagnosticsLogTests`, `RunJournalTests`, `SecurityJournalTests`; `ServiceGrids` | Функциональные, пагинация/экспорт, UI журнальных таблиц; пользователь журнала сверяется в 1000 `lifecycle` | Чтение/очистка/экспорт при многопользовательском дописывании, отсутствие потерь курсора и чужих личных данных |
| Настройки, горячие клавиши, категории и реестр подключений | `DbSettingsRegistryTests`, `DbShortcutSettingsTests`, `CategoryRepositoryTests`; Unit settings/shortcuts; `Sections` | Функциональные и UI; настройки пользователя отделены от общих данных | `ConcurrentPersonalFilesTests`: 1000 отдельных хранилищ/реестров подключений; остается межпроцессный запуск, горячие клавиши и смена настроек при активных работах |
| Оболочка: панели, масштаб/шрифты, меню, прокрутка, сочетания, автозапуск и выбор стартового раздела | Unit `SidebarModeTests`, `IconRegistryTests`, `ScreenReferenceTests`, `PopupListStandardTests`, `TransientPopupsTests`, `DragScrollTests`, `ShortcutActionsTests`, `AutostartTests`, `StartupModuleTests`; `Sections`, `EditGeometry`, `PopupGeometry`, `MarkupLayout` | Логика и headless геометрия; системный автозапуск и разные оконные платформы требуют своей ОС | UI-наблюдатель при фоновой нагрузке, переключение разделов/темы/масштаба без потери ввода, реальные системные службы/оконные менеджеры |
| Старт, миграции, один экземпляр, подключение/закрытие базы | `InfrastructureSmokeTests`, `IntroSeedTests`, `SingleInstanceTests`, `MountResilienceTests`, `MountBackgroundTests`, `PragmaTests`, migration tests | Лок-файлы, живой/протухший migration lock, потеря пути, асинхронный mount; `Sections --only app/start` | Одновременные первые подключения/миграции разных процессов, закрытие во время I/O и потоков, восстановление после смерти клиента |
| SQLite-пул, PostgreSQL-подключения и сетевые отказы | `SqliteGateTests`, `SharedDbResilienceTests`, `MountResilienceTests`, `PgCreateDatabaseTests`, `PgWriteTransactionTests`, `SqliteTransactionAdmissionTests`; Unit `WriteAdmissionTests`; `MultiProcess` | Пул/очистка, очередь FIFO, отмена/dispose/отказ BEGIN, sync/async одиночные EF-сохранения; native deferred read не занимает writer slot; разрыв собственных PG-сессий | Реальная SMB/NFS, раздел сети, перезапуск сервера, исчерпание диска и распределенные клиенты требуют отдельного стенда |

## Все зарегистрированные расширения

Источник состава — [ExtensionRegistry.cs](../../../Extensions/ExtensionRegistry.cs): девять
расширений. Наборы отделены от ядра в `Tests/Extensions/<Имя>` и запускаются собственной
командой. В каждом из девяти проектов добавлен `ConcurrentEngineTests`: восемь
независимых вызовов настоящего движка с синтетическими файлами, проверкой результата
и сохранности входов. Общей нагрузки на 1000 одновременно работающих PDF/Excel-движков
нет; функциональный прогон набора нельзя выдавать за такой результат.

Для расширений нагрузка должна соответствовать ресурсу: разные синтетические входные файлы,
отдельные выходные папки и ограниченное число тяжелых исполнителей; отдельно — общий выходной
файл/журнал/номер как конфликтный ресурс. Тысяча заданий в очереди и тысяча одновременно
работающих обработчиков PDF — разные модели, их следует отражать в отчете.

| Расширение | Функциональные наборы в `Tests/Extensions` | Конкурентная проверка и оставшаяся граница | Команда |
|---|---|---|---|
| Переименование PDF (`PdfRename`) | `PdfRenamerTests`, `PdfRenamerProcessTests`, `PdfRenameExtensionTests` | Есть 8 независимых папок/планов и сохранность байтов; общий конфликт целевого имени и отмена отдельно | `./Tests/run-extensions.sh PdfRename` |
| Сравнение изделий/расшифровок (`ProductCompare`) | `ProductCompareEngineTests`, `ProductCompareRunTests`, `BreakdownCompareRunTests`, `BreakdownFilesTests` | Есть 8 независимых пар книг/выходов, разбор результата и SHA-256 входов; общий выход отдельно | `./Tests/run-extensions.sh ProductCompare` |
| Расчет состава (`AssemblyCalc`) | `AssemblyCalcEngineTests`, `AssemblyCalcRunTests` | Есть 8 расчетов независимых книг и проверка значений; конфликт одного выходного пути отдельно | `./Tests/run-extensions.sh AssemblyCalc` |
| Анализ PDF (`PdfAnalyzer`) | `PdfAnalyzerEngineTests`, `PdfAnalyzerRunTests`, `DocumentCodes*Tests`, `PdfStandSmokeTests` | Есть 8 независимых PDF/отчетов с разбором результата; общий изменяемый вход и отмена отдельно | `./Tests/run-extensions.sh PdfAnalyzer` |
| Сборка документов (`DocsCollection`) | `DocsCollectionScanTests`, `DocsCollectionShellTests`, `DocsCollectionFillTests`, `DocsCollectionVolumeTests` | Есть 8 обходов/заполнений собственных коллекций и сохранность файлов; меняющийся общий каталог отдельно | `./Tests/run-extensions.sh DocsCollection` |
| Сбор времени (`TimeCollector`) | `TimeCollectorEngineTests`, `TimeCollectorRunTests`, `TimeCollectionWriterTests`, `OperationColumnsTests` | Есть 8 независимых книг, номера/строки результата и SHA-256 входов; общий выходной файл отдельно | `./Tests/run-extensions.sh TimeCollector` |
| Реестр документов (`DocRegistry`) | `DocRegistrarTests`, `RegistryFileTests`, `MessageRegistrationTests`, `RegisterMessageActionTests`, `ScenarioRunTests` и другие | Есть 8 независимых журналов и 8 регистраторов одной серии (001–008); занятый явный ключ проверяется отдельно; kill/recovery журнала отдельно | `./Tests/run-extensions.sh DocRegistry` |
| Маркировка PDF (`PdfWatermark`) | `PdfWatermarkEngineTests`, `WatermarkStampTests`, `IssueJournalTests`, `CopyNumberTests`, `PdfStandSmokeTests` | Есть 8 независимых маркировок, 8 выдач общего документа и устаревшие снимки журнала с проверкой уникальности; kill/recovery и отказ выхода отдельно | `./Tests/run-extensions.sh PdfWatermark` |
| Автоматизации (`Automations`) | `SumByKeyTests`, `SheetActionTests`, `AutomationRegistryTests` | Есть 8 вызовов общих stateless SumByKey/SheetAction с независимыми книгами; общий целевой файл отдельно | `./Tests/run-extensions.sh Automations` |

Всего в девяти `ConcurrentEngineTests` определено 13 случаев: девять независимых нагрузок
и четыре проверки общих журналов/ключей. Повторная ручная регистрация документа или
повторная выдача копии сами по себе не являются идемпотентной транспортной доставкой:
DocRegistry и PdfWatermark вправе выдать следующий номер. Повтор Inbox с тем же MessageId
проверяется отдельно по его контракту квитанций.

Контракт регистрации, действий и настроек всех расширений проверяет отдельный набор
`Tests/Extensions/Registry`. Это контроль отделяемости и оболочки, не нагрузка вычислительных
движков и не многопользовательский доступ к общим документам.

## Команды проверки

Запуски в одной рабочей копии выполняются последовательно. Перед проверкой PG нужны
локальный тестовый сервер на `5455`, собственные тестовые БД и соответствующие клиентские
утилиты. Для негативных проверок пароля сервер должен запрашивать пароль: `trust` не подходит.

```bash
./Tests/run-concurrency.sh --out /tmp/sb-concurrency-small
# Полная нагрузочная матрица отдельно, после малой:
./Tests/run-concurrency.sh --clients 1000 --out /tmp/sb-concurrency-thousand
dotnet test Tests/Unit -c Release
dotnet test Tests/Integration -c Release --blame-hang --blame-hang-timeout 4m --blame-hang-dump-type none
./Tests/run-extensions.sh --configuration Release
dotnet run -c Release --project Tests/DocReader -f net10.0
dotnet run -c Release --project Tests/BackupUi -f net10.0
dotnet run -c Release --project Tests/ConcurrentUi -- --mode all --out /tmp/sb-ui-new-run
dotnet run -c Release --project Tests/RecordPage -- --only fileauto
dotnet run -c Release --project Tests/RecordPage -- --only fileprobe
dotnet run -c Release --project Tests/Benchmarks -- multiuser --provider sqlite --profile wal --clients 1000
dotnet run -c Release --project Tests/Benchmarks -- multiuser --provider sqlite --profile shared --clients 1000
dotnet run -c Release --project Tests/Benchmarks -- multiuser --provider postgres --clients 1000
# Для каждого из planning/library/integrations/access/structure, по очереди:
dotnet run -c Release --project Tests/Benchmarks -- multiuser --provider postgres --scenario planning --clients 1000
```

Страницы записи, большая таблица, обход разделов и их пиксельные эталоны требуют подходящей
фикстуры и шрифтов: [Tests/README.md](../../../Tests/README.md). `Sections --only tableops`
и часть других стендов подключают переданную базу как есть. Для этого этапа использовать
только специально созданные синтетические БД/копии; рабочие файлы пользователя не подходят.
Принятие первого кадра в эталон само по себе не доказывает правильность картинки.

## Фактический исход и границы

На начало расширенного этапа сохранены результаты 25.10.2026: 2927 прошедших Unit-тестов,
27 новых сценариев жизненного цикла (11 прошли, 16 выявляют два описанных дефекта),
49 прошедших смежных интеграционных тестов и девять нагрузочных прогонов на 1000 сессий.
Полная таблица исходов нагрузки находится в [concurrency.md](concurrency.md); PostgreSQL
там обслужил все три сценария, SQLite при части нагрузок возвращал BUSY. Это результаты
конкретной фикстуры на одной Linux-машине, не гарантия пропускной способности рабочей сети.

### Нагрузка после исправлений 26.10.2026

Последние результаты всех **24 вариантов — PASS**: восемь сценариев на трех профилях.
В каждом варианте **1000/1000** сессий подготовились и завершили все действия; итоговые
проверки данных, авторов, журнала и проекций прошли. SQLite также прошел integrity/FK.
Для структуры использованы повторные прогоны после исправления оформления и одиночных
EF-сохранений метаданных; остальные строки взяты из полного повторного прогона.
Предыдущие неуспешные результаты ниже сохранены как история обнаружения ошибок.

В таблице — время рабочей фазы `RunSeconds`, без подготовки и открытия подключений.

| Сценарий | SQLite WAL | SQLite DELETE+FULL | PostgreSQL |
|---|---:|---:|---:|
| `lifecycle` | 18,5 с | 27,7 с | 28,7 с |
| `hotcell` | 2,8 с | 3,8 с | 7,7 с |
| `rollback` | 5,7 с | 5,7 с | 15,1 с |
| `planning` | 17,3 с | 20,6 с | 48,2 с |
| `library` | 21,4 с | 34,5 с | 28,9 с |
| `integrations` | 20,5 с | 24,7 с | 45,6 с |
| `access` | 2,8 с | 4,1 с | 3,0 с |
| `structure` | 25,2 с | 47,2 с | 23,3 с |

`access` по-прежнему означает 500 писателей, 500 читателей и 1500 проверенных запретов.
Запрошенная одновременность не снижена: все 1000 сессий доходят до общего старта, затем
пользуются обычными очередями приложения. SQLite EF-писатели одного процесса идут по одному,
PostgreSQL WriteScope — до 16; очереди разных процессов независимы. Это успешность команд и
сверки данных, без продуктового SLA задержек, 1000 окон, долгого soak или настоящей SMB/NFS.

Сводка: `/workspace/.setup/concurrency-fixed-summary.json`. Полная повторная матрица —
`concurrency-fixed-final-1000`, повтор структуры с оформлением — `concurrency-fixed-style-1000`,
итоговые SQLite-повторы структуры с EF Always — `concurrency-fixed-metadata-1000`
внутри `/workspace/.setup/`; точный путь каждого выбранного JSON указан в сводке.

### Отдельные процессы и чувствительность регрессий

Дополнительно 32 писателя и 16 читателей работали отдельными процессами с новым локальным
SQLite-файлом DELETE+FULL в течение 60 секунд на участника. Все 48 процессов отчитались и
выполнили полезную работу: 2501 запись, 3737 чтений страниц, 741 чтение дерева.
BUSY и прочих ошибок нет; integrity, значения и журнал прошли проверки. У записей p50 8 мс,
p95 1344 мс, p99 10339 мс, максимум 22053 мс; у страниц p95 18 мс.
Замер включает холодные первые действия процессов, это не доказательство такой же задержки
на сетевом диске или у 1000 отдельных компьютеров. Полный журнал:
`/workspace/.setup/concurrency-fixed-final-checks/MultiProcess60.log`.

Чувствительность новых границ проверена временным снятием пяти защит: снимка календаря,
восстановления навигаций после savepoint, PG-блокировки метаданных, локальных родителей
автосвязи и SQLite Always. Из 13 выбранных случаев 11 ожидаемо упали; два SQLite-контроля
автосвязей сохранили защиту native writer lock. Все четыре исходника восстановлены побайтно,
снова собраны: **13/13 PASS**. Это проверка выбранных регрессий, не мутационное покрытие
всего проекта. Журналы: `/workspace/.setup/concurrency-fixed-mutations.log` и
`concurrency-fixed-final-checks/SensitivityRestored.log`; их TRX сохранены рядом.

### Первичные интеграционные прогоны до исправлений 26.10.2026

| Прогон | Всего | Прошли | Упали | Пропущены | Источник |
|---|---:|---:|---:|---:|---|
| Полный `Tests/Integration` | 2084 | 2043 | 35 | 6 | `concurrency-expanded-integration/integration.trx`, сводка `integration.summary.json` |
| Дополнение: типы свойств, продолжение работы после устаревшей правки и повтор локали | 50 | 38 | 12 | 0 | `concurrency-expanded-additional/additional.trx`, сводка `additional.summary.json` |

Файлы этих запусков сохранены в `/workspace/.setup/results/` тестовой сессии. Это два
отдельных прогона: в дополнении есть четыре повторных случая, поэтому складывать строки
таблицы как число уникальных проверок нельзя. На том этапе полный набор после дополнения
заново не запускался; исходные 35 падений в его TRX не переименованы в успехи.
Итоговый полный прогон после исправлений приведен в текущей сводке выше.

Три падения полного прогона относились к поиску/обратным связям с кириллицей на тестовом
PostgreSQL с локалью `C`: `PgSearchTests`, `RelationBacklinkTests` и PG-вариант
`FileQueryTests`. Локаль стенда исправлена на `C.UTF-8`; все четыре выбранных повторных
случая прошли, включая контрольный SQLite-вариант запроса файлов. Это исправление среды,
а не изменение продукта. Аутентификация тестового PG — `SCRAM-SHA-256`, поэтому проверки
неверного пароля не обходятся настройкой `trust`.

Шесть пропусков полного прогона остались видимыми: три проверки установленного PG/службы
Windows, одна проверка D-Bus без шины сеанса и два ранее отмеченных дефекта продукта
(пользователь в дополнительных user-полях новой задачи и фильтр/сортировка/экспорт
вычисляемых колонок автора/дат). Пропуск не означает успешную проверку.

Среди двух прогонов **170 новых случаев `Concurrent*`: 126 прошли, 44 упали, пропусков нет**.
Это отдельная выборка из TRX, а не третий прогон:

| Набор | Прошли | Упали |
|---|---:|---:|
| `ConcurrentLifecycleTests` | 11 | 16 |
| `ConcurrentFieldTypesTests` | 34 | 0 |
| `ConcurrentStaleRecoveryTests` | 0 | 12 |
| `ConcurrentStructureTests` | 12 | 15 |
| `ConcurrentPlanningTests` | 9 | 0 |
| `ConcurrentLibraryTests` | 12 | 1 |
| `ConcurrentAccessTests` | 18 | 0 |
| `ConcurrentIntegrationTests` | 12 | 0 |
| `ConcurrentBackupTests` | 12 | 0 |
| `ConcurrentConnectionRecoveryTests` | 2 | 0 |
| `ConcurrentPersonalFilesTests` | 4 | 0 |

44 красных случая — варианты провайдера, типа значения и состояния контекста, а не
44 независимых дефекта. Они воспроизводят несколько групп проблем:

- Устаревший редактор удаленной записи/свойства получает конфликт трекера или FK.
  В 12 дополнительных случаях после такого отказа следующая правка действующей записи
  через тот же контекст тоже падает с FK; проверка не очищает трекер за приложение.
  До итоговых утверждений об истории/событиях эти 12 случаев не доходят, поэтому утечка
  отложенной истории ими пока не подтверждена.
- При искусственном отказе составной правки значение уже изменено, хотя вызов вернул
  ошибку; для даты проверяется также согласованность проекции и журнала.
- Дублирование через ранее загруженный источник создает копии после удаления источника
  соседом; изолированный вариант этого теста проходит.
- Два переноса одного источника могут опубликовать две логические копии.
- Параллельные переносы с созданием одной недостающей целевой схемы конфликтуют на имени
  свойства вместо завершения всех переносов.
- Удаление цели между проверкой и сохранением связи оставляет новое висячее локальное ребро.
- Суммирование больших счетчиков 1000 фоновых работ переполняет `int`, и общий прогресс
  становится пустым вместо ожидаемого значения.

Прошедшие случаи подтверждают конкретные пути: десять хранимых типов свойств, задачи и
календарь, доступ/события, обмен и квитанции, копии при живых писателях, завершение дочернего
процесса внутри транзакции, восстановление через новое PG-подключение и личные файловые
хранилища. Они не закрывают перечисленные выше падения и оставшиеся границы матрицы.

### Первичная матрица расширенного этапа на 1000 сессий

Завершены 15 прогонов: пять модулей на SQLite WAL, SQLite DELETE+FULL (`shared`) и
PostgreSQL. В каждом подготовились и стартовали **1000 сессий**. Строгий критерий прошли
**9 из 15**, не прошли 6: успех требует полного цикла каждого участника и всех итоговых
проверок, а не только завершения потоков либо исправного файла БД.

В каждой ячейке таблицы: **полностью завершившие цикл / 1000; `RunSeconds`; исход**.
Время — рабочая фаза после общего старта, без подготовки фикстуры и подключений.

| Режим | SQLite WAL | SQLite DELETE+FULL | PostgreSQL |
|---|---|---|---|
| `planning` | 550/1000; 14,398 с; FAIL | 256/1000; 43,343 с; FAIL | 461/1000; 39,200 с; FAIL |
| `library` | 1000/1000; 25,532 с; PASS | 468/1000; 61,724 с; FAIL | 1000/1000; 43,032 с; PASS |
| `integrations` | 1000/1000; 23,772 с; PASS | 331/1000; 66,550 с; FAIL | 1000/1000; 33,005 с; PASS |
| `access` | 1000/1000; 3,425 с; PASS | 1000/1000; 11,382 с; PASS | 1000/1000; 3,493 с; PASS |
| `structure` | 1000/1000; 20,944 с; PASS | 355/1000; 43,879 с; FAIL | 1000/1000; 35,288 с; PASS |

В `access` участвуют **500 писателей и 500 пользователей только с просмотром**.
Три запрещенных действия каждого читателя дают 1500 ожидаемых отказов, проверяемых отдельно;
это не 1000 успешно записывающих пользователей и не 1500 неожиданных ошибок.

Во всех шести неуспешных прогонах не прошла проверка завершения полных циклов всеми
участниками. Дополнительные неуспешные проверки первичных отчетов:

| Прогон | Что еще не совпало |
|---|---|
| `planning`, WAL | Число созданных задач: факт и подтверждения 578 при плане 1000; исполнители, назначившие, стадии, даты или авторы |
| `planning`, DELETE+FULL | Число задач: факт и подтверждения 562 при плане 1000; поля задач, соответствие подтвержденных дат календарным строкам и пользовательские счетчики дней |
| `planning`, PG | Число задач: факт и подтверждения 721 при плане 1000 |
| `library`, DELETE+FULL | Файлы, типы и карточки относительно подтвержденных значений; счетчики, гистограмма типов и перепись библиотеки |
| `integrations`, DELETE+FULL | Число записей: факт 831, подтверждено 392 при плане 1000; набор подтвержденных записей, уникальность ключей/наличие обеих колонок, точные названия/ключи/значения/авторы |
| `structure`, DELETE+FULL | Полные циклы создания/удаления свойства и ветки дерева; записи и значения относительно подтвержденных изменений схемы |

В неуспешных SQLite-прогонах сохранены ошибки `SQLITE_BUSY` (код 5); у `planning` также
есть `InvalidOperationException`, а на PG — `InvalidOperationException` и `TimeoutException`.
Название исключения само по себе не устанавливает причину каждого отказа: подробности
сохраняются отдельно, и диагностические повторы не должны подменять эту первичную серию.

Во всех десяти SQLite-прогонах прошли `integrity_check` и `foreign_key_check`. Это подтверждает
проверенную физическую целостность файла и ограничения FK, но не совпадение всей логики
с журналом подтверждений: в таблице выше есть отдельные несоответствия данных.

Источник — `/workspace/.setup/concurrency-expanded-1000-summary.json`; он содержит путь
к `report.json` и полному журналу каждого прогона. Это короткие локальные серии на
синтетических данных: результаты не подтверждают длительную смешанную нагрузку,
1000 GUI-процессов, распределенные машины или работу SQLite на реальной SMB/NFS-папке.

### Диагностический повтор планирования

Для трех неуспешных вариантов `planning` выполнена отдельная серия на 1000 сессий.
В стенде расширены сообщения о неверных строках календаря и повторяющихся ключах;
условия проверки остались прежними. Эта серия уточняет причину отказов, а не заменяет
результаты первоначальных 15 прогонов.

| Профиль | Готовы / стартовали | Полностью завершили | `RunSeconds` | Отказы участников |
|---|---:|---:|---:|---|
| SQLite WAL | 1000 / 1000 | 549/1000 | 14,64 с | 436 `SQLITE_BUSY`, 15 повторов ключа в календарном ответе |
| SQLite DELETE+FULL | 1000 / 1000 | 254/1000 | 47,88 с | 673 `SQLITE_BUSY`, 73 повтора ключа в календарном ответе |
| PostgreSQL | 1000 / 1000 | 446/1000 | 38,00 с | 312 `TimeoutException` на `task-create`, 242 повтора ключа в календарном ответе |

Например, PG-сессия 4 получила в одном ответе календаря две строки с одинаковыми
`Id=703`, `FieldId=8`, `CollectionId=3`: интервал **02.11.2026 10:00–11:00** и интервал
**02.12.2026 10:00–11:00**. `InvalidOperationException` здесь сообщает об обнаруженном
повторе `(Id, FieldId)` в результате API. Это не доказывает наличие двух физических строк
под одним уникальным индексом и не означает ошибку уникального индекса: отдельно требуется
различать согласованность возвращенной выборки и ограничения хранения.

Все три диагностических прогона не прошли строгий критерий. Итоги и полные сообщения —
`/workspace/.setup/concurrency-planning-diagnostics/*/report.json`, коды завершения —
`matrix.tsv` в той же папке.

Новый [`Tests/run-concurrency.sh`](../../../Tests/run-concurrency.sh) проверен этой полной
серией из трех вариантов: каждый неуспешный вариант вернул код 1, общий запуск также
завершился неуспешно. Дополнительно прошли семь проверок защитных условий: недопустимое
число клиентов, неизвестные провайдер/сценарий, `NaN`/нулевой порог p95 и уже существующий
каталог результата. Существующий результат не перезаписан; контроль SHA-256 подтвердил
сохранность проверенных файлов. Сводка защит — `/workspace/.setup/concurrency-runner-guards.json`.

### UI-наблюдатель расширенного этапа

Все три режима `Tests/ConcurrentUi` прошли функциональные утверждения: поток 1000 обновлений
десяти работ, открытие панели с 1000 подготовленными работами и 1000 результатов копий через
настоящий сервис уведомлений с fake notifier/badge. В `jobs-live` максимальный разрыв
heartbeat составил **8,6 с**: функциональный успех не означает приемлемую отзывчивость.
Порог производительности продуктом пока не задан, стенд только сохраняет измерения.
Полный отчет — `/workspace/.setup/concurrent-ui-first/report.json`; времена и ограничения —
в [concurrency.md](concurrency.md#ui-наблюдатель-расширенного-этапа).

### Первичные наборы расширений и смежные проверки до исправлений

Завершены все девять наборов движков расширений и отдельный контракт реестра.
Суммарно **1065 случаев: 1061 прошел, 4 упали, пропусков нет**.
Среди них 13 новых `ConcurrentEngineTests`: девять прошли, четыре упали.

| Набор | Всего | Прошли | Упали | Новые concurrent: прошли / упали |
|---|---:|---:|---:|---:|
| `AssemblyCalc` | 37 | 37 | 0 | 1 / 0 |
| `Automations` | 23 | 23 | 0 | 1 / 0 |
| `DocRegistry` | 464 | 462 | 2 | 1 / 2 |
| `DocsCollection` | 53 | 53 | 0 | 1 / 0 |
| `PdfAnalyzer` | 159 | 159 | 0 | 1 / 0 |
| `PdfRename` | 28 | 28 | 0 | 1 / 0 |
| `PdfWatermark` | 115 | 113 | 2 | 1 / 2 |
| `ProductCompare` | 60 | 60 | 0 | 1 / 0 |
| `Registry` | 22 | 22 | 0 | — |
| `TimeCollector` | 104 | 104 | 0 | 1 / 0 |

Четыре падения относятся к двум группам проблем общего резервирования номеров,
а не к четырем уникальным багам:

- `DocRegistry`: восемь регистраторов одной серии получают повторяющиеся обозначения;
  восемь конкурирующих регистраций явно заданного `Number=1`, `Execution=1` все успешны
  при `ConfirmedDuplicate=false`. Это конфликт выдачи номера, а не требование сделать
  идемпотентной обычную новую ручную заявку.
- `PdfWatermark`: восемь обработок одного документа успешно создают PDF в независимых
  выходных папках, но все получают номер 1 вместо 1–8. Вторая проверка с заранее загруженными
  ведомостями создает восемь строк журнала с тем же номером 1. Отсутствие защиты между
  отдельными выдающими уже документировано в `NumberLedger`; новые тесты воспроизводят
  ограничение, но не устраняют его.

Все девять сценариев независимых обработок движков прошли: общий старт восьми заданий, разбор
собственных готовых файлов/записей и сохранность исходников. Это не одновременный запуск
1000 PDF/табличных обработчиков и не проверка настоящих пользовательских документов.
Подробности оракулов — в [extension-tests.md](extension-tests.md).

Источник сводки — `/workspace/.setup/concurrency-expanded-extension-summary.json`;
TRX, сводка и полный журнал каждого набора находятся в
`/workspace/.setup/concurrency-expanded-checks/Extension-<Имя>/` и соседнем `.log`.

Смежный полный прогон Unit завершился **2927/2927**, без падений и пропусков.
Стенды `DocReader` и `BackupUi` завершились с кодом 0; у каждого собственный журнал в
`/workspace/.setup/concurrency-expanded-checks/`. Эти функциональные проверки не отменяют
красные интеграционные и нагрузочные сценарии и не объявляют весь этап зеленым.

Все семь выбранных самостоятельных режимов `RecordPage` также завершились с кодом 0:
`empty`, `fileauto`, `fileprobe`, `relations`, `relationcomputed`, `relationintegrity`,
`backlinks`. Каждый использовал собственные синтетические данные и рабочий шрифт Skia;
готовые базы пользователя этим прогоном не открывались. Остальные режимы `RecordPage`
этим результатом не покрыты. Полные журналы —
`/workspace/.setup/concurrency-expanded-checks/RecordPage-<режим>.log`.

Чувствительность новой матрицы хранимых типов проверена временным дефектом: `SetFieldValue`
перестал записывать значение. Все 33 сценария настоящих редакторов упали; отдельная проверка
состава типов прошла. Исходник восстановлен побайтно до `HEAD`, пересобран, тестовая ветка
отсутствует в итоговой сборке. После восстановления повтор дал **34/34 PASS**, без пропусков.
Это проверка чувствительности матрицы типов к потере записи, а не утверждение о мутационном
покрытии всех тестов проекта. TRX: `results/concurrency-expanded-mutation/mutation.trx`
и `results/concurrency-expanded-restored/restored.trx` внутри `/workspace/.setup/`.

Ограничения среды и обязательные отдельные проверки:

- Нет результатов 1000 физических компьютеров и отдельных GUI-процессов на разных ОС.
  Один headless UI-наблюдатель и 1000 DB-сессий полезны, но не заменяют распределенный стенд.
- SQLite DELETE+FULL на локальном диске проверяет профиль журнала, а не блокировки, задержку,
  отключение и восстановление настоящей SMB/NFS-папки.
- Программное завершение дочернего процесса не равно отключению питания, аварии диска,
  разрыву сети либо реальному заполнению диска. Для каждого нужен отдельный контролируемый стенд.
- Fake notifier/badge подтверждает решение показать уведомление и его маршрутизацию;
  фактическую доставку нативных баннеров/значков нужно проверять на целевых ОС.
- Закрытые сетевые политики, IPv6, системные службы, установку PG, доверие сертификатам и
  платформенное хранилище секретов необходимо проверять в подходящем окружении; их пропуск
  в облаке не является успешным тестом функции.
- Внешние серверы и пользовательские документы/шаблоны не используются без отдельной
  задачи и разрешенного доступа. Стенды обмена используют петлевой адрес и синтетические данные;
  расширения — временные PDF/книги/каталоги, а не настоящие рабочие папки.
- Совпадение итогового числа строк недостаточно: проверяются каждый подтвержденный результат,
  содержимое и аудит, отсутствие лишних данных, связи, проекции и журналы. Сообщение об ошибке
  после частичного commit — отдельный дефект, даже при успешных `integrity_check` и FK.

## Связанные документы

- [concurrency.md](concurrency.md) — реализованный нагрузочный стенд и сохраненные результаты.
- [automated-tests.md](automated-tests.md) — функциональные тесты ядра и правила обвязки.
- [extension-tests.md](extension-tests.md) — отдельные проекты расширений.
- [multi-process.md](multi-process.md) — многопроцессный SQLite-стенд.
- [overview.md](overview.md) — карта тестовых проектов.
- [Roadmap](../../ROADMAP.md) — открытые дефекты и принятые продуктовые решения.
