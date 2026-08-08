#!/bin/bash
#
# Заводит команду `git push actions` — по ней и только по ней GitHub гоняет полную проверку.
#
#   Scripts/ci/setup-remote.sh
#
# Выполняется один раз в каждой копии репозитория: настройка живёт в .git/config, а он не под
# версионным контролем. Скрипт идемпотентен — повторный запуск просто переписывает её заново.
#
# Что получается:
#   git push          → отправляет main; GitHub не запускает ничего
#   git push actions  → отправляет main И обновляет ветку `ci`; её пуш и есть сигнал к проверке
#
# Ветка `ci` служебная: она всегда указывает на тот же коммит, что main, и нужна ровно затем,
# чтобы у запуска проверки был собственный сигнал, не смешанный с обычной работой. Отдельный
# удалённый репозиторий `actions` смотрит на тот же адрес, что и origin, — разница только
# в том, какие ссылки он отправляет.
set -uo pipefail

REMOTE="actions"
BRANCH="ci"

cd "$(dirname "$0")/../.." || exit 1

die() { echo "✗ $1" >&2; exit 1; }

git rev-parse --git-dir > /dev/null 2>&1 || die "это не репозиторий git"

URL="$(git remote get-url origin 2>/dev/null)"
[ -n "$URL" ] || die "нет удалённого репозитория origin — настраивать нечего"

if git remote get-url "$REMOTE" > /dev/null 2>&1; then
  git remote set-url "$REMOTE" "$URL"
else
  git remote add "$REMOTE" "$URL"
fi

# Обе ссылки задаются от refs/heads/main, а НЕ от HEAD: иначе `git push actions`, набранный
# с любой другой ветки, отправил бы её содержимое в main.
git config --unset-all "remote.$REMOTE.push" 2>/dev/null
git config --add "remote.$REMOTE.push" "refs/heads/main:refs/heads/main"
# Со знаком «+»: ветка `ci` перезаписывается всегда, её история значения не имеет.
git config --add "remote.$REMOTE.push" "+refs/heads/main:refs/heads/$BRANCH"

# Чтобы `git fetch --all` не ходил к тому же самому серверу второй раз.
git config "remote.$REMOTE.skipFetchAll" true

echo "✓ Готово. Адрес: $URL"
echo
echo "  git push          — отправить main, ничего не запуская"
echo "  git push actions  — отправить main и запустить полную проверку на всех ОС"
echo
echo "Ход прогона: вкладка Actions в репозитории."
# Фигурные скобки обязательны: bash 3.2, который стоит в macOS, затягивает следующий за
# переменной многобайтный символ «»» в её имя и падает с «unbound variable».
echo "Если код не менялся с прошлого раза, ссылка «${BRANCH}» не сдвинется и прогон не начнётся —"
echo "перезапустите его там же кнопкой «Run workflow»."
