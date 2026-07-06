using GitKit.App.MVVM;
using GitKit.Core.Models;

namespace GitKit.App.ViewModels;

/// <summary>Colaborador do repositório com um estado de seleção (revisor da PR).</summary>
public sealed class ReviewerOption : ObservableObject
{
    public ReviewerOption(GitHubUser user) => User = user;

    public GitHubUser User { get; }

    public string Login => User.Login;
    public string Display => User.Display;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
