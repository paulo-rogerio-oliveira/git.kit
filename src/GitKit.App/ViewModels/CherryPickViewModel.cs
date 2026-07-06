using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using GitKit.App.MVVM;
using GitKit.App.Services;
using GitKit.Core.Models;
using GitKit.Core.Services;

namespace GitKit.App.ViewModels;

/// <summary>
/// Tela "Cherry-pick": lista branches e commits via <c>gh</c> (SEM clonar), o
/// usuário seleciona um ou mais commits e o branch alvo, e a replicação roda em
/// background (clone + cherry-pick dos selecionados + push).
/// </summary>
public sealed class CherryPickViewModel : ObservableObject
{
    private const int CommitPageSize = 100;

    private readonly IGitService _git;
    private readonly IGitHubService _gh;
    private readonly BackgroundJobService _jobs;
    private readonly IDialogService _dialogs;
    private readonly IRecentRepositories _recent;
    private readonly Action _goToProcesses;
    private readonly RepositorySourceResolver _resolver;

    private ResolvedRepositorySource? _source;

    public CherryPickViewModel(
        IGitService git, IGitHubService gh, BackgroundJobService jobs, IDialogService dialogs,
        IRecentRepositories recent, Action goToProcesses)
    {
        _git = git;
        _gh = gh;
        _jobs = jobs;
        _dialogs = dialogs;
        _recent = recent;
        _goToProcesses = goToProcesses;
        _resolver = new RepositorySourceResolver(git);

        // Views filtráveis (por texto digitado) para os combos editáveis. Origem e
        // destino usam views INDEPENDENTES sobre Branches para não interferirem entre si.
        RepositoriesView = new CollectionViewSource { Source = Repositories }.View;
        RepositoriesView.Filter = o => MatchesText(o, RepositorySource);
        SourceBranchesView = new CollectionViewSource { Source = Branches }.View;
        SourceBranchesView.Filter = o => MatchesText(o, SourceBranch);
        TargetBranchesView = new CollectionViewSource { Source = Branches }.View;
        TargetBranchesView.Filter = o => MatchesText(o, TargetBranch);
        CommitsView = new CollectionViewSource { Source = Commits }.View;
        CommitsView.Filter = FilterCommit;

        LoadBranchesCommand = new AsyncRelayCommand(LoadBranchesAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(RepositorySource));
        LoadCommitsCommand = new AsyncRelayCommand(LoadCommitsAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(SourceBranch));
        ReplicateCommand = new AsyncRelayCommand(ReplicateAsync, CanReplicate);

        LoadRecent();
        if (Repositories.Count > 0)
            RepositorySource = Repositories[0];
    }

    // URLs de repositórios: recentes + todos a que o usuário tem acesso.
    public ObservableCollection<string> Repositories { get; } = new();
    public ObservableCollection<string> Branches { get; } = new();
    public ObservableCollection<CommitOption> Commits { get; } = new();

    // Views filtradas exibidas nos combos editáveis e no grid de commits.
    public ICollectionView RepositoriesView { get; }
    public ICollectionView SourceBranchesView { get; }
    public ICollectionView TargetBranchesView { get; }
    public ICollectionView CommitsView { get; }

    // Filtra strings por substring do termo digitado (vazio = mostra tudo).
    private static bool MatchesText(object item, string term)
    {
        term = term?.Trim() ?? string.Empty;
        return term.Length == 0 || (item is string s && s.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    // Filtra os commits por hash, autor ou mensagem (assunto).
    private bool FilterCommit(object item)
    {
        var term = CommitFilter.Trim();
        if (term.Length == 0)
            return true;

        return item is CommitOption c
               && (c.ShortHash.Contains(term, StringComparison.OrdinalIgnoreCase)
                   || c.Author.Contains(term, StringComparison.OrdinalIgnoreCase)
                   || c.Subject.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public AsyncRelayCommand LoadBranchesCommand { get; }
    public AsyncRelayCommand LoadCommitsCommand { get; }
    public AsyncRelayCommand ReplicateCommand { get; }

    private string _repositorySource = string.Empty;
    public string RepositorySource
    {
        get => _repositorySource;
        set
        {
            if (SetProperty(ref _repositorySource, value))
                RepositoriesView?.Refresh();
        }
    }

    // Combo editável de origem: o próprio texto é o valor E o filtro (sem SelectedItem,
    // para o texto selecionado não sumir ao refiltrar a view).
    private string _sourceBranch = string.Empty;
    public string SourceBranch
    {
        get => _sourceBranch;
        set
        {
            if (SetProperty(ref _sourceBranch, value))
                SourceBranchesView?.Refresh();
        }
    }

    // Editável: branch existente selecionado ou um novo nome digitado (também é o filtro).
    private string _targetBranch = string.Empty;
    public string TargetBranch
    {
        get => _targetBranch;
        set
        {
            if (SetProperty(ref _targetBranch, value))
                TargetBranchesView?.Refresh();
        }
    }

    // Filtro do grid de commits (hash/autor/mensagem).
    private string _commitFilter = string.Empty;
    public string CommitFilter
    {
        get => _commitFilter;
        set
        {
            if (SetProperty(ref _commitFilter, value))
                CommitsView?.Refresh();
        }
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

    private string _statusMessage = "Informe a URL do repositório GitHub e carregue os branches (nada é clonado ainda).";
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Mescla os repositórios acessíveis (via gh) à lista, mantendo os recentes no topo.</summary>
    public void SetAccessibleRepositories(IEnumerable<string> urls)
    {
        foreach (var url in urls)
        {
            if (!Repositories.Contains(url))
                Repositories.Add(url);
        }
    }

    private void LoadRecent()
    {
        Repositories.Clear();
        foreach (var source in _recent.GetAll())
            Repositories.Add(source);
    }

    private async Task LoadBranchesAsync()
    {
        var input = RepositorySource.Trim();

        try
        {
            IsBusy = true;

            StatusMessage = "Identificando o repositório...";
            var source = await _resolver.ResolveAsync(input);
            if (source is null)
            {
                _dialogs.ShowError("Repositório inválido",
                    "Informe uma URL de repositório GitHub (https://github.com/owner/repo) ou o caminho de um repositório git local.");
                StatusMessage = "Repositório inválido.";
                return;
            }
            _source = source;

            IReadOnlyList<string> branches;
            if (source.IsLocal)
            {
                StatusMessage = "Lendo os branches do repositório local...";
                var local = await _git.GetBranchesAsync(source.CloneSource);
                branches = local.Where(b => !b.IsRemote).Select(b => b.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            else
            {
                if (!await _gh.IsAvailableAsync())
                {
                    _dialogs.ShowError("gh não encontrado",
                        "O GitHub CLI (gh) não foi encontrado no PATH. Instale-o e execute 'gh auth login'.");
                    StatusMessage = "GitHub CLI (gh) indisponível.";
                    return;
                }

                StatusMessage = $"Consultando branches de {source.GhRepo!.Slug} (sem clonar)...";
                branches = await _gh.ListBranchesAsync(source.GhRepo);
            }

            Branches.Clear();
            foreach (var branch in branches)
                Branches.Add(branch);

            _recent.Add(input);
            if (!Repositories.Contains(input))
                Repositories.Insert(0, input);
            StatusMessage = $"{Branches.Count} branch(es). Selecione a origem e carregue os commits.";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Falha ao consultar o repositório", ex.Message);
            StatusMessage = "Falha ao consultar o repositório.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadCommitsAsync()
    {
        if (_source is null || string.IsNullOrWhiteSpace(SourceBranch))
            return;

        try
        {
            IsBusy = true;
            StatusMessage = $"Consultando commits de '{SourceBranch}'...";
            var commits = _source.IsLocal
                ? await _git.GetCommitsAsync(_source.CloneSource, SourceBranch.Trim(), CommitPageSize)
                : await _gh.ListCommitsAsync(_source.GhRepo!, SourceBranch.Trim(), CommitPageSize);

            Commits.Clear();
            foreach (var commit in commits)
                Commits.Add(new CommitOption(commit));

            StatusMessage = $"{Commits.Count} commit(s). Marque os que deseja replicar e informe o branch alvo.";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Falha ao consultar commits", ex.Message);
            StatusMessage = "Falha ao consultar commits.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanReplicate()
        => !IsBusy && _source is not null
           && !string.IsNullOrWhiteSpace(SourceBranch)
           && !string.IsNullOrWhiteSpace(TargetBranch)
           && Commits.Any(c => c.IsSelected);

    private Task ReplicateAsync()
    {
        if (_source is null || string.IsNullOrWhiteSpace(SourceBranch))
            return Task.CompletedTask;

        // Ordem cronológica (mais antigo → mais novo) para o cherry-pick sequencial.
        var selected = Commits
            .Where(c => c.IsSelected)
            .Select(c => c.Commit)
            .OrderBy(c => c.Date)
            .ToArray();

        if (selected.Length == 0)
            return Task.CompletedTask;

        var job = _jobs.StartCherryPick(
            _source, SourceBranch.Trim(), TargetBranch.Trim(), selected);

        StatusMessage = $"Processo iniciado em background: {job.Title}.";
        _goToProcesses();
        return Task.CompletedTask;
    }
}
