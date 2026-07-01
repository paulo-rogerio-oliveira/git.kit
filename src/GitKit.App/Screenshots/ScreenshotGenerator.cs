using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GitKit.App.Services;
using GitKit.App.ViewModels;
using GitKit.App.Views;
using GitKit.Core.Models;
using GitKit.Core.Services;

namespace GitKit.App.Screenshots;

/// <summary>
/// Gera capturas das janelas (para o manual) usando serviços falsos e dados de
/// exemplo. Ativado por <c>--screenshots &lt;pasta&gt;</c>. Não afeta o uso normal.
/// </summary>
public static class ScreenshotGenerator
{
    public static void Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        // Evita que fechar uma janela dispare o shutdown da aplicação.
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            Generate(outputDirectory);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(outputDirectory, "_erro.txt"), ex.ToString());
        }
        finally
        {
            Application.Current.Shutdown();
        }
    }

    private static void Generate(string outputDirectory)
    {
        var git = new FakeGitService();
        var tortoise = new FakeTortoise();
        var dialogs = new FakeDialogs();
        var coordinator = new ConflictResolutionCoordinator(git, tortoise, dialogs);

        // ---- 1. Tela principal populada ----
        var main = new MainViewModel(git, coordinator, dialogs, new WorkspaceService(), new FakeRepositoryCache());
        main.RepositorySource = "git@github.com:exemplo/when.it.git";
        main.StartCommand.Execute(null);
        Pump();

        main.SelectedSourceBranch = main.Branches.FirstOrDefault(b => b.Name == "develop")
                                    ?? main.Branches.FirstOrDefault();
        Pump();
        main.DestinationBranch = "feature/nova-feature-dev";
        main.SelectedCommit = main.Commits.FirstOrDefault();
        Pump();

        var mainWindow = new MainWindow { DataContext = main };
        Capture(mainWindow, Path.Combine(outputDirectory, "tela-principal.png"), 1000, 860);

        // ---- 2. Formulário de conflitos ----
        var conflicts = new[]
        {
            new ConflictEntry("src/Program.cs", "UU", "Ambos modificaram"),
            new ConflictEntry(".gitignore", "UU", "Ambos modificaram"),
            new ConflictEntry("README.md", "AA", "Adicionado por ambos"),
        };
        var commit = new GitCommit("1967a47add14b4d69728e5d8268b50993bbfc4b7", "Paulo Oliveira", DateTimeOffset.Now, "commit dev");
        var conflictsVm = new ConflictsViewModel(
            git, coordinator, dialogs,
            @"C:\Users\...\AppData\Local\Temp\git.kit\when.it",
            commit, ReplicationMode.CherryPick, conflicts);
        conflictsVm.Items[0].IsResolved = true; // mostra status misto (Resolvido/Pendente)
        Pump();

        var conflictsWindow = new ConflictsWindow { DataContext = conflictsVm };
        Capture(conflictsWindow, Path.Combine(outputDirectory, "tela-conflitos.png"), 840, 480);
    }

    private static void Capture(Window window, string path, int width, int height)
    {
        window.Width = width;
        window.Height = height;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.ShowInTaskbar = false;
        window.Left = -12000;   // fora da tela para não piscar
        window.Top = -12000;
        window.Show();
        Pump();

        window.UpdateLayout();
        Pump();

        var w = (int)Math.Ceiling(window.ActualWidth);
        var h = (int)Math.Ceiling(window.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path))
            encoder.Save(fs);

        window.Close();
    }

    // Processa a fila do Dispatcher para concluir as operações assíncronas dos fakes.
    private static void Pump(int times = 12)
    {
        for (var i = 0; i < times; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(25);
        }
    }

    // ---------------- Serviços falsos (somente para captura) ----------------

    private sealed class FakeGitService : IGitService
    {
        public event Action<GitCommandResult>? CommandExecuted;

        private static GitCommandResult Ok(string cmd = "git") => new(cmd, 0, string.Empty, string.Empty);

        public Task<bool> IsGitAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> IsRepositoryAsync(string path, CancellationToken ct = default) => Task.FromResult(true);

        public Task<GitCommandResult> CloneAsync(string url, string dest, CancellationToken ct = default)
        {
            CommandExecuted?.Invoke(new GitCommandResult($"clone {url} {dest}", 0, "Cloning into ...\ndone.", string.Empty));
            return Task.FromResult(Ok());
        }

        public Task<GitCommandResult> CloneMirrorAsync(string url, string cacheDirectory, CancellationToken ct = default)
            => Task.FromResult(Ok());

        public Task<GitCommandResult> UpdateCacheAsync(string cacheDirectory, CancellationToken ct = default)
            => Task.FromResult(Ok());

        public Task<GitCommandResult> FetchAsync(string repositoryPath, CancellationToken ct = default) => Task.FromResult(Ok());

        public Task<string> GetRemoteUrlAsync(string repositoryPath, CancellationToken ct = default)
            => Task.FromResult("git@github.com:exemplo/when.it.git");

        public Task<GitCommandResult> SetRemoteUrlAsync(string repositoryPath, string remoteUrl, CancellationToken ct = default)
            => Task.FromResult(Ok());

        public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repositoryPath, CancellationToken ct = default)
        {
            IReadOnlyList<GitBranch> list = new List<GitBranch>
            {
                new("main", false, true),
                new("develop", false, false),
                new("feature/1", false, false),
                new("origin/develop", true, false),
                new("origin/main", true, false),
                new("origin/feature/2", true, false),
            };
            return Task.FromResult(list);
        }

        public Task<IReadOnlyList<GitCommit>> GetCommitsAsync(string repositoryPath, string branch, int max = 100, CancellationToken ct = default)
        {
            var now = DateTimeOffset.Now;
            IReadOnlyList<GitCommit> list = new List<GitCommit>
            {
                new("1967a47add14b4d69728e5d8268b50993bbfc4b7", "Paulo Oliveira", now.AddHours(-2), "commit dev"),
                new("a50c3e09f1c0b2d3e4f5a6b7c8d9e0f1a2b3c4d5", "Paulo Oliveira", now.AddDays(-1), "ajuste no parser de configuração"),
                new("ba05fdc1234567890abcdef1234567890abcdef12", "Maria Souza", now.AddDays(-2), "adiciona testes de integração"),
                new("54b1c63abcdef1234567890abcdef1234567890ab", "João Lima", now.AddDays(-3), "corrige validação de branch"),
                new("c0ffee00abcdef1234567890abcdef1234567890ab", "Maria Souza", now.AddDays(-5), "refatora serviço de replicação"),
            };
            return Task.FromResult(list);
        }

        public Task<ReplicationResult> ReplicateCommitAsync(string repositoryPath, GitCommit commit, string destinationBranch, ReplicationMode mode, CancellationToken ct = default)
            => Task.FromResult(ReplicationResult.Ok("ok", repositoryPath));

        public Task<GitCommandResult> AbortReplicationAsync(string repositoryPath, ReplicationMode mode, CancellationToken ct = default) => Task.FromResult(Ok());

        public Task<ReplicationResult> ContinueReplicationAsync(string repositoryPath, GitCommit commit, ReplicationMode mode, CancellationToken ct = default)
            => Task.FromResult(ReplicationResult.Ok("ok", repositoryPath));

        public Task<GitCommandResult> PushAsync(string repositoryPath, string branch, bool setUpstream = true, CancellationToken ct = default) => Task.FromResult(Ok());

        public Task<IReadOnlyList<string>> GetConflictedFilesAsync(string repositoryPath, CancellationToken ct = default)
            // Mantém estes como "ainda em conflito"; o refresh marca os demais como resolvidos.
            => Task.FromResult((IReadOnlyList<string>)new List<string> { ".gitignore", "README.md" });

        public Task<IReadOnlyList<ConflictEntry>> GetConflictsAsync(string repositoryPath, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<ConflictEntry>)new List<ConflictEntry>());

        public Task<string?> ExtractConflictStageAsync(string repositoryPath, string file, int stage, string destinationPath, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class FakeTortoise : ITortoiseGitLauncher
    {
        public bool IsAvailable => true;
        public string? ExecutablePath => @"C:\Program Files\TortoiseGit\bin\TortoiseGitProc.exe";
        public bool IsMergeToolAvailable => true;
        public string? MergeToolPath => @"C:\Program Files\TortoiseGit\bin\TortoiseGitMerge.exe";
        public bool TrySetExecutable(string path) => true;
        public bool OpenResolveDialog(string repositoryPath) => true;
        public bool OpenConflictEditor(string repositoryPath, string conflictedFile) => true;
        public bool OpenMerge(string b, string m, string t, string merged, string? bn = null, string? mn = null, string? tn = null, string? mgn = null) => true;
        public bool OpenCommitDialog(string repositoryPath) => true;
    }

    private sealed class FakeRepositoryCache : IRepositoryCache
    {
        // Sem cache na captura: o fluxo cai no clone direto (o FakeGitService responde).
        public Task<string?> EnsureCacheAsync(string repositoryUrl, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class FakeDialogs : IDialogService
    {
        public bool ShowConflicts(ConflictsViewModel viewModel) => false;
        public string? PickFile(string title, string filter) => null;
        public void ShowInfo(string title, string message) { }
        public void ShowError(string title, string message) { }
        public bool Confirm(string title, string message) => true;
    }
}
