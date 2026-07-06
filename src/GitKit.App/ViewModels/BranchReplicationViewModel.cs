using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using GitKit.App.MVVM;
using GitKit.App.Services;
using GitKit.Core.Models;
using GitKit.Core.Services;

namespace GitKit.App.ViewModels;

/// <summary>
/// Tela "Replicar branch": preenche os parâmetros (repositório, branch de origem,
/// destino, revisores) SEM clonar — a lista de branches/colaboradores vem do
/// <c>gh</c>. O usuário escolhe o destino (branch da PR, ex.: develop/master); o
/// nome do novo branch recebe o sufixo do destino e o título da PR é preenchido
/// com um padrão. Ao acionar "Replicar", inicia um processo em background
/// (clone + cherry-pick de todos os commits + push + PR).
/// </summary>
public sealed class BranchReplicationViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly IGitHubService _gh;
    private readonly BackgroundJobService _jobs;
    private readonly IDialogService _dialogs;
    private readonly IRecentRepositories _recent;
    private readonly Action _goToProcesses;
    private readonly RepositorySourceResolver _resolver;

    private ResolvedRepositorySource? _source;

    // Controle de preenchimento automático (não sobrescreve edições manuais).
    private bool _branchNameAuto = true;
    private string _lastAutoBranchName = string.Empty;
    private bool _prTitleAuto = true;
    private string _lastAutoPrTitle = string.Empty;

    public BranchReplicationViewModel(
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

        // Views filtráveis (por texto digitado) para os combos editáveis.
        RepositoriesView = new CollectionViewSource { Source = Repositories }.View;
        RepositoriesView.Filter = o => MatchesText(o, RepositorySource);
        BranchesView = new CollectionViewSource { Source = Branches }.View;
        BranchesView.Filter = o => MatchesText(o, SourceBranch);
        DestinationView = new CollectionViewSource { Source = Branches }.View;
        DestinationView.Filter = o => MatchesText(o, Destination);

        LoadCommand = new AsyncRelayCommand(LoadAsync, () => !IsBusy && !string.IsNullOrWhiteSpace(RepositorySource));
        ReplicateCommand = new AsyncRelayCommand(ReplicateAsync, CanReplicate);

        LoadRecent();
        if (Repositories.Count > 0)
            RepositorySource = Repositories[0];
    }

    // URLs de repositórios: recentes + todos a que o usuário tem acesso.
    public ObservableCollection<string> Repositories { get; } = new();
    public ObservableCollection<string> Branches { get; } = new();
    public ObservableCollection<ReviewerOption> Reviewers { get; } = new();

    // Views filtradas exibidas nos combos editáveis.
    public ICollectionView RepositoriesView { get; }
    public ICollectionView BranchesView { get; }
    public ICollectionView DestinationView { get; }

    // Filtra strings por substring do termo digitado (vazio = mostra tudo).
    private static bool MatchesText(object item, string term)
    {
        term = term?.Trim() ?? string.Empty;
        return term.Length == 0 || (item is string s && s.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public AsyncRelayCommand LoadCommand { get; }
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

    // Combo editável de origem: o próprio texto é o valor E o filtro.
    private string _sourceBranch = string.Empty;
    public string SourceBranch
    {
        get => _sourceBranch;
        set
        {
            if (SetProperty(ref _sourceBranch, value))
            {
                BranchesView?.Refresh();
                UpdateDerivedFields();
            }
        }
    }

    // Destino da replicação = branch para o qual a Pull Request será aberta
    // (develop/master). Combo editável/filtrável sobre a lista de branches.
    private string _destination = string.Empty;
    public string Destination
    {
        get => _destination;
        set
        {
            if (SetProperty(ref _destination, value))
            {
                DestinationView?.Refresh();
                UpdateDerivedFields();
            }
        }
    }

    private string _newBranchName = string.Empty;
    public string NewBranchName
    {
        get => _newBranchName;
        set
        {
            if (SetProperty(ref _newBranchName, value) && value != _lastAutoBranchName)
                _branchNameAuto = false; // usuário editou manualmente
        }
    }

    private string _prTitle = string.Empty;
    public string PrTitle
    {
        get => _prTitle;
        set
        {
            if (SetProperty(ref _prTitle, value) && value != _lastAutoPrTitle)
                _prTitleAuto = false; // usuário editou manualmente
        }
    }

    private string _prBody = string.Empty;
    public string PrBody
    {
        get => _prBody;
        set => SetProperty(ref _prBody, value);
    }

    // Preenche automaticamente o nome do novo branch (com prefixo do destino) e o
    // título da PR (descrição padrão), enquanto o usuário não os editar manualmente.
    private void UpdateDerivedFields()
    {
        var source = SourceBranch.Trim();
        var dest = Destination.Trim();
        if (source.Length == 0 || dest.Length == 0)
            return;

        if (_branchNameAuto)
        {
            _lastAutoBranchName = $"{source}-{DestinationSuffix(dest)}";
            NewBranchName = _lastAutoBranchName;
        }

        if (_prTitleAuto)
        {
            _lastAutoPrTitle = $"Replicação do branch {source} para {dest}";
            PrTitle = _lastAutoPrTitle;
        }
    }

    // Sufixo do nome do branch conforme o destino: develop → "dev"; senão o próprio
    // nome do destino (master/main/...). Ex.: "feature/1-dev", "feature/1-master".
    private static string DestinationSuffix(string destination)
    {
        var d = destination.Trim();
        return d.StartsWith("develop", StringComparison.OrdinalIgnoreCase) || d.Equals("dev", StringComparison.OrdinalIgnoreCase)
            ? "dev"
            : d;
    }

    private bool _isReady;
    public bool IsReady
    {
        get => _isReady;
        private set => SetProperty(ref _isReady, value);
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

    private string _statusMessage = "Informe a URL do repositório GitHub e clique em Carregar (nada é clonado ainda).";
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

    private async Task LoadAsync()
    {
        var input = RepositorySource.Trim();

        try
        {
            IsBusy = true;
            IsReady = false;

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
            IReadOnlyList<GitHubUser> collaborators = Array.Empty<GitHubUser>();

            if (source.IsLocal)
            {
                StatusMessage = "Lendo os branches do repositório local...";
                var local = await _git.GetBranchesAsync(source.CloneSource);
                // Apenas branches LOCAIS: são os que existirão na cópia clonada.
                branches = local.Where(b => !b.IsRemote).Select(b => b.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                // Colaboradores só quando o repo local tem remote GitHub e o gh está disponível.
                if (source.GhRepo is not null && await _gh.IsAvailableAsync())
                    collaborators = await _gh.ListCollaboratorsAsync(source.GhRepo);
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

                StatusMessage = $"Consultando branches e colaboradores de {source.GhRepo!.Slug} (sem clonar)...";
                branches = await _gh.ListBranchesAsync(source.GhRepo);
                collaborators = await _gh.ListCollaboratorsAsync(source.GhRepo);
            }

            Branches.Clear();
            foreach (var branch in branches)
                Branches.Add(branch);

            Reviewers.Clear();
            foreach (var user in collaborators)
                Reviewers.Add(new ReviewerOption(user));

            if (Branches.Count == 0)
            {
                StatusMessage = "Nenhum branch encontrado no repositório.";
                return;
            }

            // Sugere um destino padrão entre os habituais, quando existir.
            Destination = new[] { "develop", "master", "main" }
                .FirstOrDefault(c => Branches.Any(b => b.Equals(c, StringComparison.OrdinalIgnoreCase)))
                ?? string.Empty;

            _recent.Add(input);
            if (!Repositories.Contains(input))
                Repositories.Insert(0, input);
            IsReady = true;
            var reviewersNote = source.IsLocal && source.GhRepo is null
                ? "repositório local sem remote GitHub — sem revisores/PR"
                : $"{Reviewers.Count} colaborador(es)";
            StatusMessage = $"{Branches.Count} branch(es), {reviewersNote}. Escolha origem, destino e revisores.";
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

    private bool CanReplicate()
        => !IsBusy && IsReady && _source is not null
           && !string.IsNullOrWhiteSpace(SourceBranch)
           && Branches.Any(b => b.Equals(SourceBranch.Trim(), StringComparison.OrdinalIgnoreCase))
           && !string.IsNullOrWhiteSpace(Destination)
           && Branches.Any(b => b.Equals(Destination.Trim(), StringComparison.OrdinalIgnoreCase))
           && !string.IsNullOrWhiteSpace(NewBranchName);

    private Task ReplicateAsync()
    {
        if (_source is null || string.IsNullOrWhiteSpace(SourceBranch) || string.IsNullOrWhiteSpace(Destination))
            return Task.CompletedTask;

        var reviewers = Reviewers.Where(r => r.IsSelected).Select(r => r.Login).ToArray();

        var job = _jobs.StartBranchReplication(
            _source, SourceBranch.Trim(), NewBranchName.Trim(), Destination.Trim(),
            reviewers, PrTitle, PrBody);

        StatusMessage = $"Processo iniciado em background: {job.Title}.";
        _goToProcesses();
        return Task.CompletedTask;
    }
}
