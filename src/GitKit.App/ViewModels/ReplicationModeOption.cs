using GitKit.Core.Models;

namespace GitKit.App.ViewModels;

/// <summary>Opção de estratégia de replicação exibida na UI.</summary>
public sealed record ReplicationModeOption(ReplicationMode Mode, string Display)
{
    public override string ToString() => Display;

    /// <summary>Opções padrão (cherry-pick e integração de diff).</summary>
    public static IReadOnlyList<ReplicationModeOption> Defaults { get; } = new[]
    {
        new ReplicationModeOption(ReplicationMode.CherryPick, "Cherry-pick (replica o commit)"),
        new ReplicationModeOption(ReplicationMode.DiffIntegration, "Integração de diff (aplica as diferenças)"),
    };
}
