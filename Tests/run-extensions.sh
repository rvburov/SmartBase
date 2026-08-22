#!/usr/bin/env bash
# Прогон тестов расширений SmartBase — по проекту на расширение (Tests/Extensions/<Имя>/).
#   ./Tests/run-extensions.sh              — все наборы подряд
#   ./Tests/run-extensions.sh PdfRename    — только один (имя папки)
#   ./Tests/run-extensions.sh -c Release   — аргументы уходят в dotnet test
#
# Список берётся ГЛОБОМ по папкам и нигде не перечислен: завели расширение — его тесты
# подхватятся сами, удалили папку — исчезли из прогона. Править этот файл не нужно никогда.
#
# Прогон СТРОГО последовательный: все проекты ссылаются на один SmartBase.csproj и при
# параллельном запуске писали бы в один artifacts/bin/SmartBase.
#
# Тесты ядра здесь не запускаются вовсе — это и есть техническая граница между наборами
# (см. docs/developer/testing/extension-tests.md).
set -euo pipefail
cd "$(dirname "$0")/.."   # корень репозитория

# Первый аргумент без дефиса — имя папки расширения; остальное уходит в dotnet test.
only=""
if [[ "${1:-}" != "" && "${1:-}" != -* ]]; then only="$1"; shift; fi

found=0
failed=""
for proj in Tests/Extensions/*/*.csproj; do
  [[ -e "$proj" ]] || break            # глоб не раскрылся — наборов нет
  name="$(basename "$(dirname "$proj")")"
  [[ -z "$only" || "$name" == "$only" ]] || continue
  found=1
  echo
  echo "== $name =="
  # set -e не должен ронять прогон на первом же падении: собираем список и падаем в конце.
  dotnet test "$proj" "$@" || failed="$failed $name"
done

if [[ $found -eq 0 ]]; then
  echo "Тестов расширений не найдено${only:+ (нет Tests/Extensions/$only)}."
  [[ -z "$only" ]] || exit 1           # спросили конкретное и не нашли — это ошибка
  exit 0                               # расширений нет вовсе — не ошибка
fi

if [[ -n "$failed" ]]; then
  echo
  echo "Упали:$failed"
  exit 1
fi

echo
echo "Все наборы расширений пройдены."
