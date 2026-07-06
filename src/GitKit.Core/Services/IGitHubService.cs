using GitKit.Core.Models;

namespace GitKit.Core.Services;

/// <summary>
/// Operações contra o GitHub via <c>gh</c> (GitHub CLI). Permite consultar
/// branches/commits/colaboradores <b>sem clonar</b> o repositório e criar a Pull
/// Request ao final da replicação.
/// </summary>
public interface IGitHubService : IGitCommandSource
{
    /// <summary>Verifica se o <c>gh</c> está disponível no PATH.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Verifica se há uma sessão autenticada no <c>gh</c>.</summary>
    Task<bool> IsAuthenticatedAsync(CancellationToken ct = default);

    /// <summary>
    /// Lista os repositórios a que o usuário autenticado tem acesso (próprios,
    /// como colaborador e via organização), no formato <c>owner/repo</c>.
    /// </summary>
    Task<IReadOnlyList<string>> ListAccessibleReposAsync(CancellationToken ct = default);

    /// <summary>Lista os nomes dos branches do repositório (sem clonar).</summary>
    Task<IReadOnlyList<string>> ListBranchesAsync(GitHubRepo repo, CancellationToken ct = default);

    /// <summary>Lista os commits mais recentes de um branch (sem clonar).</summary>
    Task<IReadOnlyList<GitCommit>> ListCommitsAsync(GitHubRepo repo, string branch, int max = 100, CancellationToken ct = default);

    /// <summary>Lista os colaboradores do repositório (candidatos a revisores).</summary>
    Task<IReadOnlyList<GitHubUser>> ListCollaboratorsAsync(GitHubRepo repo, CancellationToken ct = default);

    /// <summary>
    /// Cria uma Pull Request de <paramref name="headBranch"/> para
    /// <paramref name="baseBranch"/>, opcionalmente adicionando revisores. Executa
    /// <c>gh pr create</c> dentro de <paramref name="repositoryPath"/> (cujo
    /// <c>origin</c> aponta ao remote real). Retorna a URL da PR criada ou uma
    /// falha detalhada.
    /// </summary>
    Task<GitCommandResult> CreatePullRequestAsync(
        string repositoryPath,
        string baseBranch,
        string headBranch,
        string title,
        string body,
        IReadOnlyList<string> reviewers,
        CancellationToken ct = default);
}
