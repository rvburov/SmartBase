#!/bin/bash
#
# Выпуск релиза SmartBase на GitHub — локальная половина процесса.
#
#   Scripts/github-release/release.sh 1.0.4                 # выпустить версию 1.0.4
#   Scripts/github-release/release.sh 1.0.4 --dry-run       # только проверки, ничего не менять
#   Scripts/github-release/release.sh 1.0.4 --skip-tests    # без прогона Tests/Unit
#   Scripts/github-release/release.sh 1.0.4 --allow-dirty   # разрешить незакоммиченные правки
#
# Что делает: проверяет предусловия → правит версию в SmartBase.csproj и бейдж в README.md →
# проставляет дату в секции CHANGELOG → коммитит `release: v1.0.4` → ставит аннотированный тег
# v1.0.4 → пушит ветку и тег. Собирает и публикует уже GitHub — .github/workflows/release.yml
# ловит push тега. Обычный `git push` релиз не запускает.
#
# Сам скрипт ничего не собирает и не требует gh CLI — только git.
# Полное описание процесса: docs/developer/guides/release.md
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
HERE="$(cd "$(dirname "$0")" && pwd)"
CSPROJ="$ROOT/SmartBase.csproj"
README="$ROOT/README.md"
CHANGELOG="$ROOT/CHANGELOG.md"
REPO_URL="https://github.com/rvburov/SmartBase"

VERSION=""
DRY_RUN=0
SKIP_TESTS=0
ALLOW_DIRTY=0

for arg in "$@"; do
  case "$arg" in
    --dry-run)     DRY_RUN=1 ;;
    --skip-tests)  SKIP_TESTS=1 ;;
    --allow-dirty) ALLOW_DIRTY=1 ;;
    -h|--help)     sed -n '2,20p' "$0"; exit 0 ;;
    -*)            echo "Неизвестный ключ: $arg" >&2; exit 1 ;;
    *)             [ -z "$VERSION" ] && VERSION="$arg" || { echo "Лишний аргумент: $arg" >&2; exit 1; } ;;
  esac
done

die()  { echo "✗ $1" >&2; exit 1; }
info() { echo "  $1"; }
step() { echo "→ $1"; }

[ -n "$VERSION" ] || die "не указана версия. Пример: Scripts/github-release/release.sh 1.0.4"

# ── 1. Формат версии ──────────────────────────────────────────────────────────
# Только X.Y.Z. Суффиксы вида 1.0.4-rc.1 запрещены: у shields.io дефис — разделитель полей
# label-message-color, и бейдж «version-1.0.4-rc.1-blue» распадётся на message=1.0.4 и
# color=rc.1. Бейдж сломается, а check-version.sh прочитает «1.0.4» и пропустит расхождение.
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] \
  || die "версия «${VERSION}» не в формате X.Y.Z (предвыпускные суффиксы не поддерживаются)"

cd "$ROOT" || die "не удалось перейти в $ROOT"

# ── 2. Ветка и чистота рабочего дерева ────────────────────────────────────────
BRANCH="$(git rev-parse --abbrev-ref HEAD)"
[ "$BRANCH" = "main" ] || die "текущая ветка «${BRANCH}», релизы выпускаются из main"

if [ -n "$(git status --porcelain)" ]; then
  if [ $ALLOW_DIRTY -eq 1 ]; then
    echo "⚠ рабочее дерево не чистое, продолжаю из-за --allow-dirty"
  else
    git status --short >&2
    die "рабочее дерево не чистое — закоммитьте или спрячьте правки"
  fi
fi

# ── 3. Синхронность с origin ──────────────────────────────────────────────────
# Сеть может быть недоступна. В боевом запуске это отказ: без сверки с origin легко выпустить
# тег на устаревшем коммите. В --dry-run — предупреждение, чтобы прогонять проверки офлайн.
NET_OK=1
git fetch --quiet origin main --tags 2>/dev/null || NET_OK=0

if [ $NET_OK -eq 1 ]; then
  LOCAL="$(git rev-parse HEAD)"
  REMOTE="$(git rev-parse origin/main)"
  if [ "$LOCAL" != "$REMOTE" ]; then
    git merge-base --is-ancestor "$REMOTE" "$LOCAL" \
      && die "есть неотправленные коммиты — сначала git push origin main" \
      || die "локальная main разошлась с origin/main — сначала git pull"
  fi
elif [ $DRY_RUN -eq 1 ]; then
  echo "⚠ origin недоступен: проверки против удалённого репозитория пропущены (--dry-run)"
else
  die "не удалось связаться с origin — проверьте сеть и доступ к $REPO_URL"
fi

# ── 4. Тег свободен ───────────────────────────────────────────────────────────
TAG="v$VERSION"
git rev-parse -q --verify "refs/tags/$TAG" >/dev/null \
  && die "тег $TAG уже существует локально. Пересобрать релиз, не двигая тег, можно через
    вкладку Actions → Release → Run workflow с параметром tag=$TAG"

if [ $NET_OK -eq 1 ] && git ls-remote --exit-code --tags origin "$TAG" >/dev/null 2>&1; then
  die "тег $TAG уже опубликован на GitHub — версия занята"
fi

# ── 5. Новая версия строго больше текущей ─────────────────────────────────────
CURRENT="$(sed -nE 's#.*<Version>([^<]*)</Version>.*#\1#p' "$CSPROJ" | head -n1)"
[ -n "$CURRENT" ] || die "не удалось прочитать <Version> из SmartBase.csproj"

ver_gt() {   # 0, если $1 строго больше $2 покомпонентно
  local IFS=.
  # shellcheck disable=SC2206
  local a=($1) b=($2) i
  for i in 0 1 2; do
    (( 10#${a[i]} > 10#${b[i]} )) && return 0
    (( 10#${a[i]} < 10#${b[i]} )) && return 1
  done
  return 1
}
ver_gt "$VERSION" "$CURRENT" || die "версия $VERSION не больше текущей $CURRENT"

# ── 6. Согласованность текущего состояния и готовность CHANGELOG ──────────────
"$HERE/check-version.sh" >/dev/null \
  || die "текущее состояние рассогласовано — запустите Scripts/github-release/check-version.sh"

"$HERE/check-version.sh" --changelog-only "$VERSION" || die "CHANGELOG.md не готов к выпуску.
    Переименуйте секцию «## [Не выпущено]» в «## [$VERSION]» и опишите изменения."

# ── 7. Быстрый гейт: модульные тесты ──────────────────────────────────────────
if [ $SKIP_TESTS -eq 0 ]; then
  step "dotnet test Tests/Unit"
  dotnet test Tests/Unit --nologo --verbosity quiet \
    || die "модульные тесты не прошли — релиз отменён"
else
  echo "⚠ модульные тесты пропущены (--skip-tests)"
fi

# ── 8. План и подтверждение ───────────────────────────────────────────────────
TODAY="$(date +%F)"
echo
echo "Релиз SmartBase $VERSION (сейчас $CURRENT)"
info "SmartBase.csproj   <Version> → $VERSION"
info "README.md          бейдж → version-$VERSION-blue"
info "CHANGELOG.md       ## [$VERSION] → ## [$VERSION] — $TODAY"
info "коммит             release: $TAG"
info "тег                $TAG (аннотированный)"
info "push               origin main, затем origin $TAG"
info "далее              GitHub соберёт win-x64, linux-x64, osx-arm64, osx-x64 и опубликует релиз"
echo

if [ $DRY_RUN -eq 1 ]; then
  echo "✓ проверки пройдены. Это --dry-run: ни один файл не изменён."
  exit 0
fi

read -r -p "Выпустить $TAG? [y/N] " answer
case "$answer" in
  [yYдД]) ;;
  *) echo "Отменено."; exit 0 ;;
esac

# ── 9. Правка версии ──────────────────────────────────────────────────────────
# Через временный файл — портативно к GNU/BSD sed (без sed -i), как в Scripts/*-build.sh.
# Выражения совпадают с теми, что в build-скриптах: одно поведение на два пути.
step "правка версии"
tmp="$(mktemp)"
sed -E "s#<Version>[^<]*</Version>#<Version>${VERSION}</Version>#" "$CSPROJ" > "$tmp" && mv "$tmp" "$CSPROJ"
tmp="$(mktemp)"
sed -E "s#version-[^-]*-blue#version-${VERSION}-blue#" "$README" > "$tmp" && mv "$tmp" "$README"

# Дата в заголовке секции: «## [1.0.4]» или «## [1.0.4] — старая дата» → «## [1.0.4] — сегодня».
# Разделитель @, а не #: в замене стоит «##», которое оборвало бы команду s#...#...#.
# Точки в номере версии экранируются — иначе «1.0.4» совпало бы и с «1a0b4».
VERSION_RE="${VERSION//./\\.}"
tmp="$(mktemp)"
sed -E "s@^## \[${VERSION_RE}\].*\$@## [${VERSION}] — ${TODAY}@" "$CHANGELOG" > "$tmp" && mv "$tmp" "$CHANGELOG"

# Ссылки-сноски внизу CHANGELOG: сдвинуть «Не выпущено» на новую версию и добавить строку
# сравнения для неё. Если сносок нет — молча пропускаем, файл остаётся валидным.
if grep -q '^\[Не выпущено\]:' "$CHANGELOG"; then
  tmp="$(mktemp)"
  awk -v new="$VERSION" -v old="$CURRENT" -v url="$REPO_URL" '
    /^\[Не выпущено\]:/ {
      print "[Не выпущено]: " url "/compare/v" new "...HEAD"
      print "[" new "]: " url "/compare/v" old "...v" new
      next
    }
    { print }
  ' "$CHANGELOG" > "$tmp" && mv "$tmp" "$CHANGELOG"
fi

"$HERE/check-version.sh" "$VERSION" || die "после правки версии остались расхождения"

# ── 10. Коммит, тег, отправка ─────────────────────────────────────────────────
step "коммит и тег"
git add "$CSPROJ" "$README" "$CHANGELOG"
git commit -q -m "release: $TAG"
git tag -a "$TAG" -m "SmartBase $VERSION"

# Порядок важен: если push ветки отклонён (кто-то опередил), тег в удалёнку не уедет,
# и версию можно перевыпустить, ничего не разбирая на сервере.
step "push origin main"
git push --quiet origin main || die "push ветки отклонён. Тег $TAG создан локально; после
    git pull --rebase повторите: git push origin main && git push origin $TAG"

step "push origin $TAG"
git push --quiet origin "$TAG" || die "push тега отклонён — отправьте вручную: git push origin $TAG"

echo
echo "✓ $TAG отправлен. Сборка идёт на GitHub:"
echo "  $REPO_URL/actions"
echo "  $REPO_URL/releases/tag/$TAG   (появится через ~12 минут)"
