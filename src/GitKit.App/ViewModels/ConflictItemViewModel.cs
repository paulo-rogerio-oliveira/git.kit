using GitKit.App.MVVM;
using GitKit.Core.Models;

namespace GitKit.App.ViewModels;

/// <summary>
/// Linha do grid de conflitos: um arquivo, seu tipo e o status de resolução.
/// </summary>
public sealed class ConflictItemViewModel : ObservableObject
{
    public ConflictItemViewModel(ConflictEntry entry, Func<ConflictItemViewModel, Task> resolve)
    {
        Path = entry.Path;
        Code = entry.Code;
        Description = entry.Description;
        ResolveCommand = new AsyncRelayCommand(() => resolve(this), () => !IsResolved);
    }

    public string Path { get; }

    public string Code { get; }

    public string Description { get; }

    private bool _isResolved;
    public bool IsResolved
    {
        get => _isResolved;
        set
        {
            if (SetProperty(ref _isResolved, value))
                OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>Texto exibido na coluna de status.</summary>
    public string StatusText => IsResolved ? "Resolvido" : "Pendente";

    public AsyncRelayCommand ResolveCommand { get; }
}
