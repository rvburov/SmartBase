using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace SmartBase.Controls.Components;

/// <summary>
/// Геометрия строк виртуализированного списка с горизонтальной прокруткой: список значений
/// «экселевского» фильтра (колонка таблицы, история профиля, журнал логов), а также окна
/// «Отображение записей» и «Отображение свойств». Решает две задачи, которые сами собой не
/// решаются из-за виртуализации (вторая нужна только там, где справа от текста что-то есть).
///
/// 1. ПОСТОЯННАЯ ШИРИНА СТРОКИ. Экстент горизонтальной прокрутки виртуализирующая панель
///    считает по РЕАЛИЗОВАННЫМ строкам. Прокрутил вниз к коротким значениям — экстент
///    схлопнулся, полоса дернулась и пропала. Здесь один раз меряется самое широкое
///    значение и самый широкий счетчик, и обе ширины ставятся как MinWidth КАЖДОЙ строке:
///    все строки одинаковой ширины, экстент не зависит от того, что сейчас видно.
///
/// 2. ЗАКРЕПЛЕННЫЙ СЧЕТЧИК. Счетчик повторений живет в правой колонке строки и уезжал
///    вместе с текстом. <see cref="Pin"/> — сдвиг (TranslateTransform.X), который держит
///    его у правого края ВИДИМОЙ области при любой позиции прокрутки: тот же прием, что
///    закрепленная колонка таблицы. Сдвиг рендерный, в раскладке не участвует, поэтому
///    экстент от него не меняется. Фон у счетчика непрозрачный — текст уезжает ПОД него.
/// </summary>
public sealed class FilterValueMetrics : INotifyPropertyChanged
{
    // Мерить все значения нельзя — их бывают сотни тысяч. Сначала отбираем самые длинные
    // ПО СИМВОЛАМ (это дешево), потом честно меряем только их: шрифт пропорциональный, и
    // самая длинная строка не обязательно самая широкая.
    private const int Candidates = 12;

    // Запас к измеренной ширине. Промах даже на пиксель вернул бы дрожание экстента:
    // строка, которая шире своего MinWidth, снова растянула бы панель под себя.
    private const double Slack = 4;

    /// <summary>Просвет между числом и текстом, который оно закрывает. Такой же, как
    /// справа от числа (его дает правый отступ списка), — расстояние с обеих сторон
    /// одинаковое и не зависит от количества цифр.</summary>
    public const double Gap = 8;

    private double _textMinWidth;
    private double _countMinWidth;
    private double _pin;

    /// <summary>Ширина самого широкого значения — MinWidth текста строки.</summary>
    public double TextMinWidth { get => _textMinWidth; private set => Set(ref _textMinWidth, value); }

    /// <summary>Ширина МЕСТА под счетчик: самое длинное число плюс <see cref="Gap"/>.
    /// Место одинаковое во всех строках (иначе ширина строки, а с ней и экстент, зависела
    /// бы от количества цифр), а сама непрозрачная плашка обхватывает свое число.</summary>
    public double CountMinWidth { get => _countMinWidth; private set => Set(ref _countMinWidth, value); }

    /// <summary>Сдвиг счетчика, удерживающий его у правого края видимой области.</summary>
    public double Pin { get => _pin; private set => Set(ref _pin, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Пересчитать ширины по текущему (отфильтрованному) набору значений.</summary>
    public void Measure(IEnumerable<string> displays, IEnumerable<string> counts,
                        double fontSize, FontFamily? family = null)
    {
        Measure(displays, fontSize, family);
        CountMinWidth = Widest(counts, fontSize, family) + Gap;
    }

    /// <summary>Только ширина текста — для списков без счетчика (окна «Отображение
    /// записей» и «Отображение свойств»: там название последнее в строке и закреплять
    /// справа нечего, нужна лишь постоянная ширина строки).</summary>
    public void Measure(IEnumerable<string> displays, double fontSize, FontFamily? family = null) =>
        TextMinWidth = Widest(displays, fontSize, family);

    /// <summary>Следить за прокруткой списка, чтобы держать счетчик на месте.</summary>
    public void Track(ScrollViewer scroll)
    {
        void Update()
        {
            // Счетчик стоит у правого края СОДЕРЖИМОГО. Сдвигаем его влево ровно на
            // непрокрученный остаток — тогда он всегда у правого края видимой области:
            // в начале прокрутки остаток максимален, в конце равен нулю.
            var rest = Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width);
            Pin = scroll.Offset.X - rest;
        }

        scroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.OffsetProperty ||
                e.Property == ScrollViewer.ExtentProperty ||
                e.Property == ScrollViewer.ViewportProperty)
                Update();
        };
        Update();
    }

    private static double Widest(IEnumerable<string> values, double fontSize, FontFamily? family)
    {
        var top = new List<string>(Candidates + 1);
        foreach (var v in values)
        {
            if (string.IsNullOrEmpty(v)) continue;
            if (top.Count < Candidates)
            {
                top.Add(v);
                top.Sort(LongestFirst);
                continue;
            }
            if (v.Length <= top[^1].Length) continue;
            top[^1] = v;
            top.Sort(LongestFirst);
        }

        var max = 0.0;
        foreach (var s in top)
        {
            var probe = new TextBlock { Text = s, FontSize = fontSize };
            if (family is not null) probe.FontFamily = family;
            probe.Measure(Size.Infinity);
            max = Math.Max(max, probe.DesiredSize.Width);
        }
        return max > 0 ? Math.Ceiling(max) + Slack : 0;
    }

    private static int LongestFirst(string a, string b) => b.Length.CompareTo(a.Length);

    private void Set(ref double field, double value, [CallerMemberName] string? name = null)
    {
        if (field.Equals(value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
    }
}
