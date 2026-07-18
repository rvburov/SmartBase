using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.ViewModels.Dialogs;

public partial class CollectionDialogViewModel : ViewModelBase
{
    private readonly ConstructorRepository _constructor;
    private readonly DbCollection?         _editing;

    [ObservableProperty] private string  _name  = "";
    [ObservableProperty] private string  _color = "#007AFF";
    [ObservableProperty] private string  _icon  = "table";
    [ObservableProperty] private int?    _groupId;
    [ObservableProperty] private string? _errorMessage;

    public ObservableCollection<DbGroup> Groups { get; } = [];

    public bool        IsEdit => _editing is not null;
    public event Action? Accepted;
    public event Action? DeleteRequested;

    public CollectionDialogViewModel(ConstructorRepository constructor, DbCollection? editing = null)
    {
        _constructor = constructor;
        _editing     = editing;

        foreach (var g in constructor.GetAllGroups()) Groups.Add(g);

        if (editing is not null)
        {
            Name    = editing.Name;
            Color   = editing.Color;
            Icon    = editing.Icon;
            GroupId = editing.GroupId;
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name)) { ErrorMessage = "Введите название"; return; }

        if (_editing is null)
        {
            var maxPos = _constructor.GetAllCollections().Select(c => c.Position).DefaultIfEmpty(0).Max();
            _constructor.AddCollection(new DbCollection
            {
                Name = Name.Trim(), Color = Color, Icon = Icon,
                GroupId = GroupId, Position = maxPos + 1000,
            });
        }
        else
        {
            _editing.Name    = Name.Trim();
            _editing.Color   = Color;
            _editing.Icon    = Icon;
            _editing.GroupId = GroupId;
            _constructor.UpdateCollection(_editing);
        }
        Accepted?.Invoke();
    }

    [RelayCommand]
    private void Delete()
    {
        if (_editing?.Id is not null)
            _constructor.DeleteCollection(_editing.Id.Value);
        DeleteRequested?.Invoke();
    }
}
