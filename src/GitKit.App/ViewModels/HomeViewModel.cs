using GitKit.App.MVVM;

namespace GitKit.App.ViewModels;

/// <summary>
/// Tela inicial: apresenta as duas funcionalidades (Replicar branch e Cherry-pick)
/// e navega para a aba correspondente ao ser acionada.
/// </summary>
public sealed class HomeViewModel : ObservableObject
{
    public HomeViewModel(Action goToBranchReplication, Action goToCherryPick)
    {
        ReplicateBranchCommand = new RelayCommand(goToBranchReplication);
        CherryPickCommand = new RelayCommand(goToCherryPick);
    }

    public RelayCommand ReplicateBranchCommand { get; }
    public RelayCommand CherryPickCommand { get; }
}
