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
    /// <paramref name="onOutputLine"/> (opcional) é invocado a cada linha de
    /// stdout/stderr assim que ela chega — inclusive linhas de progresso
    /// terminadas em <c>\r</c> (ex.: <c>git clone --progress</c>).
    /// O cancelamento MATA o processo (e sua árvore) e lança
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<GitCommandResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default);
}
