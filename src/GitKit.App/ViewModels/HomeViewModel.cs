using GitKit.App.MVVM;

namespace GitKit.App.ViewModels;

/// <summary>
/// Tela inicial: apresenta as funcionalidades (Replicar branch, Cherry-pick e
/// User Stories) e navega para a aba correspondente ao ser acionada.
/// </summary>
public sealed class HomeViewModel : ObservableObject
{
    public HomeViewModel(Action goToBranchReplication, Action goToCherryPick, Action goToUserStories)
    {
        ReplicateBranchCommand = new RelayCommand(goToBranchReplication);
        CherryPickCommand = new RelayCommand(goToCherryPick);
        UserStoriesCommand = new RelayCommand(goToUserStories);
    }

    public RelayCommand ReplicateBranchCommand { get; }
    public RelayCommand CherryPickCommand { get; }
    public RelayCommand UserStoriesCommand { get; }
}
