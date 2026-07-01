namespace GitKit.Core.Services;

/// <summary>
/// Cache local de repositórios remotos: mantém um clone espelho por remote e um
/// índice JSON. Ao clonar de uma URL, a cópia de trabalho é feita a partir do
/// espelho local (mais ágil), e o espelho é atualizado a cada uso.
/// </summary>
public interface IRepositoryCache
{
    /// <summary>
    /// Garante um espelho atualizado de <paramref name="repositoryUrl"/> e retorna o
    /// caminho do espelho (para clonar a cópia de trabalho a partir dele). Retorna
    /// <c>null</c> se não for possível preparar o cache (o chamador deve então clonar
    /// diretamente do remote).
    /// <paramref name="progress"/> (opcional) recebe as linhas de progresso do git em tempo real.
    /// </summary>
    Task<string?> EnsureCacheAsync(string repositoryUrl, IProgress<string>? progress = null, CancellationToken ct = default);
}
