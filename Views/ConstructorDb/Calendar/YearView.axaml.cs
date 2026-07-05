using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using DataBase.Core.Models;
using System.Windows.Input;

namespace DataBase.Views.ConstructorDb.Calendar;

public partial class YearView : UserControl
{
    public event EventHandler<DateTime>? MonthSelected;

    // Размеры/веса шрифта — из токенов Typography.axaml (резолв ресурса в рантайме).
    // Свежий размер из Application.Resources — меняется живьём при изменении FontScale.
    private static double FontSizeBase =>
        (double)Application.Current!.FindResource("FontSizeBase")!;

    private static readonly FontWeight WeightBold   = (FontWeight)Application.Current!.FindResource("FontWeightBold")!;
    private static readonly FontWeight WeightNormal = (FontWeight)Application.Current!.FindResource("FontWeightNormal")!;
    // Кисти из общей палитры Colors.axaml (запасной hex — на случай отсутствия ресурса).
    private static readonly IBrush Secondary = Brush("BrushSecondaryText", "#8E8E93");
    private static readonly IBrush Primary   = Brush("BrushPrimaryText",   "#1C1C1E");
    private static readonly IBrush Red        = Brush("BrushRed",          "#FF3B30");

    private static IBrush Brush(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var r) && r is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallbackHex));

    private static readonly string[] MonthNames =
        ["Январь", "Февраль", "Март", "Апрель", "Май", "Июнь",
         "Июль",   "Август",  "Сентябрь", "Октябрь", "Ноябрь", "Декабрь"];

    private static readonly string[] DayNames = ["Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс"];

    public YearView()
    {
        InitializeComponent();
        DayPopupSearch.TextChanged += (_, _) => { if (!_popupResetting) _ = OnPopupSearchChanged(); };
    }

    // Поиск в попапе дня: провайдер — фоновый SQL LIKE по ВСЕМУ дню (инъекция контроллера,
    // у вида нет доступа к репозиториям): попап показывает лишь первые PopupMaxEvents
    // доставленных записей, локальный фильтр по ним на дне-гиганте бессмысленен.
    // null — локальный фильтр по снимку (на всякий случай).
    public Func<DateTime, string, Task<List<CalendarRecord>>>? DaySearchProvider { get; set; }

    // Список дня для попапа — ПО ТРЕБОВАНИЮ из БД (инъекция контроллера): Год события окна
    // не читает вовсе (числам достаточно карты счётчиков, а чтение всех событий года —
    // сотни тысяч строк — показывало оверлей «Загрузка…» при листании лет). Per-day
    // capped-чтение сиками — миллисекунды даже на дне-гиганте. null — снимок года.
    public Func<DateTime, Task<List<CalendarRecord>>>? DayEventsProvider { get; set; }

    private DateTime _popupDay;          // день открытого попапа
    private int      _popupSearchGen;    // дебаунс/отмена устаревших поисков
    private bool     _popupResetting;    // сброс текста при открытии не запускает поиск

    // Доставленные события года (ограничены чтением окна — GridDayCap на день) и карта
    // счётчиков дней (доезжает второй фазой; null — ещё едет/поиск). Раскладку по дням НЕ
    // храним: на сотнях тысяч событий словарь списков перестраивался при КАЖДОЙ перерисовке
    // (правки сетки перерисовывают Год на месте) — счётчики считаются одним проходом,
    // а список дня для попапа фильтруется по клику (см. ShowDayEvents).
    private List<CalendarRecord> _events = [];
    private IReadOnlyDictionary<DateOnly, int>? _dayTotals;

    public void Refresh(IEnumerable<CalendarRecord> events, int year,
                        IReadOnlyDictionary<DateOnly, int>? dayTotals = null)
    {
        _events    = events as List<CalendarRecord> ?? events.ToList();
        _dayTotals = dayTotals;

        // Счётчики на числа: событие учитывается в каждом дне своего диапазона; карта
        // счётчиков (по дню НАЧАЛА) дополняет недоставленное сверх капа чтения — день-гигант
        // получает честное число, хотя событий пришло только GridDayCap.
        var eventCounts = new Dictionary<DateTime, int>();
        var received    = new Dictionary<DateTime, int>();   // доставлено по дню СТАРТА
        foreach (var e in _events)
        {
            received[e.StartDt.Date] = received.GetValueOrDefault(e.StartDt.Date) + 1;
            foreach (var d in EachDay(e.StartDt.Date, e.EndDt.Date))
                eventCounts[d] = eventCounts.GetValueOrDefault(d) + 1;
        }
        if (dayTotals is not null)
            foreach (var (d, total) in dayTotals)
            {
                var key   = d.ToDateTime(TimeOnly.MinValue);
                var extra = total - received.GetValueOrDefault(key);
                if (extra > 0) eventCounts[key] = eventCounts.GetValueOrDefault(key) + extra;
            }

        MonthGrid.Children.Clear();

        for (var m = 1; m <= 12; m++)
        {
            var month = m;
            MonthGrid.Children.Add(BuildMonthCard(year, month, eventCounts));
        }
    }

    // Лимит попапа дня: контрол на каждое событие — день-гигант отдаёт сетке до GridDayCap
    // (2000) записей, столько строк попапу ни к чему (и это секунды построения). Скрытое
    // сверх лимита — честный «+N ещё» по карте счётчиков; смотреть весь день — «Расписание».
    private const int PopupMaxEvents = 100;

    // Открывает попап со списком событий дня, привязанный к ячейке числа.
    // Список дня грузится по требованию (DayEventsProvider, миллисекунды); без провайдера —
    // фильтром из доставленного снимка года.
    private async void ShowDayEvents(Control anchor, DateTime day)
    {
        // Переоткрываем, чтобы попап переместился к новому числу
        // (PlacementTarget применяется при открытии).
        if (DayPopup.IsOpen) DayPopup.IsOpen = false;

        _popupDay = day;
        var gen = ++_popupSearchGen;        // отменяет незавершённый поиск/загрузку прошлого дня
        _popupResetting = true;
        DayPopupSearch.Text = "";
        _popupResetting = false;

        DayPopupTitle.Text = day.ToString("d MMMM yyyy");
        if (DayEventsProvider is null)
            FillPopupList(DayEventsFromSnapshot(day), searchMode: false);
        else
            SetPopupHint("Загрузка…");

        DayPopup.PlacementTarget = anchor;
        DayPopup.IsOpen          = true;

        if (DayEventsProvider is null) return;
        await FillPopupFromProviderAsync(gen, day);
    }

    // Список дня из провайдера (по требованию); применяется, только если попап всё ещё
    // показывает этот день (не переключились/не начали искать).
    private async Task FillPopupFromProviderAsync(int gen, DateTime day)
    {
        List<CalendarRecord> events;
        try { events = await DayEventsProvider!(day); }
        catch { if (gen == _popupSearchGen && DayPopup.IsOpen) SetPopupHint("Нет записей"); return; }
        if (gen != _popupSearchGen || !DayPopup.IsOpen) return;
        FillPopupList(events.OrderBy(x => x.AllDay ? TimeSpan.Zero : x.StartDt.TimeOfDay).ToList(),
                      searchMode: false);
    }

    private void SetPopupHint(string text)
    {
        DayPopupList.Children.Clear();
        DayPopupList.Children.Add(new TextBlock
        {
            Text       = text,
            FontSize   = FontSizeBase,
            Foreground = Secondary,
        });
    }

    // События дня из доставленного снимка года, в порядке попапа.
    private List<CalendarRecord> DayEventsFromSnapshot(DateTime day) => _events
        .Where(e => e.StartDt.Date <= day && day <= e.EndDt.Date)
        .OrderBy(x => x.AllDay ? TimeSpan.Zero : x.StartDt.TimeOfDay)
        .ToList();

    // Дебаунс поиска в попапе: пустой запрос — снимок дня; иначе — фоновый SQL LIKE по
    // ВСЕМУ дню через DaySearchProvider (найдёт и то, что не доставлено капом чтения).
    private async Task OnPopupSearchChanged()
    {
        var gen = ++_popupSearchGen;
        await Task.Delay(250);
        if (gen != _popupSearchGen || !DayPopup.IsOpen) return;

        var q = DayPopupSearch.Text?.Trim() ?? "";
        if (q.Length == 0)
        {
            // Возврат из поиска: список дня заново (по требованию или из снимка года).
            if (DayEventsProvider is not null) await FillPopupFromProviderAsync(gen, _popupDay);
            else FillPopupList(DayEventsFromSnapshot(_popupDay), searchMode: false);
            return;
        }

        List<CalendarRecord> found;
        if (DaySearchProvider is not null)
        {
            try { found = await DaySearchProvider(_popupDay, q); }
            catch { return; }   // БД занята/недоступна — оставляем текущий список
            if (gen != _popupSearchGen || !DayPopup.IsOpen) return;
            found = found.OrderBy(x => x.AllDay ? TimeSpan.Zero : x.StartDt.TimeOfDay).ToList();
        }
        else
            found = DayEventsFromSnapshot(_popupDay)
                .Where(e => e.DisplayTitle.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();

        FillPopupList(found, searchMode: true);
    }

    // Наполняет список попапа (не больше PopupMaxEvents строк + «+N ещё»).
    // searchMode: результат поиска — «+N ещё» только от фактически найденного
    // (итог по всему дню неизвестен, провайдер лимитирован).
    private void FillPopupList(List<CalendarRecord> events, bool searchMode)
    {
        DayPopupList.Children.Clear();

        if (events.Count == 0)
        {
            DayPopupList.Children.Add(new TextBlock
            {
                Text       = searchMode ? "Ничего не найдено" : "Нет записей",
                FontSize   = FontSizeBase,
                Foreground = Secondary,
            });
            return;
        }

        foreach (var e in events.Take(PopupMaxEvents))
        {
            var time = e.AllDay ? "весь день" : e.StartDt.ToString("HH:mm");
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 8,
            };
            row.Children.Add(new Border
            {
                Width             = 4,
                CornerRadius      = new Avalonia.CornerRadius(2),
                Background         = new SolidColorBrush(Color.Parse(EventColors.Display(e.Color))),
                VerticalAlignment = VerticalAlignment.Stretch,
            });
            row.Children.Add(new StackPanel
            {
                Spacing  = 1,
                Children =
                {
                    new TextBlock
                    {
                        Text       = e.DisplayTitle,
                        FontSize   = FontSizeBase,
                        FontWeight = WeightBold,
                        Foreground = Primary,
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text       = time,
                        FontSize   = FontSizeBase,
                        Foreground = Secondary,
                    },
                },
            });
            DayPopupList.Children.Add(row);
        }

        // «+N ещё»: не показанные сверх лимита попапа + (вне поиска) недоставленные капом
        // чтения — день-гигант: показаны первые 100, всего — из карты счётчиков.
        var total = !searchMode && _dayTotals is not null &&
                    _dayTotals.TryGetValue(DateOnly.FromDateTime(_popupDay), out var t)
            ? Math.Max(t, events.Count)   // карта — по дню начала; перетёкшие мультидни добавляем
            : events.Count;
        var hidden = total - Math.Min(events.Count, PopupMaxEvents);
        if (hidden > 0)
            DayPopupList.Children.Add(new TextBlock
            {
                Text       = $"+{hidden} ещё",
                FontSize   = FontSizeBase,
                Foreground = Secondary,
            });
    }

    private static readonly (Color Bg, Color Text)[] EventSteps =
    [
        (Color.FromArgb(40, 255, 204,   0), Color.FromArgb(255, 140, 100,   0)),
        (Color.FromArgb(40, 255, 149,   0), Color.FromArgb(255, 160,  70,   0)),
        (Color.FromArgb(40, 255,  90, 140), Color.FromArgb(255, 180,  30,  80)),
        (Color.FromArgb(40, 255,  59,  48), Color.FromArgb(255, 160,  20,  10)),
    ];


    private const double Dch        = 24;
    private const double TitleH     = 32;
    private const double CardHeight = TitleH + 7 * Dch; // 32 + 168 = 200
    private const int    CellW      = 31;               // 220/7=31.43 → floor, чтобы колонки были целыми
    private const int    DayGridW   = 7 * CellW;        // 217

    private Control BuildMonthCard(int year, int month, Dictionary<DateTime, int> eventCounts)
    {
        var isTitleMonth = year == DateTime.Today.Year && month == DateTime.Today.Month;

        // Заголовок месяца: высота 30px (top=6, text=22, gap=2)
        var titleBtn = new TextBlock
        {
            Text                = MonthNames[month - 1],
            FontSize            = FontSizeBase,
            FontWeight          = WeightBold,
            Height              = TitleH,
            Padding             = new Avalonia.Thickness(6, 6, 0, 2),
            Foreground          = isTitleMonth ? Red : Primary,
        };

        // Один UniformGrid: 7 заголовков + 42 ячейки = 49 элементов, 7 строк × 7 колонок
        var dayGrid    = new UniformGrid { Columns = 7, Width = DayGridW, Height = 7 * Dch };
        var firstDay   = new DateTime(year, month, 1);
        var startOffset = ((int)firstDay.DayOfWeek + 6) % 7;

        // Строка заголовков дней недели
        for (var di = 0; di < DayNames.Length; di++)
            dayGrid.Children.Add(new TextBlock
            {
                Text              = DayNames[di],
                FontSize          = FontSizeBase,
                FontWeight        = WeightNormal,
                TextAlignment     = Avalonia.Media.TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground        = di >= 5 ? Red : Secondary,
            });

        // 42 ячейки с числами
        for (var i = 0; i < 42; i++)
        {
            var dc             = i % 7;
            var d              = firstDay.AddDays(i - startOffset);
            var isCurrentMonth = d.Month == month;
            var isToday        = d.Date == DateTime.Today;
            var isWeekend      = dc >= 5;
            var count          = isCurrentMonth ? eventCounts.GetValueOrDefault(d.Date) : 0;

            // Число всегда в одной позиции (центр ячейки 24×24) — выделение его не двигает.
            // Дни с событиями = цветной треугольник одинакового размера с числом по центру.
            var label = new TextBlock
            {
                Text                = d.Day.ToString(),
                FontSize            = FontSizeBase,
                FontWeight          = WeightNormal,
                TextAlignment       = Avalonia.Media.TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            };

            Control cell;

            if (isToday && isCurrentMonth)
            {
                label.Foreground = Avalonia.Media.Brushes.White;
                cell = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Width               = 22,
                    Height              = 22,
                    CornerRadius        = new Avalonia.CornerRadius(11),
                    Background          = Red,
                    Child               = label,
                };
            }
            else if (count >= 1 && isCurrentMonth)
            {
                var step = count == 1 ? 0 : count <= 5 ? 1 : count <= 8 ? 2 : 3;
                label.Foreground = new SolidColorBrush(EventSteps[step].Text);

                // Прямоугольник почти на всю ширину ячейки (зазор 1px по бокам),
                // высота 22 по центру. Ширина одинакова у всех ячеек → число по центру,
                // блоки стоят плотно друг к другу.
                cell = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Height              = 22,
                    Margin              = new Avalonia.Thickness(1, 0),
                    CornerRadius        = new Avalonia.CornerRadius(3),
                    Background           = new SolidColorBrush(EventSteps[step].Bg),
                    Child                = label,
                };
            }
            else
            {
                label.Foreground = !isCurrentMonth
                    ? new SolidColorBrush(Color.Parse("#E5E5EA"))
                    : isWeekend ? Secondary : Primary;
                cell = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment   = VerticalAlignment.Center,
                    Width               = 22,
                    Height              = 22,
                    Background          = Brushes.Transparent,
                    Child               = label,
                };
            }

            // Клик по числу текущего месяца → попап со списком событий дня,
            // привязанный к этой ячейке. Оборачиваем в прозрачный Border на всю
            // ячейку, чтобы кликабельной была вся площадь, а не только блок 22×22.
            if (isCurrentMonth)
            {
                var dayDate = d.Date;
                var hit = new Border
                {
                    Background = Brushes.Transparent,
                    Cursor     = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                    Child      = cell,
                };
                // Один клик — попап со списком событий дня
                hit.Tapped += (_, _) => ShowDayEvents(hit, dayDate);
                dayGrid.Children.Add(hit);
            }
            else
            {
                dayGrid.Children.Add(cell);
            }
        }

        var panel = new StackPanel
        {
            Width               = 220,
            Height              = CardHeight,
            Margin              = new Avalonia.Thickness(13),
            Spacing             = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment   = VerticalAlignment.Top,
            Background          = Brushes.Transparent,
        };
        panel.Children.Add(titleBtn);
        panel.Children.Add(dayGrid);

        panel.DoubleTapped += (_, _) => MonthSelected?.Invoke(this, new DateTime(year, month, 1));

        return panel;
    }

    private static IEnumerable<DateTime> EachDay(DateTime from, DateTime to)
    {
        for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            yield return d;
    }
}
