using System.ComponentModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SmartBase.Controls.Widgets;
using SmartBase.Core.Models;

namespace SmartBase.Controls.Dialogs;

/// <summary>Ручная проверка связей: отчет без записи, выбор подтвержденных исправлений,
/// предварительный просмотр и отдельное подтверждение перед резервной копией и записью.</summary>
public partial class RelationIntegrityDialog : Window
{
    private readonly Func<IProgress<RelationIntegrityProgress>, CancellationToken, Task<RelationIntegrityReport>> _scan;
    private readonly Func<RelationIntegrityReport, IReadOnlyCollection<string>, CancellationToken, Task<RelationIntegrityRepairPlan>> _preview;
    private readonly Func<RelationIntegrityRepairPlan, IProgress<string>, CancellationToken, Task<RelationIntegrityRepairResult>> _apply;
    private readonly List<FindingVm> _all = [];
    private readonly AvaloniaList<FindingVm> _shown = [];
    private RelationIntegrityReport? _report;
    private bool _busy;
    private bool _confirmedRepair;
    private string? _backupPath;
    private CancellationTokenSource? _operationCancellation;
    private int _permissionGeneration;

    public RelationIntegrityDialog(string databaseName,
        Func<IProgress<RelationIntegrityProgress>, CancellationToken, Task<RelationIntegrityReport>> scan,
        Func<RelationIntegrityReport, IReadOnlyCollection<string>, CancellationToken, Task<RelationIntegrityRepairPlan>> preview,
        Func<RelationIntegrityRepairPlan, IProgress<string>, CancellationToken, Task<RelationIntegrityRepairResult>> apply)
    {
        InitializeComponent();
        _scan = scan;
        _preview = preview;
        _apply = apply;
        DatabaseText.Text = databaseName;
        FindingsList.ItemTemplate = BuildFindingTemplate();
        FindingsList.ItemsSource = _shown;
        SearchBox.TextChanged += (_, _) => FilterRows();
        CategoryCombo.SelectionChanged += (_, _) => FilterRows();
        ScanBtn.Click += async (_, _) => await ScanAsync();
        RepairBtn.Click += async (_, _) => await RepairAsync();
        CloseBtn.Click += (_, _) => Close(_confirmedRepair);
        BackupLink.Click += (_, _) =>
        {
            if (_backupPath is { } path && Path.GetDirectoryName(path) is { } directory)
                FileEditor.OpenWithSystem(directory);
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !_busy) Close(_confirmedRepair); };
        Opened += async (_, _) => await ScanAsync();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_busy) e.Cancel = true;
        base.OnClosing(e);
    }

    /// <summary>Смена прав делает снимок отчета и подтверждение непригодными. Поздний ответ
    /// фонового чтения не может вернуть в окно подписи из прежнего снимка доступа.</summary>
    public void InvalidatePermissions()
    {
        Dispatcher.UIThread.VerifyAccess();
        _permissionGeneration++;
        _operationCancellation?.Cancel();
        _report = null;
        _all.Clear();
        _shown.Clear();
        CategoryCombo.ItemsSource = null;
        _backupPath = null;
        BackupLink.IsVisible = false;
        BackupLink.Content = null;
        BackupPathText.IsVisible = false;
        BackupPathText.Text = null;
        ResultText.IsVisible = false;
        ResultText.Text = null;
        CoverageText.IsVisible = false;
        SummaryText.Text = "Права доступа изменились. Запустите проверку заново.";
        NoticeBanner.Kind = NoticeBanner.BannerKind.Info;
        NoticeBanner.Show("Прежний отчет и выбранные исправления сброшены после изменения прав доступа.");
        foreach (var progress in OwnedWindows.OfType<ProgressDialog>())
        {
            progress.SetSaving("Права доступа изменились. Остановка…");
            progress.SetInfo("");
        }
        foreach (var confirmation in OwnedWindows.OfType<ConfirmDialog>().ToArray()) confirmation.Close(false);
        RefreshActions();
    }

    private async Task ScanAsync(bool preserveResult = false)
    {
        if (_busy) return;
        var generation = _permissionGeneration;
        NoticeBanner.Hide();
        _report = null;
        _all.Clear();
        _shown.Clear();
        if (!preserveResult) ResultText.IsVisible = false;
        SummaryText.Text = "Проверка связей…";
        CoverageText.IsVisible = false;
        try
        {
            var report = await RunWorkAsync("Проверка связей", async (dialog, ct) =>
            {
                var progress = new Progress<RelationIntegrityProgress>(p =>
                {
                    if (generation == _permissionGeneration) dialog.SetInfo(
                        $"Проверено связей: {p.ScannedEdges:N0}; свойств: {p.ScannedFields:N0}; замечаний: {p.Issues:N0}.");
                });
                return await _scan(progress, ct);
            });
            if (generation != _permissionGeneration) return;
            _report = report;
            SummaryText.Text = $"Проверено связей: {report.ScannedEdges:N0}; свойств: {report.ScannedFields:N0}.\n" +
                $"Подтвержденные ошибки: {report.ConfirmedIssues:N0}; " +
                $"не удалось проверить: {report.UnverifiedIssues:N0}; информационные замечания: {report.InformationalIssues:N0}.";
            CoverageText.Text = report.IsTruncated
                ? $"Показаны первые {report.Issues.Count:N0} замечаний. Исправления выбираются только из показанного списка."
                : "Недоступные цели требуют повторной проверки. Информационные замечания не означают повреждение данных.";
            CoverageText.IsVisible = true;
            foreach (var issue in report.Issues) _all.Add(new FindingVm(issue,
                report.Status == RelationIntegrityStatus.Completed && issue.IsConfirmed && !issue.IsInformational && issue.CanRepair, RefreshActions));
            CategoryCombo.ItemsSource = new[] { new Category(null, $"Все замечания ({report.Counts.Values.Sum():N0})") }
                .Concat(report.Counts.OrderBy(p => KindLabel(p.Key)).Select(p => new Category(p.Key, $"{KindLabel(p.Key)} ({p.Value:N0})"))).ToList();
            CategoryCombo.SelectedIndex = 0;
            FilterRows();
            if (report.Status != RelationIntegrityStatus.Completed)
            {
                NoticeBanner.Kind = report.Status == RelationIntegrityStatus.Cancelled
                    ? NoticeBanner.BannerKind.Info : NoticeBanner.BannerKind.Error;
                NoticeBanner.Show(report.Message);
            }
            else if (report.Counts.Count == 0) SummaryText.Text += "\nЗамечаний не найдено.";
        }
        catch (OperationCanceledException)
        {
            if (generation != _permissionGeneration) return;
            SummaryText.Text = "Проверка отменена. Данные не изменены.";
        }
        catch (Exception ex)
        {
            if (generation != _permissionGeneration) return;
            ShowFailure(ex, "Проверка не завершена. Повторите ее после восстановления доступа к базе.");
            SummaryText.Text = "Проверка не завершена.";
        }
        RefreshActions();
    }

    private async Task RepairAsync()
    {
        if (_busy || _report is not { Status: RelationIntegrityStatus.Completed } report) return;
        var generation = _permissionGeneration;
        var selected = _all.Where(r => r.Selected && r.CanRepair).Select(r => r.Issue.Key).ToArray();
        if (selected.Length == 0) return;
        NoticeBanner.Hide();
        try
        {
            var plan = await RunWorkAsync("Предварительный просмотр исправлений",
                (_, ct) => _preview(report, selected, ct));
            if (generation != _permissionGeneration) return;
            if (!plan.CanApply)
            {
                NoticeBanner.Kind = NoticeBanner.BannerKind.Info;
                NoticeBanner.Show(plan.Message);
                if (plan.Status != RelationIntegrityStatus.Completed) InvalidateReport();
                return;
            }
            if (plan.Issues.Any(i => !i.IsConfirmed || i.IsInformational || !i.CanRepair || !selected.Contains(i.Key)))
            {
                InvalidateReport();
                NoticeBanner.Show("План исправлений не соответствует выбранным замечаниям. Запустите проверку заново.");
                return;
            }
            var examples = string.Join("\n", plan.Issues.Take(3).Select(i => "• " + Limited(Location(i), 70) + ": " + Limited(i.Description, 90)));
            var confirm = new ConfirmDialog(
                $"Выбрано замечаний: {plan.Issues.Count:N0}. Удалить подтвержденные ошибочные связи: {plan.EdgesToRemove:N0}.\n\n" +
                $"Примеры выбранных замечаний (первые {Math.Min(3, plan.Issues.Count)}):\n{examples}\n\n" +
                "Перед изменением будет создана и проверена резервная копия базы. Исправления применятся одним пакетом.",
                "Исправить");
            if (await confirm.ShowDialog<bool>(this) != true) return;
            if (generation != _permissionGeneration) return;
            var result = await RunWorkAsync("Резервная копия и исправление связей", async (dialog, ct) =>
            {
                var progress = new Progress<string>(message =>
                { if (generation == _permissionGeneration) dialog.SetSaving(message); });
                return await _apply(plan, progress, ct);
            });
            if (generation != _permissionGeneration) return;
            if (result.Backup is { } backup) ShowBackup(backup.ArtifactPath);
            ResultText.Text = result.Applied
                ? $"Исправление завершено. Удалено ошибочных связей: {result.RemovedEdges:N0}."
                : result.Message;
            ResultText.IsVisible = true;
            InvalidateReport();
            if (result.Applied)
            {
                _confirmedRepair = true;
                await ScanAsync(preserveResult: true);
            }
            else
            {
                NoticeBanner.Kind = result.Status == RelationIntegrityStatus.Cancelled
                    ? NoticeBanner.BannerKind.Info : NoticeBanner.BannerKind.Error;
                NoticeBanner.Show(result.Message);
            }
        }
        catch (OperationCanceledException)
        {
            if (generation != _permissionGeneration) return;
            NoticeBanner.Kind = NoticeBanner.BannerKind.Info;
            NoticeBanner.Show("Исправление отменено. Запустите проверку перед следующей попыткой.");
            InvalidateReport();
        }
        catch (Exception ex)
        {
            if (generation != _permissionGeneration) return;
            ShowFailure(ex, "Исправление не выполнено. Запустите проверку заново.");
            InvalidateReport();
        }
        RefreshActions();
    }

    private async Task<T> RunWorkAsync<T>(string title, Func<ProgressDialog, CancellationToken, Task<T>> work)
    {
        _busy = true;
        RefreshActions();
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        var dialog = new ProgressDialog { Title = title };
        dialog.SetSaving(title + "…");
        dialog.EnableCancel(cancellation.Cancel);
        var closed = dialog.ShowDialog(this);
        try { return await work(dialog, cancellation.Token); }
        finally
        {
            dialog.Done();
            await closed;
            _operationCancellation = null;
            _busy = false;
            RefreshActions();
        }
    }

    private void InvalidateReport()
    {
        _report = null;
        foreach (var row in _all) row.Disable();
        RefreshActions();
    }

    private void ShowBackup(string path)
    {
        _backupPath = path;
        BackupLink.Content = "Резервная копия: " + Path.GetFileName(path);
        BackupLink.IsVisible = true;
        BackupPathText.Text = Path.GetDirectoryName(path);
        BackupPathText.IsVisible = true;
    }

    private void RefreshActions()
    {
        ScanBtn.IsEnabled = !_busy;
        CloseBtn.IsEnabled = !_busy;
        FindingsList.IsEnabled = !_busy;
        SearchBox.IsEnabled = !_busy;
        CategoryCombo.IsEnabled = !_busy;
        RepairBtn.IsEnabled = !_busy && _report?.Status == RelationIntegrityStatus.Completed
            && _all.Any(r => r.Selected && r.CanRepair);
    }

    private void FilterRows()
    {
        var text = SearchBox.Text?.Trim() ?? "";
        var category = (CategoryCombo.SelectedItem as Category)?.Kind;
        _shown.Clear();
        _shown.AddRange(_all.Where(r => (!category.HasValue || r.Issue.Kind == category)
            && (text.Length == 0 || (r.Title + " " + r.Detail).Contains(text, StringComparison.CurrentCultureIgnoreCase))));
    }

    private void ShowFailure(Exception ex, string message)
    {
        Serilog.Log.Warning("Действие проверки связей не завершено: {ErrorType}", ex.GetType().Name);
        NoticeBanner.Kind = NoticeBanner.BannerKind.Error;
        NoticeBanner.Show(message);
    }

    private static string Limited(string value, int limit = 400) => value.Length <= limit ? value : value[..limit] + "…";
    private static string Location(RelationIntegrityIssue issue) => string.Join(" / ",
        new[] { issue.CollectionName, issue.FieldName, issue.RecordTitle }.Where(s => s.Length > 0).Select(s => Limited(s)));

    private IDataTemplate BuildFindingTemplate() => new FuncDataTemplate<FindingVm>((_, _) =>
    {
        var box = new CheckBox { Margin = new Thickness(0, 2, 8, 0), VerticalAlignment = VerticalAlignment.Top };
        box.Bind(IsEnabledProperty, new Binding(nameof(FindingVm.CanRepair)));
        box.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(FindingVm.Selected)) { Mode = BindingMode.TwoWay });
        var title = new TextBlock { TextWrapping = TextWrapping.Wrap };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(FindingVm.Title)));
        var detail = new TextBlock { TextWrapping = TextWrapping.Wrap,
            Foreground = (IBrush)this.FindResource("BrushSecondaryText")! };
        detail.Bind(TextBlock.TextProperty, new Binding(nameof(FindingVm.Detail)));
        var labels = new StackPanel { Spacing = 4 };
        labels.Children.Add(title);
        labels.Children.Add(detail);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(4, 6) };
        Grid.SetColumn(box, 0);
        Grid.SetColumn(labels, 1);
        grid.Children.Add(box);
        grid.Children.Add(labels);
        return grid;
    });

    private sealed record Category(RelationIntegrityIssueKind? Kind, string Label)
    { public override string ToString() => Label; }

    private sealed class FindingVm(RelationIntegrityIssue issue, bool canRepair, Action selectionChanged) : INotifyPropertyChanged
    {
        private bool _selected;
        public RelationIntegrityIssue Issue { get; } = issue;
        public bool CanRepair { get; private set; } = canRepair;
        public string Title => KindLabel(Issue.Kind) + " · " + (Issue.IsInformational ? "сведения" : Issue.IsConfirmed
            ? CanRepair ? "можно исправить" : "подтвержденное замечание" : "не проверено");
        public string Detail => string.Join("\n", new[] { Location(Issue), Limited(Issue.Description) }.Where(v => v.Length > 0));
        public bool Selected
        {
            get => _selected;
            set
            {
                _selected = value && CanRepair;
                PropertyChanged?.Invoke(this, new(nameof(Selected)));
                selectionChanged();
            }
        }
        public void Disable()
        {
            CanRepair = false;
            _selected = false;
            PropertyChanged?.Invoke(this, new(nameof(CanRepair)));
            PropertyChanged?.Invoke(this, new(nameof(Selected)));
            PropertyChanged?.Invoke(this, new(nameof(Title)));
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static string KindLabel(RelationIntegrityIssueKind kind) => kind switch
    {
        RelationIntegrityIssueKind.MissingSourceRecord => "Удаленная исходная запись",
        RelationIntegrityIssueKind.MissingSourceField => "Удаленное свойство связи",
        RelationIntegrityIssueKind.MissingSourceCollection => "Удаленная исходная коллекция",
        RelationIntegrityIssueKind.InvalidSourceFieldType => "Измененный тип свойства",
        RelationIntegrityIssueKind.SourceCollectionMismatch => "Несовпадение исходной коллекции",
        RelationIntegrityIssueKind.MissingTargetRecord => "Удаленная целевая запись",
        RelationIntegrityIssueKind.MissingTargetCollection => "Удаленная целевая коллекция",
        RelationIntegrityIssueKind.TargetUnavailable => "Целевая база недоступна",
        RelationIntegrityIssueKind.TargetAccessDenied => "Нет доступа к цели",
        RelationIntegrityIssueKind.MissingConfiguration => "Отсутствует настройка связи",
        RelationIntegrityIssueKind.InvalidConfiguration => "Неверная настройка связи",
        RelationIntegrityIssueKind.MissingDependency => "Отсутствует зависимое свойство",
        RelationIntegrityIssueKind.InvalidDependency => "Неверное зависимое свойство",
        RelationIntegrityIssueKind.DependencyCycle => "Цикл зависимостей",
        RelationIntegrityIssueKind.DependencyUnverified => "Зависимость не проверена",
        RelationIntegrityIssueKind.CanonicalDuplicate => "Дублирующаяся связь",
        RelationIntegrityIssueKind.SingleCardinality => "Несколько целей одиночной связи",
        RelationIntegrityIssueKind.InvalidSelfLink => "Связь записи с собой",
        RelationIntegrityIssueKind.InvalidTargetIdentity => "Неверная идентичность цели",
        RelationIntegrityIssueKind.LegacyMixedTarget => "Старые цели из другой базы",
        RelationIntegrityIssueKind.TargetCollectionMismatch => "Цель из другой коллекции",
        RelationIntegrityIssueKind.NumericProjectionCollision => "Свойство цели из другой базы",
        RelationIntegrityIssueKind.ConditionMismatch => "Не подходит под условие выбора",
        RelationIntegrityIssueKind.NewerAutoRule => "Автоподбор более новой версии",
        _ => "Замечание",
    };
}
