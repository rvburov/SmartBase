# Тесты расширений

Одна папка = одно расширение. `Tests/Extensions/<Имя>/` зеркалит `Extensions/<Имя>/`, namespace и
имя сборки — `SmartBase.Tests.Extensions.<Имя>`.

Исключение ровно одно: **`Registry/`** — это не расширение, а контракт реестра
(`Extensions/ExtensionRegistry.cs`): инварианты, которые нельзя проверить изнутри одного
расширения. Ни один тест там не называет конкретное расширение, поэтому удаление любого из них
набор не ломает.

Общая обвязка (временная база, временная папка, синхронный прогресс) лежит НЕ здесь, а рядом —
[`Tests/ExtensionsShared/`](../ExtensionsShared/). Снаружи, чтобы не попасть в глоб прогона:
это библиотека без тестов, и запускать ее незачем.

## Запуск

```bash
dotnet test Tests/Extensions/PdfRename     # одно расширение
./Tests/run-extensions.sh                  # все наборы подряд
./Tests/run-extensions.sh PdfAnalyzer      # одно через скрипт
./Tests/run-all.sh --with-extensions       # ядро + расширения
```

Список наборов нигде не перечислен — скрипт берет проекты глобом `Tests/Extensions/*/*.csproj`.

## Добавить расширение

Скопировать папку-образец (`PdfRename/` — самая простая), переименовать `.csproj`,
`RootNamespace` и `AssemblyName`. Больше ничего: ни `SmartBase.sln`, ни скрипт прогона, ни CI
списка расширений не содержат.

## Удалить расширение

```bash
rm -rf Extensions/<Имя> Tests/Extensions/<Имя>
# убрать 1 using и 1 строку из Extensions/ExtensionRegistry.cs

# если это был последний набор, пользовавшийся общей обвязкой, —
# ее тоже можно удалить (пустой вывод = потребителей не осталось):
grep -rl ExtensionsShared Tests/Extensions
```

## Почему `AssemblyInfo.cs` есть не у всех

`DisableTestParallelization` стоит только там, где есть причина, — и причина всегда одна:
процесс-глобальное состояние.

| Набор | Что глобально |
|---|---|
| `DocsCollection`, `TimeCollector`, `DocRegistry` | `SqliteConnection.ClearAllPools()` при уборке файловой БД + регистрация текущего пользователя через статику `CurrentUser` |
| `Registry` | `AppSettings.AttachStore` — статическое поле |

Остальные наборы чистые либо работают в своих временных папках, и параллельность им на пользу —
у `PdfAnalyzer` больше сотни вычислительных тестов. Отсутствие файла там — решение, а не
забывчивость.

Подробности, стенды и список сознательно не покрытого —
[docs/developer/testing/extension-tests.md](../../docs/developer/testing/extension-tests.md).
