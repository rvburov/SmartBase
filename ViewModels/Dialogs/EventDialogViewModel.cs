using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyPlanner.Core.Models;
using MyPlanner.Core.Validators;
using MyPlanner.Database.Repositories;

namespace MyPlanner.ViewModels.Dialogs;

public partial class EventDialogViewModel : ViewModelBase
{
    private readonly ConstructorRepository _constructor;
    private readonly Event?                _editingEvent;

    [ObservableProperty] private string  _title       = "";
    [ObservableProperty] private string  _description = "";
    [ObservableProperty] private DateTime _startDate  = DateTime.Today;
    [ObservableProperty] private TimeSpan _startTime  = new(9, 0, 0);
    [ObservableProperty] private DateTime _endDate    = DateTime.Today;
    [ObservableProperty] private TimeSpan _endTime    = new(10, 0, 0);
    [ObservableProperty] private bool    _allDay      = false;
    [ObservableProperty] private DbCollection? _selectedCollection;
    [ObservableProperty] private string? _errorMessage;

    public ObservableCollection<DbCollection> Collections { get; } = [];

    public bool   IsEdit      => _editingEvent is not null;
    public Event? ResultEvent { get; private set; }
    public bool   Deleted     { get; private set; }

    public event Action? Accepted;
    public event Action? DeleteRequested;

    public EventDialogViewModel(ConstructorRepository constructor, Event? editingEvent = null, DateTime? presetDate = null)
    {
        _constructor  = constructor;
        _editingEvent = editingEvent;

        LoadCollections();

        if (editingEvent is not null)
        {
            Title       = editingEvent.Title;
            Description = editingEvent.Description;
            StartDate   = editingEvent.StartDt.Date;
            StartTime   = editingEvent.StartDt.TimeOfDay;
            EndDate     = editingEvent.EndDt.Date;
            EndTime     = editingEvent.EndDt.TimeOfDay;
            AllDay      = editingEvent.AllDay;
            SelectedCollection = Collections.FirstOrDefault(c => c.Id == editingEvent.CollectionId);
        }
        else if (presetDate.HasValue)
        {
            StartDate = presetDate.Value;
            EndDate   = presetDate.Value;
        }
    }

    private void LoadCollections()
    {
        Collections.Clear();
        foreach (var c in _constructor.GetAllCollections())
            Collections.Add(c);

        SelectedCollection ??= Collections.FirstOrDefault();
    }

    [RelayCommand]
    private void Save()
    {
        var startDt = AllDay
            ? StartDate.Date
            : StartDate.Date + StartTime;

        var endDt = AllDay
            ? EndDate.Date.AddHours(23).AddMinutes(59).AddSeconds(59)
            : EndDate.Date + EndTime;

        var candidate = new Event
        {
            Id           = _editingEvent?.Id,
            Title        = Title.Trim(),
            Description  = Description,
            StartDt      = startDt,
            EndDt        = endDt,
            AllDay       = AllDay,
            CollectionId = SelectedCollection?.Id,
        };

        var error = EventValidator.Validate(candidate);
        if (error is not null) { ErrorMessage = error; return; }

        ResultEvent = candidate;
        Accepted?.Invoke();
    }

    [RelayCommand]
    private void Delete()
    {
        Deleted = true;
        DeleteRequested?.Invoke();
    }
}
