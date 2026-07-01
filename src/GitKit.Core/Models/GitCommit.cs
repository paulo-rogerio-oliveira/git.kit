namespace GitKit.Core.Models;

/// <summary>
/// Representa um commit que pode ser replicado entre branches.
/// </summary>
public sealed class GitCommit
{
    public GitCommit(string hash, string author, DateTimeOffset date, string subject)
    {
        Hash = hash;
        Author = author;
        Date = date;
        Subject = subject;
    }

    /// <summary>Hash completo (SHA-1/SHA-256) do commit.</summary>
    public string Hash { get; }

    /// <summary>Autor do commit.</summary>
    public string Author { get; }

    /// <summary>Data do commit.</summary>
    public DateTimeOffset Date { get; }

    /// <summary>Primeira linha da mensagem do commit.</summary>
    public string Subject { get; }

    /// <summary>Hash abreviado (7 caracteres).</summary>
    public string ShortHash => Hash.Length >= 7 ? Hash[..7] : Hash;

    public string Display => $"{ShortHash}  {Date:yyyy-MM-dd HH:mm}  {Author}  {Subject}";

    public override string ToString() => Display;
}
