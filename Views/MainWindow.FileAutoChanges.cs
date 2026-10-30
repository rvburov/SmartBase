using Avalonia.Threading;
using SmartBase.Database;
using SmartBase.Database.Repositories;

namespace SmartBase.Views;

public partial class MainWindow
{
    private readonly Dictionary<(string Path, int Field), Dictionary<int, FileAutoWork>> _fileAutoPending = [];
    private readonly HashSet<DatabaseManager> _fileAutoMounted = [];
    private HashSet<string> _fileAutoPaths = [];
    private CancellationTokenSource _fileAutoCancellation = new();
    private DispatcherTimer? _fileAutoTimer;
    private bool _fileAutoRunning;
    private bool _fileAutoClosed;
    private string? _fileAutoFailure;

    private void ConfigureFileAutoChanges()
    {
        ConstructorRepository.FileAutoWorkRequested += OnFileAutoWork;
        ConstructorRepository.FileAutoWorkSupported += AcceptsFileAutoWork;
        Closed += (_, _) =>
        {
            _fileAutoClosed = true;
            ConstructorRepository.FileAutoWorkRequested -= OnFileAutoWork;
            ConstructorRepository.FileAutoWorkSupported -= AcceptsFileAutoWork;
            Volatile.Write(ref _fileAutoPaths, []);
            _fileAutoTimer?.Stop();
            _fileAutoCancellation.Cancel();
            _fileAutoPending.Clear();
        };
        FileAutoMountsChanged();
    }

    private void FileAutoMountsChanged()
    {
        if (_fileAutoClosed) return;
        var current = _workspace.Mounted.ToHashSet();
        if (_fileAutoMounted.SetEquals(current)) return;
        _fileAutoMounted.Clear();
        _fileAutoMounted.UnionWith(current);
        Volatile.Write(ref _fileAutoPaths, current.Select(m => m.Path).ToHashSet(StringComparer.Ordinal));
        _fileAutoCancellation.Cancel();
        _fileAutoCancellation = new();
    }

    private bool AcceptsFileAutoWork(string path) => Volatile.Read(ref _fileAutoPaths).Contains(path);

    private void OnFileAutoWork(FileAutoWork work)
    {
        if (AcceptsFileAutoWork(work.DbPath)) Dispatcher.UIThread.Post(() => QueueFileAutoWork(work));
    }

    private void QueueFileAutoWork(FileAutoWork work)
    {
        if (_fileAutoClosed) return;
        var key = (work.DbPath, work.FieldId);
        if (!_fileAutoPending.TryGetValue(key, out var records)) _fileAutoPending[key] = records = [];
        if (records.ContainsKey(0)) return;
        if (work.RecordId is null || records.Count >= 128)
        {
            records.Clear();
            records[0] = work with { RecordId = null };
        }
        else records[work.RecordId.Value] = work;
        _fileAutoTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _fileAutoTimer.Tick -= FlushFileAutoWork;
        _fileAutoTimer.Tick += FlushFileAutoWork;
        _fileAutoTimer.Stop();
        _fileAutoTimer.Start();
    }

    private async void FlushFileAutoWork(object? sender, EventArgs args)
    {
        _fileAutoTimer?.Stop();
        if (_fileAutoClosed || _fileAutoRunning || _fileAutoPending.Count == 0) return;
        _fileAutoRunning = true;
        var pending = _fileAutoPending.Values.SelectMany(rows => rows.Values).ToArray();
        _fileAutoPending.Clear();
        var token = _fileAutoCancellation.Token;
        try
        {
            foreach (var work in pending)
            {
                if (_fileAutoClosed) break;
                var manager = _workspace.Mounted.FirstOrDefault(m => m.Path == work.DbPath);
                if (manager is null) continue;
                try
                {
                    var result = await Task.Run(() => ConstructorRepository.ExecuteFileAutoWork(work, token));
                    if (_fileAutoClosed) break;
                    // После await прежний manager мог быть отключен или заменен новым.
                    // Результат старого владельца не должен трогать закрытый UI-контекст.
                    if (!_workspace.Mounted.Contains(manager)) continue;
                    if (token.IsCancellationRequested || result == ConstructorRepository.FileAutoResolveResult.Cancelled)
                    {
                        QueueFileAutoWork(work);
                        continue;
                    }
                    if (result == ConstructorRepository.FileAutoResolveResult.Ok)
                    {
                        manager.Constructor.ForgetAutoFileValues(work.FieldId);
                        ApplyLocalChanges(manager.DbId, structural: false, ChangeBus.Now());
                        _fileAutoFailure = null;
                    }
                    else
                    {
                        var message = ConstructorRepository.FileAutoFailureMessage(result);
                        if (message.Length > 0 && message != _fileAutoFailure)
                        { _fileAutoFailure = message; Notify(message); }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Ошибка одной базы не теряет остальные запросы снятой пачки.
                    Serilog.Log.Warning("Не удалось завершить фоновый подбор файлов; прежние привязки сохранены");
                }
            }
        }
        finally
        {
            _fileAutoRunning = false;
            if (!_fileAutoClosed && _fileAutoPending.Count > 0) _fileAutoTimer?.Start();
        }
    }
}
