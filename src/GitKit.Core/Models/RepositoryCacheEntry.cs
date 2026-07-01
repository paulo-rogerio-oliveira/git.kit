namespace GitKit.Core.Models;

/// <summary>
/// Registro de um repositório remoto que já foi espelhado localmente (cache),
/// persistido no índice JSON do cache.
/// </summary>
public sealed class RepositoryCacheEntry
{
    /// <summary>URL do remote original.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Caminho do espelho (clone --mirror) no disco.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Quando o cache foi criado (UTC).</summary>
    public DateTimeOffset CreatedUtc { get; set; }

    /// <summary>Última atualização do cache (UTC).</summary>
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>Índice (serializado em JSON) dos repositórios em cache.</summary>
public sealed class RepositoryCacheIndex
{
    public List<RepositoryCacheEntry> Entries { get; set; } = new();
}
