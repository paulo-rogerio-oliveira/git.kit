using System.Collections.ObjectModel;
using System.Linq;
using GitKit.App.MVVM;
using GitKit.App.Services;
using GitKit.Core.Models;
using GitKit.Core.Services;

namespace GitKit.App.ViewModels;

/// <summary>Fase da tela do processo: resolvendo conflitos, pronta para enviar ou só visualização.</summary>
public enum ConflictResolutionPhase
{
    Resolving,
    ReadyToPush,
    Info,
}

/// <summary>
/// ViewModel do formulário de resolução de conflitos de um processo em background.
/// Resolve os conflitos do commit corrente, retoma os commits restantes e, quando
/// tudo está resolvido, oferece o envio (push) — e a criação da PR, na replicação
/// de branch — na própria tela.
/// </summary>
public sealed class ConflictsViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly ConflictResolutionCoordinator _coordinator;
    private readonly IDialogService _dialogs;
    private readonly BackgroundJobService _jobs;
    private readonly JobViewModel _job;

    private GitCommit? _commit;
    private readonly ReplicationMode _mode;

    public ConflictsViewModel(
        IGitService git,
        ConflictResolutionCoordinator coordinator,
        IDialogService dialogs,
        BackgroundJobService jobs,
        JobViewModel job,
        IReadOnlyList<ConflictEntry> conflicts)
    {
        _git = git;
        _coordinator = coordinator;
        _dialogs = dialogs;
        _jobs = jobs;
        _job = job;

        RepositoryPath = job.WorkingDir;
        _commit = job.PendingCommit;
        _mode = job.Mode;
        _phase = job.Status switch
        {
            JobStatus.NeedsConflictResolution => ConflictResolutionPhase.Resolving,
            JobStatus.ReadyToPush => ConflictResolutionPhase.ReadyToPush,
            _ => ConflictResolutionPhase.Info, // qualquer outro status: só visualização
        };

        foreach (var entry in conflicts)
            Items.Add(new ConflictItemViewModel(entry, ResolveItemAsync));

        RefreshCommand = new AsyncRelayCommand(RefreshStatusAsync, () => !IsBusy && Phase == ConflictResolutionPhase.Resolving);
        ConcludeCommand = new AsyncRelayCommand(ConcludeAsync,
            () => !IsBusy && Phase == ConflictResolutionPhase.Resolving && AllResolved && Items.Count > 0);
        PushCommand = new AsyncRelayCommand(PushAsync, () => !IsBusy && Phase == ConflictResolutionPhase.ReadyToPush);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(this, EventArgs.Empty));

        StatusMessage = _phase switch
        {
            ConflictResolutionPhase.ReadyToPush => $"Conflitos resolvidos. Clique em '{PushButtonLabel}'.",
            ConflictResolutionPhase.Info => _job.StatusText,
            _ => StatusMessage,
        };
    }

    public string RepositoryPath { get; }

    /// <summary>O processo em background, para exibir seus dados (status/detalhe) ao vivo.</summary>
    public JobViewModel Job => _job;

    // ----- Informações do processo (cabeçalho do popup) -----
    public string ProcessTitle => _job.Title;
    public string RepositoryUrl => _job.RepositoryUrl;
    public string WorkingDir => _job.WorkingDir;

    public ObservableCollection<ConflictItemViewModel> Items { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ConcludeCommand { get; }
    public AsyncRelayCommand PushCommand { get; }
    public RelayCommand CloseCommand { get; }

    /// <summary>Disparado quando o formulário deve ser fechado.</summary>
    public event EventHandler? RequestClose;

    // ----- Estado -----

    private ConflictResolutionPhase _phase;
    public ConflictResolutionPhase Phase
    {
        get => _phase;
        private set
        {
            if (SetProperty(ref _phase, value))
            {
                OnPropertyChanged(nameof(ShowResolving));
                OnPropertyChanged(nameof(ShowPush));
                OnPropertyChanged(nameof(ShowInfo));
                RefreshCommand.NotifyCanExecuteChanged();
                ConcludeCommand.NotifyCanExecuteChanged();
                PushCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool ShowResolving => Phase == ConflictResolutionPhase.Resolving;
    public bool ShowPush => Phase == ConflictResolutionPhase.ReadyToPush;
    public bool ShowInfo => Phase == ConflictResolutionPhase.Info;

    public string PushButtonLabel => _job.Kind == JobKind.BranchReplication
        ? "Enviar (push) e criar PR"
        : "Enviar (push)";

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
                PushCommand.NotifyCanExecuteChanged();
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
        if (IsBusy || Phase != ConflictResolutionPhase.Resolving)
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
                ? "Todos os conflitos resolvidos. Clique em 'Concluir resolução'."
                : $"{Items.Count(i => i.IsResolved)} de {Items.Count} resolvido(s).";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ConcludeAsync()
    {
        if (Phase != ConflictResolutionPhase.Resolving || !AllResolved || _commit is null)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "Concluindo a resolução do commit...";
            var result = await _git.ContinueReplicationAsync(RepositoryPath, _commit, _mode);

            switch (result.Status)
            {
                case ReplicationStatus.Success:
                case ReplicationStatus.AlreadyApplied:
                    StatusMessage = "Commit resolvido. Verificando os commits restantes...";
                    await ContinueJobAsync();
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

    // Aplica os commits restantes; passa para a fase de push ou recarrega o próximo conflito.
    private async Task ContinueJobAsync()
    {
        var step = await _jobs.ContinueAfterResolutionAsync(_job);
        switch (step)
        {
            case BackgroundJobService.RecoverStep.ReadyToPush:
                Phase = ConflictResolutionPhase.ReadyToPush;
                StatusMessage = $"Todos os conflitos resolvidos. Clique em '{PushButtonLabel}'.";
                break;

            case BackgroundJobService.RecoverStep.MoreConflicts:
                await LoadCurrentConflictAsync();
                break;

            default:
                StatusMessage = _job.StatusText;
                _dialogs.ShowError("Falha ao retomar", _job.StatusText);
                RequestClose?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    // Recarrega a lista de conflitos para o novo commit pendente do job.
    private async Task LoadCurrentConflictAsync()
    {
        _commit = _job.PendingCommit;
        var conflicts = await _git.GetConflictsAsync(RepositoryPath);

        Items.Clear();
        foreach (var entry in conflicts)
            Items.Add(new ConflictItemViewModel(entry, ResolveItemAsync));

        OnPropertyChanged(nameof(AllResolved));
        ConcludeCommand.NotifyCanExecuteChanged();
        StatusMessage = $"Novo conflito no commit {_commit?.ShortHash}. Resolva os arquivos e conclua.";
    }

    private async Task PushAsync()
    {
        try
        {
            IsBusy = true;
            StatusMessage = _job.Kind == JobKind.BranchReplication
                ? "Enviando o branch e criando a Pull Request..."
                : "Enviando o branch (push)...";

            var ok = await _jobs.FinishAsync(_job);
            if (ok)
            {
                StatusMessage = _job.StatusText;
                var extra = _job.HasPullRequest ? $"\n\nPR: {_job.PullRequestUrl}" : string.Empty;
                _dialogs.ShowInfo("Concluído", _job.StatusText + extra);
                RequestClose?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                StatusMessage = _job.StatusText;
                _dialogs.ShowError("Falha ao enviar", _job.StatusText);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}
