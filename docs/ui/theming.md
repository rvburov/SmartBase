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
  - `BrushCardBg` — фон карточки-блока внутри диалога (например, «Параметры ручного
    импорта»): серый как у полей ввода. Контролы НА такой карточке помечаются классом
    `onCard` — `TextBox.onCard` (белый фон во всех состояниях, Controls.axaml) и
    `CheckBox.onCard` (белый незакрашенный квадрат, тема AppCheckBox) — иначе они
    сливаются с фоном карточки.
- **Typography.axaml** — размеры/начертания шрифтов, например `FontSizeBase`.
- **Controls.axaml** — стили стандартных и кастомных контролов.
  - Контекстное меню текстовых полей — по-русски: глобальный стиль `TextBox`
    задаёт `ContextFlyout` с пунктами «Вырезать/Копировать/Вставить» (команды и
    жесты — те же, что у дефолтного флайаута темы Fluent, где заголовки — английские
    Cut/Copy/Paste: Avalonia сама не локализуется). Действует на все поля ввода
    приложения на любой ОС.
  - **Единая высота элементов формы** — токен `ControlHeightStandard` (double, 30).
    Поля (`TextBox`), выпадающие списки (`ComboBox`) и кнопки-действия (`Button.dialog`)
    в строках настроек/диалогов стоят рядом и обязаны совпадать по высоте. Тема Fluent
    навязывает `TextBox`/`ComboBox` свою min-высоту (~32px) — глобальные стили обоих
    перебивают её `MinHeight="{StaticResource ControlHeightStandard}"`, поэтому заданный
    в разметке `Height="{StaticResource ControlHeightStandard}"` реально применяется.
    Многострочные редакторы (`notesEditor`) растут выше — токен задаёт лишь нижнюю
    границу; поле-значение карточки (`TextBox.propValue`) сбрасывает её в 0 (высоту там
    диктует строка карточки). Новые поля/списки в формах — с этим токеном.

## Стандарт состояний контролов (радиус, обводка, наведение)

Повторяющиеся визуальные элементы полей/списков/кнопок — **только токенами**, не
литералами. Единый набор (все — в `Assets/Styles/`):

| Элемент | Токен | Значение | Где применяется |
|---|---|---|---|
| Скругление углов | `RadiusXS`/`Small`/`Base`/`Medium`/`Large`/`Pill` | 4/5/6/8/12/15 | поля и списки — `RadiusBase`(6), плашки — `RadiusMedium`(8) |
| Обычная (серая) обводка | `BrushSeparator` + толщина `BorderLine` | `#E5E5EA`, 1px | рамка `TextBox`/`ComboBox`/`Button.dialog` в покое |
| Обводка фокуса/наведения (синяя) | `BrushAccent` | `#007AFF` | `TextBox`/`ComboBox` `:pointerover`/`:focus` → `BorderBrush` (толщина остаётся `BorderLine` 1px) |
| Толщина «жирной» акцентной обводки | `BorderLineFocus` | 2px | поле даты/времени (`Border.timeField:focus-within`). Обычным `TextBox`/`ComboBox` НЕ задаём: у безрамочных полей (поиск, редактор ячейки, `timeInput`) 2px рисует квадрат поверх скруглённого поля |
| Фон наведения | `BrushHover` | `#E5E5EA` | `Button:pointerover` |
| Выделение/акцентная подложка | `BrushAccentLight` | `#E5F0FF` | выбранная строка, `:selected` |
| Высота элемента формы | `ControlHeightStandard` | 30 | см. раздел выше |

Правило: цвет обводки/фона и радиус в разметке и в code-behind берутся из этих ключей
(`{StaticResource …}` / `TryFindResource`), а не хардкодятся. Так «синяя обводка при
фокусе», «серая рамка» и скругления одинаковы во всех контролах и меняются в одном месте.

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

Масштаб (0.7–1.3, шаг 0.1) хранится в настройках (`AppSettings.LoadScale/SaveScale`),
центр — [Utils/Scaling.cs](../../Utils/Scaling.cs); диапазон и шаг — константы в
[AppSettings](../../Utils/AppSettings.cs). Зумируется ВСЁ приложение, четырьмя
механизмами (новые поверхности обязаны попадать в один из них):

- **MainWindow** — `LayoutTransform` на ScaleHost (MainWindow.ApplyScale по
  `Scaling.ScaleChanged`).
- **Отдельные окна** (диалоги, окна расширений, сплэш, установщик) —
  [Utils/WindowZoom.cs](../../Utils/WindowZoom.cs), подключается глобальным стилем
  `Window` в Controls.axaml: контент оборачивается в LayoutTransformControl, конечные
  габариты окна (Width/Height/Min/Max) умножаются на зум, SizeToContent пересчитывается
  сам; пересчёт живых окон по `Scaling.ScaleChanged`.
- **Попапы с доступным Popup** — `Popup.InheritsTransform="True"`: попап берёт
  накопленный трансформ плейсмент-таргета. Ставится атрибутом у XAML-попапов
  (TimeField/DateField/DateEditor, время в RecordDialog, пузырьки настроек, попап дня
  в Годе) и стилями в Controls.axaml для сабменю `MenuItem /template/ Popup#PART_Popup`
  и `CalendarDatePicker`; ComboBox наследует из коробки (Fluent).
- **Попапы с недоступным Popup** — ContextMenu и ToolTip (их Popup создаётся внутри
  Avalonia): локальные копии тем с обёрткой корня шаблона в LayoutTransformControl —
  [ZoomPopupThemes.axaml](../../Assets/Styles/ZoomPopupThemes.axaml) (пришпилены к
  Avalonia 11.2.3, паспорт в файле). Flyout-контент из кода — через
  `WindowZoom.WrapPopupContent(...)` (фильтр таблицы, Toast).

Трансформ зума публикуется ресурсом приложения `AppZoomTransform` (Scaling) — его
используют все обёртки через DynamicResource. Токены линий: всё, что внутри зума
(включая контент попапов), использует ОБЫЧНЫЕ токены (`BorderLine`,
`SeparatorThickness`); `BorderLinePopup`/`SeparatorThicknessPopup` остаются только для
хрома вне трансформа (плашки FlyoutPresenter: filterFlyout, toast).

## Связанные документы

[[widgets]], [[dialogs]], [[../features/settings]]
