using GitKit.App.MVVM;
using GitKit.Core.Models;

namespace GitKit.App.ViewModels;

/// <summary>Um commit listado (via gh) com um estado de seleção para o cherry-pick.</summary>
public sealed class CommitOption : ObservableObject
{
    public CommitOption(GitCommit commit) => Commit = commit;

    public GitCommit Commit { get; }

    public string ShortHash => Commit.ShortHash;
    public string Author => Commit.Author;
    public DateTimeOffset Date => Commit.Date;
    public string Subject => Commit.Subject;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
