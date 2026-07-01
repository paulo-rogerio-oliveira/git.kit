namespace GitKit.Core.Models;

/// <summary>
/// Estratégia usada para replicar um commit de um branch em outro.
/// </summary>
public enum ReplicationMode
{
    /// <summary>Replica o commit via <c>git cherry-pick</c>.</summary>
    CherryPick,

    /// <summary>Replica aplicando o diff do commit (<c>git diff | git apply</c>).</summary>
    DiffIntegration
}
