namespace GitKit.Core.Services;

/// <summary>
/// Histórico persistente das origens de repositório já utilizadas (URLs ou
/// caminhos locais), do mais recente para o mais antigo.
/// </summary>
public interface IRecentRepositories
{
    /// <summary>Lista as origens usadas, do mais recente para o mais antigo.</summary>
    IReadOnlyList<string> GetAll();

    /// <summary>Registra (ou promove) uma origem como a mais recente.</summary>
    void Add(string source);
}
