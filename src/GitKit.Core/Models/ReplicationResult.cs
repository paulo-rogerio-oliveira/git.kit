namespace GitKit.Core.Models;

/// <summary>
/// Situação final de uma tentativa de replicação de commit.
/// </summary>
public enum ReplicationStatus
{
    /// <summary>Replicação aplicada e commitada automaticamente.</summary>
    Success,

    /// <summary>
    /// As mudanças do commit já existem no branch de destino — não há
    /// nada a replicar (cherry-pick vazio / diff sem alterações).
    /// </summary>
    AlreadyApplied,

    /// <summary>
    /// Houve conflito de merge que o git não conseguiu resolver
    /// automaticamente — é necessária intervenção manual (TortoiseGit).
    /// </summary>
    ConflictsNeedManualResolution,

    /// <summary>Falha não relacionada a conflito (ex.: hash inválido).</summary>
    Failed
}

/// <summary>
/// Resultado consolidado de uma operação de replicação.
/// </summary>
public sealed class ReplicationResult
{
    private ReplicationResult(ReplicationStatus status, string message, string workingDirectory, string branchName = "")
    {
        Status = status;
        Message = message;
        WorkingDirectory = workingDirectory;
        BranchName = branchName;
    }

    public ReplicationStatus Status { get; }

    public string Message { get; }

    /// <summary>Diretório de trabalho onde a operação ocorreu.</summary>
    public string WorkingDirectory { get; }

    /// <summary>
    /// Nome do branch LOCAL efetivamente preparado/destino da operação
    /// (sem o prefixo <c>origin/</c>). É o branch correto para o push.
    /// </summary>
    public string BranchName { get; }

    public bool RequiresManualResolution => Status == ReplicationStatus.ConflictsNeedManualResolution;

    /// <summary>Retorna uma cópia com uma linha extra antes da mensagem atual.</summary>
    public ReplicationResult WithPrefix(string prefix)
        => new(Status, string.IsNullOrWhiteSpace(prefix) ? Message : $"{prefix}\n{Message}", WorkingDirectory, BranchName);

    /// <summary>Retorna uma cópia associando o nome do branch local da operação.</summary>
    public ReplicationResult WithBranch(string branchName)
        => new(Status, Message, WorkingDirectory, branchName);

    public static ReplicationResult Ok(string message, string workingDirectory)
        => new(ReplicationStatus.Success, message, workingDirectory);

    public static ReplicationResult AlreadyApplied(string message, string workingDirectory)
        => new(ReplicationStatus.AlreadyApplied, message, workingDirectory);

    public static ReplicationResult Conflicts(string message, string workingDirectory)
        => new(ReplicationStatus.ConflictsNeedManualResolution, message, workingDirectory);

    public static ReplicationResult Failure(string message, string workingDirectory)
        => new(ReplicationStatus.Failed, message, workingDirectory);
}
