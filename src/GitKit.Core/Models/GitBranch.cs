namespace GitKit.Core.Models;

/// <summary>
/// Representa um branch (local ou remoto) de um repositório git.
/// </summary>
public sealed class GitBranch
{
    public GitBranch(string name, bool isRemote, bool isCurrent)
    {
        Name = name;
        IsRemote = isRemote;
        IsCurrent = isCurrent;
    }

    /// <summary>Nome do branch (ex.: "main" ou "origin/feature/x").</summary>
    public string Name { get; }

    /// <summary>Indica se o branch é remoto.</summary>
    public bool IsRemote { get; }

    /// <summary>Indica se é o branch atualmente em checkout.</summary>
    public bool IsCurrent { get; }

    /// <summary>Nome curto, sem o prefixo "origin/" quando remoto.</summary>
    public string ShortName => IsRemote && Name.Contains('/')
        ? Name[(Name.IndexOf('/') + 1)..]
        : Name;

    public override string ToString() => IsCurrent ? $"* {Name}" : Name;
}
