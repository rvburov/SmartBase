using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SmartBase.Core;

namespace SmartBase.Controls.Components;

public static partial class BackgroundJobsPanel
{
    /// <summary>Состояние одного открытия: очередь старой панели не затрагивает новую.</summary>
    private sealed class PanelState
    {
        private readonly Visual _owner;
        private readonly StackPanel _host;
        private readonly Dictionary<string, JobRow> _rows = new(StringComparer.Ordinal);
        private readonly TextBlock _heading = new() { Text = "Фоновые работы" };
        private readonly TextBlock _note = new()
        {
            Text = "Работы идут, пока программа запущена.", TextWrapping = TextWrapping.Wrap,
        };
        private readonly Border _header;
        private readonly Border _footer;
        private int _queued;
        private int _detached;

        internal PanelState(Visual owner, StackPanel host)
        {
            _owner = owner;
            _host = host;
            _header = new Border
            {
                Padding = new Thickness(12, 10, 12, 8), BorderThickness = new Thickness(0, 0, 0, 1),
                Child = _heading,
            };
            _footer = new Border { Padding = new Thickness(12, 8), Child = _note };
            _host.Children.Add(_header);
            _host.Children.Add(_footer);
        }

        internal void Detach()
        {
            Interlocked.Exchange(ref _detached, 1);
            BackgroundJobs.Changed -= RequestRefresh;
        }

        internal void RequestRefresh()
        {
            // Идущий callback может иметь ровно одно продолжение: отчет, пришедший после
            // снимка, не потеряется, а тысяча отчетов не создаст тысячу перестроений.
            if (Volatile.Read(ref _detached) != 0 || Interlocked.Exchange(ref _queued, 1) != 0) return;
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _queued, 0);
                if (Volatile.Read(ref _detached) != 0 || !ReferenceEquals(_state, this)
                    || _popup is not { IsOpen: true }) return;
                var jobs = BackgroundJobs.All;
                if (jobs.Count == 0) { Close(); return; }
                Render(jobs);
            }, DispatcherPriority.Background);
        }

        internal void Render(IReadOnlyList<BackgroundJob> jobs)
        {
            var style = new RowStyle(
                Res<IBrush>(_owner, "BrushPrimaryText", Brushes.Black),
                Res<IBrush>(_owner, "BrushSecondaryText", Brushes.Gray),
                Res<IBrush>(_owner, "BrushSeparator", Brushes.LightGray),
                Res<IBrush>(_owner, "BrushAccent", Brushes.DodgerBlue),
                Res<IBrush>(_owner, "BrushRed", Brushes.Red),
                Res<IBrush>(_owner, "BrushSecondaryBg", Brushes.WhiteSmoke),
                Res(_owner, "FontWeightBold", FontWeight.SemiBold),
                Res(_owner, "FontSizeSmall", 10.0));
            _header.BorderBrush = style.Separator;
            _heading.FontWeight = style.Bold;
            _heading.Foreground = style.Primary;
            _footer.Background = style.SecondaryBackground;
            _note.FontSize = style.Small;
            _note.Foreground = style.Secondary;

            var ids = jobs.Select(job => job.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _rows.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                _host.Children.Remove(_rows[id].Container);
                _rows.Remove(id);
            }
            for (var i = 0; i < jobs.Count; i++)
            {
                var job = jobs[i];
                if (!_rows.TryGetValue(job.Id, out var row))
                    _rows.Add(job.Id, row = new JobRow());
                var position = i + 1; // Служебная шапка остается первой, подвал последним.
                if (!ReferenceEquals(_host.Children[position], row.Container))
                {
                    _host.Children.Remove(row.Container);
                    _host.Children.Insert(position, row.Container);
                }
                row.Update(job, style, i == jobs.Count - 1);
            }
        }
    }

    private sealed record RowStyle(IBrush Primary, IBrush Secondary, IBrush Separator, IBrush Accent,
        IBrush Red, IBrush SecondaryBackground, FontWeight Bold, double Small);

    /// <summary>Ход меняет готовые контролы; имя, ссылка и полоса сохраняют свой экземпляр.</summary>
    private sealed class JobRow
    {
        private static readonly Cursor LinkCursor = new(StandardCursorType.Hand);
        internal Border Container { get; }
        private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _detail = new();
        private readonly TextBlock _link = new()
        {
            Cursor = LinkCursor, VerticalAlignment = VerticalAlignment.Center,
        };
        private readonly Border _icon = new();
        private readonly Grid _label = new() { ColumnDefinitions = new ColumnDefinitions("Auto,8,*") };
        private readonly Grid _bar = new()
        {
            Height = 4, Margin = new Thickness(0, 7, 0, 0),
            ColumnDefinitions = new ColumnDefinitions("*,*"),
        };
        private readonly Border _filled = new() { CornerRadius = new CornerRadius(2) };
        private readonly Border _rest = new() { CornerRadius = new CornerRadius(2) };
        private BackgroundJob? _job;
        private string? _iconKey;
        private IBrush? _iconBrush;
        private double _iconSize;
        private double _iconStroke;

        internal JobRow()
        {
            _label.Children.Add(_icon);
            Grid.SetColumn(_name, 2);
            _label.Children.Add(_name);
            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto") };
            top.Children.Add(_label);
            Grid.SetColumn(_link, 2);
            top.Children.Add(_link);
            _link.Tapped += (_, e) =>
            {
                e.Handled = true;
                var current = _job;
                if (current is null) return;
                if (current.Failed) BackgroundJobs.Finish(current.Id);
                else current.Stop?.Invoke();
            };
            _bar.Children.Add(_filled);
            Grid.SetColumn(_rest, 1);
            _bar.Children.Add(_rest);
            var rows = new StackPanel { Spacing = 2 };
            rows.Children.Add(top);
            rows.Children.Add(_detail);
            rows.Children.Add(_bar);
            Container = new Border { Padding = new Thickness(12, 10), Child = rows };
        }

        internal void Update(BackgroundJob job, RowStyle style, bool last)
        {
            _job = job;
            _name.Text = job.Title;
            _name.FontWeight = style.Bold;
            _name.Foreground = job.Failed ? style.Red : style.Primary;
            var iconBrush = job.Failed ? style.Red : style.Secondary;
            // Размер общей иконки следует за ресурсами масштаба шрифта. На каждом проценте
            // ее создавать заново не нужно; смена темы/масштаба обновит ее при следующем отчете.
            var iconSize = Widgets.SettingRows.Metric("IconSizeMedium", 16.0);
            var iconStroke = Widgets.SettingRows.Metric("IconStrokeMedium", 1.6);
            if (_iconKey != job.Icon || !ReferenceEquals(_iconBrush, iconBrush)
                || _iconSize != iconSize || _iconStroke != iconStroke)
            {
                _icon.Child = job.Icon is { } key ? Widgets.SettingRows.Icon(key, iconBrush) : null;
                _iconKey = job.Icon;
                _iconBrush = iconBrush;
                _iconSize = iconSize;
                _iconStroke = iconStroke;
            }
            _icon.IsVisible = job.Icon is not null;
            _label.ColumnDefinitions[1].Width = new GridLength(job.Icon is null ? 0 : 8);
            _link.Text = job.Failed ? "Убрать" : job.Stop is not null ? "Остановить" : null;
            _link.IsVisible = job.Failed || job.Stop is not null;
            _link.Foreground = style.Accent;
            _link.FontWeight = style.Bold;
            _detail.Text = job.Detail;
            _detail.FontSize = style.Small;
            _detail.Foreground = style.Secondary;
            _detail.TextTrimming = job.Failed ? TextTrimming.None : TextTrimming.CharacterEllipsis;
            _detail.TextWrapping = job.Failed ? TextWrapping.Wrap : TextWrapping.NoWrap;
            _bar.IsVisible = job.Total > 0;
            if (job.Total > 0)
            {
                var done = Math.Clamp(job.Done, 0, job.Total);
                _bar.ColumnDefinitions[0].Width = new GridLength(done, GridUnitType.Star);
                _bar.ColumnDefinitions[1].Width = new GridLength(job.Total - done, GridUnitType.Star);
            }
            _filled.Background = job.Failed ? style.Red : style.Accent;
            _rest.Background = style.SecondaryBackground;
            Container.BorderBrush = style.Separator;
            Container.BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1);
        }
    }
}
