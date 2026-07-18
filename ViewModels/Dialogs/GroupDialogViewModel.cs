using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartBase.Core.Models;
using SmartBase.Database.Repositories;

namespace SmartBase.ViewModels.Dialogs;

public partial class GroupDialogViewModel : ViewModelBase
{
    private readonly ConstructorRepository _constructor;
    private readonly DbGroup?              _editing;

    [ObservableProperty] private string  _name = "";
    [ObservableProperty] private string? _errorMessage;

    public bool        IsEdit => _editing is not null;
    public event Action? Accepted;
    public event Action? DeleteRequested;

    public GroupDialogViewModel(ConstructorRepository constructor, DbGroup? editing = null)
    {
        _constructor = constructor;
        _editing     = editing;
        if (editing is not null) Name = editing.Name;
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name)) { ErrorMessage = "Введите название"; return; }

        if (_editing is null)
        {
            var maxPos = _constructor.GetAllGroups().Select(g => g.Position).DefaultIfEmpty(0).Max();
            _constructor.AddGroup(new DbGroup { Name = Name.Trim(), Position = maxPos + 1000 });
        }
        else
        {
            _editing.Name = Name.Trim();
            _constructor.UpdateGroup(_editing);
        }
        Accepted?.Invoke();
    }

    [RelayCommand]
    private void Delete()
    {
        if (_editing?.Id is not null)
            _constructor.DeleteGroup(_editing.Id.Value);
        DeleteRequested?.Invoke();
    }
}
