using GitKit.Core.Models;

namespace GitKit.Core.Services;

/// <summary>Configuração do agente de código executado via CLI.</summary>
public sealed record AgentOptions(string Executable = "claude", string PermissionMode = "acceptEdits", string ExtraArgs = "");

/// <summary>
/// Executa turnos de um agente de código via CLI dentro de uma pasta de trabalho.
/// Cada turno envia um prompt e retorna a resposta completa do agente; a conversa
/// é contínua por diretório (turnos seguintes usam <c>--continue</c>).
/// </summary>
public interface IAgentRunner
{
    /// <summary>Define executável/permissões/args usados nos próximos turnos.</summary>
    void Configure(AgentOptions options);

    /// <summary>Verifica se o CLI do agente está disponível no PATH.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Executa um turno do agente em <paramref name="workingDirectory"/> com o
    /// <paramref name="prompt"/> (enviado via stdin). Com
    /// <paramref name="continueSession"/> true, continua a conversa mais recente
    /// daquele diretório. Retorna o resultado bruto do CLI.
    /// </summary>
    Task<GitCommandResult> RunTurnAsync(
        string workingDirectory,
        string prompt,
        bool continueSession,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default);
}
