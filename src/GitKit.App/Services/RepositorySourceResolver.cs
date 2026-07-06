using System.IO;
using GitKit.Core.Models;
using GitKit.Core.Services;

namespace GitKit.App.Services;

/// <summary>
/// Origem de repositório resolvida: uma URL GitHub ou um caminho local de repositório git.
/// </summary>
/// <param name="IsLocal">True quando a origem é um caminho local.</param>
/// <param name="CloneSource">O que será clonado (a URL ou o caminho local).</param>
/// <param name="RemoteUrl">O remote REAL para push/PR (URL de origem do repo local, ou a própria URL).</param>
/// <param name="GhRepo">Repositório GitHub (para colaboradores/PR), ou null se não for GitHub.</param>
public sealed record ResolvedRepositorySource(bool IsLocal, string CloneSource, string RemoteUrl, GitHubRepo? GhRepo);

/// <summary>
/// Classifica a origem informada no campo de repositório: caminho local de um repo git
/// ou URL GitHub. Para caminho local, descobre o remote real (origin) e, se for GitHub,
/// o <see cref="GitHubRepo"/> correspondente.
/// </summary>
public sealed class RepositorySourceResolver
{
    private readonly IGitService _git;

    public RepositorySourceResolver(IGitService git) => _git = git;

    public async Task<ResolvedRepositorySource?> ResolveAsync(string source, CancellationToken ct = default)
    {
        source = (source ?? string.Empty).Trim();
        if (source.Length == 0)
            return null;

        if (LooksLikeLocalPath(source))
        {
            if (!await _git.IsRepositoryAsync(source, ct))
                return null; // caminho existe, mas não é um repositório git

            var remoteUrl = await _git.GetRemoteUrlAsync(source, ct);
            GitHubRepo.TryParse(remoteUrl, out var ghRepoLocal);
            // Sem remote: o push retorna ao próprio repositório local.
            var effectiveRemote = string.IsNullOrWhiteSpace(remoteUrl) ? source : remoteUrl;
            return new ResolvedRepositorySource(true, source, effectiveRemote, ghRepoLocal);
        }

        // URL: precisa ser um repositório GitHub.
        if (!GitHubRepo.TryParse(source, out var ghRepo))
            return null;

        return new ResolvedRepositorySource(false, source, source, ghRepo);
    }

    private static bool LooksLikeLocalPath(string source)
    {
        try
        {
            return Directory.Exists(source);
        }
        catch
        {
            return false;
        }
    }
}
