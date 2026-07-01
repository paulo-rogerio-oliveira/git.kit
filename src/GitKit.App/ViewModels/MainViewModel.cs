using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using GitKit.App.MVVM;
using GitKit.App.Services;
using GitKit.Core.Models;
using GitKit.Core.Services;

namespace GitKit.App.ViewModels;

/// <summary>
/// ViewModel principal: orquestra clonagem, seleção de branches/commit e replicação.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly ConflictResolutionCoordinator _coordinator;
    private readonly IDialogService _dialogs;
    private readonly WorkspaceService _workspace;
    private readonly IRepositoryCache _cache;
    private readonly IRecentRepositories _recent;
    private readonly StringBuilder _log = new();

    public MainViewModel(
        IGitService git,
        ConflictResolutionCoordinator coordinator,
        IDialogService dialogs,
        WorkspaceService workspace,
        IRepositoryCache cache,
        IRecentRepositories recent)
    {
        _git = git;
        _coordinator = coordinator;
        _dialogs = dialogs;
        _workspace = workspace;
        _cache = cache;
        _recent = recent;

        _git.CommandExecuted += OnGitCommandExecuted;

        // Cada combo/grid usa uma view independente (não a default view compartilhada),
        // para que os filtros de origem e destino não interfiram um no outro.
        SourceBranchesView = new CollectionViewSource { Source = Branches }.View;
        SourceBranchesView.Filter = FilterSourceBranch;
        DestinationBranchesView = new CollectionViewSource { Source = Branches }.View;
        DestinationBranchesView.Filter = FilterDestinationBranch;
        CommitsView = new CollectionViewSource { Source = Commits }.View;
        CommitsView.Filter = FilterCommit;

        Modes = new[]
        {
            new ReplicationModeOption(ReplicationMode.CherryPick, "Cherry-pick (replica o commit)"),
            new ReplicationModeOption(ReplicationMode.DiffIntegration, "Integração de diff (aplica as diferenças)"),
        };
        _selectedMode = Modes[0];

        StartCommand = new AsyncRelayCommand(StartAsync, CanStart);
        RefreshBranchesCommand = new AsyncRelayCommand(RefreshBranchesAsync, () => !IsBusy && IsRepositoryReady);
        LoadCommitsCommand = new AsyncRelayCommand(LoadCommitsAsync, () => !IsBusy && SelectedSourceBranch is not null);
        ReplicateCommand = new AsyncRelayCommand(ReplicateAsync, CanReplicate);
        PushCommand = new AsyncRelayCommand(PushAsync, () => !IsBusy && IsRepositoryReady && !string.IsNullOrWhiteSpace(PushableBranch));
        ResolveConflictsCommand = new AsyncRelayCommand(ShowConflictsWindowAsync, () => !IsBusy && PendingManualResolution && _pendingCommit is not null);

        // Carrega o histórico e pré-preenche com o último repositório usado.
        LoadRecentRepositories();
        if (RecentRepositories.Count > 0)
            RepositorySource = RecentRepositories[0];
    }

    private void LoadRecentRepositories()
    {
        RecentRepositories.Clear();
        foreach (var source in _recent.GetAll())
            RecentRepositories.Add(source);
    }

    // Registra a origem usada como a mais recente e reflete no combo.
    private void RegisterRecentRepository(string source)
    {
        _recent.Add(source);
        LoadRecentRepositories();
    }

    // ----- Coleções e opções -----

    public ObservableCollection<GitBranch> Branches { get; } = new();
    public ObservableCollection<GitCommit> Commits { get; } = new();
    // Repositórios já utilizados (URLs/caminhos), do mais recente para o mais antigo.
    public ObservableCollection<string> RecentRepositories { get; } = new();
    public IReadOnlyList<ReplicationModeOption> Modes { get; }

    // Views filtradas exibidas na UI. Os branches são filtrados pelo TEXTO DIGITADO
    // no próprio combo editável (origem = BranchFilter; destino = DestinationBranch).
    public ICollectionView SourceBranchesView { get; }
    public ICollectionView DestinationBranchesView { get; }
    public ICollectionView CommitsView { get; }

    // ----- Filtros -----

    // Texto digitado no combo de ORIGEM (filtra a lista e é o termo de busca).
    private string _branchFilter = string.Empty;
    public string BranchFilter
    {
        get => _branchFilter;
        set
        {
            if (SetProperty(ref _branchFilter, value))
                SourceBranchesView.Refresh();
        }
    }

    private string _commitFilter = string.Empty;
    public string CommitFilter
    {
        get => _commitFilter;
        set
        {
            if (SetProperty(ref _commitFilter, value))
                CommitsView.Refresh();
        }
    }

    private bool FilterSourceBranch(object item)
        => MatchesBranch(item, BranchFilter);

    private bool FilterDestinationBranch(object item)
        => MatchesBranch(item, DestinationBranch);

    private static bool MatchesBranch(object item, string term)
    {
        term = term.Trim();
        return term.Length == 0
            || (item is GitBranch b && b.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private bool FilterCommit(object item)
    {
        var term = CommitFilter.Trim();
        if (term.Length == 0)
            return true;
        return item is GitCommit c
            && (c.ShortHash.Contains(term, StringComparison.OrdinalIgnoreCase)
                || c.Author.Contains(term, StringComparison.OrdinalIgnoreCase)
                || c.Subject.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    // ----- Comandos -----

    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand RefreshBranchesCommand { get; }
    public AsyncRelayCommand LoadCommitsCommand { get; }
    public AsyncRelayCommand ReplicateCommand { get; }
    public AsyncRelayCommand PushCommand { get; }
    public AsyncRelayCommand ResolveConflictsCommand { get; }

    // ----- Estado -----

    // Entrada do usuário: pode ser uma URL de clone ou um caminho local.
    private string _repositorySource = string.Empty;
    public string RepositorySource
    {
        get => _repositorySource;
        set => SetProperty(ref _repositorySource, value);
    }

    // URL do remote resolvida (após clonar ou ler de um repositório local).
    private string _repositoryUrl = string.Empty;
    public string RepositoryUrl
    {
        get => _repositoryUrl;
        private set => SetProperty(ref _repositoryUrl, value);
    }

    private string _repositoryPath = string.Empty;
    public string RepositoryPath
    {
        get => _repositoryPath;
        private set
        {
            if (SetProperty(ref _repositoryPath, value))
                OnPropertyChanged(nameof(IsRepositoryReady));
        }
    }

    public bool IsRepositoryReady => !string.IsNullOrWhiteSpace(RepositoryPath);

    private GitBranch? _selectedSourceBranch;
    public GitBranch? SelectedSourceBranch
    {
        get => _selectedSourceBranch;
        set
        {
            if (SetProperty(ref _selectedSourceBranch, value))
                _ = LoadCommitsAsync();
        }
    }

    // Editável: pode ser um branch existente (selecionado) ou um nome novo
    // digitado pelo usuário, que será criado durante a replicação.
    private string _destinationBranch = string.Empty;
    public string DestinationBranch
    {
        get => _destinationBranch;
        set
        {
            if (SetProperty(ref _destinationBranch, value))
                DestinationBranchesView.Refresh();
        }
    }

    private GitCommit? _selectedCommit;
    public GitCommit? SelectedCommit
    {
        get => _selectedCommit;
        set => SetProperty(ref _selectedCommit, value);
    }

    private ReplicationModeOption _selectedMode;
    public ReplicationModeOption SelectedMode
    {
        get => _selectedMode;
        set => SetProperty(ref _selectedMode, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
                OnPropertyChanged(nameof(IsNotBusy));
        }
    }

    public bool IsNotBusy => !IsBusy;

    private string _statusMessage = "Informe a URL ou o caminho do repositório e clique em Iniciar.";
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    private bool _pendingManualResolution;
    public bool PendingManualResolution
    {
        get => _pendingManualResolution;
        private set => SetProperty(ref _pendingManualResolution, value);
    }

    // Operação de replicação aguardando resolução manual de conflitos.
    private GitCommit? _pendingCommit;
    private ReplicationMode _pendingMode;
    private string _pendingRepoPath = string.Empty;

    // Branch preparado pela última replicação, disponível para push.
    private string _pushableBranch = string.Empty;
    public string PushableBranch
    {
        get => _pushableBranch;
        private set
        {
            if (SetProperty(ref _pushableBranch, value))
                OnPropertyChanged(nameof(CanShowPush));
        }
    }

    public bool CanShowPush => !string.IsNullOrWhiteSpace(PushableBranch);

    public string Log => _log.ToString();

    // ----- Implementação dos comandos -----

    private bool CanStart()
        => !IsBusy && !string.IsNullOrWhiteSpace(RepositorySource);

    private async Task StartAsync()
    {
        // Um caminho existente no sistema de arquivos é tratado como repositório
        // local; caso contrário, a entrada é interpretada como URL de clone.
        var source = RepositorySource.Trim();
        if (LooksLikeLocalPath(source))
            await OpenLocalAsync(source);
        else
            await CloneAsync(source);
    }

    private static bool LooksLikeLocalPath(string source)
    {
        try
        {
            return Directory.Exists(source);
        }
        catch
        {
            return false;
        }
    }

    private async Task CloneAsync(string url)
    {
        await RunBusyAsync("Preparando repositório...", async () =>
        {
            // Sempre clona numa pasta de trabalho única.
            var target = _workspace.CreateWorkFolder();

            // Cache: mantém/atualiza um espelho local do remote e clona a cópia de
            // trabalho a partir dele (bem mais ágil que clonar do remote toda vez).
            StatusMessage = "Atualizando cache local do repositório...";
            var cachePath = await _cache.EnsureCacheAsync(url);

            GitCommandResult result;
            if (cachePath is not null)
            {
                StatusMessage = "Clonando a partir do cache local...";
                result = await _git.CloneAsync(cachePath, target);
                if (result.Success)
                {
                    // O push deve ir para o remote REAL, não para o espelho local:
                    // reaponta o origin da cópia para a URL informada.
                    await _git.SetRemoteUrlAsync(target, url);
                }
            }
            else
            {
                // Sem cache disponível: clona diretamente do remote.
                StatusMessage = "Clonando repositório...";
                result = await _git.CloneAsync(url, target);
            }

            if (!result.Success)
            {
                _dialogs.ShowError("Falha na clonagem", result.CombinedOutput);
                StatusMessage = "Falha ao clonar o repositório.";
                return;
            }

            ResetSelections();
            RepositoryPath = target;
            RepositoryUrl = await _git.GetRemoteUrlAsync(target);
            RegisterRecentRepository(url);
            StatusMessage = cachePath is not null
                ? $"Repositório pronto (via cache) em {target}."
                : $"Repositório clonado em {target}.";
            await LoadBranchesAsync(fetch: false);
        });
    }

    private async Task OpenLocalAsync(string path)
    {
        await RunBusyAsync("Validando repositório local...", async () =>
        {
            if (!await _git.IsRepositoryAsync(path))
            {
                _dialogs.ShowError("Repositório inválido",
                    $"O caminho informado não é a raiz de um repositório git:\n{path}");
                StatusMessage = "O caminho informado não é um repositório git.";
                return;
            }

            // URL real do remote do repositório original (apenas para exibição).
            var originUrl = await _git.GetRemoteUrlAsync(path);

            // Clona o repositório local numa pasta temporária para NÃO forçar
            // troca de branch nem alterar a árvore de trabalho do projeto corrente.
            StatusMessage = "Replicando repositório local em pasta temporária...";
            var target = _workspace.CreateWorkFolder();
            var result = await _git.CloneAsync(path, target);
            if (!result.Success)
            {
                _dialogs.ShowError("Falha ao copiar repositório", result.CombinedOutput);
                StatusMessage = "Falha ao criar a cópia de trabalho.";
                return;
            }

            ResetSelections();
            RepositoryPath = target;

            // O push deve ter como upstream o remote do repositório ORIGINÁRIO,
            // não o caminho local de onde clonamos. Aponta o origin da cópia para
            // a URL real do remote (quando o repositório de origem tiver uma).
            if (!string.IsNullOrWhiteSpace(originUrl))
            {
                await _git.SetRemoteUrlAsync(target, originUrl);
                RepositoryUrl = originUrl;
                StatusMessage = $"Cópia de trabalho criada em {target} — push direcionado ao remote {originUrl}.";
            }
            else
            {
                // Sem remote no repositório de origem: mantém o origin apontando
                // para o próprio caminho local (push retorna ao repositório de origem).
                RepositoryUrl = path;
                StatusMessage = $"Cópia de trabalho criada em {target} — o repositório original não tem remote; o push retornará a {path}.";
            }

            RegisterRecentRepository(path);

            // Não faz fetch aqui: isso buscaria do remote real e poderia podar
            // refs locais recém-clonadas do caminho de origem.
            await LoadBranchesAsync(fetch: false);
        });
    }

    private void ResetSelections()
    {
        Branches.Clear();
        Commits.Clear();
        SelectedSourceBranch = null;
        DestinationBranch = string.Empty;
        SelectedCommit = null;
        PendingManualResolution = false;
        PushableBranch = string.Empty;
        ClearPending();
        RepositoryUrl = string.Empty;
    }

    // Comando "Atualizar": busca do remote (fetch + prune) e recarrega.
    private Task RefreshBranchesAsync() => LoadBranchesAsync(fetch: true);

    private async Task LoadBranchesAsync(bool fetch)
    {
        if (!IsRepositoryReady)
            return;

        await RunBusyAsync(fetch ? "Atualizando branches..." : "Carregando branches...", async () =>
        {
            if (fetch)
                await _git.FetchAsync(RepositoryPath);

            var branches = await _git.GetBranchesAsync(RepositoryPath);

            Branches.Clear();
            foreach (var branch in branches.OrderBy(b => b.IsRemote).ThenBy(b => b.Name))
                Branches.Add(branch);

            StatusMessage = $"{Branches.Count} branch(es) carregado(s).";
        });
    }

    private async Task LoadCommitsAsync()
    {
        if (SelectedSourceBranch is null || !IsRepositoryReady)
            return;

        await RunBusyAsync("Carregando commits...", async () =>
        {
            var commits = await _git.GetCommitsAsync(RepositoryPath, SelectedSourceBranch.Name);
            Commits.Clear();
            foreach (var commit in commits)
                Commits.Add(commit);

            StatusMessage = $"{Commits.Count} commit(s) do branch '{SelectedSourceBranch.Name}'.";
        });
    }

    private bool CanReplicate()
        => !IsBusy
           && IsRepositoryReady
           && SelectedCommit is not null
           && SelectedSourceBranch is not null
           && !string.IsNullOrWhiteSpace(DestinationBranch)
           && !string.Equals(SelectedSourceBranch.Name, DestinationBranch.Trim(), StringComparison.Ordinal);

    private async Task ReplicateAsync()
    {
        var commit = SelectedCommit!;
        var destination = DestinationBranch.Trim();

        var mode = SelectedMode.Mode;

        await RunBusyAsync($"Replicando {commit.ShortHash}...", async () =>
        {
            PendingManualResolution = false;
            PushableBranch = string.Empty;
            ClearPending();

            var result = await _git.ReplicateCommitAsync(
                RepositoryPath, commit, destination, mode);

            // Usa o nome do branch LOCAL realmente preparado (sem 'origin/'),
            // que é o ref correto para o push.
            var localBranch = string.IsNullOrWhiteSpace(result.BranchName) ? destination : result.BranchName;

            switch (result.Status)
            {
                case ReplicationStatus.Success:
                    PushableBranch = localBranch;
                    StatusMessage = result.Message;
                    _dialogs.ShowInfo("Replicação concluída", result.Message);
                    break;

                case ReplicationStatus.AlreadyApplied:
                    PushableBranch = localBranch;
                    StatusMessage = result.Message;
                    _dialogs.ShowInfo("Nada a replicar", result.Message);
                    break;

                case ReplicationStatus.ConflictsNeedManualResolution:
                    // O branch já existe localmente; após resolver e commitar, pode ser enviado.
                    PushableBranch = localBranch;
                    PendingManualResolution = true;
                    // Guarda o contexto para concluir a replicação após a resolução.
                    _pendingCommit = commit;
                    _pendingMode = mode;
                    _pendingRepoPath = result.WorkingDirectory;
                    StatusMessage = "Conflitos detectados — abrindo o formulário de resolução.";
                    await ShowConflictsWindowAsync();
                    break;

                default:
                    StatusMessage = "Falha na replicação.";
                    _dialogs.ShowError("Falha na replicação", result.Message);
                    break;
            }
        });
    }

    private async Task PushAsync()
    {
        var branch = PushableBranch;
        if (string.IsNullOrWhiteSpace(branch))
            return;

        var target = string.IsNullOrWhiteSpace(RepositoryUrl) ? "origin" : RepositoryUrl;
        if (!_dialogs.Confirm("Enviar branch (push)",
                $"Enviar o branch '{branch}' para o remote:\n{target}\n\nConfirma o push?"))
        {
            StatusMessage = "Push cancelado.";
            return;
        }

        await RunBusyAsync($"Enviando '{branch}'...", async () =>
        {
            var result = await _git.PushAsync(RepositoryPath, branch);
            if (result.Success)
            {
                StatusMessage = $"Branch '{branch}' enviado para o remote com sucesso.";
                _dialogs.ShowInfo("Push concluído",
                    $"Branch '{branch}' enviado.\n\n{result.CombinedOutput}");
            }
            else
            {
                StatusMessage = "Falha ao enviar o branch.";
                _dialogs.ShowError("Falha no push", result.CombinedOutput);
            }
        });
    }

    /// <summary>
    /// Abre o formulário de resolução de conflitos. Ao concluir a replicação
    /// dentro dele, libera o push e limpa o estado pendente.
    /// </summary>
    private async Task ShowConflictsWindowAsync()
    {
        if (_pendingCommit is null || string.IsNullOrWhiteSpace(_pendingRepoPath))
            return;

        var conflicts = await _git.GetConflictsAsync(_pendingRepoPath);

        var conflictsVm = new ConflictsViewModel(
            _git, _coordinator, _dialogs, _pendingRepoPath, _pendingCommit, _pendingMode, conflicts);

        _dialogs.ShowConflicts(conflictsVm);

        if (conflictsVm.Concluded)
        {
            if (!string.IsNullOrWhiteSpace(conflictsVm.ResultBranch))
                PushableBranch = conflictsVm.ResultBranch;

            PendingManualResolution = false;
            ClearPending();
            StatusMessage = conflictsVm.ResultMessage + " Você já pode enviar o branch (push).";
            _dialogs.ShowInfo("Replicação concluída", conflictsVm.ResultMessage);
        }
        else
        {
            StatusMessage = "Resolução de conflitos pendente. Use 'Resolver conflitos...' para retomar.";
        }
    }

    private void ClearPending()
    {
        _pendingCommit = null;
        _pendingRepoPath = string.Empty;
    }

    // ----- Infra -----

    private async Task RunBusyAsync(string status, Func<Task> action)
    {
        try
        {
            IsBusy = true;
            StatusMessage = status;
            await action();
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Erro", ex.Message);
            StatusMessage = "Ocorreu um erro: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnGitCommandExecuted(GitCommandResult result)
    {
        void Append()
        {
            _log.AppendLine($"$ {result.Command}");
            var output = result.CombinedOutput;
            if (!string.IsNullOrWhiteSpace(output))
                _log.AppendLine(output);
            _log.AppendLine();
            OnPropertyChanged(nameof(Log));
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Append();
        else
            dispatcher.Invoke(Append);
    }
}

/// <summary>Opção de estratégia exibida na UI.</summary>
public sealed record ReplicationModeOption(ReplicationMode Mode, string Display)
{
    public override string ToString() => Display;
}
