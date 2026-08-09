#!/bin/bash
#
# Тесты модуля полной проверки. Чистый bash: .NET, сеть и gh не нужны.
#
#   Scripts/ci/ci-test.sh
#
# Живут внутри модуля и удаляются вместе с ним — в Tests/Unit ничего не добавляется.
# Главный инвариант тот же, что у workflow: прогон НИЧЕГО не меняет.
#
# Проверки 3 и 4 — машинная запись требования «не пересекаться с выпуском релиза». Словами оно
# живёт в комментариях, а здесь перестаёт зависеть от того, прочитал их кто-то или нет:
# workflow не может запуститься ни на main, ни на теге и не вызывает никаких скриптов.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HERE="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT" || exit 1

WF=".github/workflows/build.yml"

# Пути модуля — единственное, что появляется в репозитории. Тот же список в разделе
# «Как удалить модуль» файла docs/developer/guides/ci.md.
MODULE_PATHS=(
  "$WF"
  "Scripts/ci/setup-remote.sh"
  "Scripts/ci/ci-test.sh"
  "docs/developer/guides/ci.md"
)

passed=0
failed=0
skipped=0

ok()   { echo "✅ $1"; passed=$((passed + 1)); }
bad()  { echo "❌ $1"; [ $# -gt 1 ] && printf '   %s\n' "${@:2}"; failed=$((failed + 1)); }
skip() { echo "⏭  $1"; skipped=$((skipped + 1)); }

STATUS_BEFORE="$(git status --porcelain)"

echo "Тесты модуля полной проверки"
echo

[ -f "$WF" ] || { echo "❌ Нет файла $WF — проверять нечего"; exit 1; }

# Шаги workflow без комментариев: проверки ниже смотрят на то, что действительно выполняется.
# Знак «#» внутри значений в этом файле не встречается, поэтому обрезка по нему безопасна.
WF_CODE="$(sed 's/#.*$//' "$WF")"

# ── 1. Workflow разбирается как YAML ──────────────────────────────────────────
# Не формальность: значение `if`, ключ с двоеточием внутри строки, многострочный `options` —
# всё это ломается молча. GitHub при неразбираемом файле просто не запускает workflow.
if command -v actionlint > /dev/null 2>&1; then
  out="$(actionlint "$WF" 2>&1)" \
    && ok "1. actionlint не нашёл замечаний" \
    || bad "1. actionlint" "$out"
elif python3 -c 'import yaml' > /dev/null 2>&1; then
  out="$(python3 -c 'import sys, yaml; yaml.safe_load(open(sys.argv[1], encoding="utf-8"))' "$WF" 2>&1)" \
    && ok "1. YAML разбирается (PyYAML)" \
    || bad "1. YAML не разбирается" "$out"
elif command -v ruby > /dev/null 2>&1; then
  out="$(ruby -ryaml -e 'YAML.safe_load(File.read(ARGV[0]), aliases: true)' "$WF" 2>&1)" \
    && ok "1. YAML разбирается (ruby)" \
    || bad "1. YAML не разбирается" "$out"
else
  skip "1. Синтаксис YAML — нет ни actionlint, ни PyYAML, ни ruby"
fi

# ── 2. Синтаксис скриптов модуля ──────────────────────────────────────────────
errs=""
for s in "$HERE"/*.sh; do
  bash -n "$s" 2>/dev/null || errs="$errs $(basename "$s")"
done
[ -z "$errs" ] && ok "2. Синтаксис всех скриптов модуля" \
               || bad "2. Синтаксис скриптов" "ошибки в:$errs"

# ── 3. Прогон запускается только по команде ───────────────────────────────────
# Блок `on:` целиком — от строки «on:» до следующего ключа нулевого отступа.
on_block="$(awk '/^on:/ { f = 1 } f && /^[a-zA-Z]/ && !/^on:/ { exit } f' "$WF")"
# Here-string, а не «printf | grep -q»: grep закрывает канал на первом совпадении, источник
# получает SIGPIPE, и pipefail отдаёт ненулевой код ПРИ найденном совпадении — проверка начинает
# врать наоборот. Ровно на этом однажды падал шаг workflow.
triggers="$(awk '/^  [a-z_]+:/ { gsub(/[ :]/, ""); print }' <<< "$on_block" | sort | tr '\n' ' ')"

prob=()
[ "$triggers" = "push workflow_dispatch " ] \
  || prob+=("триггеры: «${triggers}», ожидалось «push workflow_dispatch »")
grep -q 'branches: \[ci\]' <<< "$on_block" \
  || prob+=("нет фильтра «branches: [ci]» — прогон пошёл бы не по команде")
grep -q 'main' <<< "$on_block" \
  && prob+=("в блоке on: упомянут main — обычный push начнёт запускать прогон")
grep -q 'tags' <<< "$on_block" \
  && prob+=("в блоке on: упомянуты теги — это территория выпуска релиза")

if [ ${#prob[@]} -eq 0 ]; then
  ok "3. Запуск только по пушу ветки ci и вручную: ни main, ни тегов"
else
  bad "3. Триггеры разошлись с замыслом" "${prob[@]}"
fi

# ── 4. Не вызывается ни один скрипт репозитория ───────────────────────────────
# Разрешены только dotnet и psql. Условие нарочно строже, чем «не трогать выпуск»: пока
# workflow не зависит ни от какого скрипта, он не сломается от чужой правки — и наоборот.
# «\.sh» с хвостом-не-буквой: иначе под шаблон попадает github.sha из ссылки на коммит.
hits="$(grep -nE 'Scripts/|\.sh([^a-zA-Z]|$)' <<< "$WF_CODE")"
if [ -z "$hits" ]; then
  ok "4. Скриптов репозитория не вызывает — только dotnet и psql"
else
  bad "4. Появилась зависимость от скрипта" "$hits"
fi

# ── 5. То, что workflow собирает и гоняет, существует ─────────────────────────
# Ловит переименование проекта или папки: без этой проверки расхождение всплывёт только красным
# прогоном, то есть через десять минут после пуша.
prob=()
for t in "SmartBase.csproj" "Tests/Unit" "Tests/Integration" \
         "Tests/Benchmarks" "Tests/DbGenerator" "Tests/MultiProcess"; do
  grep -qF "$t" <<< "$WF_CODE" || prob+=("workflow не упоминает $t")
  [ -e "$t" ]                  || prob+=("в репозитории нет $t")
done
# Фильтры тестов заданы строками с именами классов — их легко осиротить переименованием.
[ -f "Tests/Integration/PgProviderTests.cs" ] \
  || prob+=("нет Tests/Integration/PgProviderTests.cs — фильтр в job postgres ничего не найдёт")
[ -f "Tests/Integration/LinuxNotificationsTests.cs" ] \
  || prob+=("нет Tests/Integration/LinuxNotificationsTests.cs — шаг про D-Bus ничего не найдёт")

if [ ${#prob[@]} -eq 0 ]; then
  ok "5. Все проекты и наборы тестов из workflow на месте"
else
  bad "5. Цели прогона разошлись с репозиторием" "${prob[@]}"
fi

# ── 6. Покрыты все операционные системы ───────────────────────────────────────
prob=()
for r in "ubuntu-latest" "windows-latest" "macos-latest" "macos-15-intel"; do
  grep -qF "$r" <<< "$WF_CODE" || prob+=("в матрице нет $r")
done
[ ${#prob[@]} -eq 0 ] \
  && ok "6. Матрица: Linux, Windows, macOS на Apple Silicon и на Intel" \
  || bad "6. Матрица неполна" "${prob[@]}"

# ── 7. Служебная ветка убирается после прогона ────────────────────────────────
# Ветка `ci` — механизм запуска, а не место для кода: в репозитории должна оставаться одна main.
# Без always() мусор копился бы ровно после неудачных прогонов, то есть чаще всего.
prob=()
grep -q '^  cleanup:'            <<< "$WF_CODE" || prob+=("нет job cleanup — ветка ci останется на сервере")
grep -qF 'always()'              <<< "$WF_CODE" || prob+=("уборка не помечена always() — после красного или отменённого прогона ветка останется")
grep -qF 'git/refs/heads/ci'     <<< "$WF_CODE" || prob+=("уборка не удаляет refs/heads/ci")
grep -qF 'contents: write'       <<< "$WF_CODE" || prob+=("у уборки нет права на запись — удаление не пройдёт")
grep -qF 'contents: read'        <<< "$WF_CODE" || prob+=("права по умолчанию не сужены до чтения")
grep -qF 'ref: ${{ github.sha }}' <<< "$WF_CODE" || prob+=("checkout не пришпилен к коммиту — сломается об удалённую ветку")

[ ${#prob[@]} -eq 0 ] \
  && ok "7. Ветка ci удаляется после прогона, права на запись только у уборки" \
  || bad "7. Уборка ветки не гарантирована" "${prob[@]}"

# ── 8. Отделяемость модуля ────────────────────────────────────────────────────
# а) все файлы модуля на месте; б) ничто вне модуля на него не ссылается — значит удаление
# не оставит битых ссылок; в) прогон тестов ничего не изменил.
prob=()
for p in "${MODULE_PATHS[@]}"; do
  [ -f "$p" ] || prob+=("нет файла $p")
  # В .gitignore есть стандартное для .NET правило «[Rr]elease/» — папка с похожим именем молча
  # выпала бы из git целиком. Проверяем явно, чтобы это не всплыло при переименовании.
  git check-ignore -q "$p" 2>/dev/null && prob+=("$p игнорируется git — файл не попадёт в репозиторий")
done

# Ищем упоминания модуля в файлах ВНЕ модуля. Внутри модуля ссылки свободны.
#
# --others --exclude-standard добавляет к отслеживаемым ещё и новые файлы, которые git пока
# не видел, но и не игнорирует. Без них проверка слепа ровно в тот момент, когда нужна: файл
# только что создан, ссылка на модуль в нём уже есть, а замечено это будет лишь после коммита.
# Игнорируемое (artifacts/, packages/) не попадает — иначе обход занял бы минуты.
refs="$(git ls-files -z --cached --others --exclude-standard -- . ':(exclude).github/workflows/build.yml' ':(exclude)Scripts/ci' \
        ':(exclude)docs/developer/guides/ci.md' \
        | xargs -0 grep -lIE 'Scripts/ci|guides/ci\.md|workflows/build\.yml' 2>/dev/null)"
[ -n "$refs" ] && prob+=("на модуль ссылаются файлы вне него: $(echo "$refs" | tr '\n' ' ')")

[ "$(git status --porcelain)" = "$STATUS_BEFORE" ] \
  || prob+=("прогон тестов изменил рабочее дерево")

if [ ${#prob[@]} -eq 0 ]; then
  ok "8. Модуль отделяем: 4 файла на месте, ссылок извне нет, дерево не тронуто"
else
  bad "8. Отделяемость нарушена" "${prob[@]}"
fi

# ── Итог ──────────────────────────────────────────────────────────────────────
echo
echo "Пройдено: $passed, пропущено: $skipped, провалено: $failed"
exit $(( failed > 0 ))
