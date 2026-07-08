using System.Media;
using System.Text;
using System.Windows;
using GitKit.App.MVVM;
using GitKit.App.Services;
using GitKit.Core.Data;
using GitKit.Core.Models;
using GitKit.Core.Services;

namespace GitKit.App.ViewModels;

/// <summary>
/// ViewModel raiz (shell) da janela principal. Hospeda as abas Início, Replicar
/// branch, Cherry-pick, User Stories, Processos e Log, coordena a navegação, as
/// notificações de jobs e concentra o log unificado dos comandos git/gh.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    // Índices das abas do TabControl da MainWindow.
    private const int TabHome = 0;
    private const int TabBranch = 1;
    private const int TabCherryPick = 2;
    private const int TabUserStories = 3;
    private const int TabProcesses = 4;

    private readonly BackgroundJobService _jobs;
    private readonly GitCommandLogger? _fileLogger;
    private readonly StringBuilder _log = new();

    public ShellViewModel(
        IGitService git,
        IGitHubService gh,
        BackgroundJobService jobs,
        IDialogService dialogs,
        IRecentRepositories recent,
        IAzureDevOpsService devops,
        AppDatabase db,
        IAgentRunner agent,
        GitCommandLogger? fileLogger = null)
    {
        _jobs = jobs;
        _fileLogger = fileLogger;

        git.CommandExecuted += OnCommandExecuted;
        gh.CommandExecuted += OnCommandExecuted;

        Home = new HomeViewModel(
            goToBranchReplication: () => SelectedTabIndex = TabBranch,
            goToCherryPick: () => SelectedTabIndex = TabCherryPick,
            goToUserStories: () => SelectedTabIndex = TabUserStories);
        BranchReplication = new BranchReplicationViewModel(git, gh, jobs, dialogs, recent, () => SelectedTabIndex = TabProcesses);
        CherryPick = new CherryPickViewModel(git, gh, jobs, dialogs, recent, () => SelectedTabIndex = TabProcesses);
        UserStories = new UserStoriesViewModel(devops, db, agent, jobs, dialogs, git, recent, () => SelectedTabIndex = TabProcesses);
        Processes = new ProcessesViewModel(jobs, RecoverJobAsync);

        // Notificação: o agente aguarda interação → som + mensagem de status, para o
        // dev saber que precisa abrir o processo na aba Processos e responder.
        _jobs.JobNeedsAttention += OnJobNeedsAttention;

        // Melhor-esforço em background: popula os combos com os repositórios a que
        // o usuário tem acesso (somados aos recentes já carregados).
        _ = LoadAccessibleRepositoriesAsync(gh);
    }

    private void OnJobNeedsAttention(JobViewModel job)
    {
        void Notify()
        {
            try { SystemSounds.Exclamation.Play(); } catch { /* som é best-effort */ }
            AttentionMessage = $"⚠ {job.Title}: {job.StatusText} (aba Processos)";
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Notify();
        else
            dispatcher.Invoke(Notify);
    }

    // Última notificação de atenção (exibida na barra de status do shell).
    private string _attentionMessage = string.Empty;
    public string AttentionMessage
    {
        get => _attentionMessage;
        private set => SetProperty(ref _attentionMessage, value);
    }

    private async Task LoadAccessibleRepositoriesAsync(IGitHubService gh)
    {
        try
        {
            if (!await gh.IsAvailableAsync())
                return;

            var repos = await gh.ListAccessibleReposAsync();
            if (repos.Count == 0)
                return;

            // owner/repo → URL https, uniforme com os recentes e com o parser de URL.
            var urls = repos.Select(slug => $"https://github.com/{slug}").ToArray();

            void Apply()
            {
                BranchReplication.SetAccessibleRepositories(urls);
                CherryPick.SetAccessibleRepositories(urls);
                UserStories.SetAccessibleRepositories(urls);
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
                Apply();
            else
                dispatcher.Invoke(Apply);
        }
        catch
        {
            // Sem gh/sem auth: mantém apenas os recentes.
        }
    }

    public HomeViewModel Home { get; }
    public BranchReplicationViewModel BranchReplication { get; }
    public CherryPickViewModel CherryPick { get; }
    public UserStoriesViewModel UserStories { get; }
    public ProcessesViewModel Processes { get; }

    private int _selectedTabIndex = TabHome;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    // ----- Recuperação de um processo à tela principal -----

    private Task RecoverJobAsync(JobViewModel job)
        // Abre a tela do processo (popup) sem mudar de aba — a resolução e o envio
        // (push/PR) acontecem no próprio popup.
        => _jobs.RecoverAsync(job);

    // ----- Log unificado (git + gh) -----

    private bool _isLogEnabled;
    public bool IsLogEnabled
    {
        get => _isLogEnabled;
        set
        {
            if (SetProperty(ref _isLogEnabled, value) && _fileLogger is not null)
                _fileLogger.Enabled = value;
        }
    }

    public string Log => _log.ToString();

    private void OnCommandExecuted(GitCommandResult result)
    {
        if (!IsLogEnabled)
            return;

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
