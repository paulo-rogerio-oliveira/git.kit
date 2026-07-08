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
        var gh = new FakeGitHubService();
        var tortoise = new FakeTortoise();
        var dialogs = new FakeDialogs();
        var coordinator = new ConflictResolutionCoordinator(git, tortoise, dialogs);
        var db = new GitKit.Core.Data.AppDatabase(
            Path.Combine(Path.GetTempPath(), $"gitkit-shots-{Guid.NewGuid():N}.db"));
        var agent = new FakeAgentRunner();
        var devops = new FakeDevOpsService();
        var jobs = new BackgroundJobService(
            git, gh, new WorkspaceService(), new FakeRepositoryCache(), dialogs, coordinator, agent, db);

        // ---- 1. Tela principal (Replicar branch) populada ----
        var shell = new ShellViewModel(git, gh, jobs, dialogs, new FakeRecentRepositories(), devops, db, agent);
        shell.BranchReplication.RepositorySource = "https://github.com/exemplo/when.it";
        shell.BranchReplication.LoadCommand.Execute(null);
        Pump();

        shell.BranchReplication.SourceBranch = shell.BranchReplication.Branches
            .FirstOrDefault(b => b == "feature/1") ?? shell.BranchReplication.Branches.FirstOrDefault() ?? string.Empty;
        Pump();
        if (shell.BranchReplication.Reviewers.Count > 0)
            shell.BranchReplication.Reviewers[0].IsSelected = true;
        shell.SelectedTabIndex = 1; // aba "Replicar branch"
        Pump();

        var mainWindow = new MainWindow { DataContext = shell };
        Capture(mainWindow, Path.Combine(outputDirectory, "tela-principal.png"), 1000, 700);

        // ---- 2. Formulário de conflitos ----
        var conflicts = new[]
        {
            new ConflictEntry("src/Program.cs", "UU", "Ambos modificaram"),
            new ConflictEntry(".gitignore", "UU", "Ambos modificaram"),
            new ConflictEntry("README.md", "AA", "Adicionado por ambos"),
        };
        var commit = new GitCommit("1967a47add14b4d69728e5d8268b50993bbfc4b7", "Paulo Oliveira", DateTimeOffset.Now, "commit dev");
        var conflictJob = new JobViewModel(JobKind.CherryPick, "Cherry-pick (2) → 'develop'")
        {
            Mode = ReplicationMode.CherryPick,
            RepositoryUrl = "https://github.com/exemplo/when.it",
            TargetBranch = "develop",
        };
        conflictJob.WorkingDir = @"C:\gtk\3";
        conflictJob.PendingCommit = commit;
        conflictJob.MarkConflict("Conflito no cherry-pick de 1967a47 (1/2)."); // fase de resolução
        var conflictsVm = new ConflictsViewModel(git, coordinator, dialogs, jobs, conflictJob, conflicts);
        conflictsVm.Items[0].IsResolved = true; // mostra status misto (Resolvido/Pendente)
        Pump();

        var conflictsWindow = new ConflictsWindow { DataContext = conflictsVm };
        Capture(conflictsWindow, Path.Combine(outputDirectory, "tela-conflitos.png"), 840, 560);

        // ---- 3. Aba User Stories populada ----
        shell.UserStories.LoadCommand.Execute(null);
        Pump();
        shell.UserStories.SelectedStory = shell.UserStories.Stories.FirstOrDefault();
        shell.UserStories.TechnicalPlan = "1. Criar o provider de SSO\n2. Ajustar a tela de login\n3. Cobrir com testes";
        shell.SelectedTabIndex = 3; // aba "User Stories"
        Pump();
        var usWindow = new MainWindow { DataContext = shell };
        Capture(usWindow, Path.Combine(outputDirectory, "tela-user-stories.png"), 1040, 720);

        // ---- 4. Popup do agente ----
        var agentJob = new JobViewModel(JobKind.AgentTask, "US #1234 — Permitir login com SSO")
        {
            RepositoryUrl = "https://github.com/exemplo/when.it",
            NewBranch = "us/1234",
            WorkItemId = 1234,
            WorkItemTitle = "Permitir login com SSO",
        };
        agentJob.WorkingDir = @"C:\gtk\5";
        agentJob.AppendTranscript("sistema", "US #1234: Permitir login com SSO\nPlanejamento técnico: criar o provider de SSO...");
        agentJob.AppendTranscript("agente", "Entendi o plano. Implementei o provider e ajustei a tela de login.\nDúvida: o SSO deve valer também para a API pública?");
        agentJob.MarkWaitingInput("O agente aguarda sua interação — abra o processo e responda.");
        agentJob.ProposedCommitMessage = "Ab#1234 implementa login com SSO corporativo";
        var agentVm = new AgentSessionViewModel(jobs, agentJob);
        Pump();
        var agentWindow = new AgentWindow { DataContext = agentVm };
        Capture(agentWindow, Path.Combine(outputDirectory, "tela-agente.png"), 900, 640);
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

        public Task<GitCommandResult> CloneAsync(string url, string dest, IProgress<string>? progress = null, CancellationToken ct = default)
        {
            CommandExecuted?.Invoke(new GitCommandResult($"clone {url} {dest}", 0, "Cloning into ...\ndone.", string.Empty));
            return Task.FromResult(Ok());
        }

        public Task<GitCommandResult> CloneMirrorAsync(string url, string cacheDirectory, IProgress<string>? progress = null, CancellationToken ct = default)
            => Task.FromResult(Ok());

        public Task<GitCommandResult> UpdateCacheAsync(string cacheDirectory, IProgress<string>? progress = null, CancellationToken ct = default)
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

        public Task<IReadOnlyList<GitCommit>> SearchCommitsAsync(string repositoryPath, string branch, string term, int max = 100, CancellationToken ct = default)
            => GetCommitsAsync(repositoryPath, branch, max, 0, ct);

        public Task<IReadOnlyList<GitCommit>> GetCommitsAsync(string repositoryPath, string branch, int max = 100, int skip = 0, CancellationToken ct = default)
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

        public Task<GitCommandResult> ConfigureGhCredentialHelperAsync(string repositoryPath, string host, CancellationToken ct = default) => Task.FromResult(Ok());

        public Task<GitCommandResult> CheckoutNewBranchAsync(string repositoryPath, string branch, CancellationToken ct = default) => Task.FromResult(Ok());

        public Task<GitCommandResult> CommitAllAsync(string repositoryPath, string message, CancellationToken ct = default) => Task.FromResult(Ok());

        public Task<IReadOnlyList<string>> GetConflictedFilesAsync(string repositoryPath, CancellationToken ct = default)
            // Mantém estes como "ainda em conflito"; o refresh marca os demais como resolvidos.
            => Task.FromResult((IReadOnlyList<string>)new List<string> { ".gitignore", "README.md" });

        public Task<IReadOnlyList<ConflictEntry>> GetConflictsAsync(string repositoryPath, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<ConflictEntry>)new List<ConflictEntry>());

        public Task<string?> ExtractConflictStageAsync(string repositoryPath, string file, int stage, string destinationPath, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<GitCommit>> ListCommitsBetweenAsync(string repositoryPath, string baseRef, string sourceRef, CancellationToken ct = default)
            => GetCommitsAsync(repositoryPath, sourceRef, 100, 0, ct);

        public Task<BranchReplicationResult> ReplicateBranchAsync(string repositoryPath, IReadOnlyList<GitCommit> commits, int startIndex, string newBranch, string baseRef, ReplicationMode mode, CancellationToken ct = default)
            => Task.FromResult(BranchReplicationResult.Ok("ok", repositoryPath, newBranch, commits.Count));
    }

    private sealed class FakeGitHubService : IGitHubService
    {
        public event Action<GitCommandResult>? CommandExecuted;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            CommandExecuted?.Invoke(new GitCommandResult("gh --version", 0, "gh version 2.x", string.Empty));
            return Task.FromResult(true);
        }
        public Task<bool> IsAuthenticatedAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<IReadOnlyList<string>> ListAccessibleReposAsync(CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<string>)new[] { "exemplo/when.it", "exemplo/outro-projeto", "acme/backend" });

        public Task<IReadOnlyList<string>> ListBranchesAsync(GitHubRepo repo, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<string>)new[] { "main", "develop", "feature/1", "feature/2", "hotfix/login" });

        public Task<IReadOnlyList<GitCommit>> ListCommitsAsync(GitHubRepo repo, string branch, int max = 100, CancellationToken ct = default)
        {
            var now = DateTimeOffset.Now;
            IReadOnlyList<GitCommit> list = new List<GitCommit>
            {
                new("1967a47add14b4d69728e5d8268b50993bbfc4b7", "Paulo Oliveira", now.AddHours(-2), "commit dev"),
                new("a50c3e09f1c0b2d3e4f5a6b7c8d9e0f1a2b3c4d5", "Paulo Oliveira", now.AddDays(-1), "ajuste no parser de configuração"),
                new("ba05fdc1234567890abcdef1234567890abcdef12", "Maria Souza", now.AddDays(-2), "adiciona testes de integração"),
            };
            return Task.FromResult(list);
        }

        public Task<IReadOnlyList<GitHubUser>> ListCollaboratorsAsync(GitHubRepo repo, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<GitHubUser>)new[]
            {
                new GitHubUser("maria-souza", "Maria Souza"),
                new GitHubUser("joao-lima", "João Lima"),
                new GitHubUser("ana-costa", "Ana Costa"),
            });

        public Task<GitCommandResult> CreatePullRequestAsync(string repositoryPath, string baseBranch, string headBranch, string title, string body, IReadOnlyList<string> reviewers, CancellationToken ct = default)
            => Task.FromResult(new GitCommandResult("gh pr create", 0, "https://github.com/exemplo/when.it/pull/42", string.Empty));
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
        public Task<string?> EnsureCacheAsync(string repositoryUrl, IProgress<string>? progress = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    private sealed class FakeRecentRepositories : IRecentRepositories
    {
        public IReadOnlyList<string> GetAll() => new[]
        {
            "git@github.com:exemplo/when.it.git",
            "https://github.com/exemplo/outro-projeto.git",
            @"C:\projetos\meu-repo-local",
        };

        public void Add(string source) { }
    }

    private sealed class FakeAgentRunner : IAgentRunner
    {
        public void Configure(AgentOptions options) { }
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<GitCommandResult> RunTurnAsync(string workingDirectory, string prompt, bool continueSession, Action<string>? onOutputLine = null, CancellationToken ct = default)
            => Task.FromResult(new GitCommandResult("claude -p", 0, "Plano compreendido. Alterações aplicadas.", string.Empty));
    }

    private sealed class FakeDevOpsService : IAzureDevOpsService
    {
        public bool IsConfigured => true;
        public void Configure(DevOpsSettings settings) { }

        public Task<IReadOnlyList<WorkItem>> GetMyTaskUserStoriesAsync(CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<WorkItem>)new[]
            {
                new WorkItem(1234, "User Story", "Permitir login com SSO", "Active", "Paulo Oliveira",
                    "Como usuário quero entrar com SSO corporativo."),
            });

        public Task<IReadOnlyList<WorkItem>> GetUnassignedUserStoriesAsync(CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<WorkItem>)new[]
            {
                new WorkItem(1240, "User Story", "Exportar relatório em PDF", "New", string.Empty,
                    "Como gestor quero exportar o relatório mensal em PDF."),
            });

        public Task<int> AssignToMeAsync(int userStoryId, string taskTitle, CancellationToken ct = default)
            => Task.FromResult(9001);
    }

    private sealed class FakeDialogs : IDialogService
    {
        public bool ShowConflicts(ConflictsViewModel viewModel) => false;
        public void ShowAgent(AgentSessionViewModel viewModel) { }
        public string? PickFile(string title, string filter) => null;
        public void ShowInfo(string title, string message) { }
        public void ShowError(string title, string message) { }
        public bool Confirm(string title, string message) => true;
    }
}
