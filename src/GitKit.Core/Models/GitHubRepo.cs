using System.Diagnostics.CodeAnalysis;

namespace GitKit.Core.Models;

/// <summary>
/// Identificação de um repositório GitHub (host/owner/name) extraída de uma URL
/// de clone. Usada para acionar o <c>gh</c> (<c>-R owner/repo</c>) sem precisar
/// clonar o repositório antes.
/// </summary>
public sealed record GitHubRepo(string Host, string Owner, string Name)
{
    /// <summary>Forma <c>owner/repo</c> aceita pelo argumento <c>-R</c> do gh.</summary>
    public string Slug => $"{Owner}/{Name}";

    public override string ToString() => Slug;

    /// <summary>
    /// Tenta interpretar uma URL de repositório GitHub nos formatos comuns:
    /// <c>https://github.com/owner/repo(.git)</c>, <c>ssh://git@github.com/owner/repo.git</c>
    /// e o formato "scp" <c>git@github.com:owner/repo.git</c>.
    /// </summary>
    public static bool TryParse(string? url, [NotNullWhen(true)] out GitHubRepo? repo)
    {
        repo = null;
        if (string.IsNullOrWhiteSpace(url))
            return false;

        var text = url.Trim();

        string host;
        string path;

        // Formato scp-like: git@github.com:owner/repo.git (sem esquema, com ':').
        if (!text.Contains("://", StringComparison.Ordinal) && text.Contains(':'))
        {
            var at = text.IndexOf('@');
            var colon = text.IndexOf(':');
            if (colon <= 0)
                return false;

            host = text[(at >= 0 ? at + 1 : 0)..colon];
            path = text[(colon + 1)..];
        }
        else
        {
            // Formatos com esquema: https://, http://, ssh://, git://.
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
                return false;

            host = uri.Host;
            path = uri.AbsolutePath;
        }

        // Normaliza o caminho para "owner/repo".
        path = path.Trim('/', '\\');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(host))
            return false;

        // owner/repo são os DOIS ÚLTIMOS segmentos (cobre subgrupos/paths extras).
        var owner = parts[^2];
        var name = parts[^1];
        if (owner.Length == 0 || name.Length == 0)
            return false;

        repo = new GitHubRepo(host, owner, name);
        return true;
    }
}
