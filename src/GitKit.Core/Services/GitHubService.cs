using System.Globalization;
using System.Text;
using System.Text.Json;
using GitKit.Core.Models;

namespace GitKit.Core.Services;

/// <summary>
/// Implementação de <see cref="IGitHubService"/> orquestrando o <c>gh</c> (GitHub CLI).
/// Consultas de leitura usam <c>gh api</c> com <c>--jq</c> simples (ou parse JSON em
/// C#) para evitar escapes frágeis na linha de comando.
/// </summary>
public sealed class GitHubService : IGitHubService
{
    private readonly IProcessRunner _runner;
    private readonly string _ghExecutable;

    public GitHubService(IProcessRunner runner, string ghExecutable = "gh")
    {
        _runner = runner;
        _ghExecutable = ghExecutable;
    }

    public event Action<GitCommandResult>? CommandExecuted;

    private async Task<GitCommandResult> GhAsync(string arguments, string? workingDirectory, CancellationToken ct)
    {
        var result = await _runner.RunAsync(_ghExecutable, arguments, workingDirectory, null, ct).ConfigureAwait(false);
        CommandExecuted?.Invoke(result);
        return result;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var result = await GhAsync("--version", null, ct).ConfigureAwait(false);
        return result.Success;
    }

    public async Task<bool> IsAuthenticatedAsync(CancellationToken ct = default)
    {
        var result = await GhAsync("auth status", null, ct).ConfigureAwait(false);
        return result.Success;
    }

    public async Task<IReadOnlyList<string>> ListAccessibleReposAsync(CancellationToken ct = default)
    {
        // Repositórios do usuário + onde é colaborador + via organização.
        var result = await GhAsync(
            "api \"user/repos?per_page=100&sort=updated&affiliation=owner,collaborator,organization_member\" --paginate --jq \".[].full_name\"",
            null, ct).ConfigureAwait(false);

        return result.Success ? SplitLines(result.StandardOutput) : Array.Empty<string>();
    }

    public async Task<IReadOnlyList<string>> ListBranchesAsync(GitHubRepo repo, CancellationToken ct = default)
    {
        // per_page=100 cobre a grande maioria dos repositórios; jq simples (sem aspas
        // internas) é seguro no parsing de argumentos do Windows.
        var result = await GhAsync(
            $"api \"repos/{repo.Owner}/{repo.Name}/branches?per_page=100\" --paginate --jq \".[].name\"",
            null, ct).ConfigureAwait(false);

        return result.Success ? SplitLines(result.StandardOutput) : Array.Empty<string>();
    }

    public async Task<IReadOnlyList<GitHubUser>> ListCollaboratorsAsync(GitHubRepo repo, CancellationToken ct = default)
    {
        var result = await GhAsync(
            $"api \"repos/{repo.Owner}/{repo.Name}/collaborators?per_page=100\" --paginate --jq \".[].login\"",
            null, ct).ConfigureAwait(false);

        if (!result.Success)
            return Array.Empty<GitHubUser>();

        return SplitLines(result.StandardOutput)
            .Select(login => new GitHubUser(login))
            .ToArray();
    }

    public async Task<IReadOnlyList<GitCommit>> ListCommitsAsync(GitHubRepo repo, string branch, int max = 100, CancellationToken ct = default)
    {
        // A mensagem de commit pode conter aspas/quebras de linha; em vez de escapar
        // um jq complexo, buscamos o JSON cru e parseamos em C#.
        var perPage = Math.Clamp(max, 1, 100);
        var result = await GhAsync(
            $"api \"repos/{repo.Owner}/{repo.Name}/commits?sha={Uri.EscapeDataString(branch)}&per_page={perPage}\"",
            null, ct).ConfigureAwait(false);

        if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
            return Array.Empty<GitCommit>();

        return ParseCommitsJson(result.StandardOutput);
    }

    public async Task<GitCommandResult> CreatePullRequestAsync(
        string repositoryPath,
        string baseBranch,
        string headBranch,
        string title,
        string body,
        IReadOnlyList<string> reviewers,
        CancellationToken ct = default)
    {
        // Corpo (possivelmente multilinha) vai por arquivo para evitar qualquer escape.
        var bodyFile = Path.Combine(Path.GetTempPath(), $"gitkit-pr-{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(bodyFile, body ?? string.Empty, new UTF8Encoding(false), ct).ConfigureAwait(false);

            var sb = new StringBuilder("pr create");
            sb.Append(" --base \"").Append(baseBranch).Append('"');
            sb.Append(" --head \"").Append(headBranch).Append('"');
            sb.Append(" --title \"").Append(EscapeArg(title)).Append('"');
            sb.Append(" --body-file \"").Append(bodyFile).Append('"');
            foreach (var reviewer in reviewers ?? Array.Empty<string>())
            {
                var login = reviewer.Trim();
                if (login.Length > 0)
                    sb.Append(" --reviewer \"").Append(login).Append('"');
            }

            return await GhAsync(sb.ToString(), repositoryPath, ct).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(bodyFile)) File.Delete(bodyFile); } catch { /* limpeza best-effort */ }
        }
    }

    // Escapa aspas para uso dentro de um argumento entre aspas (regra do CommandLineToArgvW).
    private static string EscapeArg(string value) => (value ?? string.Empty).Replace("\"", "\\\"");

    private static IReadOnlyList<string> SplitLines(string output)
        => output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0)
            .ToArray();

    private static IReadOnlyList<GitCommit> ParseCommitsJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<GitCommit>();

            var commits = new List<GitCommit>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                var sha = element.TryGetProperty("sha", out var shaEl) ? shaEl.GetString() ?? string.Empty : string.Empty;
                if (sha.Length == 0)
                    continue;

                var commitEl = element.GetProperty("commit");
                var authorEl = commitEl.GetProperty("author");
                var author = authorEl.TryGetProperty("name", out var an) ? an.GetString() ?? string.Empty : string.Empty;
                var dateText = authorEl.TryGetProperty("date", out var ad) ? ad.GetString() : null;
                var date = DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? parsed
                    : DateTimeOffset.MinValue;

                var message = commitEl.TryGetProperty("message", out var msgEl) ? msgEl.GetString() ?? string.Empty : string.Empty;
                var subject = message.Split('\n', 2)[0].TrimEnd('\r');

                commits.Add(new GitCommit(sha, author, date, subject));
            }

            return commits;
        }
        catch (JsonException)
        {
            return Array.Empty<GitCommit>();
        }
    }
}
