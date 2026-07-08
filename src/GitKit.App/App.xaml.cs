using System.IO;
using System.Linq;
using System.Windows;
using GitKit.App.Screenshots;
using GitKit.App.Services;
using GitKit.App.ViewModels;
using GitKit.App.Views;
using GitKit.Core.Data;
using GitKit.Core.Services;

namespace GitKit.App;

/// <summary>
/// Ponto de entrada. Faz a composição manual das dependências (poor man's DI).
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Modo de captura de telas para o manual: --screenshots <pasta>
        var idx = Array.FindIndex(e.Args, a => a.Equals("--screenshots", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            var outDir = idx + 1 < e.Args.Length ? e.Args[idx + 1] : "screenshots";
            ScreenshotGenerator.Run(outDir);
            return;
        }

        var processRunner = new ProcessRunner();
        var gitService = new GitService(processRunner);
        var gitHubService = new GitHubService(processRunner);

        // Registra os comandos git/gh em uma pasta de logs separada (uma por sessão).
        // Nasce DESABILITADO: só grava quando o usuário marca o checkbox de log
        // na UI (o ShellViewModel liga/desliga via IsLogEnabled).
        var gitLogger = new GitCommandLogger();
        gitLogger.Attach(gitService);
        gitLogger.Attach(gitHubService);

        // Limpa em background as pastas de trabalho de sessões anteriores. O snapshot
        // é tirado AGORA, antes de qualquer pasta nova ser criada nesta sessão — assim
        // as cópias criadas durante o uso atual nunca entram na limpeza.
        var workspace = new WorkspaceService();
        var toClean = workspace.SnapshotExistingFolders();
        _ = workspace.CleanupAsync(toClean);

        // Cache de repositórios remotos (espelhos locais) para clones de trabalho ágeis.
        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "git.kit", "cache");
        var repositoryCache = new RepositoryCache(gitService, cacheRoot);

        // Histórico de repositórios já utilizados (para o combo editável do repositório).
        var recentPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "git.kit", "recent-repositories.json");
        var recentRepositories = new RecentRepositories(recentPath);

        var tortoise = new TortoiseGitLauncher();
        var dialogs = new DialogService();
        var coordinator = new ConflictResolutionCoordinator(gitService, tortoise, dialogs);

        // Banco SQL embutido: settings do DevOps/agente, planos técnicos e transcripts.
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "git.kit", "gitkit.db");
        var database = new AppDatabase(dbPath);

        // Azure DevOps (REST + PAT) e agente Claude (CLI) — configurados pela aba User Stories.
        var devops = new AzureDevOpsService();
        var agentRunner = new ClaudeAgentRunner(processRunner);

        // Gerenciador dos processos em background (replicações, cherry-pick e agente).
        var jobs = new BackgroundJobService(
            gitService, gitHubService, workspace, repositoryCache, dialogs, coordinator, agentRunner, database);

        var viewModel = new ShellViewModel(
            gitService, gitHubService, jobs, dialogs, recentRepositories, devops, database, agentRunner, gitLogger);

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
    }
}
