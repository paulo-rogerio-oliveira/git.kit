using System.IO;
using System.Linq;
using System.Windows;
using GitKit.App.Screenshots;
using GitKit.App.Services;
using GitKit.App.ViewModels;
using GitKit.App.Views;
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

        // Registra os comandos git em uma pasta de logs separada (uma por sessão).
        var gitLogger = new GitCommandLogger();
        gitLogger.Attach(gitService);

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

        var tortoise = new TortoiseGitLauncher();
        var dialogs = new DialogService();
        var coordinator = new ConflictResolutionCoordinator(gitService, tortoise, dialogs);

        var viewModel = new MainViewModel(gitService, coordinator, dialogs, workspace, repositoryCache);

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
    }
}
