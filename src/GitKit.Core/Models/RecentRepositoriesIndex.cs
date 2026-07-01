namespace GitKit.Core.Models;

/// <summary>
/// Histórico (MRU) das origens de repositório já utilizadas — URLs de clone ou
/// caminhos locais. A ordem da lista é do mais recente para o mais antigo.
/// </summary>
public sealed class RecentRepositoriesIndex
{
    public List<string> Sources { get; set; } = new();
}
