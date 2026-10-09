using System.ComponentModel;
using System.Runtime.CompilerServices;
using TabBrowser.Models.Workspaces;

namespace TabBrowser.ViewModels;

public sealed class WorkspaceSlotViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public WorkspaceSlotViewModel(Workspace workspace)
    {
        Workspace = workspace;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Workspace Workspace { get; }

    public Guid Id => Workspace.Id;

    public int Index => Workspace.Index;

    public string Name => string.IsNullOrWhiteSpace(Workspace.Name)
        ? $"Workspace {Workspace.Index}"
        : Workspace.Name;

    public string DisplayName => $"{Index}: {Name}";

    public void UpdateName(string name)
    {
        var normalizedName = name.Trim();
        if (Workspace.Name == normalizedName)
        {
            return;
        }

        Workspace.Name = normalizedName;
        Workspace.UpdatedAt = DateTimeOffset.Now;
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(DisplayName));
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
