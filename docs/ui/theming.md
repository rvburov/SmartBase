# Оформление (темы, цвета, типографика)

## Назначение

Где задаются цвета, шрифты и стили контролов, и как они подключаются.

## Где в коде

- Ресурсы и стили: [Assets/Styles/](../../Assets/Styles/) — `Colors.axaml`, `Typography.axaml`, `Controls.axaml`
- Подключение: [App.axaml](../../App.axaml)
- Масштабирование интерфейса: [Utils/Scaling.cs](../../Utils/Scaling.cs)

## Как подключено

В [App.axaml](../../App.axaml):

- `Application.Resources` подключает `Colors.axaml` (`ResourceInclude`).
- `Application.Styles` подключает базовую тему `FluentTheme`, стили `DataGrid`,
  затем `Typography.axaml`, `Controls.axaml` и стиль виджета `AppCheckBox`.
- Там же — глобальный стиль `Window` с иконкой приложения (см. [../features/icons.md](../features/icons.md)).

Тема приложения — светлая (`RequestedThemeVariant="Light"`).

## Ресурсы

- **Colors.axaml** — кисти приложения. Используются по имени как `StaticResource`,
  например `BrushBg` (фон окна), `BrushPrimaryText`, `BrushSecondaryText`.
  В календаре часть цветов задана прямо в коде (например, акцент `#FF3B30`,
  вторичный `#8E8E93`); единый «цвет по умолчанию» событий — в
  [EventColors](../../Core/Models/EventColors.cs).
- **Typography.axaml** — размеры/начертания шрифтов, например `FontSizeBase`.
- **Controls.axaml** — стили стандартных и кастомных контролов.

## Линии: единая толщина при любом зуме и DPI (стандарт)

Толщина ВСЕХ линий (разделители, рамки, сетка таблицы) — ДИНАМИЧЕСКИЕ токены, которые
[Utils/LineMetrics.cs](../../Utils/LineMetrics.cs) пишет в `Resources` каждого окна
(пересчёт при смене зума приложения и переезде окна на монитор с другим DPI):

- формула: `physTarget = max(1, round(RenderScaling))`, `t = physTarget / RenderScaling / зум` —
  визуально ~1 логический пиксель, целое число физических пикселей;
- токены: `SeparatorThickness` (double), `BorderLine`, `BorderLineFocus` (=2×, акценты),
  `BorderLineTop/Bottom/Left/Right`, `BorderLinePopup` (попапы рендерятся ВНЕ зума),
  `LineUseLayoutRounding` (bool); дефолты — в `Controls.axaml`;
- ссылаться ТОЛЬКО через `DynamicResource` (StaticResource заморозит дефолт);
  в C# — `LineMetrics.ThicknessFor(visual)`, `MainThickness` (для конвертеров) или
  `element.Bind(..., element.GetResourceObservable("токен"))`;
- **новые линии делать по этому образцу**: чисто-линейным элементам (без скруглений и
  контента) дополнительно `UseLayoutRounding={DynamicResource LineUseLayoutRounding}`
  (при зуме ≠ 1 штатное округление уничтожает компенсированную толщину) и
  `RenderOptions.EdgeMode="Aliased"` — линия защёлкивается в целый пиксель, а не
  размазывается сглаживанием. В атрибутах элементов EdgeMode ставится напрямую,
  из стилей — через мост `(u:LineMetrics.Aliased)="True"` (у RenderOptions нет
  публичных полей для сеттера). На скруглённые рамки Aliased НЕ ставить (зазубрины).
  ВАЖНО: EdgeMode наследуется на всё поддерево и переключает сглаживание ТЕКСТА —
  текст держать ВНЕ aliased-поддерева (пример: заливка «№» в таблице — отдельный
  Rectangle-подложка, TextBlock рядом, а не внутри). Значения «Antialias» у EdgeMode
  НЕТ (в 11.2.3 только Unspecified/Aliased) — «вернуть» сглаживание дочернему
  элементу невозможно, поэтому линию-бордер контейнера с контентом ВЫНОСИТЬ отдельным
  пустым элементом: `Border Classes="separator h|v"` поверх края контейнера или
  слой пустых Border-ячеек поверх сетки (примеры: `GridLines` в MonthView,
  `ColumnLines` в WeekView, разделители строк Настроек, зазор строк сайдбара);
- **одна линия — один источник**: линию в одном визуальном месте рисует ровно ОДИН
  элемент. Две совпадающие линии (например, свой separator под навбаром при уже
  нарисованной линии титульной зоны MainWindow — был такой дубль в календаре,
  удалён) при зуме округляются в разные пиксели и выглядят как утолщение/двойная
  линия. В сетках рисовать только left (кроме первой колонки) + top у каждой
  ячейки, низ/право не рисовать (примеры: MonthView, колонки WeekView/AllDayBar);
- **стык «заливка блока + отдельная линия рядом»** на дробных позициях расходится
  (мягкий край заливки против защёлкнутой линии — белая щель): закрывать общей
  aliased-подложкой под блок И линию (примеры: ActivityBar + разделитель в
  MainWindow.axaml, фон строки заголовков в шаблоне DataGrid). Если линия — бордер
  самого залитого элемента, щели не бывает, подложка не нужна;
- рамки контролов (кнопки, вводы, чекбоксы) — стандартная `BorderLine` (бывшие
  1.2/1.5 унифицированы), акценты (фокус, рамки выделения) — ровно `BorderLineFocus`;
  обводки иконок (`StrokeThickness`) токенами не трогаем — это веса рисунков;
- сетка таблицы конструктора — отдельная история: локальные копии тем DataGrid
  ([TableViewDataGridThemes.axaml](../../Views/ConstructorDb/TableViewDataGridThemes.axaml),
  пришпилены к Avalonia 11.2.3) + `/template/`-стили в TableView.axaml + динамический
  `UseLayoutRounding` на всём DataGrid; подробности — в комментарии-паспорте файла тем.

## Масштаб интерфейса

Масштаб (1.0–1.3, шаг 0.1) хранится в настройках (`AppSettings.LoadScale/SaveScale`)
и применяется через [Utils/Scaling.cs](../../Utils/Scaling.cs). Диапазон и шаг —
константы в [AppSettings](../../Utils/AppSettings.cs).

## Связанные документы

[[widgets]], [[dialogs]], [[../features/settings]]
