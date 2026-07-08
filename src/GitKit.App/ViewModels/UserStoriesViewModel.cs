using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using GitKit.App.MVVM;
using GitKit.App.Services;
using GitKit.Core.Data;
using GitKit.Core.Models;
using GitKit.Core.Services;

namespace GitKit.App.ViewModels;

/// <summary>Linha da lista de User Stories (US + categoria de origem).</summary>
public sealed record StoryRow(WorkItem Story, string Category)
{
    public int Id => Story.Id;
    public string Title => Story.Title;
    public string State => Story.State;
    public string AssignedToDisplay => Story.AssignedToDisplay;
    public bool IsUnassigned => Category == UserStoriesViewModel.CategoryUnassigned;
}

/// <summary>
/// Aba "User Stories": carrega do Azure DevOps as US em que o dev tem tasks e as US
/// sem responsável; permite atribuir a US a si (criando uma Task filha ativa),
/// escrever o planejamento técnico (persistido no SQLite) e executar a task com o
/// agente (Claude CLI) num processo em background.
/// </summary>
public sealed class UserStoriesViewModel : ObservableObject
{
    public const string CategoryMine = "Minhas";
    public const string CategoryUnassigned = "Sem responsável";

    private readonly IAzureDevOpsService _devops;
    private readonly AppDatabase _db;
    private readonly IAgentRunner _agent;
    private readonly BackgroundJobService _jobs;
    private readonly IDialogService _dialogs;
    private readonly IRecentRepositories _recent;
    private readonly RepositorySourceResolver _resolver;
    private readonly Action _goToProcesses;

    public UserStoriesViewModel(
        IAzureDevOpsService devops,
        AppDatabase db,
        IAgentRunner agent,
        BackgroundJobService jobs,
        IDialogService dialogs,
        IGitService git,
        IRecentRepositories recent,
        Action goToProcesses)
    {
        _devops = devops;
        _db = db;
        _agent = agent;
        _jobs = jobs;
        _dialogs = dialogs;
        _recent = recent;
        _resolver = new RepositorySourceResolver(git);
        _goToProcesses = goToProcesses;

        RepositoriesView = new CollectionViewSource { Source = Repositories }.View;
        RepositoriesView.Filter = o => MatchesText(o, RepositorySource);

        SaveSettingsCommand = new RelayCommand(SaveSettings);
        LoadCommand = new AsyncRelayCommand(LoadAsync, () => !IsBusy);
        AssignCommand = new AsyncRelayCommand(AssignAsync,
            () => !IsBusy && SelectedStory is { IsUnassigned: true });
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, CanExecute);

        LoadSettings();
        LoadRecentRepositories();
    }

    // ----- Coleções -----

    public ObservableCollection<StoryRow> Stories { get; } = new();
    public ObservableCollection<string> Repositories { get; } = new();
    public ICollectionView RepositoriesView { get; }

    public RelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand AssignCommand { get; }
    public AsyncRelayCommand ExecuteCommand { get; }

    private static bool MatchesText(object item, string term)
    {
        term = term?.Trim() ?? string.Empty;
        return term.Length == 0 || (item is string s && s.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    // ----- Configurações (DevOps + agente), persistidas no SQLite -----

    private string _orgUrl = string.Empty;
    public string OrgUrl { get => _orgUrl; set => SetProperty(ref _orgUrl, value); }

    private string _project = string.Empty;
    public string Project { get => _project; set => SetProperty(ref _project, value); }

    private string _pat = string.Empty;
    public string Pat { get => _pat; set => SetProperty(ref _pat, value); }

    private string _userEmail = string.Empty;
    public string UserEmail { get => _userEmail; set => SetProperty(ref _userEmail, value); }

    private string _agentExecutable = "claude";
    public string AgentExecutable { get => _agentExecutable; set => SetProperty(ref _agentExecutable, value); }

    private string _agentPermissionMode = "acceptEdits";
    public string AgentPermissionMode { get => _agentPermissionMode; set => SetProperty(ref _agentPermissionMode, value); }

    private string _agentExtraArgs = string.Empty;
    public string AgentExtraArgs { get => _agentExtraArgs; set => SetProperty(ref _agentExtraArgs, value); }

    private void LoadSettings()
    {
        OrgUrl = _db.GetSetting("devops.orgUrl") ?? string.Empty;
        Project = _db.GetSetting("devops.project") ?? string.Empty;
        Pat = _db.GetSetting("devops.pat") ?? string.Empty;
        UserEmail = _db.GetSetting("devops.userEmail") ?? string.Empty;
        AgentExecutable = _db.GetSetting("agent.executable") ?? "claude";
        AgentPermissionMode = _db.GetSetting("agent.permissionMode") ?? "acceptEdits";
        AgentExtraArgs = _db.GetSetting("agent.extraArgs") ?? string.Empty;
        ApplySettings();
    }

    private void SaveSettings()
    {
        _db.SetSetting("devops.orgUrl", OrgUrl.Trim());
        _db.SetSetting("devops.project", Project.Trim());
        _db.SetSetting("devops.pat", Pat.Trim());
        _db.SetSetting("devops.userEmail", UserEmail.Trim());
        _db.SetSetting("agent.executable", AgentExecutable.Trim());
        _db.SetSetting("agent.permissionMode", AgentPermissionMode.Trim());
        _db.SetSetting("agent.extraArgs", AgentExtraArgs.Trim());
        ApplySettings();
        StatusMessage = "Configurações salvas.";
    }

    private void ApplySettings()
    {
        _devops.Configure(new DevOpsSettings(OrgUrl.Trim(), Project.Trim(), Pat.Trim(), UserEmail.Trim()));
        _agent.Configure(new AgentOptions(AgentExecutable.Trim(), AgentPermissionMode.Trim(), AgentExtraArgs.Trim()));
    }

    // ----- Estado -----

    private StoryRow? _selectedStory;
    public StoryRow? SelectedStory
    {
        get => _selectedStory;
        set
        {
            if (SetProperty(ref _selectedStory, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedDescription));
                if (value is not null)
                {
                    // Carrega o plano persistido e sugere o branch da US.
                    _technicalPlan = _db.GetPlan(value.Id) ?? string.Empty;
                    OnPropertyChanged(nameof(TechnicalPlan));
                    BranchName = $"us/{value.Id}";
                }
            }
        }
    }

    public bool HasSelection => SelectedStory is not null;
    public string SelectedDescription => SelectedStory?.Story.Description ?? string.Empty;

    // Planejamento técnico da US selecionada — persistido a cada alteração confirmada.
    private string _technicalPlan = string.Empty;
    public string TechnicalPlan
    {
        get => _technicalPlan;
        set
        {
            if (SetProperty(ref _technicalPlan, value) && SelectedStory is not null)
                _db.SavePlan(SelectedStory.Id, value);
        }
    }

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

    private string _branchName = string.Empty;
    public string BranchName
    {
        get => _branchName;
        set => SetProperty(ref _branchName, value);
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

    private string _statusMessage = "Configure o Azure DevOps e clique em Carregar.";
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Mescla os repositórios acessíveis (via gh) à lista (recentes primeiro).</summary>
    public void SetAccessibleRepositories(IEnumerable<string> urls)
    {
        foreach (var url in urls)
        {
            if (!Repositories.Contains(url))
                Repositories.Add(url);
        }
    }

    private void LoadRecentRepositories()
    {
        Repositories.Clear();
        foreach (var source in _recent.GetAll())
            Repositories.Add(source);
        if (Repositories.Count > 0)
            RepositorySource = Repositories[0];
    }

    // ----- Ações -----

    private async Task LoadAsync()
    {
        if (!_devops.IsConfigured)
        {
            _dialogs.ShowError("Azure DevOps",
                "Preencha e salve as configurações (organização, projeto, PAT e e-mail) antes de carregar.");
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Consultando o Azure DevOps...";

            var mine = await _devops.GetMyTaskUserStoriesAsync();
            var unassigned = await _devops.GetUnassignedUserStoriesAsync();

            Stories.Clear();
            foreach (var story in mine)
                Stories.Add(new StoryRow(story, CategoryMine));
            foreach (var story in unassigned)
            {
                // Uma US pode aparecer nas duas consultas; mantém só a de "Minhas".
                if (!Stories.Any(r => r.Id == story.Id))
                    Stories.Add(new StoryRow(story, CategoryUnassigned));
            }

            StatusMessage = $"{mine.Count} US com tasks suas e {unassigned.Count} sem responsável.";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Falha ao consultar o Azure DevOps", ex.Message);
            StatusMessage = "Falha ao consultar o Azure DevOps.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AssignAsync()
    {
        if (SelectedStory is not { IsUnassigned: true } row)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = $"Atribuindo a US #{row.Id} e criando a task ativa...";

            var taskId = await _devops.AssignToMeAsync(row.Id, $"Desenvolvimento — {row.Title}");

            // Reflete na lista: a US passa para "Minhas" com o dev como responsável.
            var index = Stories.IndexOf(row);
            var updated = new StoryRow(row.Story with { AssignedTo = UserEmail }, CategoryMine);
            if (index >= 0)
                Stories[index] = updated;
            SelectedStory = updated;

            StatusMessage = $"US #{row.Id} atribuída a você — task #{taskId} criada e ativada.";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Falha ao atribuir a US", ex.Message);
            StatusMessage = "Falha ao atribuir a US.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanExecute()
        => !IsBusy && SelectedStory is not null
           && !string.IsNullOrWhiteSpace(TechnicalPlan)
           && !string.IsNullOrWhiteSpace(RepositorySource)
           && !string.IsNullOrWhiteSpace(BranchName);

    private async Task ExecuteAsync()
    {
        if (SelectedStory is null)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "Identificando o repositório...";

            var source = await _resolver.ResolveAsync(RepositorySource.Trim());
            if (source is null)
            {
                _dialogs.ShowError("Repositório inválido",
                    "Informe uma URL de repositório GitHub ou o caminho de um repositório git local.");
                StatusMessage = "Repositório inválido.";
                return;
            }

            _recent.Add(RepositorySource.Trim());
            if (!Repositories.Contains(RepositorySource.Trim()))
                Repositories.Insert(0, RepositorySource.Trim());

            var job = _jobs.StartAgentTask(source, BranchName.Trim(), SelectedStory.Story, TechnicalPlan);
            StatusMessage = $"Agente iniciado em background: {job.Title}.";
            _goToProcesses();
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Falha ao iniciar o agente", ex.Message);
            StatusMessage = "Falha ao iniciar o agente.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
