using System.Text;

namespace GitKit.Core.Models;

/// <summary>
/// Resultado da execução de um comando git via CLI.
/// </summary>
public sealed class GitCommandResult
{
    public GitCommandResult(string command, int exitCode, string standardOutput, string standardError)
    {
        Command = command;
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    /// <summary>Linha de comando executada (para fins de log).</summary>
    public string Command { get; }

    public int ExitCode { get; }

    public string StandardOutput { get; }

    public string StandardError { get; }

    public bool Success => ExitCode == 0;

    /// <summary>Saída combinada (stdout + stderr) já tratada.</summary>
    public string CombinedOutput
    {
        get
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(StandardOutput))
                sb.AppendLine(StandardOutput.TrimEnd());
            if (!string.IsNullOrWhiteSpace(StandardError))
                sb.AppendLine(StandardError.TrimEnd());
            return sb.ToString().TrimEnd();
        }
    }
}
