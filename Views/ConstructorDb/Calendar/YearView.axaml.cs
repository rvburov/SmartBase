using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using SmartBase.Core.Calendar;
using SmartBase.Core.Models;
using SmartBase.Utils;
using System.Windows.Input;

namespace SmartBase.Views.ConstructorDb.Calendar;

public partial class YearView : UserControl
{
    public event EventHandler<DateTime>? MonthSelected;

    // Размеры/веса шрифта — из токенов Typography.axaml (резолв ресурса в рантайме).
    // Свежий размер из Application.Resources — меняется живьем при изменении FontScale.
    private static double FontSizeBase =>
        (double)Application.Current!.FindResource("FontSizeBase")!;

    private static readonly FontWeight WeightBold   = (FontWeight)Application.Current!.FindResource("FontWeightBold")!;
    // Кисти из общей палитры Colors.axaml (запасной hex — на случай отсутствия ресурса).
    private static readonly IBrush Secondary = Brush("BrushSecondaryText", "#8E8E93");
    private static readonly IBrush Primary   = Brush("BrushPrimaryText",   "#1C1C1E");

    private static IBrush Brush(string key, string fallbackHex) =>
        Application.Current!.TryFindResource(key, out var r) && r is IBrush b
            ? b
            : new SolidColorBrush(Color.Parse(fallbackHex));

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
    // не читает вовсе (числам достаточно карты счетчиков, а чтение всех событий года —
    // сотни тысяч строк — показывало оверлей «Загрузка…» при листании лет). Per-day
    // capped-чтение сиками — миллисекунды даже на дне-гиганте. null — снимок года.
    public Func<DateTime, Task<List<CalendarRecord>>>? DayEventsProvider { get; set; }

    private DateTime _popupDay;          // день открытого попапа
    private int      _popupSearchGen;    // дебаунс/отмена устаревших поисков
    private bool     _popupResetting;    // сброс текста при открытии не запускает поиск

    // Доставленные события года (ограничены чтением окна — GridDayCap на день) и карта
    // счетчиков дней (доезжает второй фазой; null — еще едет/поиск). Раскладку по дням НЕ
    // храним: на сотнях тысяч событий словарь списков перестраивался при КАЖДОЙ перерисовке
    // (правки сетки перерисовывают Год на месте) — счетчики считаются одним проходом,
    // а список дня для попапа фильтруется по клику (см. ShowDayEvents).
    private List<CalendarRecord> _events = [];
    private IReadOnlyDictionary<DateOnly, int>? _dayTotals;

    public void Refresh(IEnumerable<CalendarRecord> events, int year,
                        IReadOnlyDictionary<DateOnly, int>? dayTotals = null)
    {
        _events    = events as List<CalendarRecord> ?? events.ToList();
        _dayTotals = dayTotals;

        // Счетчики на числа: событие учитывается в каждом дне своего диапазона; карта
        // счетчиков (по дню НАЧАЛА) дополняет недоставленное сверх капа чтения — день-гигант
        // получает честное число, хотя событий пришло только GridDayCap. Контроллер доставляет
        // сюда МНОГОДНЕВНЫЕ события года (их мало) — как раз потому, что карта знает только
        // день начала; однодневные приходят ею. Диапазон обрезается показанным годом:
        // событие на три года иначе крутило бы тысячу дней, из которых нужны 365.
        var yearStart   = new DateTime(year, 1, 1);
        var yearEnd     = new DateTime(year, 12, 31);
        var fiveDay     = CalendarSettings.FiveDayWeek;
        var eventCounts = new Dictionary<DateTime, int>();
        var received    = new Dictionary<DateTime, int>();   // доставлено по дню СТАРТА
        foreach (var e in _events)
        {
            received[e.StartDt.Date] = received.GetValueOrDefault(e.StartDt.Date) + 1;
            var from = e.StartDt.Date < yearStart ? yearStart : e.StartDt.Date;
            var to   = e.EndDt.Date   > yearEnd   ? yearEnd   : e.EndDt.Date;
            foreach (var d in EachDay(from, to))
            {
                // 5-дневка: выходные не подсвечиваются совсем — ни диапазонами
                // многодневных, ни однодневными записями выходного дня (сами записи
                // остаются доступны кликом по числу — попап дня их показывает).
                if (fiveDay && CalendarMath.IsWeekend(d)) continue;
                eventCounts[d] = eventCounts.GetValueOrDefault(d) + 1;
            }
        }
        if (dayTotals is not null)
            foreach (var (d, total) in dayTotals)
            {
                var key = d.ToDateTime(TimeOnly.MinValue);
                if (fiveDay && CalendarMath.IsWeekend(key)) continue;
                var extra = total - received.GetValueOrDefault(key);
                if (extra > 0) eventCounts[key] = eventCounts.GetValueOrDefault(key) + extra;
            }

        // Тот же год — только новые числа (вторая фаза, точечная правка): месяцы не
        // перестраиваются. Другой год — двенадцать новых месяцев.
        if (MonthGrid.Children.Count == 12 && MonthGrid.Children[0] is YearMonthCard first && first.Year == year)
        {
            foreach (var card in MonthGrid.Children.OfType<YearMonthCard>()) card.SetCounts(eventCounts);
            return;
        }
        MonthGrid.Children.Clear();
        for (var m = 1; m <= 12; m++)
        {
            var month = m;
            var card  = new YearMonthCard(year, month, eventCounts);
            // Нажатие по числу — список событий дня под ячейкой; двойное — переход в месяц.
            card.DayTapped   += (c, cell, day) => ShowDayEvents(c, cell, day);
            card.Tapped      += (_, e) => card.OnTapped(e.GetPosition(card));
            card.DoubleTapped += (_, _) => MonthSelected?.Invoke(this, new DateTime(year, month, 1));
            MonthGrid.Children.Add(card);
        }
    }

    // Лимит попапа дня: контрол на каждое событие — день-гигант отдает сетке до GridDayCap
    // (2000) записей, столько строк попапу ни к чему (и это секунды построения). Скрытое
    // сверх лимита — честный «+N еще» по карте счетчиков; смотреть весь день — «Расписание».
    private const int PopupMaxEvents = 100;

    // Открывает попап со списком событий дня, привязанный к ячейке числа.
    // Список дня грузится по требованию (DayEventsProvider, миллисекунды); без провайдера —
    // фильтром из доставленного снимка года.
    private async void ShowDayEvents(Control anchor, Rect cell, DateTime day)
    {
        // Переоткрываем, чтобы попап переместился к новому числу
        // (PlacementTarget применяется при открытии).
        if (DayPopup.IsOpen) DayPopup.IsOpen = false;

        _popupDay = day;
        var gen = ++_popupSearchGen;        // отменяет незавершенный поиск/загрузку прошлого дня
        _popupResetting = true;
        DayPopupSearch.Text = "";
        _popupResetting = false;

        DayPopupTitle.Text = day.ToString("d MMMM yyyy");
        if (DayEventsProvider is null)
            FillPopupList(DayEventsFromSnapshot(day), searchMode: false);
        else
            SetPopupHint("Загрузка…");

        DayPopup.PlacementTarget = anchor;
        DayPopup.PlacementRect   = cell;
        DayPopup.IsOpen          = true;

        if (DayEventsProvider is null) return;
        await FillPopupFromProviderAsync(gen, day);
    }

    // Список дня из провайдера (по требованию); применяется, только если попап все еще
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
    // ВСЕМУ дню через DaySearchProvider (найдет и то, что не доставлено капом чтения).
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
            // Ищем по ПОЛНОМУ названию (в показе от него остается первая строка, см.
            // TitleText) плюс по DisplayTitle — иначе безымянные события перестали бы
            // находиться по «Без названия».
            found = DayEventsFromSnapshot(_popupDay)
                .Where(e => e.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                            e.DisplayTitle.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();

        FillPopupList(found, searchMode: true);
    }

    // Наполняет список попапа (не больше PopupMaxEvents строк + «+N еще»).
    // searchMode: результат поиска — «+N еще» только от фактически найденного
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
                    // Без переноса: ширина попапа фиксированная, длинное название
                    // дочитывается горизонтальной полосой карточки (как значения в
                    // фильтре колонки), а не растит строку на пол-попапа.
                    new TextBlock
                    {
                        Text       = e.DisplayTitle,
                        FontSize   = FontSizeBase,
                        FontWeight = WeightBold,
                        Foreground = Primary,
                        TextWrapping = TextWrapping.NoWrap,
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

        // «+N еще»: не показанные сверх лимита попапа + (вне поиска) недоставленные капом
        // чтения — день-гигант: показаны первые 100, всего — из карты счетчиков.
        var total = !searchMode && _dayTotals is not null &&
                    _dayTotals.TryGetValue(DateOnly.FromDateTime(_popupDay), out var t)
            ? Math.Max(t, events.Count)   // карта — по дню начала; перетекшие мультидни добавляем
            : events.Count;
        var hidden = total - Math.Min(events.Count, PopupMaxEvents);
        if (hidden > 0)
            DayPopupList.Children.Add(new TextBlock
            {
                Text       = $"+{hidden} еще",
                FontSize   = FontSizeBase,
                Foreground = Secondary,
            });
    }

    private static IEnumerable<DateTime> EachDay(DateTime from, DateTime to)
    {
        for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            yield return d;
    }
}
