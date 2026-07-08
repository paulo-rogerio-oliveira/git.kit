using System.Text;
using GitKit.Core.Models;

namespace GitKit.Core.Services;

/// <summary>
/// Implementação de <see cref="IAgentRunner"/> sobre o Claude Code CLI:
/// <c>claude -p [--continue] --permission-mode &lt;modo&gt;</c>, com o prompt via
/// stdin (evita qualquer escaping de linha de comando). A UI é apenas uma casca —
/// todo o trabalho acontece no CLI, dentro da pasta de trabalho do processo.
/// </summary>
public sealed class ClaudeAgentRunner : IAgentRunner
{
    private readonly IProcessRunner _runner;
    private AgentOptions _options = new();

    public ClaudeAgentRunner(IProcessRunner runner) => _runner = runner;

    public void Configure(AgentOptions options) => _options = options;

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _runner.RunAsync(Executable(), "--version", null, null, ct).ConfigureAwait(false);
            return result.Success;
        }
        catch
        {
            return false;
        }
    }

    public Task<GitCommandResult> RunTurnAsync(
        string workingDirectory,
        string prompt,
        bool continueSession,
        Action<string>? onOutputLine = null,
        CancellationToken ct = default)
    {
        var args = new StringBuilder("-p");
        if (continueSession)
            args.Append(" --continue");
        if (!string.IsNullOrWhiteSpace(_options.PermissionMode))
            args.Append(" --permission-mode ").Append(_options.PermissionMode.Trim());
        if (!string.IsNullOrWhiteSpace(_options.ExtraArgs))
            args.Append(' ').Append(_options.ExtraArgs.Trim());

        // Prompt via stdin: o 'claude -p' lê o prompt do stdin quando não há argumento.
        return _runner.RunAsync(Executable(), args.ToString(), workingDirectory, onOutputLine, ct, prompt);
    }

    private string Executable()
        => string.IsNullOrWhiteSpace(_options.Executable) ? "claude" : _options.Executable.Trim();
}
