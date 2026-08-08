#!/bin/bash
#
# Тесты модуля выпуска релизов. Чистый bash: .NET, сеть и gh не нужны.
#
#   Scripts/github-release/release-test.sh
#
# Живут внутри модуля и удаляются вместе с ним — в Tests/Unit ничего не добавляется.
# Главный инвариант: прогон НИЧЕГО не меняет. Проверка 8 подтверждает это машинно и заодно
# следит за тем, что модуль остаётся отделяемым — ни один файл вне него на модуль не ссылается.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HERE="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT" || exit 1

# Пути модуля — единственное, что появляется в репозитории. Тот же список в разделе
# «Как удалить модуль» файла docs/developer/guides/release.md.
MODULE_PATHS=(
  ".github/workflows/release.yml"
  "Scripts/github-release/release.sh"
  "Scripts/github-release/check-version.sh"
  "Scripts/github-release/release-notes.sh"
  "Scripts/github-release/release-test.sh"
  "docs/developer/guides/release.md"
  "CHANGELOG.md"
)

passed=0
failed=0
skipped=0

ok()   { echo "✅ $1"; passed=$((passed + 1)); }
bad()  { echo "❌ $1"; [ $# -gt 1 ] && printf '   %s\n' "${@:2}"; failed=$((failed + 1)); }
skip() { echo "⏭  $1"; skipped=$((skipped + 1)); }

STATUS_BEFORE="$(git status --porcelain)"
VER="$(sed -nE 's#.*<Version>([^<]*)</Version>.*#\1#p' SmartBase.csproj | head -n1)"
# Следующая версия — текущая с увеличенной patch-компонентой. Нужна для проверки «счастливого
# пути» --dry-run: она заведомо свободна и заведомо больше текущей.
NEXT="$(echo "$VER" | awk -F. '{ printf "%s.%s.%d", $1, $2, $3 + 1 }')"

echo "Тесты модуля выпуска релизов (текущая версия $VER)"
echo

# ── 1. Синтаксис скриптов ─────────────────────────────────────────────────────
errs=""
for s in "$HERE"/*.sh; do
  bash -n "$s" 2>/dev/null || errs="$errs $(basename "$s")"
done
[ -z "$errs" ] && ok "1. Синтаксис всех скриптов модуля" \
               || bad "1. Синтаксис скриптов" "ошибки в:$errs"

# ── 2. check-version.sh ───────────────────────────────────────────────────────
if "$HERE/check-version.sh" >/dev/null 2>&1; then
  out="$("$HERE/check-version.sh" 9.9.9 2>&1)"
  if [ $? -ne 0 ] && [ "$(printf '%s\n' "$out" | grep -c '✗')" -eq 3 ]; then
    ok "2. check-version.sh: согласовано сейчас, три расхождения на 9.9.9"
  else
    bad "2. check-version.sh не отчитался о трёх расхождениях на 9.9.9" "$out"
  fi
else
  bad "2. check-version.sh падает на текущем состоянии" \
      "запустите Scripts/github-release/check-version.sh и посмотрите вывод"
fi

# ── 3. release.sh отвергает плохие версии ─────────────────────────────────────
reject() {   # аргумент должен быть отвергнут
  "$HERE/release.sh" "$1" --dry-run --skip-tests --allow-dirty >/dev/null 2>&1
  [ $? -ne 0 ]
}
bad3=""
reject "1.0"        || bad3="$bad3 «1.0» (не X.Y.Z)"
reject "1.0.4-rc.1" || bad3="$bad3 «1.0.4-rc.1» (предвыпускной суффикс)"
reject "$VER"       || bad3="$bad3 «$VER» (не больше текущей)"
reject "1.0.4.5"    || bad3="$bad3 «1.0.4.5» (четыре компоненты)"
[ -z "$bad3" ] && ok "3. release.sh отвергает некорректные версии" \
               || bad "3. release.sh пропустил:$bad3"

# ── 4. Счастливый путь --dry-run ──────────────────────────────────────────────
out="$("$HERE/release.sh" "$NEXT" --dry-run --skip-tests --allow-dirty 2>&1)"
code=$?
if [ $code -ne 0 ]; then
  # Секция под следующую версию в CHANGELOG ещё не написана — это штатное состояние
  # между релизами, а не поломка: именно её отсутствие и должно останавливать выпуск.
  if printf '%s\n' "$out" | grep -q "CHANGELOG.md не готов"; then
    ok "4. release.sh --dry-run $NEXT: остановлен из-за незаполненного CHANGELOG (так и задумано)"
  else
    bad "4. release.sh --dry-run $NEXT завершился с ошибкой" "$out"
  fi
elif printf '%s\n' "$out" | grep -q "проверки пройдены"; then
  ok "4. release.sh --dry-run $NEXT: план напечатан, файлы не тронуты"
else
  bad "4. release.sh --dry-run $NEXT не напечатал итог" "$out"
fi

# ── 5. release-notes.sh ───────────────────────────────────────────────────────
notes="$("$HERE/release-notes.sh" "$VER" 2>&1)"
if [ $? -ne 0 ]; then
  bad "5. release-notes.sh завершился с ошибкой" "$notes"
else
  miss=""
  for marker in "## Что нового" "## Установка" "SmartBase-$VER-win-x64.zip" \
                "не подписаны" "## Автообновление" "SHA256SUMS.txt"; do
    printf '%s\n' "$notes" | grep -qF "$marker" || miss="$miss «$marker»"
  done
  # Незаполненные заготовки: <версия>, TODO, XXX, {{…}}. Латиница в угловых скобках не
  # ищется намеренно — в теле релиза законно встречается <details>/<summary>.
  left="$(printf '%s\n' "$notes" | grep -nE '<[а-яА-Я][а-яА-Я ]*>|TODO|XXX|\{\{')"
  if [ -n "$miss" ]; then
    bad "5. В теле релиза не хватает:$miss"
  elif [ -n "$left" ]; then
    bad "5. В теле релиза остались заготовки" "$left"
  else
    ok "5. release-notes.sh: тело релиза собрано полностью"
  fi
fi

# ── 6. CHANGELOG под текущую версию ───────────────────────────────────────────
if "$HERE/check-version.sh" --changelog-only "$VER" >/dev/null 2>&1; then
  ok "6. CHANGELOG.md: секция «## [$VER]» на месте и непуста"
else
  bad "6. CHANGELOG.md: нет непустой секции «## [$VER]»"
fi

# ── 7. Workflow ───────────────────────────────────────────────────────────────
WF=".github/workflows/release.yml"
if [ ! -f "$WF" ]; then
  bad "7. Нет файла $WF"
elif command -v actionlint >/dev/null 2>&1; then
  out="$(actionlint "$WF" 2>&1)"
  [ $? -eq 0 ] && ok "7. actionlint: $WF без замечаний" || bad "7. actionlint нашёл проблемы" "$out"
else
  # actionlint не установлен (brew install actionlint) — проверяем хотя бы то, что триггеры
  # именно такие, как задумано: релиз не должен запускаться обычным push в ветку.
  miss=""
  grep -q "tags:" "$WF"             || miss="$miss триггер по тегам"
  grep -q "workflow_dispatch:" "$WF" || miss="$miss workflow_dispatch"
  grep -qE "^\s+branches:" "$WF"     && miss="$miss ЛИШНИЙ триггер по веткам"
  grep -q "pull_request:" "$WF"      && miss="$miss ЛИШНИЙ триггер pull_request"
  if [ -z "$miss" ]; then
    skip "7. actionlint не установлен; триггеры проверены вручную — только теги и ручной запуск"
  else
    bad "7. Триггеры workflow не те, что задуманы:$miss"
  fi
fi

# ── 8. Отделяемость модуля ────────────────────────────────────────────────────
# а) все файлы модуля на месте; б) ничто вне модуля на него не ссылается — значит удаление
# не оставит битых ссылок; в) прогон тестов ничего не изменил.
prob=()
for p in "${MODULE_PATHS[@]}"; do
  [ -f "$p" ] || prob+=("нет файла $p")
  # Папка называется github-release, а не release, не для красоты: в .gitignore есть
  # стандартное для .NET правило «[Rr]elease/», и папка Scripts/release/ молча выпала бы
  # из git целиком. Проверяем явно, чтобы это не повторилось при переименовании.
  git check-ignore -q "$p" 2>/dev/null && prob+=("$p игнорируется git — файл не попадёт в репозиторий")
done

# Ищем упоминания модуля в отслеживаемых файлах ВНЕ модуля. Внутри модуля ссылки свободны.
refs="$(git ls-files -z -- . ':(exclude).github' ':(exclude)Scripts/github-release' \
        ':(exclude)docs/developer/guides/release.md' ':(exclude)CHANGELOG.md' \
        | xargs -0 grep -lIE 'Scripts/github-release|CHANGELOG\.md|guides/release\.md|workflows/release\.yml' \
          2>/dev/null)"
[ -n "$refs" ] && prob+=("на модуль ссылаются файлы вне него: $(echo "$refs" | tr '\n' ' ')")

[ "$(git status --porcelain)" = "$STATUS_BEFORE" ] \
  || prob+=("прогон тестов изменил рабочее дерево")

if [ ${#prob[@]} -eq 0 ]; then
  ok "8. Модуль отделяем: 7 файлов на месте, ссылок извне нет, дерево не тронуто"
else
  bad "8. Отделяемость нарушена" "${prob[@]}"
fi

# ── Итог ──────────────────────────────────────────────────────────────────────
echo
echo "Пройдено: $passed, пропущено: $skipped, провалено: $failed"
if [ $failed -eq 0 ]; then
  echo
  echo "Полная проверка удаляемости (руками, до первого релиза):"
  echo "  git stash -u && dotnet build && dotnet test Tests/Unit && git stash pop"
fi
exit $(( failed > 0 ))
