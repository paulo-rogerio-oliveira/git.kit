using GitKit.Core.Models;

namespace GitKit.Core.Services;

/// <summary>
/// Fonte de comandos de linha de comando (git ou gh) que notifica cada execução,
/// permitindo que a UI registre um log unificado.
/// </summary>
public interface IGitCommandSource
{
    /// <summary>Disparado a cada comando executado (para log na UI/arquivo).</summary>
    event Action<GitCommandResult>? CommandExecuted;
}
