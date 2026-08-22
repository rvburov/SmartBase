# Тесты расширений

Одна папка = одно расширение. `Tests/Extensions/<Имя>/` зеркалит `Extensions/<Имя>/`, namespace и
имя сборки — `SmartBase.Tests.Extensions.<Имя>`.

Исключение ровно одно: **`Registry/`** — это не расширение, а контракт реестра
(`Extensions/ExtensionRegistry.cs`): инварианты, которые нельзя проверить изнутри одного
расширения. Ни один тест там не называет конкретное расширение, поэтому удаление любого из них
набор не ломает.

## Запуск

```bash
dotnet test Tests/Extensions/PdfRename     # одно расширение
./Tests/run-extensions.sh                  # все наборы подряд
./Tests/run-extensions.sh PdfAnalyzer      # одно через скрипт
./Tests/run-all.sh --with-extensions       # ядро + расширения
```

Список наборов нигде не перечислен — скрипт берёт проекты глобом `Tests/Extensions/*/*.csproj`.

## Добавить расширение

Скопировать папку-образец (`PdfRename/` — самая простая), переименовать `.csproj`,
`RootNamespace` и `AssemblyName`. Больше ничего: ни `SmartBase.sln`, ни скрипт прогона, ни CI
списка расширений не содержат.

## Удалить расширение

```bash
rm -rf Extensions/<Имя> Tests/Extensions/<Имя>
# убрать 1 using и 1 строку из Extensions/ExtensionRegistry.cs
```

## Почему у пяти проектов нет `AssemblyInfo.cs`

`DisableTestParallelization` стоит только там, где есть причина: в `DocsCollection`
(`SqliteConnection.ClearAllPools()` — процесс-глобальная) и в `Registry`
(`AppSettings.AttachStore` — статическое поле). Остальные наборы чистые либо работают в своих
временных папках, и параллельность им на пользу — у `PdfAnalyzer` больше сотни вычислительных
тестов. Отсутствие файла здесь — решение, а не забывчивость.

Подробности, стенды и список сознательно не покрытого —
[docs/developer/testing/extension-tests.md](../../docs/developer/testing/extension-tests.md).
