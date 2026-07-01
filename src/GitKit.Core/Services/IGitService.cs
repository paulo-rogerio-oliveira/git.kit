using GitKit.Core.Models;

namespace GitKit.Core.Services;

/// <summary>
/// Operações de alto nível sobre um repositório git, realizadas via CLI.
/// </summary>
public interface IGitService
{
    /// <summary>Disparado a cada comando git executado (para log na UI).</summary>
    event Action<GitCommandResult>? CommandExecuted;

    /// <summary>Verifica se o git está disponível no PATH.</summary>
    Task<bool> IsGitAvailableAsync(CancellationToken ct = default);

    /// <summary>Verifica se o diretório informado é a raiz de um repositório git.</summary>
    Task<bool> IsRepositoryAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Clona <paramref name="repositoryUrl"/> dentro de <paramref name="destinationDirectory"/>.
    /// Retorna o caminho do repositório clonado.
    /// </summary>
    Task<GitCommandResult> CloneAsync(string repositoryUrl, string destinationDirectory, CancellationToken ct = default);

    /// <summary>Atualiza as referências remotas (<c>git fetch --all --prune</c>).</summary>
    Task<GitCommandResult> FetchAsync(string repositoryPath, CancellationToken ct = default);

    /// <summary>
    /// Retorna a URL do remote <c>origin</c> do repositório, ou string vazia
    /// se não houver remote configurado.
    /// </summary>
    Task<string> GetRemoteUrlAsync(string repositoryPath, CancellationToken ct = default);

    /// <summary>
    /// Define a URL do remote <c>origin</c> do repositório (cria o remote se necessário).
    /// </summary>
    Task<GitCommandResult> SetRemoteUrlAsync(string repositoryPath, string remoteUrl, CancellationToken ct = default);

    /// <summary>Lista branches locais e remotos.</summary>
    Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repositoryPath, CancellationToken ct = default);

    /// <summary>Lista os commits mais recentes do branch informado.</summary>
    Task<IReadOnlyList<GitCommit>> GetCommitsAsync(string repositoryPath, string branch, int max = 100, CancellationToken ct = default);

    /// <summary>
    /// Replica <paramref name="commit"/> do branch de origem no branch de destino,
    /// usando a estratégia escolhida. O branch de destino é colocado em checkout.
    /// </summary>
    Task<ReplicationResult> ReplicateCommitAsync(
        string repositoryPath,
        GitCommit commit,
        string destinationBranch,
        ReplicationMode mode,
        CancellationToken ct = default);

    /// <summary>Aborta uma operação de replicação em andamento (cherry-pick/merge).</summary>
    Task<GitCommandResult> AbortReplicationAsync(string repositoryPath, ReplicationMode mode, CancellationToken ct = default);

    /// <summary>
    /// Conclui uma replicação após o usuário resolver os conflitos manualmente:
    /// estagia os arquivos resolvidos e finaliza (cherry-pick --continue ou commit).
    /// </summary>
    Task<ReplicationResult> ContinueReplicationAsync(
        string repositoryPath, GitCommit commit, ReplicationMode mode, CancellationToken ct = default);

    /// <summary>Lista os caminhos dos arquivos atualmente em conflito (não mesclados).</summary>
    Task<IReadOnlyList<string>> GetConflictedFilesAsync(string repositoryPath, CancellationToken ct = default);

    /// <summary>
    /// Lista os arquivos em conflito com metadados (tipo de conflito) para exibição.
    /// </summary>
    Task<IReadOnlyList<ConflictEntry>> GetConflictsAsync(string repositoryPath, CancellationToken ct = default);

    /// <summary>
    /// Extrai o conteúdo de um estágio do índice de um arquivo em conflito
    /// (1 = ancestral comum/base, 2 = nosso/destino, 3 = deles/origem) para
    /// <paramref name="destinationPath"/>. Retorna o caminho gravado, ou null
    /// se o estágio não existir (ex.: conflito de adição sem ancestral).
    /// </summary>
    Task<string?> ExtractConflictStageAsync(
        string repositoryPath, string file, int stage, string destinationPath, CancellationToken ct = default);

    /// <summary>
    /// Envia (<c>git push</c>) o branch informado para o remote <c>origin</c>,
    /// definindo o upstream quando solicitado.
    /// </summary>
    Task<GitCommandResult> PushAsync(string repositoryPath, string branch, bool setUpstream = true, CancellationToken ct = default);
}
