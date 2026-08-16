# Инструкция пользователя (раздел настроек)

## Назначение

Показ пользовательской документации прямо в программе: **Настройки → Инструкция
пользователя**. Слева текст, справа панель **«Содержание»** — клик по пункту
прокручивает документ к нужному месту, ширина панели тянется ручкой и помнится
между запусками.

Источник — те же файлы `docs/user/*.md`, что читаются на GitHub. Копии в `Assets/`
намеренно нет: две копии разъехались бы.

## Где в коде

- Логика раздела: [SettingsGuide.cs](../../../Views/Settings/SettingsGuide.cs) (`partial class SettingsView`)
- Разметка: [SettingsView.axaml](../../../Views/Settings/SettingsView.axaml) — `GuidePanel`, `GuideTocPanel`, `GuideTocDivider`, `GuideTocResizeGrip`, стили `Border.tocrow`
- Порядок документов: [GuideRegistry.cs](../../../Settings/GuideRegistry.cs)
- Пункт сайдбара: [SettingsRegistry.cs](../../../Settings/SettingsRegistry.cs) (`guide`, иконка `IconBook`)
- Вид markdown: [Assets/Styles/Markdown.axaml](../../../Assets/Styles/Markdown.axaml)
- Ширина панели: [AppSettings](../../../Utils/AppSettings.cs) (`guideTocWidth`, Min/Max/Default)
- Файлы в сборке: [SmartBase.csproj](../../../SmartBase.csproj) — `<AvaloniaResource Include="docs\user\**\*.md" />`

## Как работает

### Документы

`GuideRegistry.All` задаёт порядок; заголовок пункта в оглавлении в реестре **не
дублируется** — берётся первый `#` самого документа. `README.md` из папки в реестр не
входит: это оглавление для GitHub, в программе его роль играет панель «Содержание».

Файлы читаются `AssetLoader.Open` по адресу `avares://SmartBase/docs/user/<файл>.md`
(`GuideRegistry.UriOf`). Не прочитанный файл — предупреждение в журнал и пропуск;
если не прочитался ни один, в разделе показывается пояснение вместо текста.

### Почему текст режется на секции

Документ делится по заголовкам второго уровня (`SplitBySections`), каждая секция
рендерится своим `MarkdownScrollViewer` в общий `StackPanel` (`GuideHost`), а прокруткой
заведует НАШ `ScrollViewer` (`GuidePanel`).

Так сделано ради навигации: у одного `MarkdownScrollViewer` на весь текст своя
прокрутка, и положение заголовка внутри отрендеренного markdown пришлось бы искать
по визуальному дереву. С секциями положение любой из них известно точно
(`TranslatePoint` относительно `GuidePanel`).

Вопреки имени, `MarkdownScrollViewer` — **не** `ScrollViewer`: свойств
`VerticalScrollBarVisibility`/`HorizontalScrollBarVisibility` у типа нет, отключать
его прокрутку не нужно и нечем. Внутри вертикального `StackPanel` высота не ограничена,
поэтому контрол разворачивается на весь текст.

Секция `## Связанные документы` в программу не попадает: wiki-ссылки `[[имя]]` там
не кликаются, это навигация для GitHub.

### Оглавление и прокрутка

Пункты строятся в code-behind (`AddTocRow`): заголовок документа — жирной строкой,
разделы — подпунктами с отступом. Стили `Border.tocrow` повторяют строку сайдбара
(`SidebarList.axaml`) — панель это та же навигация, только внутри раздела.

Клик прокручивает `GuidePanel.Offset` анимацией easeOutCubic за 300 мс — тот же приём,
что в календаре (`ListView.AnimateScroll`). Активный пункт подсвечивается по
`ScrollChanged`: Y-координаты секций считаются один раз и кешируются, кеш сбрасывается
по изменению `Bounds` хоста (зум, размер шрифта, ширина панели).

### Панель «Содержание»

Устроена как панель «Детали» в «Диагностике»: лежит в колонке 2 корневой сетки во всю
высоту, навбар высотой 56 встаёт вровень с навбаром настроек. Своей линии под шапкой
нет — её рисует общая линия окна на `y=56` (MainWindow); вторая при зуме дала бы двойную.

Ручка ширины — копия `LogsDetailResizeGrip` (`Pointer`-триада + `Math.Clamp`). Отличие
одно: ширина **сохраняется** в `app.db` (`guideTocWidth`), потому что панель открыта всё
время, пока выбран раздел, — у «Деталей» она появляется по клику и живёт в пределах сессии.

Панель, разделитель и ручка лежат в корне сетки, поэтому `ShowCategory` гасит их явно
(`ShowGuideToc`) — как `CloseLogsDetail` у журнала.

### Вид markdown

`Assets/Styles/Markdown.axaml` ставится контролу как `MarkdownStyle`
(`SettingsGuide.MarkdownStyles`) **вместо** встроенных тем пакета (`MarkdownStyleGithubLike`
и прочие). Через `Application.Styles` это не работает: собственный стиль контрола сильнее
глобального, и встроенная тема всегда выигрывала — заголовки шли своим размером, таблицы
рисовались чёрными рамками с «зеброй».

Рендер вешает на элементы классы (`Heading1`…`Heading6`, `Paragraph`, `Blockquote`,
`CodeBlock`, `Table`, `TableHeader`, `TableRowOdd`/`TableRowEven`, `List`, `ListMarker`,
`Note`), по ним и стилизуем. Текст — `CTextBlock` из ColorTextBlock.Avalonia, а не
Avalonia-шный `TextBlock`, поэтому селекторы пишутся по `ctxt|CTextBlock`.

Иерархия заголовков потребовала промежуточного размера между `FontSizeBase` (12) и
`FontSizeLarge` (20) — добавлен токен **`FontSizeMedium`** (16). Он перечислен поимённо
в [FontScale](../../../Utils/FontScale.cs): без этого «Размер шрифта» его бы не двигал.

### Сборка раздела

Ленивая, как у «Диагностики»: `ShowCategory("guide")` → `BuildGuide()`, дальше флаг
`_guideBuilt` не даёт пересобирать. Пересборка при смене размера шрифта не нужна —
размеры сидят на `DynamicResource`.

## Взаимодействия

Добавить документ: положить файл в `docs/user/`, дописать строку в `GuideRegistry.All`.
Правка csproj не нужна — глоб `docs\user\**\*.md` заберёт файл сам.

## Связанные документы

[[settings]], [[icons]], [[../ui/theming]], [[logging]]
