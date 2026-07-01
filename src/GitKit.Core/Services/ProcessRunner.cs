using System.Diagnostics;
using System.Text;
using GitKit.Core.Models;

namespace GitKit.Core.Services;

/// <summary>
/// Implementação de <see cref="IProcessRunner"/> usando <see cref="Process"/>.
/// Captura stdout e stderr de forma assíncrona evitando deadlock de buffer.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<GitCommandResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
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

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderr.AppendLine(e.Data);
        };

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

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new GitCommandResult(
            $"{fileName} {arguments}",
            process.ExitCode,
            stdout.ToString(),
            stderr.ToString());
    }
}
