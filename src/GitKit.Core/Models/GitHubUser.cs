namespace GitKit.Core.Models;

/// <summary>
/// Um usuário do GitHub (colaborador do repositório) candidato a revisor da PR.
/// </summary>
public sealed record GitHubUser(string Login, string? Name = null)
{
    /// <summary>Texto exibido na UI: login e, quando houver, o nome real.</summary>
    public string Display => string.IsNullOrWhiteSpace(Name) ? Login : $"{Login} ({Name})";

    public override string ToString() => Display;
}
