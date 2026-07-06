using System.Collections.ObjectModel;
using System.Windows;
using GitKit.App.ViewModels;
using GitKit.Core.Models;
using GitKit.Core.Services;

namespace GitKit.App.Services;

/// <summary>
/// Gerencia os processos (jobs) que rodam em background: clonagem + replicação de
/// branch (com criação de PR) e cherry-pick de commits selecionados. Os jobs são
/// mantidos apenas em memória durante a sessão. Em caso de conflito, o job fica
/// "recuperável" — o usuário resolve no fluxo de conflitos atual e o job retoma.
/// </summary>
public sealed class BackgroundJobService
{
    private readonly IGitService _git;
    private readonly IGitHubService _gh;
    private readonly WorkspaceService _workspace;
    private readonly IRepositoryCache _cache;
    private readonly IDialogService _dialogs;
    private readonly ConflictResolutionCoordinator _coordinator;

    public BackgroundJobService(
        IGitService git,
        IGitHubService gh,
        WorkspaceService workspace,
        IRepositoryCache cache,
        IDialogService dialogs,
        ConflictResolutionCoordinator coordinator)
    {
        _git = git;
        _gh = gh;
        _workspace = workspace;
        _cache = cache;
        _dialogs = dialogs;
        _coordinator = coordinator;
    }

    /// <summary>Processos em background (mais recentes no topo), para a aba "Processos".</summary>
    public ObservableCollection<JobViewModel> Jobs { get; } = new();

    // ----- Início dos jobs -----

    public JobViewModel StartBranchReplication(
        ResolvedRepositorySource source, string sourceBranch, string newBranch, string? targetBranch,
        IReadOnlyList<string> reviewers, string prTitle, string prBody)
    {
        var targetLabel = string.IsNullOrWhiteSpace(targetBranch) ? "(auto)" : targetBranch;
        var job = new JobViewModel(JobKind.BranchReplication, $"Replicar '{sourceBranch}' → '{targetLabel}'")
        {
            RepositoryUrl = source.CloneSource,
            RemoteUrl = source.RemoteUrl,
            IsLocalSource = source.IsLocal,
            GhRepo = source.GhRepo,
            SourceBranch = sourceBranch,
            NewBranch = string.IsNullOrWhiteSpace(newBranch) ? $"{sourceBranch}-replicado" : newBranch.Trim(),
            RequestedTarget = string.IsNullOrWhiteSpace(targetBranch) ? null : targetBranch.Trim(),
            Mode = ReplicationMode.CherryPick,
            Reviewers = reviewers,
            PrTitle = string.IsNullOrWhiteSpace(prTitle) ? $"Replicação de {sourceBranch}" : prTitle,
            PrBody = prBody ?? string.Empty,
        };

        Register(job);
        _ = Task.Run(() => RunBranchReplicationAsync(job));
        return job;
    }

    public JobViewModel StartCherryPick(
        ResolvedRepositorySource source, string sourceBranch, string targetBranch,
        IReadOnlyList<GitCommit> commits)
    {
        var job = new JobViewModel(JobKind.CherryPick, $"Cherry-pick ({commits.Count}) → '{targetBranch}'")
        {
            RepositoryUrl = source.CloneSource,
            RemoteUrl = source.RemoteUrl,
            IsLocalSource = source.IsLocal,
            GhRepo = source.GhRepo,
            SourceBranch = sourceBranch,
            TargetBranch = targetBranch.Trim(),
            Mode = ReplicationMode.CherryPick,
            Commits = commits,
        };

        Register(job);
        _ = Task.Run(() => RunCherryPickAsync(job));
        return job;
    }

    private void Register(JobViewModel job)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            Jobs.Insert(0, job);
        else
            dispatcher.Invoke(() => Jobs.Insert(0, job));
    }

    public void Cancel(JobViewModel job)
    {
        try { job.Cts.Cancel(); } catch { /* já finalizado */ }
    }

    // ----- Replicação de branch -----

    private async Task RunBranchReplicationAsync(JobViewModel job)
    {
        var ct = job.Cts.Token;
        try
        {
            var repoPath = await CloneWorkingCopyAsync(job, ct);
            if (repoPath is null)
                return; // já marcado como falho

            job.WorkingDir = repoPath;

            // O destino (branch da PR) é escolhido pelo usuário na tela.
            if (string.IsNullOrWhiteSpace(job.RequestedTarget))
            {
                job.MarkFailed("Nenhum branch de destino informado para a replicação.");
                return;
            }

            var targetBase = $"origin/{job.RequestedTarget}";
            job.TargetBaseRef = targetBase;
            job.TargetBranchName = StripOrigin(targetBase);
            job.Title = $"Replicar '{job.SourceBranch}' → '{job.TargetBranchName}'";

            var sourceRef = $"origin/{job.SourceBranch}";
            job.Report($"Listando commits de '{job.SourceBranch}' ausentes em '{job.TargetBranchName}'...");
            var commits = await _git.ListCommitsBetweenAsync(repoPath, targetBase, sourceRef, ct);
            if (commits.Count == 0)
            {
                job.MarkFailed($"Nenhum commit a replicar: '{job.SourceBranch}' já está contido em '{job.TargetBranchName}'.");
                return;
            }

            job.Commits = commits;
            // Caminho feliz (sem conflito): aplica e já envia (push + PR). Em conflito,
            // para e aguarda a recuperação — o envio será acionado pelo usuário na tela.
            if (await ApplyBranchAsync(job, startIndex: 0, ct) == ApplyOutcome.ReadyToFinish)
                await FinishBranchReplicationAsync(job, ct);
        }
        catch (OperationCanceledException)
        {
            job.MarkCanceled("Processo cancelado.");
        }
        catch (Exception ex)
        {
            job.MarkFailed("Erro inesperado: " + ex.Message);
        }
    }

    // Aplica (cherry-pick) os commits do range a partir de startIndex, SEM enviar.
    private async Task<ApplyOutcome> ApplyBranchAsync(JobViewModel job, int startIndex, CancellationToken ct)
    {
        job.MarkRunning($"Replicando {job.Commits.Count} commit(s) em '{job.NewBranch}'...");
        var result = await _git.ReplicateBranchAsync(
            job.WorkingDir, job.Commits, startIndex, job.NewBranch, job.TargetBaseRef!, job.Mode, ct);

        switch (result.Status)
        {
            case ReplicationStatus.ConflictsNeedManualResolution:
                job.PendingCommit = result.PendingCommit;
                job.NextIndex = result.NextIndex;
                job.MarkConflict(result.Message);
                return ApplyOutcome.Conflict;

            case ReplicationStatus.Failed:
                job.MarkFailed(result.Message);
                return ApplyOutcome.Failed;

            default:
                return ApplyOutcome.ReadyToFinish;
        }
    }

    private async Task FinishBranchReplicationAsync(JobViewModel job, CancellationToken ct)
    {
        job.MarkRunning($"Enviando '{job.NewBranch}' para {job.PushUpstream}...");
        var push = await _git.PushAsync(job.WorkingDir, job.NewBranch, ct: ct);
        if (!push.Success)
        {
            job.MarkFailed($"Falha ao enviar o branch para {job.PushUpstream}:\n{push.CombinedOutput}");
            return;
        }

        // Sem repositório GitHub (repo local sem remote GitHub): não há como abrir PR.
        if (job.GhRepo is null)
        {
            job.MarkCompleted(
                $"Branch '{job.NewBranch}' enviado para {job.PushUpstream}. " +
                "Sem repositório GitHub: a Pull Request não foi criada.");
            return;
        }

        job.Report($"Criando Pull Request para '{job.TargetBranchName}'...");
        var pr = await _gh.CreatePullRequestAsync(
            job.WorkingDir, job.TargetBranchName!, job.NewBranch, job.PrTitle, job.PrBody, job.Reviewers, ct);

        if (pr.Success)
        {
            var url = ExtractUrl(pr.StandardOutput);
            var suffix = string.IsNullOrWhiteSpace(url) ? string.Empty : $" {url}";
            job.MarkCompleted($"PR criada para '{job.TargetBranchName}' a partir de '{job.NewBranch}'.{suffix}", url);
        }
        else
        {
            job.MarkFailed(
                $"Branch '{job.NewBranch}' enviado, mas a criação da PR falhou:\n{pr.CombinedOutput}");
        }
    }

    // ----- Cherry-pick -----

    private async Task RunCherryPickAsync(JobViewModel job)
    {
        var ct = job.Cts.Token;
        try
        {
            var repoPath = await CloneWorkingCopyAsync(job, ct);
            if (repoPath is null)
                return;

            job.WorkingDir = repoPath;
            if (await ApplyCherryPickAsync(job, startIndex: 0, ct) == ApplyOutcome.ReadyToFinish)
                await FinishCherryPickAsync(job, ct);
        }
        catch (OperationCanceledException)
        {
            job.MarkCanceled("Processo cancelado.");
        }
        catch (Exception ex)
        {
            job.MarkFailed("Erro inesperado: " + ex.Message);
        }
    }

    // Aplica os commits selecionados no branch alvo, SEM enviar.
    private async Task<ApplyOutcome> ApplyCherryPickAsync(JobViewModel job, int startIndex, CancellationToken ct)
    {
        var total = job.Commits.Count;
        for (var i = startIndex; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var commit = job.Commits[i];
            job.MarkRunning($"Cherry-pick {commit.ShortHash} ({i + 1}/{total}) em '{job.TargetBranch}'...");

            var result = await _git.ReplicateCommitAsync(job.WorkingDir, commit, job.TargetBranch, job.Mode, ct);

            // Guarda o nome do branch LOCAL preparado (é o ref correto para o push).
            if (!string.IsNullOrWhiteSpace(result.BranchName))
            {
                job.TargetBranchName = result.BranchName;
                job.NotifyPushTargetChanged();
            }

            switch (result.Status)
            {
                case ReplicationStatus.Success:
                case ReplicationStatus.AlreadyApplied:
                    break;

                case ReplicationStatus.ConflictsNeedManualResolution:
                    job.PendingCommit = commit;
                    job.NextIndex = i;
                    job.MarkConflict(
                        $"Conflito no cherry-pick de {commit.ShortHash} ({i + 1}/{total}). " +
                        "Resolva para retomar os commits restantes.");
                    return ApplyOutcome.Conflict;

                default:
                    job.MarkFailed($"Falha no cherry-pick de {commit.ShortHash}:\n{result.Message}");
                    return ApplyOutcome.Failed;
            }
        }

        return ApplyOutcome.ReadyToFinish;
    }

    private async Task FinishCherryPickAsync(JobViewModel job, CancellationToken ct)
    {
        var total = job.Commits.Count;
        var branch = string.IsNullOrWhiteSpace(job.TargetBranchName) ? job.TargetBranch : job.TargetBranchName!;
        job.MarkRunning($"Enviando '{branch}' para {job.PushUpstream}...");
        var push = await _git.PushAsync(job.WorkingDir, branch, ct: ct);
        if (push.Success)
            job.MarkCompleted(
                $"{total} commit(s) replicado(s) e enviado(s) para {job.PushUpstream}.\n{push.CombinedOutput}".TrimEnd());
        else
            job.MarkFailed($"Cherry-pick concluído, mas o push para {job.PushUpstream} falhou:\n{push.CombinedOutput}");
    }

    // ----- Recuperação / retomada -----

    /// <summary>Resultado de um passo de recuperação dirigido pela tela de resolução.</summary>
    public enum RecoverStep { ReadyToPush, MoreConflicts, Failed }

    private enum ApplyOutcome { Conflict, ReadyToFinish, Failed }

    /// <summary>
    /// Abre o popup do processo (a qualquer momento) mostrando seus dados. Se estiver
    /// em conflito, conduz a resolução; se pronto, o envio (push/PR); caso contrário,
    /// apenas exibe status/detalhe do processo.
    /// </summary>
    public async Task RecoverAsync(JobViewModel job)
    {
        var conflicts = job.Status == JobStatus.NeedsConflictResolution && !string.IsNullOrWhiteSpace(job.WorkingDir)
            ? await _git.GetConflictsAsync(job.WorkingDir)
            : (IReadOnlyList<ConflictEntry>)Array.Empty<ConflictEntry>();

        var vm = new ConflictsViewModel(_git, _coordinator, _dialogs, this, job, conflicts);
        _dialogs.ShowConflicts(vm);
    }

    /// <summary>
    /// Após o usuário resolver o conflito do commit corrente (já commitado), aplica
    /// os commits restantes SEM enviar. Indica se está pronto para push, se surgiu
    /// um novo conflito, ou se falhou.
    /// </summary>
    public async Task<RecoverStep> ContinueAfterResolutionAsync(JobViewModel job)
    {
        var ct = job.Cts.Token;
        try
        {
            var startIndex = job.NextIndex + 1;
            job.PendingCommit = null;

            var outcome = job.Kind == JobKind.BranchReplication
                ? await ApplyBranchAsync(job, startIndex, ct)
                : await ApplyCherryPickAsync(job, startIndex, ct);

            switch (outcome)
            {
                case ApplyOutcome.ReadyToFinish:
                    job.MarkReadyToPush("Conflitos resolvidos. Pronto para enviar (push).");
                    return RecoverStep.ReadyToPush;
                case ApplyOutcome.Conflict:
                    return RecoverStep.MoreConflicts;
                default:
                    return RecoverStep.Failed;
            }
        }
        catch (OperationCanceledException)
        {
            job.MarkCanceled("Processo cancelado.");
            return RecoverStep.Failed;
        }
        catch (Exception ex)
        {
            job.MarkFailed("Erro ao retomar: " + ex.Message);
            return RecoverStep.Failed;
        }
    }

    /// <summary>
    /// Envia (push) o branch resultante e, na replicação de branch, cria a PR.
    /// Retorna true se o job terminou como concluído.
    /// </summary>
    public async Task<bool> FinishAsync(JobViewModel job)
    {
        var ct = job.Cts.Token;
        try
        {
            if (job.Kind == JobKind.BranchReplication)
                await FinishBranchReplicationAsync(job, ct);
            else
                await FinishCherryPickAsync(job, ct);
        }
        catch (OperationCanceledException)
        {
            job.MarkCanceled("Processo cancelado.");
        }
        catch (Exception ex)
        {
            job.MarkFailed("Erro ao enviar: " + ex.Message);
        }

        return job.Status == JobStatus.Completed;
    }

    // ----- Clonagem (via cache, reaponta origin para o remote real) -----

    private async Task<string?> CloneWorkingCopyAsync(JobViewModel job, CancellationToken ct)
    {
        var cloneSource = job.RepositoryUrl;   // URL GitHub ou caminho local
        var remoteUrl = string.IsNullOrWhiteSpace(job.RemoteUrl) ? cloneSource : job.RemoteUrl;
        var target = _workspace.CreateWorkFolder();

        var cacheProgress = new Progress<string>(line => job.Report($"Cache: {line}"));
        var cloneProgress = new Progress<string>(line => job.Report($"Clone: {line}"));

        // O cache (espelho) só faz sentido para URLs remotas. Repos locais são clonados direto.
        string? cachePath = null;
        if (!job.IsLocalSource)
        {
            job.Report("Atualizando cache local do repositório...");
            cachePath = await _cache.EnsureCacheAsync(cloneSource, cacheProgress, ct);
        }

        job.Report(cachePath is not null ? "Clonando a partir do cache local..." : "Clonando repositório...");
        var result = await _git.CloneAsync(cachePath ?? cloneSource, target, cloneProgress, ct);
        if (!result.Success)
        {
            job.MarkFailed("Falha ao clonar o repositório:\n" + result.CombinedOutput);
            return null;
        }

        // Reaponta o origin para o remote REAL: o push (cherry-pick e replicação de branch)
        // precisa ir ao repositório remoto (não ao espelho de cache nem ao caminho local).
        if (!string.IsNullOrWhiteSpace(remoteUrl))
        {
            var setRemote = await _git.SetRemoteUrlAsync(target, remoteUrl, ct);
            if (!setRemote.Success)
            {
                job.MarkFailed(
                    "Não foi possível apontar o 'origin' para o remote real; o push não iria ao destino esperado.\n"
                    + setRemote.CombinedOutput);
                return null;
            }
        }

        // Garante que o 'git push' (HTTPS GitHub) autentique com o token do gh já logado.
        if (job.GhRepo is not null)
            await _git.ConfigureGhCredentialHelperAsync(target, job.GhRepo.Host, ct);

        return target;
    }

    // ----- Utilidades -----

    private static string StripOrigin(string reference)
        => reference.StartsWith("origin/", StringComparison.Ordinal) ? reference["origin/".Length..] : reference;

    // A saída do 'gh pr create' contém a URL da PR; pega a primeira linha http(s).
    private static string ExtractUrl(string output)
        => output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
}
