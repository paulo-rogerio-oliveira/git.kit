namespace GitKit.Core.Models;

/// <summary>
/// Resultado da replicação de <b>todos</b> os commits de um branch (um range) para
/// um novo branch. Diferente de <see cref="ReplicationResult"/> (que trata um único
/// commit), carrega o ponto de retomada quando o range para num conflito no meio.
/// </summary>
public sealed class BranchReplicationResult
{
    private BranchReplicationResult(
        ReplicationStatus status, string message, string workingDirectory,
        string branchName, GitCommit? pendingCommit, int nextIndex, int replicated)
    {
        Status = status;
        Message = message;
        WorkingDirectory = workingDirectory;
        BranchName = branchName;
        PendingCommit = pendingCommit;
        NextIndex = nextIndex;
        Replicated = replicated;
    }

    public ReplicationStatus Status { get; }

    public string Message { get; }

    public string WorkingDirectory { get; }

    /// <summary>Nome do novo branch (local) preparado, alvo dos cherry-picks.</summary>
    public string BranchName { get; }

    /// <summary>
    /// Commit que gerou o conflito e aguarda resolução manual (apenas quando
    /// <see cref="Status"/> == <see cref="ReplicationStatus.ConflictsNeedManualResolution"/>).
    /// </summary>
    public GitCommit? PendingCommit { get; }

    /// <summary>
    /// Índice, na lista original, do commit em conflito. Após a resolução manual
    /// desse commit, a replicação deve ser retomada a partir de <c>NextIndex + 1</c>.
    /// </summary>
    public int NextIndex { get; }

    /// <summary>Quantidade de commits efetivamente aplicados/pulados nesta passagem.</summary>
    public int Replicated { get; }

    public bool RequiresManualResolution => Status == ReplicationStatus.ConflictsNeedManualResolution;

    public static BranchReplicationResult Ok(string message, string workingDirectory, string branchName, int replicated)
        => new(ReplicationStatus.Success, message, workingDirectory, branchName, null, -1, replicated);

    public static BranchReplicationResult Conflicts(
        string message, string workingDirectory, string branchName, GitCommit pendingCommit, int index, int replicated)
        => new(ReplicationStatus.ConflictsNeedManualResolution, message, workingDirectory, branchName, pendingCommit, index, replicated);

    public static BranchReplicationResult Failure(string message, string workingDirectory, string branchName, int replicated)
        => new(ReplicationStatus.Failed, message, workingDirectory, branchName, null, -1, replicated);
}
