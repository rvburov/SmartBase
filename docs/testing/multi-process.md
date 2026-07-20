# Многопроцессные проверки (Tests/MultiProcess)

Сценарии одновременного доступа нескольких **процессов** к одному SQLite-файлу — то, что
требует настоящих двух ОС-процессов или наблюдения за живым приложением и потому не сводится
к быстрому детерминированному автотесту. «Сетевой» профиль без реальной шары включается
`NetworkPath.ForceNetwork(path)`.

Запуск из корня репозитория:

```bash
dotnet run -c Release --project Tests/MultiProcess -- --check-pragmas
dotnet run -c Release --project Tests/MultiProcess -- --contend
dotnet run -c Release --project Tests/MultiProcess -- --writer <db>
dotnet run -c Release --project Tests/MultiProcess -- --hold-write-lock <db> [сек]
```

| Режим | Что делает |
|-------|-----------|
| **--check-pragmas** | Профиль журнала/прагм: локально `WAL` + `NORMAL` + `mmap`; «сеть» → `DELETE` + `synchronous=FULL` + `mmap=0` + `busy_timeout=15000`; и что WAL-файл при «переносе на шару» конвертируется в `DELETE`. |
| **--contend** | Два **настоящих** дочерних процесса 15 с бьют в одну ячейку: без падений, итоговое значение — от одного из них, `integrity_check = ok`, журнал монотонен. |
| **--writer <db>** | «Второй пользователь»: раз в 2 с пишет значение + строку `change_log` с чужим `UserKey`. Пока работает — запущенная копия приложения должна вживую подхватить чужую правку (проверка `ChangePoller`). |
| **--hold-write-lock <db> N** | Держит `BEGIN IMMEDIATE` write-замок N секунд — проверка тоста «база занята». |

Что можно было автоматизировать — перенесено в автотесты (см. [automated-tests.md](automated-tests.md)):
локальный профиль прагм из `--check-pragmas` → `PragmaTests`, гонка ячейки из `--contend`
(в упрощённом in-process виде) → `ContentionTests`. Здесь остаются межпроцессные и ручные
сценарии, которые в `dotnet test` не выразить.

Подробнее про многопользовательскую работу — [../architecture/multi-database.md](../architecture/multi-database.md).
