using GitKit.Core.Models;

namespace GitKit.Core.Services;

/// <summary>
/// Abstração para executar um processo externo (git) e capturar a saída.
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// Executa <paramref name="fileName"/> com os argumentos informados no
    /// diretório de trabalho dado, aguardando a finalização.
    /// </summary>
    Task<GitCommandResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default);
}
