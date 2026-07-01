using System.Collections.ObjectModel;
using System.Linq;
using GitKit.App.MVVM;
using GitKit.App.Services;
using GitKit.Core.Models;
using GitKit.Core.Services;

namespace GitKit.App.ViewModels;

/// <summary>
/// ViewModel do formulário de resolução de conflitos: lista os arquivos em
/// conflito, permite resolvê-los no TortoiseGitMerge e concluir a replicação.
/// </summary>
public sealed class ConflictsViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly ConflictResolutionCoordinator _coordinator;
    private readonly IDialogService _dialogs;
    private readonly GitCommit _commit;
    private readonly ReplicationMode _mode;

    public ConflictsViewModel(
        IGitService git,
        ConflictResolutionCoordinator coordinator,
        IDialogService dialogs,
        string repositoryPath,
        GitCommit commit,
        ReplicationMode mode,
        IReadOnlyList<ConflictEntry> conflicts)
    {
        _git = git;
        _coordinator = coordinator;
        _dialogs = dialogs;
        RepositoryPath = repositoryPath;
        _commit = commit;
        _mode = mode;

        foreach (var entry in conflicts)
            Items.Add(new ConflictItemViewModel(entry, ResolveItemAsync));

        RefreshCommand = new AsyncRelayCommand(RefreshStatusAsync, () => !IsBusy);
        ConcludeCommand = new AsyncRelayCommand(ConcludeAsync, () => !IsBusy && AllResolved && Items.Count > 0);
    }

    public string RepositoryPath { get; }

    public ObservableCollection<ConflictItemViewModel> Items { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ConcludeCommand { get; }

    /// <summary>Disparado quando o formulário deve ser fechado.</summary>
    public event EventHandler? RequestClose;

    // ----- Resultado consumido pela MainViewModel após o fechamento -----

    public bool Concluded { get; private set; }
    public string ResultBranch { get; private set; } = string.Empty;
    public string ResultMessage { get; private set; } = string.Empty;

    // ----- Estado -----

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsNotBusy));
                RefreshCommand.NotifyCanExecuteChanged();
                ConcludeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    public bool AllResolved => Items.Count > 0 && Items.All(i => i.IsResolved);

    private string _statusMessage = "Resolva cada conflito no TortoiseGitMerge e marque como resolvido.";
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    // ----- Ações -----

    private async Task ResolveItemAsync(ConflictItemViewModel item)
    {
        StatusMessage = $"Abrindo TortoiseGitMerge para '{item.Path}'...";
        await _coordinator.OpenMergeForFileAsync(RepositoryPath, item.Path);
        StatusMessage = "Após resolver e marcar como resolvido no TortoiseGit, clique em 'Atualizar status'.";
    }

    /// <summary>
    /// Reconsulta o git: arquivos que não constam mais como não-mesclados
    /// (porque foram marcados como resolvidos no TortoiseGit) viram "Resolvido".
    /// </summary>
    public async Task RefreshStatusAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            var stillConflicted = await _git.GetConflictedFilesAsync(RepositoryPath);
            var pending = new HashSet<string>(stillConflicted, StringComparer.OrdinalIgnoreCase);

            foreach (var item in Items)
                item.IsResolved = !pending.Contains(item.Path);

            OnPropertyChanged(nameof(AllResolved));
            ConcludeCommand.NotifyCanExecuteChanged();

            StatusMessage = AllResolved
                ? "Todos os conflitos resolvidos. Clique em 'Concluir replicação'."
                : $"{Items.Count(i => i.IsResolved)} de {Items.Count} resolvido(s).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ConcludeAsync()
    {
        if (!AllResolved)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "Concluindo replicação...";
            var result = await _git.ContinueReplicationAsync(RepositoryPath, _commit, _mode);

            switch (result.Status)
            {
                case ReplicationStatus.Success:
                case ReplicationStatus.AlreadyApplied:
                    Concluded = true;
                    ResultBranch = result.BranchName;
                    ResultMessage = result.Message;
                    RequestClose?.Invoke(this, EventArgs.Empty);
                    break;

                case ReplicationStatus.ConflictsNeedManualResolution:
                    StatusMessage = result.Message;
                    _dialogs.ShowError("Ainda há conflitos", result.Message);
                    await RefreshStatusAsync();
                    break;

                default:
                    StatusMessage = "Falha ao concluir a replicação.";
                    _dialogs.ShowError("Falha ao concluir", result.Message);
                    break;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
