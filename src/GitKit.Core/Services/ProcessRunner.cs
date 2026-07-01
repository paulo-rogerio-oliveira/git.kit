using System.Diagnostics;
using System.Text;
using GitKit.Core.Models;

namespace GitKit.Core.Services;

/// <summary>
/// Implementação de <see cref="IProcessRunner"/> usando <see cref="Process"/>.
/// Lê stdout e stderr de forma assíncrona (evitando deadlock de buffer) e trata
/// <c>\r</c> como fim de linha, para que atualizações de progresso do git
/// (ex.: "Receiving objects: 45%") sejam entregues em tempo real via callback.
/// No cancelamento, o processo e toda a sua árvore são finalizados — nenhum git
/// órfão continua rodando em background.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<GitCommandResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Garante mensagens do git em inglês/UTF-8 para parsing previsível.
        startInfo.Environment["LC_ALL"] = "C.UTF-8";
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // Editor "no-op": evita que comandos como cherry-pick --continue abram um
        // editor interativo e travem; usam a mensagem já preparada.
        startInfo.Environment["GIT_EDITOR"] = "true";

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new GitCommandResult(
                $"{fileName} {arguments}", -1, string.Empty,
                $"Falha ao iniciar o processo: {ex.Message}");
        }

        var stdoutTask = ReadStreamAsync(process.StandardOutput, stdout, onOutputLine);
        var stderrTask = ReadStreamAsync(process.StandardError, stderr, onOutputLine);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* já finalizou */ }
            try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); } catch { /* melhor-esforço */ }
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        return new GitCommandResult(
            $"{fileName} {arguments}",
            process.ExitCode,
            stdout.ToString(),
            stderr.ToString());
    }

    /// <summary>
    /// Lê o stream quebrando linhas em <c>\n</c>, <c>\r</c> ou <c>\r\n</c>.
    /// Linhas vazias são preservadas na captura (mensagens de commit dependem
    /// da linha em branco entre assunto e corpo), mas não geram callback.
    /// </summary>
    private static async Task ReadStreamAsync(StreamReader reader, StringBuilder sink, Action<string>? onLine)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        var lastWasCr = false;
        int read;

        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c == '\n')
                {
                    // O \n de um par \r\n: a linha já foi emitida no \r.
                    if (!lastWasCr)
                        FlushLine(line, sink, onLine);
                    lastWasCr = false;
                }
                else if (c == '\r')
                {
                    FlushLine(line, sink, onLine);
                    lastWasCr = true;
                }
                else
                {
                    line.Append(c);
                    lastWasCr = false;
                }
            }
        }

        if (line.Length > 0)
            FlushLine(line, sink, onLine);
    }

    private static void FlushLine(StringBuilder line, StringBuilder sink, Action<string>? onLine)
    {
        var text = line.ToString();
        line.Clear();
        sink.AppendLine(text);
        if (text.Length > 0)
            onLine?.Invoke(text);
    }
}
