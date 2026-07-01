namespace GitKit.Core.Models;

/// <summary>
/// Representa um arquivo em conflito (não mesclado) e seu tipo de conflito.
/// </summary>
public sealed class ConflictEntry
{
    public ConflictEntry(string path, string code, string description)
    {
        Path = path;
        Code = code;
        Description = description;
    }

    /// <summary>Caminho do arquivo (relativo à raiz do repositório).</summary>
    public string Path { get; }

    /// <summary>Código de status do git (ex.: "UU", "AA", "DU").</summary>
    public string Code { get; }

    /// <summary>Descrição amigável do tipo de conflito.</summary>
    public string Description { get; }
}
