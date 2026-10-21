#!/bin/bash
#
# Тесты модуля выпуска релизов. Чистый bash: .NET, сеть и gh не нужны.
#
#   Scripts/github-release/release-test.sh
#
# Живут внутри модуля и удаляются вместе с ним — в Tests/Unit ничего не добавляется.
# Главный инвариант: прогон НИЧЕГО не меняет. Проверка 8 подтверждает это машинно и заодно
# следит за тем, что модуль остается отделяемым — ни один файл вне него на модуль не ссылается.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HERE="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT" || exit 1

# Пути модуля. При удалении также убирается его единственная строка в docs/README.md.
# Тот же список в разделе «Как удалить модуль» файла docs/developer/guides/release.md.
MODULE_PATHS=(
  ".github/workflows/release.yml"
  "Scripts/github-release/release.sh"
  "Scripts/github-release/check-version.sh"
  "Scripts/github-release/release-notes.sh"
  "Scripts/github-release/release-test.sh"
  "Scripts/github-release/dmg-background.swift"
  "docs/developer/guides/release.md"
  "Scripts/github-release/release-history.md"
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

# Предусловия release.sh проверяем в отдельном репозитории с локальным origin. Настоящая
# рабочая ветка может называться иначе, а ее origin может быть недоступен. Ни сеть, ни
# переключение ветки пользователя для проверки выпуска не нужны.
FIXTURE="$(mktemp -d "${TMPDIR:-/tmp}/smartbase-release-test.XXXXXX")" || exit 1
trap 'rm -rf "$FIXTURE"' EXIT
FIXTURE_ROOT="$FIXTURE/source"
FIXTURE_HERE="$FIXTURE_ROOT/Scripts/github-release"
fixture_git() { git -C "$FIXTURE_ROOT" "$@"; }
fixture_commit() {
  fixture_git -c user.name='Roman Burov' -c user.email='romanburov89@gmail.com' \
    -c commit.gpgsign=false commit -qm "$1"
}
fixture_setup() {
  mkdir -p "$FIXTURE_HERE" "$FIXTURE/no-hooks" || return 1
  cp "$HERE/release.sh" "$HERE/check-version.sh" "$FIXTURE_HERE/" || return 1
  cp "$ROOT/SmartBase.csproj" "$ROOT/README.md" "$FIXTURE_ROOT/" || return 1
  printf '# История релизов\n\n## [%s]\n\n- Проверочная версия.\n' "$VER" \
    > "$FIXTURE_HERE/release-history.md"
  git init --bare -q "$FIXTURE/origin.git" || return 1
  git init -q -b main "$FIXTURE_ROOT" || return 1
  fixture_git config core.hooksPath "$FIXTURE/no-hooks" || return 1
  fixture_git add . || return 1
  fixture_commit 'Проверочная версия' || return 1
  fixture_git remote add origin "$FIXTURE/origin.git" || return 1
  fixture_git push -q -u origin main || return 1
}
fixture_state() {
  fixture_git status --porcelain
  fixture_git rev-parse HEAD
  fixture_git show-ref
}
if ! fixture_setup; then
  bad 'Не удалось создать изолированный стенд выпуска'
  exit 1
fi

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
    bad "2. check-version.sh не отчитался о трех расхождениях на 9.9.9" "$out"
  fi
else
  bad "2. check-version.sh падает на текущем состоянии" \
      "запустите Scripts/github-release/check-version.sh и посмотрите вывод"
fi

# ── 3. release.sh отвергает плохие версии ─────────────────────────────────────
reject() {   # отказ должен быть именно из-за версии, а не ветки/сети/истории
  local result code
  result="$(bash "$FIXTURE_HERE/release.sh" "$1" --dry-run --skip-tests 2>&1)"
  code=$?
  [ "$code" -ne 0 ] && printf '%s\n' "$result" | grep -F "$2" >/dev/null
}
bad3=""
reject "1.0" 'не в формате X.Y.Z'        || bad3="$bad3 «1.0» (не X.Y.Z)"
reject "1.0.4-rc.1" 'не в формате X.Y.Z' || bad3="$bad3 «1.0.4-rc.1» (предвыпускной суффикс)"
# Фигурные скобки обязательны везде, где за подстановкой сразу идет «»»: bash 3.2 из macOS
# затягивает многобайтный символ в имя переменной и падает с «unbound variable». Строка живет
# в ветке отказа, поэтому годами не выполняется — и сломала бы ровно тот прогон, ради которого
# проверка написана.
reject "$VER" 'не больше текущей'       || bad3="$bad3 «${VER}» (не больше текущей)"
reject "1.0.4.5" 'не в формате X.Y.Z'    || bad3="$bad3 «1.0.4.5» (четыре компоненты)"
[ -z "$bad3" ] && ok "3. release.sh отвергает некорректные версии" \
               || bad "3. release.sh пропустил:$bad3"

# ── 4. Счастливый путь --dry-run ──────────────────────────────────────────────
out="$(bash "$FIXTURE_HERE/release.sh" "$NEXT" --dry-run --skip-tests 2>&1)"
code=$?
if [ "$code" -eq 0 ] || ! printf '%s\n' "$out" | grep -F 'release-history.md не готов' >/dev/null; then
  bad '4. Незаполненная история должна останавливать выпуск' "$out"
else
  printf '\n## [%s]\n\n- Изменение для проверки выпуска.\n' "$NEXT" \
    >> "$FIXTURE_HERE/release-history.md"
  fixture_git add Scripts/github-release/release-history.md
  if fixture_commit 'Описание следующей версии' && fixture_git push -q origin main; then
    fixture_before="$(fixture_state)"
    out="$(bash "$FIXTURE_HERE/release.sh" "$NEXT" --dry-run --skip-tests 2>&1)"
    code=$?
    if [ "$code" -ne 0 ]; then
      bad "4. release.sh --dry-run $NEXT завершился с ошибкой" "$out"
    elif ! printf '%s\n' "$out" | grep -F 'проверки пройдены' >/dev/null; then
      bad "4. release.sh --dry-run $NEXT не напечатал итог" "$out"
    elif [ "$(fixture_state)" != "$fixture_before" ]; then
      bad '4. --dry-run изменил файлы, коммит или ссылки стенда'
    else
      ok "4. История обязательна; --dry-run $NEXT печатает план без изменения файлов и ссылок"
    fi
  else
    bad '4. Не удалось подготовить историю на изолированном стенде'
  fi
fi

# ── 5. release-notes.sh ───────────────────────────────────────────────────────
notes="$("$HERE/release-notes.sh" "$VER" 2>&1)"
if [ $? -ne 0 ]; then
  bad "5. release-notes.sh завершился с ошибкой" "$notes"
else
  miss=""
  for marker in "## Что нового" "## Установка" "### Первый запуск" "SmartBase-Setup.exe" \
                "SmartBase-$VER-osx-arm64.dmg" "SmartBase-$VER-linux-x64.tar.gz" \
                "Все равно открыть" "Выполнить в любом случае" \
                "## Автообновление" "Не переименовывайте установщик"; do
    printf '%s\n' "$notes" | grep -F "$marker" > /dev/null || miss="$miss «${marker}»"
  done
  # Незаполненные заготовки: <версия>, TODO, XXX, {{…}}. Латиница в угловых скобках не
  # ищется намеренно — в теле релиза законно встречается <details>/<summary>.
  # Явный алфавит: диапазон а-я зависит от LC_COLLATE и бывает недопустим в C.UTF-8.
  cyrillic='абвгдежзийклмнопрстуфхцчшщъыьэюяАБВГДЕЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ'
  left_status=0
  left="$(printf '%s\n' "$notes" | grep -nE "<[$cyrillic][$cyrillic ]*>|TODO|XXX|\\{\\{")" || left_status=$?
  if [ "$left_status" -gt 1 ]; then
    bad "5. Не удалось проверить тело релиза на заготовки (код $left_status)" "$left"
  elif [ -n "$miss" ]; then
    bad "5. В теле релиза не хватает:$miss"
  elif [ -n "$left" ]; then
    bad "5. В теле релиза остались заготовки" "$left"
  else
    ok "5. release-notes.sh: тело релиза собрано полностью"
  fi
fi

# ── 6. История релизов под текущую версию ─────────────────────────────────────
if "$HERE/check-version.sh" --changelog-only "$VER" >/dev/null 2>&1; then
  ok "6. release-history.md: секция «## [$VER]» на месте и непуста"
else
  bad "6. release-history.md: нет непустой секции «## [$VER]»"
fi

# ── 7. Workflow ───────────────────────────────────────────────────────────────
WF=".github/workflows/release.yml"
wf_problems=""

# Триггеры: релиз не должен запускаться обычным push в ветку или пул-реквестом.
if [ -f "$WF" ]; then
  grep -q "tags:" "$WF"              || wf_problems="$wf_problems нет триггера по тегам;"
  grep -q "workflow_dispatch:" "$WF" || wf_problems="$wf_problems нет workflow_dispatch;"
  grep -qE "^\s+branches:" "$WF"     && wf_problems="$wf_problems ЛИШНИЙ триггер по веткам;"
  grep -q "pull_request:" "$WF"      && wf_problems="$wf_problems ЛИШНИЙ триггер pull_request;"

  # Шаги workflow идут в шелле с `-o pipefail`, где «команда | grep -q …» ломается:
  # grep закрывает канал на первом совпадении, источник получает SIGPIPE и падает, и шаг
  # объявляется неуспешным ИМЕННО ПОТОМУ, что совпадение нашлось. На этом уже сломалась
  # сборка linux-x64 в прогоне #1. В конвейере grep должен дочитывать поток: без -q,
  # с перенаправлением в /dev/null, либо awk.
  # Строки-комментарии пропускаем: в них конструкция упоминается как раз для объяснения.
  if grep -nE '^[[:space:]]*[^#[:space:]].*\|[[:space:]]*grep[[:space:]]+-[A-Za-z]*q' "$WF" > /dev/null; then
    wf_problems="$wf_problems «| grep -q» ломается о pipefail — уберите -q;"
  fi
fi

if [ ! -f "$WF" ]; then
  bad "7. Нет файла $WF"
elif [ -n "$wf_problems" ]; then
  bad "7. Проблемы в $WF" "$wf_problems"
elif command -v actionlint >/dev/null 2>&1; then
  out="$(actionlint "$WF" 2>&1)"
  [ $? -eq 0 ] && ok "7. actionlint: $WF без замечаний" || bad "7. actionlint нашел проблемы" "$out"
else
  skip "7. actionlint не установлен; триггеры и конвейеры проверены вручную"
fi

# ── 8. Отделяемость модуля ────────────────────────────────────────────────────
# а) файлы на месте; б) снаружи есть только ссылка оглавления, указанная в инструкции
# удаления; в) прогон тестов ничего не изменил.
prob=()
for p in "${MODULE_PATHS[@]}"; do
  [ -f "$p" ] || prob+=("нет файла $p")
  # Папка называется github-release, а не release, не для красоты: в .gitignore есть
  # стандартное для .NET правило «[Rr]elease/», и папка Scripts/release/ молча выпала бы
  # из git целиком. Проверяем явно, чтобы это не повторилось при переименовании.
  git check-ignore -q "$p" 2>/dev/null && prob+=("$p игнорируется git — файл не попадет в репозиторий")
done

# Ищем упоминания модуля в отслеживаемых файлах ВНЕ модуля. Внутри модуля ссылки свободны.
refs="$(git ls-files -z -- . ':(exclude).github' ':(exclude)Scripts/github-release' \
        ':(exclude)docs/developer/guides/release.md' ':(exclude)docs/README.md' \
        | xargs -0 grep -lIE 'Scripts/github-release|release-history\.md|guides/release\.md|workflows/release\.yml' \
          2>/dev/null)"
[ -n "$refs" ] && prob+=("на модуль ссылаются файлы вне него: $(echo "$refs" | tr '\n' ' ')")
# Разрешается только точная Markdown-ссылка в начале строки оглавления. Остаток строки
# проверяется обычным правилом: добавленная рядом зависимость не прячется за исключением.
index_refs="$(awk '
  /^- \[release[.]md\]\(developer\/guides\/release[.]md\)([[:space:]]|$)/ {
    sub(/^- \[release[.]md\]\(developer\/guides\/release[.]md\)/, "")
  }
  /Scripts\/github-release|release-history[.]md|guides\/release[.]md|workflows\/release[.]yml/ { print NR ":" $0 }
' docs/README.md)"
[ -n "$index_refs" ] && prob+=("неразрешенные ссылки из docs/README.md: $index_refs")

[ "$(git status --porcelain)" = "$STATUS_BEFORE" ] \
  || prob+=("прогон тестов изменил рабочее дерево")

if [ ${#prob[@]} -eq 0 ]; then
  ok "8. Восемь файлов на месте; снаружи только ссылка оглавления; дерево не тронуто"
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
